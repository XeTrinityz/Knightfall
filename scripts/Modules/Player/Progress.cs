#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > PROGRESS. Everything here changes the save, through the game's own routines.
static class Progress
{
    const int Points = 10;
    const int Experience = 5000;

    // A second press within this long confirms a permanent change.
    const double ConfirmSeconds = 4;

    // The game's own unlock-all asks Steam about all of them in one go, which held the game still,
    // and asks about numbers that aren't achievements too. Only the ones the game lists as not
    // yet earned are asked for here, spaced out over a few ticks each.
    const int TicksApart = 4;

    static bool _granting;
    static long _askedAt;
    static readonly Queue<int> Wanted = new();
    static int _unlocked;
    static int _had;
    static int _wait;

    public static readonly Command AddPoints = new(
        "player.progress.add_points", "ADD UPGRADE POINTS",
        $"Adds {Points} points to spend in the upgrade menu. Kept in your save.",
        GivePoints);

    public static readonly Command AddExperience = new(
        "player.progress.add_xp", "ADD 5000 XP",
        "Adds 5000 experience, as a fight would. Enough of it levels you up and earns upgrade points.",
        GiveExperience);

    public static readonly Slider ExperienceScale = new(
        "player.progress.xp_multiplier", "XP MULTIPLIER",
        "Multiplies the experience you earn from fights, stealth, the Batmobile and everything else.",
        min: 1, max: 10, step: 0.5, value: 1, unit: "X");

    public static readonly Command AllUpgrades = new(
        "player.progress.all_upgrades", "GET ALL UPGRADES",
        "Buys every Batman and Batmobile upgrade the story has opened and keeps your unspent points. Kept in your save. Run it again as the story opens more.",
        BuyUpgrades);

    public static readonly Command RiddlerItems = new(
        "player.progress.riddler_items", "SHOW RIDDLER ITEMS",
        "Marks every Riddler trophy and puzzle on the map. Kept in your save.",
        ShowRiddler);

    public static readonly Command AllExtras = new(
        "player.progress.all_extras", "UNLOCK ALL EXTRAS",
        "The game's own unlock-all. Challenge maps, the showcase, concept art, bios, tapes, Riddler puzzles, WayneTech and story episodes. Kept in your save.",
        UnlockExtras);

    public static readonly Command Achievements = new(
        "player.progress.achievements", "UNLOCK ALL ACHIEVEMENTS",
        "Unlocks every achievement on your Steam account, story pack ones included, skipping the ones you have. This can't be undone. Press twice to confirm.",
        UnlockAchievements);

    public static readonly Command SaveNow = new(
        "player.progress.save_now", "SAVE GAME NOW",
        "Saves now, the way the game does at story points. You load back in at the nearest start point, not the exact spot.",
        Save);

    public static readonly Catalog.Section[] Sections =
    {
        new("POINTS", AddPoints, AddExperience, ExperienceScale, AllUpgrades),
        new("UNLOCKS", RiddlerItems, AllExtras, Achievements),
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
            _askedAt = 0;
            return Outcome.Unchanged($"YOU HAVE ALL {list.Count}");
        }

        var now = Stopwatch.GetTimestamp();
        if (_askedAt == 0 || Stopwatch.GetElapsedTime(_askedAt, now).TotalSeconds > ConfirmSeconds)
        {
            _askedAt = now;
            return Outcome.Unchanged($"{missing.Count} TO GET, PRESS AGAIN");
        }

        _askedAt = 0;
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
