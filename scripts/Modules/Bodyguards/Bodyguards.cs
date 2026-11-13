#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SPAWNER > BODYGUARDS. People who follow you everywhere and fight for you. They can be hired
// from anyone the area has loaded, or recruited from the enemies in front of you. GuardBrain runs
// each one, Strikes lands their hits and Hunters sends enemies after them.
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

    // A body the game has hidden this long has gone back to its pool.
    const float GoneAfter = 3f;

    const float OrderSeconds = 30f;

    public const int Protect = 0;
    public const int Hold = 1;
    public const int StandDown = 2;

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
        "Turns the enemy in the middle of your screen into a bodyguard. DISMISS makes them an enemy again.",
        RecruitAimed);

    public static readonly Choice Orders = new(
        "spawner.bodyguards.orders", "ORDERS",
        "PROTECT ME has them follow you and fight whoever comes near. HOLD HERE keeps them in place. STAND DOWN has them follow you and fight nobody.",
        new[] { ("protect", "PROTECT ME"), ("hold", "HOLD HERE"), ("stand_down", "STAND DOWN") }, saved: false);

    public static readonly Command Attack = new(
        "spawner.bodyguards.attack", "ATTACK WHO I AIM AT",
        "Sends every bodyguard after the enemy in the middle of your screen until that one is down.",
        AttackAimed)
    {
        Block = NoTeam,
    };

    public static readonly Command Regroup = new(
        "spawner.bodyguards.regroup", "BRING THEM TO ME",
        "Puts every bodyguard still standing in a ring around you, wherever they are.",
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
        "Lets the bodyguard in the middle of your screen go. A hired one leaves the world. A recruited one goes back to being an enemy.",
        DismissOne)
    {
        Block = NoTeam,
    };

    public static readonly Command Dismiss = new(
        "spawner.bodyguards.dismiss", "DISMISS BODYGUARDS",
        "Lets every bodyguard go. Hired ones leave the world. Recruited ones go back to being enemies.",
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
        "A bodyguard left far behind, because you glided, grappled or drove off, appears behind you once you stop, where you can't see it happen.",
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
        new("BEHAVIOUR", Range, Gap, EnemiesFight, CatchUp, Unbeatable, HitsToKnockOut),
        GuardMarks.Section,
    };

    public static IReadOnlyList<Guard> All => Team;

    // Asked every frame the rows that need a team are on screen.
    static string? NoTeam() => Team.Count == 0 ? "NO BODYGUARDS" : null;

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

        var room = Most - Team.Count;
        if (room <= 0)
        {
            return Outcome.Failed($"AT MOST {Most}");
        }

        return People.Hire(Who.CurrentId, Math.Min((int)Count.Value, room), (float)Health.Value, static (body, label, health) => Adopt(body, label, false, health));
    }

    // Hires people for another feature, like a villain's goons, and hands back the bodies made.
    public static List<RPawnVillain> Enlist(string key, int count, float health)
    {
        var made = new List<RPawnVillain>();
        var room = Math.Min(count, Most - Team.Count);
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

    // Room left in the team.
    public static int Room => Math.Max(0, Most - Team.Count);

    // Takes on someone another feature made, like a villain's squad. False when the team is full.
    public static bool Take(RPawnVillain body, string label, int baseHealth)
    {
        if (Team.Count >= Most || GuardOf(body) is not null)
        {
            return false;
        }

        Adopt(body, label, false, baseHealth);
        return true;
    }

    // Lets one bodyguard go, as DISMISS BODYGUARDS does for everyone.
    public static void Let(RPawnVillain body)
    {
        if (GuardOf(body) is not { } guard)
        {
            return;
        }

        if (guard.Recruited)
        {
            Team.Remove(guard);
            GuardBrain.Release(guard, true);
        }
        else
        {
            Spawned.Take(body);
            Team.Remove(guard);
        }
    }

    static Outcome RecruitAimed()
    {
        if (World.Player is null)
        {
            return Outcome.NoPlayer;
        }

        if (Team.Count >= Most)
        {
            return Outcome.Failed($"AT MOST {Most}");
        }

        if (Editor.Aimed(Things.Kind.Enemy) is not RPawnVillain thug)
        {
            return Outcome.Failed("AIM AT AN ENEMY");
        }

        if (GuardOf(thug) is not null)
        {
            return Outcome.Unchanged("ALREADY YOURS");
        }

        if (!Fight.Standing(thug) || thug.Base is Vehicle)
        {
            return Outcome.Failed("THEY CAN'T JOIN NOW");
        }

        Adopt(thug, Things.NameOf(thug), true, Math.Max(thug.HealthMax, thug.Health));
        return Outcome.Done("RECRUITED");
    }

    static Outcome DismissOne()
    {
        if (Team.Count == 0)
        {
            return Outcome.Unchanged("NO BODYGUARDS");
        }

        if (Editor.Aimed(Things.Kind.Enemy) is not RPawnVillain thug || GuardOf(thug) is null)
        {
            return Outcome.Failed("AIM AT A BODYGUARD");
        }

        Let(thug);
        return Outcome.Done("DISMISSED");
    }

    static void Adopt(RPawnVillain body, string label, bool recruited, int baseHealth)
    {
        Hunters.Forget(body);
        var guard = new Guard
        {
            Body = body,
            Label = label,
            Recruited = recruited,
            Slot = _nextSlot++,
            BaseHealth = Math.Max(1, baseHealth),
            WakeAt = recruited ? 0f : World.Now + WakeAfter,
        };
        Team.Add(guard);

        // Stood down at once, so they never come for you while their brain starts.
        if (body.IsHostile())
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

        if (Orders.Index == StandDown)
        {
            return Outcome.Failed("THEY'RE STOOD DOWN");
        }

        if (Editor.Aimed(Things.Kind.Enemy) is not RPawnVillain thug || !Fight.Standing(thug))
        {
            return Outcome.Failed("AIM AT AN ENEMY");
        }

        if (GuardOf(thug) is not null)
        {
            return Outcome.Failed("THAT'S A BODYGUARD");
        }

        var sent = 0;
        foreach (var guard in Team)
        {
            if (Fight.Standing(guard.Body))
            {
                guard.Ordered = thug;
                guard.OrderedUntil = World.Now + OrderSeconds;
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
            if (GuardBrain.Place(standing[i].Body, spots[i], MathF.Atan2(away.Y, away.X) * 180f / MathF.PI))
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
            else if (TopUp(body))
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
                continue;
            }

            count++;
            if (guard.Recruited)
            {
                GuardBrain.Release(guard, true);
            }
            else
            {
                // The spawner tells us before it takes each one, which hands them back.
                Spawned.Take(guard.Body);
            }
        }

        Team.Clear();
        Hunters.ReleaseAll();
        return Outcome.Done($"{count} DISMISSED");
    }

    // The spawner is about to take this one out of the world. The game keeps bodies to use
    // again, so they are handed back as the game made them.
    public static void Leaving(Actor body)
    {
        if (GuardOf(body) is { } guard)
        {
            Team.Remove(guard);
            GuardBrain.Release(guard, true);
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
            // The gun is stopped, or it keeps firing at its last target from the floor.
            Fight.StopShooting(guard);
            guard.Target = null;
            if (guard.DownSince < 0f || now < guard.DownSince)
            {
                guard.DownSince = now;
            }
            else if (Unbeatable.On && !body.bHidden && now - guard.DownSince >= GetUpAfter)
            {
                GetUp(guard);
            }

            return;
        }

        guard.DownSince = -1f;
        if (Unbeatable.On)
        {
            TopUp(body);
        }

        if (body.IsIncapacitated())
        {
            Fight.StopShooting(guard);
            return;
        }

        if (now < guard.WakeAt && guard.WakeAt - now < 5f)
        {
            return;
        }

        GuardBrain.Run(guard, Enemies, Team, _you!, _scanned, now);
        Strikes.Watch(guard);
        Strikes.Combo(guard);
    }

    // Every standing enemy near you or near a bodyguard, a few times a second.
    static void Scan(Pawn you)
    {
        Enemies.Clear();
        var range = (float)Range.Value * Metre;
        var near = range + ScanMargin;
        foreach (var villain in Game.FindObjects<RPawnVillain>())
        {
            if (!villain.IsValid || villain.IsClassDefaultObject || !Crowd.Standing(villain) || villain.Base is Vehicle || GuardOf(villain) is not null)
            {
                continue;
            }

            var close = Vector3.DistanceSquared(villain.Location, you.Location) <= near * near;
            for (var i = 0; !close && i < Team.Count; i++)
            {
                close = Vector3.DistanceSquared(villain.Location, Team[i].Body.Location) <= near * near;
            }

            // Someone stood down by the game or by you, like a passive spawn, is left alone.
            if (close && villain.IsHostile())
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
                Team.RemoveAt(i);
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
                // Back in the game's pool, where it may come out again as somebody else.
                Team.RemoveAt(i);
                GuardBrain.Release(guard, guard.Recruited);
            }
        }
    }

    static int Full(RPawnVillain body) => Math.Max(Math.Max(body.HealthMax, body.MaxHealth), 1);

    static bool TopUp(RPawnVillain body)
    {
        var full = Full(body);
        if (body.Health >= full)
        {
            return false;
        }

        body.Health = full;
        return true;
    }

    // The game's own wake-up for someone knocked out. It starts them over, so they are stood
    // down again on the next tick.
    static void GetUp(Guard guard)
    {
        var body = guard.Body;
        body.WakeFromDeadWithDefaults();
        body.Health = Full(body);
        guard.DownSince = -1f;
        guard.Benched = false;
        guard.Target = null;
        guard.WakeAt = World.Now + WakeAfter;
        if (body.IsHostile())
        {
            body.SetHostile(false, Fight.Reason);
            guard.Benched = true;
        }
    }

    // On unload they are left stood down and what we changed is put back. A script reload picks
    // them up again from the list kept here.
    public static void Release()
    {
        var kept = Team.Where(guard => guard.Body.IsValid && !guard.Body.bDeleteMe).ToArray();
        Survive.Set(Survive.BodyguardBodies, kept.Select(guard => (long)guard.Body.Ptr).ToArray());
        Survive.Set(Survive.BodyguardLabels, kept.Select(guard => guard.Label).ToArray());
        Survive.Set(Survive.BodyguardRecruited, kept.Select(guard => guard.Recruited).ToArray());
        Survive.Set(Survive.BodyguardHealth, kept.Select(guard => guard.BaseHealth).ToArray());

        foreach (var guard in kept)
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
        var health = Survive.Get<int[]?>(Survive.BodyguardHealth, null);
        Survive.Set<long[]?>(Survive.BodyguardBodies, null);
        if (bodies is null || labels is null || recruited is null || health is null || bodies.Length == 0
            || labels.Length != bodies.Length || recruited.Length != bodies.Length || health.Length != bodies.Length)
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
            if (at >= 0 && GuardOf(villain) is null)
            {
                Adopt(villain, labels[at], recruited[at], health[at]);
            }
        }
    }
}
