#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SPAWNER > BODYGUARDS. People who follow you everywhere and fight for you. They can be hired
// from anyone the area has loaded, or recruited from the enemies in front of you. GuardBrain runs
// each one, Strikes lands their hits and Hunters sends enemies after them. Other features bring
// their own people in with Enlist and Take, and send them off with Let.
static class Bodyguards
{
    const float Metre = Teleport.UnitsPerMetre;

    // The most at once. Each one thinks every tick.
    const int Most = 12;

    const float ScanEvery = 0.25f;

    // Enemies are listed this far past PROTECT RANGE, so one walking in is known in time.
    const float ScanMargin = 1000f;

    // A fresh body's brain starts on a short timer after it begins play.
    const float WakeAfter = 0.5f;

    // UNBEATABLE BODYGUARDS gets a fallen one up after this long.
    const float GetUpAfter = 4f;

    // A body the game has hidden this long isn't coming back.
    const float GoneAfter = 3f;

    // BODYGUARDS RECOVER. This long after their last fight or hit, a share of their health comes
    // back each second.
    const float RecoverAfter = 5f;
    const float RecoverShare = 0.08f;

    const int Hold = 1;
    const int StandDown = 2;

    static readonly List<Guard> Team = new();
    static readonly List<RPawnVillain> Enemies = new();
    static float _scanAt;
    static bool _resumed;
    static int _nextSlot;

    // What a tick's steps share.
    static Pawn? _you;
    static bool _scanned;

    public static readonly Choice Who = new(
        "spawner.bodyguards.who", "WHO",
        "Who HIRE brings: everyone this area has loaded who can fight. Changes with where you are. Select hires them.",
        new[] { ("", "OPEN THE MENU IN GAME") }, saved: false)
    {
        Pressed = static () => Hire!.Press(),
        PressName = "HIRE",
    };

    public static readonly Slider Count = new(
        "spawner.bodyguards.count", "HOW MANY",
        "How many each HIRE brings.",
        min: 1, max: 6, step: 1, value: 1)
    {
        Preference = true,
    };

    public static readonly Slider Health = new(
        "spawner.bodyguards.health", "BODYGUARD HEALTH",
        "How much health a hired bodyguard gets, times their normal amount.",
        min: 1, max: 20, step: 1, value: 3, unit: "X")
    {
        Preference = true,
    };

    public static readonly Command Hire = new(
        "spawner.bodyguards.hire", "HIRE BODYGUARD",
        "Brings the person picked in WHO to your side as a bodyguard. They follow you everywhere and fight whoever comes for you.",
        HirePicked);

    public static readonly Command Recruit = new(
        "spawner.bodyguards.recruit", "RECRUIT WHO I AIM AT",
        "Turns the enemy in the middle of your screen into a bodyguard. DISMISS puts them back how they were.",
        RecruitAimed);

    public static readonly Choice Orders = new(
        "spawner.bodyguards.orders", "ORDERS",
        "PROTECT ME has them follow you and fight whoever comes near. HOLD HERE keeps each where they stand. They fight what comes near that spot and walk back to it after. STAND DOWN has them follow you and fight nobody.",
        new[] { ("protect", "PROTECT ME"), ("hold", "HOLD HERE"), ("stand_down", "STAND DOWN") }, saved: false);

    public static readonly Command Attack = new(
        "spawner.bodyguards.attack", "ATTACK WHO I AIM AT",
        "Sends every bodyguard after the enemy in the middle of your screen until that one is down.",
        AttackAimed)
    {
        Block = static () => NoTeam() ?? (StoodDown ? "THEY'RE STOOD DOWN" : null),
    };

    public static readonly Command Regroup = new(
        "spawner.bodyguards.regroup", "BRING THEM TO ME",
        "Puts every bodyguard still standing in a ring around you, wherever they are. Ones told to HOLD HERE hold there from then on.",
        RegroupNow)
    {
        Block = NoTeam,
    };

    public static readonly Command Heal = new(
        "spawner.bodyguards.heal", "HEAL BODYGUARDS",
        "Fills every bodyguard's health and gets the ones knocked out back on their feet.",
        HealAll)
    {
        Block = NoTeam,
    };

    public static readonly Command DismissAimed = new(
        "spawner.bodyguards.dismiss_aimed", "DISMISS WHO I AIM AT",
        "Lets the bodyguard in the middle of your screen go. A hired one leaves the world. A recruited one goes back to how they were.",
        DismissOne)
    {
        Block = NoTeam,
    };

    public static readonly Command Dismiss = new(
        "spawner.bodyguards.dismiss", "DISMISS BODYGUARDS",
        "Lets every bodyguard go. Hired ones leave the world. Recruited ones go back to how they were.",
        DismissAll)
    {
        Block = NoTeam,
    };

    public static readonly Slider Range = new(
        "spawner.bodyguards.range", "PROTECT RANGE",
        "How far from you bodyguards go after enemies. Anyone right next to a bodyguard is fought wherever you are.",
        min: 10, max: 80, step: 5, value: 30, unit: " M")
    {
        Preference = true,
    };

    public static readonly Slider Gap = new(
        "spawner.bodyguards.gap", "FOLLOW DISTANCE",
        "How close bodyguards stay to you between fights.",
        min: 1.5, max: 12, step: 0.5, value: 3, unit: " M")
    {
        Preference = true,
    };

    public static readonly Toggle EnemiesFight = new(
        "spawner.bodyguards.enemies_fight", "ENEMIES FIGHT BODYGUARDS",
        "Enemies treat your bodyguards like they treat you. One nearer a bodyguard goes for them, up to three each. Off, enemies only ever fight you.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Toggle CatchUp = new(
        "spawner.bodyguards.catch_up", "BODYGUARDS CATCH UP",
        "A bodyguard left far behind, or with no way up to you, appears behind you once you stop, where you can't see it happen.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Toggle Recover = new(
        "spawner.bodyguards.recover", "BODYGUARDS RECOVER",
        "A bodyguard's health comes back by itself, a few seconds after their last fight or hit.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Toggle Unbeatable = new(
        "spawner.bodyguards.unbeatable", "UNBEATABLE BODYGUARDS",
        "Keeps every bodyguard's health full. One who still goes down gets back up after a few seconds.");

    public static readonly Slider HitsToKnockOut = new(
        "spawner.bodyguards.hits", "HITS TO KNOCK OUT",
        "How many hits from a bodyguard it takes to knock an enemy out, when the game's own punch does nothing to them.",
        min: 1, max: 10, step: 1, value: 4)
    {
        Preference = true,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("HIRE", Who, People.RefreshList, People.Weapon, Count, Health, Hire, Recruit),
        new("ORDERS", Orders, Attack, Regroup, Heal, DismissAimed, Dismiss),
        new("BEHAVIOUR", Range, Gap, EnemiesFight, CatchUp, Recover, Unbeatable, HitsToKnockOut),
        GuardMarks.Section,
    };

    public static IReadOnlyList<Guard> All => Team;

    public static bool Holding => Orders.Index == Hold;

    public static bool StoodDown => Orders.Index == StandDown;

    // Room left in the team.
    public static int Room => Math.Max(0, Most - Team.Count);

    // Why a row that needs a team can't be used. Asked every frame such a row is on screen.
    public static string? NoTeam() => Team.Count == 0 ? "NO BODYGUARDS" : null;

    // Whether anyone is being steered. The hit redirects ask this first, so every other attack
    // in the game costs one check.
    public static bool Busy => Team.Count > 0 || Hunters.Count > 0;

    public static Guard? GuardOf(Actor? body)
    {
        if (body is null)
        {
            return null;
        }

        foreach (var guard in Team)
        {
            if (ReferenceEquals(guard.Body, body))
            {
                return guard;
            }
        }

        return null;
    }

    public static bool Is(Actor? body) => Team.Count > 0 && GuardOf(body) is not null;

    public static Fighter? FighterOf(RPawnVillain body) => (Fighter?)GuardOf(body) ?? Hunters.Of(body);

    // The fighters the area has loaded. Called each time the lists are read.
    public static void ListWho()
    {
        var fighters = People.Fighters();
        if (fighters.Length == 0)
        {
            Who.SetOptions(new[] { ("", "NO FIGHTERS LOADED HERE") }, 0);
            return;
        }

        var picked = Who.CurrentId;
        Who.SetOptions(fighters, Math.Max(0, Array.FindIndex(fighters, fighter => fighter.Key == picked)));
    }

    static Outcome HirePicked()
    {
        // A hotkey can ask before any tab has read the lists.
        People.Fresh();
        if (Who.CurrentId.Length == 0)
        {
            ListWho();
            if (Who.CurrentId.Length == 0)
            {
                return Outcome.Failed("NOBODY TO HIRE HERE");
            }
        }

        if (Room == 0)
        {
            return Outcome.Failed($"AT MOST {Most}");
        }

        return People.Hire(Who.CurrentId, Math.Min((int)Count.Value, Room), (float)Health.Value, static (body, label, health) => Adopt(body, label, false, health));
    }

    // Hires people for another feature, like a villain's goons, and hands back the bodies made.
    public static List<RPawnVillain> Enlist(string key, int count, float health)
    {
        var made = new List<RPawnVillain>();
        var room = Math.Min(count, Room);
        if (room > 0)
        {
            People.Hire(key, room, health, (body, label, normal) =>
            {
                Adopt(body, label, false, normal);
                made.Add(body);
            });
        }

        return made;
    }

    // Takes on someone another feature made, like a villain's squad. False when the team is full.
    public static bool Take(RPawnVillain body, string label, int baseHealth)
    {
        if (Room == 0 || Is(body))
        {
            return false;
        }

        Adopt(body, label, false, baseHealth);
        return true;
    }

    // Lets one bodyguard go, as DISMISS BODYGUARDS does for everyone. False when they weren't one.
    public static bool Let(RPawnVillain body)
    {
        if (GuardOf(body) is not { } guard)
        {
            return false;
        }

        SendOff(guard);
        return true;
    }

    // A recruit goes back to how they were. A hired one leaves the world, and the spawner tells
    // us as it takes them, which hands them back.
    static void SendOff(Guard guard)
    {
        if (guard.Recruited)
        {
            Drop(guard, guard.WasHostile);
            if (guard.WasHostile)
            {
                Fight.Rejoin(guard.Body);
            }
        }
        else
        {
            Spawned.Take(guard.Body);

            // Still here when their body was already gone, so the spawner had nothing to tell.
            if (Team.Contains(guard))
            {
                Drop(guard, false);
            }
        }
    }

    // Out of the team, with what we changed on them put back.
    static void Drop(Guard guard, bool hostile)
    {
        Team.Remove(guard);
        GuardBrain.Release(guard, hostile);
    }

    static Outcome RecruitAimed()
    {
        if (World.Player is null)
        {
            return Outcome.NoPlayer;
        }

        if (Room == 0)
        {
            return Outcome.Failed($"AT MOST {Most}");
        }

        if (Editor.Aimed(Things.Kind.Enemy) is not RPawnVillain thug)
        {
            return Outcome.Failed("AIM AT AN ENEMY");
        }

        if (Is(thug))
        {
            return Outcome.Unchanged("ALREADY YOURS");
        }

        if (!Fight.Standing(thug) || thug.Base is Vehicle)
        {
            return Outcome.Failed("THEY CAN'T JOIN NOW");
        }

        var label = Things.NameOf(thug);
        Adopt(thug, label, true, Math.Max(thug.HealthMax, thug.Health));
        Log.Info($"Bodyguards: recruited {label} ({thug.Class.Name}). {Team.Count} of {Most} in the team.");
        return Outcome.Done("RECRUITED");
    }

    static Outcome DismissOne()
    {
        if (Team.Count == 0)
        {
            return Outcome.Unchanged("NO BODYGUARDS");
        }

        if (Editor.Aimed(Things.Kind.Enemy) is not RPawnVillain thug || GuardOf(thug) is not { } guard)
        {
            return Outcome.Failed("AIM AT A BODYGUARD");
        }

        SendOff(guard);
        Log.Info($"Bodyguards: dismissed {guard.Label}. {Team.Count} left in the team.");
        return Outcome.Done("DISMISSED");
    }

    // WasHostile is only given for someone picked up again after a script reload, who has been
    // stood down by us since they joined.
    static void Adopt(RPawnVillain body, string label, bool recruited, int baseHealth, bool? wasHostile = null)
    {
        Hunters.Forget(body);
        var guard = new Guard
        {
            Body = body,
            Label = label,
            Recruited = recruited,
            WasHostile = wasHostile ?? body.bHostile,
            Slot = _nextSlot++,
            BaseHealth = Math.Max(1, baseHealth),
            WakeAt = recruited ? 0f : World.Now + WakeAfter,
            SeenHealth = body.Health,
        };
        Team.Add(guard);

        // Stood down at once, so they never come for you while their brain starts.
        if (body.bHostile)
        {
            body.SetHostile(false, Fight.Reason);
        }

        guard.Benched = true;
    }

    static Outcome AttackAimed()
    {
        if (Team.Count == 0)
        {
            return Outcome.Failed("NO BODYGUARDS");
        }

        if (StoodDown)
        {
            return Outcome.Failed("THEY'RE STOOD DOWN");
        }

        if (Editor.Aimed(Things.Kind.Enemy) is not RPawnVillain thug || !Fight.Standing(thug))
        {
            return Outcome.Failed("AIM AT AN ENEMY");
        }

        if (Is(thug))
        {
            return Outcome.Failed("THAT'S A BODYGUARD");
        }

        var sent = 0;
        foreach (var guard in Team)
        {
            if (Fight.Standing(guard.Body))
            {
                GuardBrain.Order(guard, thug, World.Now);
                sent++;
            }
        }

        return sent > 0 ? Outcome.Done($"{sent} SENT") : Outcome.Failed("NONE STANDING");
    }

    static Outcome RegroupNow()
    {
        if (Teleport.Mover is not { } you)
        {
            return Outcome.NoPlayer;
        }

        var standing = Team.Where(guard => Fight.Standing(guard.Body)).ToList();
        if (standing.Count == 0)
        {
            return Outcome.Failed(Team.Count == 0 ? "NO BODYGUARDS" : "NONE STANDING");
        }

        var spots = Crowd.RingSpots(you, standing.Count);
        var brought = 0;
        for (var i = 0; i < standing.Count && i < spots.Count; i++)
        {
            var away = spots[i] - you.Location;
            if (GuardBrain.Place(standing[i], spots[i], MathF.Atan2(away.Y, away.X) * 180f / MathF.PI))
            {
                brought++;
            }
        }

        return brought > 0 ? Outcome.Done($"{brought} BROUGHT") : Outcome.Failed("NO ROOM AROUND YOU");
    }

    static Outcome HealAll()
    {
        if (Team.Count == 0)
        {
            return Outcome.Failed("NO BODYGUARDS");
        }

        var healed = 0;
        foreach (var guard in Team)
        {
            var body = guard.Body;
            if (!body.IsValid || body.bDeleteMe || body.bHidden)
            {
                continue;
            }

            if (!Crowd.Standing(body))
            {
                GetUp(guard);
                healed++;
            }
            else if (TopUp(guard))
            {
                healed++;
            }
        }

        return healed > 0 ? Outcome.Done($"{healed} HEALED") : Outcome.Unchanged("ALL FULL");
    }

    static Outcome DismissAll()
    {
        if (Team.Count == 0)
        {
            return Outcome.Unchanged("NO BODYGUARDS");
        }

        var count = 0;
        foreach (var guard in Team.ToArray())
        {
            if (!guard.Body.IsValid || guard.Body.bDeleteMe)
            {
                Drop(guard, false);
                continue;
            }

            count++;
            SendOff(guard);
        }

        Hunters.ReleaseAll();
        Log.Info($"Bodyguards: dismissed all {count}.");
        return Outcome.Done($"{count} DISMISSED");
    }

    // The spawner is about to take this one out of the world. The game keeps bodies to use
    // again, so they are handed back as the game made them.
    public static void Leaving(Actor body)
    {
        if (GuardOf(body) is { } guard)
        {
            Drop(guard, true);
        }
    }

    // Runs every tick, and returns at once with no bodyguards.
    public static void Tick()
    {
        if (!_resumed)
        {
            _resumed = true;
            Resume();
        }

        if (Team.Count == 0)
        {
            Hunters.ReleaseAll();
            return;
        }

        var now = World.Now;
        Prune(now);
        if (Team.Count == 0 || Teleport.Mover is not { } you)
        {
            return;
        }

        _you = you;
        _scanned = Fight.Due(ref _scanAt, now, ScanEvery);
        if (_scanned)
        {
            Scan(you);
        }

        // One bodyguard that fails doesn't stop the rest.
        foreach (var guard in Team)
        {
            Kernel.Run("spawner.bodyguards.guard", static stepped => Step(stepped), guard);
        }

        Hunters.Tick(Enemies, Team, you, _scanned, now);
        _you = null;
    }

    static void Step(Guard guard)
    {
        var body = guard.Body;
        var now = World.Now;
        if (!Crowd.Standing(body))
        {
            GuardBrain.Fell(guard);
            if (guard.DownSince < 0f || now < guard.DownSince)
            {
                guard.DownSince = now;
                if (!body.bHidden)
                {
                    Log.Info($"Bodyguards: {guard.Label} is down.");
                }
            }
            else if (Unbeatable.On && !body.bHidden && now - guard.DownSince >= GetUpAfter)
            {
                GetUp(guard);
            }

            return;
        }

        guard.DownSince = -1f;
        Mend(guard, now);
        if (body.IsIncapacitated())
        {
            Fight.StopShooting(guard);
            return;
        }

        if (Fight.Waiting(guard.WakeAt, now, 5f))
        {
            return;
        }

        GuardBrain.Run(guard, Enemies, Team, _you!, _scanned, now);
        Strikes.Watch(guard);
        Strikes.Combo(guard);
    }

    // Every standing enemy near you or near a bodyguard, a few times a second. Someone stood down
    // by the game or by you, like a passive spawn, is left alone.
    static void Scan(Pawn you)
    {
        Enemies.Clear();
        var range = (float)Range.Value * Metre;
        var near = range + ScanMargin;
        foreach (var villain in Game.FindObjects<RPawnVillain>())
        {
            if (!villain.IsValid || villain.IsClassDefaultObject || !villain.bHostile || !Crowd.Standing(villain) || villain.Base is Vehicle
                || GuardOf(villain) is not null)
            {
                continue;
            }

            var close = Vector3.DistanceSquared(villain.Location, you.Location) <= near * near;
            for (var i = 0; !close && i < Team.Count; i++)
            {
                close = Vector3.DistanceSquared(villain.Location, Team[i].Body.Location) <= near * near;
            }

            if (close)
            {
                Enemies.Add(villain);
            }
        }
    }

    static void Prune(float now)
    {
        for (var i = Team.Count - 1; i >= 0; i--)
        {
            var guard = Team[i];
            var body = guard.Body;
            if (!body.IsValid || body.bDeleteMe)
            {
                Drop(guard, false);
                Log.Info($"Bodyguards: {guard.Label} left the world. {Team.Count} left in the team.");
                continue;
            }

            // Back in the game's pool, where it comes out again as somebody else. So it goes
            // back as the game made it.
            if (Fight.Pooled(body))
            {
                Drop(guard, true);
                Log.Info($"Bodyguards: the game took {guard.Label} back. {Team.Count} left in the team.");
                continue;
            }

            if (!body.bHidden)
            {
                guard.HiddenSince = -1f;
            }
            else if (guard.HiddenSince < 0f || now < guard.HiddenSince)
            {
                guard.HiddenSince = now;
            }
            else if (now - guard.HiddenSince >= GoneAfter)
            {
                Drop(guard, guard.Recruited && guard.WasHostile);
                Log.Info($"Bodyguards: {guard.Label} was hidden by the game and left the team. {Team.Count} left.");
            }
        }
    }

    public static int FullHealth(RPawnVillain body) => Math.Max(Math.Max(body.HealthMax, body.MaxHealth), 1);

    static bool TopUp(Guard guard)
    {
        var body = guard.Body;
        var full = FullHealth(body);
        if (body.Health >= full)
        {
            return false;
        }

        body.Health = full;
        guard.SeenHealth = full;
        return true;
    }

    // Runs every tick for someone on their feet. UNBEATABLE keeps them full. BODYGUARDS RECOVER
    // gives health back once they have been left alone for a while.
    static void Mend(Guard guard, float now)
    {
        if (Unbeatable.On)
        {
            TopUp(guard);
            return;
        }

        var body = guard.Body;
        var health = body.Health;
        if (health < guard.SeenHealth)
        {
            guard.HurtAt = now;
        }

        guard.SeenHealth = health;
        if (!Recover.On || guard.Target is not null || Fight.Since(guard.HurtAt, now) < RecoverAfter || Fight.Since(guard.FoughtAt, now) < RecoverAfter
            || !Fight.Due(ref guard.RecoverAt, now, 1f))
        {
            return;
        }

        var full = FullHealth(body);
        if (health < full)
        {
            guard.SeenHealth = Math.Min(full, health + Math.Max(1, (int)MathF.Ceiling(full * RecoverShare)));
            body.Health = guard.SeenHealth;
        }
    }

    // The game's own wake-up for someone knocked out. It starts them over, so they are stood
    // down again on the next tick.
    static void GetUp(Guard guard)
    {
        var body = guard.Body;
        body.WakeFromDeadWithDefaults();
        body.Health = FullHealth(body);
        guard.SeenHealth = body.Health;
        guard.DownSince = -1f;
        guard.Benched = false;
        guard.Target = null;
        guard.WakeAt = World.Now + WakeAfter;
        if (body.bHostile)
        {
            body.SetHostile(false, Fight.Reason);
            guard.Benched = true;
        }

        Log.Info($"Bodyguards: {guard.Label} is back up.");
    }

    // On unload they are left stood down and what we changed is put back. A script reload picks
    // them up again from the list kept here.
    public static void Release()
    {
        var kept = Team.Where(guard => guard.Body.IsValid && !guard.Body.bDeleteMe && !Fight.Pooled(guard.Body)).ToArray();
        Survive.Set(Survive.BodyguardBodies, kept.Select(guard => (long)guard.Body.Ptr).ToArray());
        Survive.Set(Survive.BodyguardLabels, kept.Select(guard => guard.Label).ToArray());
        Survive.Set(Survive.BodyguardRecruited, kept.Select(guard => guard.Recruited).ToArray());
        Survive.Set(Survive.BodyguardHostile, kept.Select(guard => guard.WasHostile).ToArray());
        Survive.Set(Survive.BodyguardHealth, kept.Select(guard => guard.BaseHealth).ToArray());
        Survive.Set(Survive.BodyguardOrders, Orders.CurrentId);

        // Everyone is let go of, kept or not, which also takes their hold markers away.
        foreach (var guard in Team)
        {
            Kernel.Run("spawner.bodyguards.release", static held => GuardBrain.Release(held, false), guard);
        }

        Team.Clear();
        Hunters.ReleaseAll();
    }

    static void Resume()
    {
        var bodies = Survive.Get<long[]?>(Survive.BodyguardBodies, null);
        var labels = Survive.Get<string[]?>(Survive.BodyguardLabels, null);
        var recruited = Survive.Get<bool[]?>(Survive.BodyguardRecruited, null);
        var hostile = Survive.Get<bool[]?>(Survive.BodyguardHostile, null);
        var health = Survive.Get<int[]?>(Survive.BodyguardHealth, null);
        Survive.Set<long[]?>(Survive.BodyguardBodies, null);
        if (bodies is null || labels is null || recruited is null || hostile is null || health is null || bodies.Length == 0
            || labels.Length != bodies.Length || recruited.Length != bodies.Length || hostile.Length != bodies.Length || health.Length != bodies.Length)
        {
            return;
        }

        foreach (var villain in Game.FindObjects<RPawnVillain>())
        {
            if (!villain.IsValid || villain.bDeleteMe || villain.bHidden)
            {
                continue;
            }

            var at = Array.IndexOf(bodies, (long)villain.Ptr);
            if (at >= 0 && !Is(villain))
            {
                Adopt(villain, labels[at], recruited[at], health[at], hostile[at]);
            }
        }

        if (Team.Count > 0)
        {
            Orders.Load(Survive.Get<string?>(Survive.BodyguardOrders, null));
            Log.Info($"Bodyguards: kept {Team.Count} of {bodies.Length} through the reload.");
        }
    }
}
