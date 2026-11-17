#nullable enable

using System.Diagnostics;
using System.Text.RegularExpressions;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > PROGRESS. Everything here changes the save, through the game's own routines.
static class Progress
{
    const int Points = 10;
    const int Experience = 5000;

    // The game's own unlock-all asks Steam about all of them in one go, which held the game still,
    // and asks about numbers that aren't achievements too. Only the ones the game lists as not
    // yet earned are asked for here, spaced out over a few ticks each.
    const int TicksApart = 4;

    static bool _granting;
    static readonly Queue<int> Wanted = new();
    static int _unlocked;
    static int _had;
    static int _wait;

    public static readonly Command AddPoints = new(
        "player.progress.add_points", "ADD UPGRADE POINTS",
        $"Adds {Points} points to spend in the upgrade menu. Kept in your save.",
        GivePoints)
    {
        Block = static () => PlayerLocks.InChallenge(),
    };

    public static readonly Command AddExperience = new(
        "player.progress.add_xp", "ADD 5000 XP",
        "Adds 5000 experience, as a fight would. Enough of it levels you up and earns upgrade points.",
        GiveExperience)
    {
        Block = static () => PlayerLocks.InChallenge(),
    };

    public static readonly Slider ExperienceScale = new(
        "player.progress.xp_multiplier", "XP MULTIPLIER",
        "Multiplies the experience you earn from fights, stealth, the Batmobile and everything else.",
        min: 1, max: 10, step: 0.5, value: 1, unit: "X");

    public static readonly Command AllUpgrades = new(
        "player.progress.all_upgrades", "GET ALL UPGRADES",
        "Buys every Batman and Batmobile upgrade the story has opened and keeps your unspent points. Kept in your save. Run it again as the story opens more.",
        BuyUpgrades)
    {
        Block = static () => PlayerLocks.InChallenge(),
    };

    public static readonly Command RiddlerItems = new(
        "player.progress.riddler_items", "SHOW RIDDLER ITEMS",
        "Marks every Riddler trophy and puzzle on the map. Kept in your save.",
        ShowRiddler)
    {
        Block = static () => PlayerLocks.InChallenge(),
    };

    public static readonly Command AllExtras = new(
        "player.progress.all_extras", "UNLOCK ALL EXTRAS",
        "The game's own unlock-all. Challenge maps, the showcase, concept art, bios, tapes, Riddler puzzles, WayneTech and story episodes. Kept in your save.",
        UnlockExtras)
    {
        Block = static () => PlayerLocks.InChallenge(),
    };

    // Picks by the names the challenge rosters use. No names is everyone. CITY AR CHALLENGES is
    // the default slot on the maps placed in the city.
    static readonly (string Id, string Label, string[] Names)[] MedalWho =
    {
        ("all", "EVERYONE", Array.Empty<string>()),
        ("city", "CITY AR CHALLENGES", Array.Empty<string>()),
        ("batman", "BATMAN", new[] { "Batman" }),
        ("robin", "ROBIN", new[] { "Robin" }),
        ("nightwing", "NIGHTWING", new[] { "Nightwing" }),
        ("azrael", "AZRAEL", new[] { "Azrael" }),
        ("catwoman", "CATWOMAN", new[] { "Catwoman" }),
        ("harley", "HARLEY QUINN", new[] { "DLCHarleyQuinn" }),
        ("redhood", "RED HOOD", new[] { "DLCRedhood" }),
        ("batgirl", "BATGIRL", new[] { "DLCBatgirl" }),
        ("cars", "BATMOBILES", new[] { "Batmobile", "BatmobileBurton", "BatmobileClassic", "BatmobileSnyder", "BatmobileTumbler", "BatmobileAsylum" }),
    };

    const int CityPick = 1;

    // The challenge menu shows times in hundredths of a second.
    const int Second = 100;

    public static readonly Choice MedalsFor = new(
        "player.progress.medals_for", "MEDALS FOR",
        "Who GET ALL CHALLENGE MEDALS gives them to. CITY AR CHALLENGES are the ones out in Gotham. EVERYONE is those, every character and every Batmobile.",
        MedalWho.Select(who => (who.Id, who.Label)).ToArray())
    {
        Preference = true,
    };

    public static readonly Command ChallengeMedals = new(
        "player.progress.challenge_medals", "GET ALL CHALLENGE MEDALS",
        "Gives three medals on every challenge to whoever is picked in MEDALS FOR, so they show as done, and clears the orange new marks on them. A score short of three medals is raised to the three medal mark. Better scores stay. Kept in your save. This can't be undone.",
        GiveMedals)
    {
        Block = static () => PlayerLocks.InChallenge(),
    };

    public static readonly Command Achievements = new(
        "player.progress.achievements", "UNLOCK ALL ACHIEVEMENTS",
        "Unlocks every achievement on your Steam account, story pack ones included, skipping the ones you have. This can't be undone.",
        UnlockAchievements);

    public static readonly Command SaveNow = new(
        "player.progress.save_now", "SAVE GAME NOW",
        "Saves now, the way the game does at story points. You load back in at the nearest start point, not the exact spot.",
        Save);

    public static readonly Catalog.Section[] Sections =
    {
        new("POINTS", AddPoints, AddExperience, ExperienceScale, AllUpgrades),
        new("UNLOCKS", RiddlerItems, AllExtras, MedalsFor, ChallengeMedals, Achievements),
        new("SAVE", SaveNow),
    };

    // Every experience award passes through here before it is banked.
    [Redirect(typeof(RExperienceSystem), nameof(RExperienceSystem.AddUnbankedXP))]
    static void AddUnbankedXP(RExperienceSystem self, int xpAwarded, bool teeth)
    {
        var scale = ExperienceScale.Value;
        if (_granting || xpAwarded <= 0 || scale <= 1.0)
        {
            Redirects.RunOriginal();
            return;
        }

        self.AddUnbankedXP((int)Math.Round(xpAwarded * scale), teeth);
    }

    static RGameInfo? Story(out Outcome refusal)
    {
        refusal = default;
        if (World.Info is not { } game || World.Player is null)
        {
            refusal = Outcome.NoPlayer;
            return null;
        }

        if (game.IsChallengeMode())
        {
            refusal = Outcome.Failed("NOT IN CHALLENGES");
            return null;
        }

        return game;
    }

    static Outcome GivePoints()
    {
        if (Story(out var refusal) is not { PersistentShared: { IsValid: true } shared })
        {
            return refusal == default ? Outcome.Failed("NO SAVE DATA") : refusal;
        }

        shared.UpgradePointsAvailable += Points;
        return Outcome.Done($"+{Points} POINTS");
    }

    static Outcome GiveExperience()
    {
        if (Story(out var refusal) is null)
        {
            return refusal;
        }

        // Exactly what was asked for, whatever XP MULTIPLIER is set to.
        _granting = true;
        try
        {
            Game.GetCheatManager().AddXP(Experience);
        }
        finally
        {
            _granting = false;
        }

        return Outcome.Done();
    }

    // The game's own purchase of every upgrade, the one its WayneTech unlock uses. That also sets
    // the unspent points to nothing, so they are kept here instead.
    static Outcome BuyUpgrades()
    {
        if (Story(out var refusal) is not { PersistentShared: { IsValid: true } shared } game)
        {
            return refusal == default ? Outcome.Failed("NO SAVE DATA") : refusal;
        }

        var before = shared.UpgradesPurchased;
        shared.UpgradesPurchased = game.UpgradesUnlockAll(out var cost);
        shared.UpgradePointsUsed = Math.Max(shared.UpgradePointsUsed, cost);

        if (World.Controller is { } controller)
        {
            if (game.Batmobile is { IsValid: true } car)
            {
                car.CheckForUpgrades(controller);
            }

            if (controller.OriginalBatmanPawn is { IsValid: true, GrappleGun: { IsValid: true } grapnel })
            {
                grapnel.UpdateGrappleBoostLevel();
            }
        }

        World.Player?.UpdateMaximumHealthAndArmour(true, true);
        var gained = shared.UpgradesPurchased - before;
        return gained > 0 ? Outcome.Done($"+{gained} UPGRADES") : Outcome.Unchanged("ALL BOUGHT");
    }

    static Outcome ShowRiddler()
    {
        if (Story(out var refusal) is null)
        {
            return refusal;
        }

        // Empty is every map. The game's default for it only applies when script calls it.
        var cheats = Game.GetCheatManager();
        cheats.RiddlerRevealItems(new FString(""));
        cheats.GiveRiddlerMap();
        return Outcome.Done();
    }

    static Outcome UnlockExtras()
    {
        if (Story(out var refusal) is null)
        {
            return refusal;
        }

        Game.GetCheatManager().UnlockAll();
        return Outcome.Done();
    }

    // The game's own debug calls set the medals and scores, the way its cheats do. Only challenge
    // maps the save has open, and only the entries short of three medals or of the score for them.
    static Outcome GiveMedals()
    {
        if (Story(out var refusal) is not { } game)
        {
            return refusal;
        }

        if (!Game.TryGetGameRI(out var info) || info.PersistentOptions is not { IsValid: true } options)
        {
            return Outcome.Failed("NO SAVE DATA");
        }

        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var table = game.ChallengeCharacterIds;
        for (var i = 0; i < table.Count; i++)
        {
            ids.TryAdd(table[i].Name.ToString(), table[i].Id);
        }

        var who = MedalWho[MedalsFor.Index].Names;
        var cityOnly = MedalsFor.Index == CityPick;
        var wanted = new List<(int Challenge, int Slot, bool Medals, int? Score)>();
        var fresh = new List<int>();
        var maps = game.ChallengeMapList;
        for (var i = 0; i < maps.Count; i++)
        {
            var map = maps[i];
            var group = map.ChallengeFrontendGroup.ToString();
            if (map.Id <= 0 || !options.IsChallengeUnlocked(map.Id))
            {
                continue;
            }

            if (options.IsChallengeNew(map.Id) || options.IsChallengeDirty(map.Id))
            {
                fresh.Add(map.Id);
            }

            // Training counts as done once played, not by medals.
            if (group.Contains("Tutorial", StringComparison.Ordinal))
            {
                continue;
            }

            var inCity = map.ChallengeFrontendState.ToString().EndsWith("_Default", StringComparison.Ordinal);
            if (cityOnly && !inCity)
            {
                continue;
            }

            var cars = group.Contains("Batmobile", StringComparison.Ordinal);
            foreach (var (slot, name) in Roster(map.Id, cars, ids))
            {
                if (cityOnly ? slot != 0 : who.Length > 0 && Array.IndexOf(who, name) < 0)
                {
                    continue;
                }

                var medals = options.GetChallengeMedals(map.Id, slot) < 3;
                var score = Bar(map.Id, name, map.ChallengeRankMode) is { } bar
                    ? Raised(options.GetChallengeScore(map.Id, slot), bar, map.ChallengeRankMode)
                    : null;
                if (medals || score is not null)
                {
                    wanted.Add((map.Id, slot, medals, score));
                }
            }
        }

        // The orange mark on a challenge is its dirty flag, the one the challenge wheel clears
        // when you look at it. Finished ones needn't keep it, or the new flag either.
        void Seen()
        {
            foreach (var id in fresh)
            {
                options.SetChallengeNew(id, false);
                options.SetChallengeDirty(id, false);
            }
        }

        if (wanted.Count == 0)
        {
            Seen();
            return Outcome.Unchanged(fresh.Count == 0 ? "ALL HAVE 3 MEDALS" : $"{fresh.Count} MARKS CLEARED");
        }

        var medalCount = wanted.Count(entry => entry.Medals);
        var scoreCount = wanted.Count(entry => entry.Score is not null);
        var ask = new Confirmation("CAN'T UNDO", "GET ALL CHALLENGE MEDALS",
            $"Gives three medals for {MedalWho[MedalsFor.Index].Label} on {medalCount} challenge {(medalCount == 1 ? "entry" : "entries")} and raises {scoreCount} {(scoreCount == 1 ? "score" : "scores")} to the three medal mark in your save. Better scores stay.",
            "GIVE", "CANCEL");
        if (!Confirm.Asked(ChallengeMedals, ask))
        {
            return Outcome.Asking;
        }

        var cheats = Game.GetCheatManager();
        foreach (var (challenge, slot, medals, score) in wanted)
        {
            if (medals)
            {
                cheats.ChallengeSetMedals(challenge, 3, slot);
            }

            if (score is { } value)
            {
                cheats.ChallengeSetScore(challenge, value, slot);
            }
        }

        Seen();
        Log.Info($"Challenge medals: three on {medalCount} entries and {scoreCount} scores raised for {MedalWho[MedalsFor.Index].Label}.");
        return Outcome.Done($"{wanted.Count} SET");
    }

    // Who can play a challenge, as its menu lists them. The starred one is the challenge's own
    // character, which the save files under 0. Team and story challenges have no list, so only
    // their own character plays them.
    static List<(int Slot, string Name)> Roster(int challenge, bool cars, Dictionary<string, int> ids)
    {
        var roster = new List<(int Slot, string Name)>();
        var own = cars ? "Batmobile" : "Batman";
        var names = Details($"{challenge}_Characters")?.Split('|');
        if (names is { Length: > 1 })
        {
            foreach (var entry in names)
            {
                var name = entry.Trim();
                if (name.StartsWith('*'))
                {
                    own = name[1..];
                }
                else if (ids.TryGetValue(name, out var id) && id > 0)
                {
                    roster.Add((id, name));
                }
            }
        }

        roster.Insert(0, (0, own));
        return roster;
    }

    // The three medal mark from the challenge's own goal text. Times come out in hundredths.
    // Goals that are tasks rather than a number give none.
    static int? Bar(int challenge, string name, RGameInfo.EChallengeRankMode rank)
    {
        var goal = Details($"{challenge}_Goal_3_{name.ToUpperInvariant()}") ?? Details($"{challenge}_Goal_3");
        if (goal is null)
        {
            return null;
        }

        if (rank is RGameInfo.EChallengeRankMode.ECRM_ShortestTime or RGameInfo.EChallengeRankMode.ECRM_LongestTime)
        {
            var times = Regex.Matches(goal, @"(\d+):(\d{2})");
            if (times.Count == 0)
            {
                return null;
            }

            var time = times[^1];
            return (int.Parse(time.Groups[1].Value) * 60 + int.Parse(time.Groups[2].Value)) * Second;
        }

        if (rank == RGameInfo.EChallengeRankMode.ECRM_HighScore)
        {
            var points = Regex.Match(goal, @"(?:\bscore\s+|\bx|\$)([\d,]+)", RegexOptions.IgnoreCase);
            return points.Success ? int.Parse(points.Groups[1].Value.Replace(",", "")) : null;
        }

        return null;
    }

    // The score to save, or null when the one there already earns three medals. A time goal
    // is beaten by a second.
    static int? Raised(int score, int bar, RGameInfo.EChallengeRankMode rank) => rank switch
    {
        RGameInfo.EChallengeRankMode.ECRM_ShortestTime => score > 0 && score < bar ? null : bar - Second,
        RGameInfo.EChallengeRankMode.ECRM_LongestTime => score > bar ? null : bar + Second,
        _ => score >= bar ? null : bar,
    };

    static string? Details(string key)
    {
        var text = GameObject.Localize("Details", key, "ARChallenges").ToString()?.Trim();
        return string.IsNullOrEmpty(text) || text.StartsWith('?') ? null : text;
    }

    static Outcome UnlockAchievements()
    {
        if (World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        if (Wanted.Count > 0)
        {
            return Outcome.Unchanged("WORKING");
        }

        if (controller.OnlinePlayerData is not { IsValid: true, AchievementsProvider: { IsValid: true } provider })
        {
            return Outcome.Failed("NO ACHIEVEMENT LIST");
        }

        // The game's own list of them, brought up to date from Steam first.
        provider.UpdateAchievements();
        var missing = new List<int>();
        var list = provider.Achievements;
        for (var i = 0; i < list.Count; i++)
        {
            var achievement = list[i];
            if (achievement.Id > 0 && !achievement.bWasAchievedOnline && !achievement.bWasAchievedOffline)
            {
                missing.Add(achievement.Id);
            }
        }

        _had = list.Count - missing.Count;
        if (list.Count == 0)
        {
            return Outcome.Failed("NO ACHIEVEMENT LIST");
        }

        if (missing.Count == 0)
        {
            return Outcome.Unchanged($"YOU HAVE ALL {list.Count}");
        }

        var ask = new Confirmation("CAN'T UNDO", "UNLOCK ALL ACHIEVEMENTS",
            missing.Count == 1
                ? "The one achievement you don't have unlocks on your Steam account. It can't be taken back off."
                : $"The {missing.Count} achievements you don't have unlock on your Steam account. They can't be taken back off.",
            "UNLOCK", "CANCEL");
        if (!Confirm.Asked(Achievements, ask))
        {
            return Outcome.Asking;
        }

        _unlocked = 0;
        _wait = 0;
        foreach (var id in missing)
        {
            Wanted.Enqueue(id);
        }

        return Outcome.Unchanged("WORKING");
    }

    public static void Tick()
    {
        if (Wanted.Count == 0 || --_wait > 0)
        {
            return;
        }

        if (World.Controller is not { } controller)
        {
            Wanted.Clear();
            Achievements.Report(Outcome.Failed("STOPPED"));
            return;
        }

        _wait = TicksApart;

        // The game's default progress only applies when script calls it, so it is given here.
        if (controller.UnlockAchievement((RGameInfo.EAchievementID)Wanted.Dequeue(), 100f))
        {
            _unlocked++;
        }

        if (Wanted.Count == 0)
        {
            Log.Info($"Achievements: {_unlocked} unlocked, {_had} you already had.");
            Achievements.Report(Outcome.Done($"{_unlocked} NEW, {_had} HAD"));
        }
        else
        {
            Achievements.Report(Outcome.Unchanged($"WORKING, {Wanted.Count} LEFT"));
        }
    }

    public static void Release()
    {
        Wanted.Clear();
    }

    static Outcome Save()
    {
        if (Story(out var refusal) is not { } game)
        {
            return refusal;
        }

        if (game.GetIsSaveInProgress())
        {
            return Outcome.Unchanged("ALREADY SAVING");
        }

        Game.GetCheatManager().AutoSaveGame();
        return Outcome.Done("SAVING");
    }
}
