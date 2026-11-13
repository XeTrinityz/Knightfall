#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SPAWNER > SPAWNED. Everything the spawner made, so it can be found, cleared up and taken away
// again. Nothing the game placed itself is ever touched.
static class Spawned
{
    const float Metre = Teleport.UnitsPerMetre;

    // Upkeep, like clearing the fallen, runs this often.
    const float CheckEvery = 0.5f;

    // GO TO IT steps beside a spawn this near. Further off it travels like any teleport, since
    // the ground there may not be loaded.
    const float Near = 100f * Metre;
    const float Beside = 300f;

    // A spawned person with its own reason for standing down, which the game reads back.
    public static readonly FName Calm = new("KnightfallCalm");

    // What SPAWN says when MOST AT ONCE leaves no room.
    public static readonly Outcome Full = Outcome.Failed("MOST AT ONCE REACHED");

    public sealed class Spawn
    {
        public required Actor Actor;
        public required string Label;

        // Counts up with every spawn, so each keeps its name in the list while others come and go.
        public int Number;
        public bool Passive;

        // A bodyguard, who follows you and so is never cleared for being far away.
        public bool Guard;
        public float DownSince = -1f;

        // What to start once the new body's brain has taken over, and when.
        public Action? Start;
        public float StartAt;

        // Start also waits for this, when set.
        public Func<bool>? Ready;
    }

    // A fresh body's brain starts on a short timer after it begins play.
    const float StartAfter = 0.5f;

    static readonly List<Spawn> Spawns = new();
    static float _checkedAt = -1f;
    static bool _listDue;
    static int _made;

    public static readonly Choice Show = new(
        "spawner.spawned.show", "SHOW",
        "Which of your spawns SPAWNED lists and DELETE ALL SHOWN takes away.",
        new[] { ("all", "EVERYTHING"), ("people", "PEOPLE"), ("vehicles", "VEHICLES"), ("objects", "OBJECTS"), ("weapons", "WEAPONS") }, saved: false)
    {
        Changed = static () => _listDue = true,
    };

    public static readonly Choice Pick = new(
        "spawner.spawned.pick", "SPAWNED",
        "What you've spawned that's still in the world, newest first. The number is the order they were spawned in.",
        new[] { ("", "NOTHING YET") }, saved: false)
    {
        Opening = List,
    };

    public static readonly Command GoTo = new(
        "spawner.spawned.go_to", "GO TO IT",
        "Puts you beside the spawn picked above.",
        GoToPicked)
    {
        Block = NonePicked,
    };

    public static readonly Command Delete = new(
        "spawner.spawned.delete", "DELETE IT",
        "Takes the spawn picked above out of the world.",
        DeletePicked)
    {
        Block = NonePicked,
    };

    public static readonly Command EditIt = new(
        "spawner.spawned.edit", "EDIT IT",
        "Makes the spawn picked above the editor's selection, ready to move on the EDITOR page.",
        () =>
        {
            if (Picked() is not { } spawn)
            {
                return Outcome.Failed("PICK ONE FIRST");
            }

            Editor.Select(spawn.Actor);
            return Outcome.Done("SELECTED");
        })
    {
        Block = NonePicked,
    };

    public static readonly Command DeleteLast = new(
        "spawner.spawned.delete_last", "DELETE LAST",
        "Takes your newest spawn out of the world, whatever SHOW says.",
        () => Spawns.Count > 0 ? Take(Spawns[^1]) : Outcome.Unchanged("NOTHING SPAWNED"))
    {
        Block = NoneSpawned,
    };

    public static readonly Command DeleteAll = new(
        "spawner.spawned.delete_all", "DELETE ALL SHOWN",
        "Takes every spawn SHOW lists out of the world. Nothing the game placed is touched.",
        TakeShown)
    {
        Block = NoneSpawned,
    };

    public static readonly Toggle ClearFallen = new(
        "spawner.spawned.clear_fallen", "CLEAR THE FALLEN",
        "Spawned people knocked out and vehicles destroyed are taken away AFTER this long, so they don't pile up.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Slider ClearAfter = new(
        "spawner.spawned.clear_after", "AFTER",
        "How long a fallen spawn stays before CLEAR THE FALLEN takes it away.",
        min: 5, max: 180, step: 5, value: 30, unit: " S")
    {
        Preference = true,
        Block = static () => ClearFallen.On ? null : "CLEAR THE FALLEN IS OFF",
    };

    public static readonly Slider ClearFar = new(
        "spawner.spawned.clear_far", "CLEAR FAR AWAY",
        "Spawns this far from you are taken away, so you can spawn and move on.",
        min: 0, max: 1000, step: 50, value: 300, unit: " M", zero: "OFF")
    {
        Preference = true,
    };

    public static readonly Slider Most = new(
        "spawner.spawned.most", "MOST AT ONCE",
        "The most spawns in the world at once. Lots of people and vehicles slow the game down, so SPAWN stops past this.",
        min: 5, max: 150, step: 5, value: 40)
    {
        Preference = true,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("YOUR SPAWNS", Show, Pick, GoTo, Delete, DeleteLast, DeleteAll),
        new("CLEAN UP", ClearFallen, ClearAfter, ClearFar, Most),
    };

    // How many more fit under MOST AT ONCE.
    public static int Room => Math.Max(0, (int)Most.Value - Alive().Count());

    // Whether a spawned person is still in the world. A row asks every frame it's on screen.
    public static bool AnyPerson
    {
        get
        {
            foreach (var spawn in Spawns)
            {
                if (spawn.Actor is RBMPawnAI && Alive(spawn))
                {
                    return true;
                }
            }

            return false;
        }
    }

    // Asked every frame their rows are on screen.
    static string? NoneSpawned() => Spawns.Count == 0 ? "NOTHING SPAWNED" : null;

    static string? NonePicked() => Pick.CurrentId.Length == 0 ? NoneSpawned() ?? "NONE OF THAT KIND" : null;

    public static Spawn Add(Actor actor, string label, bool passive, Action? start = null, bool guard = false)
    {
        var spawn = new Spawn
        {
            Actor = actor, Label = label, Number = ++_made, Passive = passive, Guard = guard, Start = start, StartAt = World.Now + StartAfter,
        };
        Spawns.Add(spawn);
        _listDue = true;

        // A bodyguard has its own marker, so the editor doesn't select it.
        if (!guard)
        {
            Editor.NewSpawn(actor);
        }

        return spawn;
    }

    // Runs every tick. Passive people are kept calm, since a hit can make them hostile again.
    public static void Tick()
    {
        // The list on screen follows SHOW, and new and lost spawns, while the menu is open.
        if (_listDue && Menu.Showing)
        {
            List();
        }

        if (Spawns.Count == 0)
        {
            return;
        }

        // Every tick, so a new spawn gets going as soon as it's ready.
        for (var i = 0; i < Spawns.Count; i++)
        {
            var spawn = Spawns[i];
            if (spawn.Start is { } start && World.Now >= spawn.StartAt && Alive(spawn) && (spawn.Ready?.Invoke() ?? true))
            {
                spawn.Start = null;
                Kernel.Run("spawner.start", start);
            }
        }

        if (MathF.Abs(World.Now - _checkedAt) < CheckEvery)
        {
            return;
        }

        _checkedAt = World.Now;
        if (Spawns.RemoveAll(spawn => !Alive(spawn)) > 0)
        {
            _listDue = true;
        }

        var you = Teleport.Mover?.Location;
        var far = (float)ClearFar.Value * Metre;
        foreach (var spawn in Spawns.ToArray())
        {
            if (Fallen(spawn))
            {
                if (spawn.DownSince < 0f)
                {
                    spawn.DownSince = World.Now;
                }
                else if (ClearFallen.On && World.Now - spawn.DownSince >= ClearAfter.Value)
                {
                    Take(spawn);
                }

                continue;
            }

            spawn.DownSince = -1f;
            if (spawn.Passive && spawn.Actor is RPawnVillain thug && thug.IsHostile())
            {
                thug.SetHostile(false, Calm);
            }

            if (!spawn.Guard && you is { } here && far > 0f && Vector3.DistanceSquared(spawn.Actor.Location, here) > far * far)
            {
                Take(spawn);
            }
        }
    }

    // A script reload keeps what was spawned in the world and only forgets the list.
    public static void Forget() => Spawns.Clear();

    static IEnumerable<Spawn> Alive() => Spawns.Where(Alive);

    static bool Alive(Spawn spawn) => spawn.Actor.IsValid && !spawn.Actor.bDeleteMe;

    static bool Fallen(Spawn spawn) => spawn.Actor switch
    {
        RPawnVillain thug => thug.Health <= 0 || thug.bGoodAsDead,
        Pawn body => body.Health <= 0 || body.HasDied,
        _ => false,
    };

    // Which of SHOW's kinds a spawn is, by that option's id.
    static string KindOf(Actor actor) => actor switch
    {
        Vehicle => "vehicles",
        Pawn => "people",
        RBMCombatThrownObject => "weapons",
        _ => "objects",
    };

    static bool Shown(Spawn spawn) => Show.Index == 0 || KindOf(spawn.Actor) == Show.CurrentId;

    static void List()
    {
        _listDue = false;
        var shown = Alive().Where(Shown).Reverse().ToList();
        if (shown.Count == 0)
        {
            Pick.SetOptions(new[] { ("", Spawns.Count == 0 ? "NOTHING YET" : "NONE OF THAT KIND") }, 0);
            return;
        }

        var picked = Pick.CurrentId;
        var options = shown.Select(spawn => (spawn.Actor.Ptr.ToString(), $"{spawn.Label} {spawn.Number}")).ToArray();
        Pick.SetOptions(options, Math.Max(0, Array.FindIndex(options, option => option.Item1 == picked)));
    }

    static Spawn? Picked() => Alive().FirstOrDefault(spawn => spawn.Actor.Ptr.ToString() == Pick.CurrentId);

    // The person picked in the list. When the pick isn't a person, the newest one still standing.
    public static RBMPawnAI? Person() =>
        Picked()?.Actor as RBMPawnAI ?? Alive().Select(spawn => spawn.Actor).OfType<RBMPawnAI>().LastOrDefault(Crowd.Standing);

    static Outcome GoToPicked()
    {
        if (Picked() is not { } spawn)
        {
            return Outcome.Failed("PICK ONE FIRST");
        }

        if (Teleport.Mover is not { } mover)
        {
            return Outcome.NoPlayer;
        }

        var beside = spawn.Actor.Location + Teleport.Flat(spawn.Actor.Rotation.ToDirection()) * Beside;
        return Vector3.DistanceSquared(beside, mover.Location) > Near * Near
            ? Teleport.Start(GoTo, beside, true)
            : Teleport.Hop(mover, beside, new Rotator(0f, spawn.Actor.Rotation.Yaw + 180f, 0f), false);
    }

    static Outcome DeletePicked()
    {
        if (Picked() is not { } spawn)
        {
            return Outcome.Failed("PICK ONE FIRST");
        }

        var outcome = Take(spawn);
        List();
        return outcome;
    }

    static Outcome TakeShown()
    {
        var count = 0;
        foreach (var spawn in Spawns.ToArray())
        {
            if (Alive(spawn) && Shown(spawn))
            {
                Take(spawn);
                count++;
            }
        }

        List();
        return count > 0 ? Outcome.Done($"{count} DELETED") : Outcome.Unchanged(NoneSpawned() ?? "NONE OF THAT KIND");
    }

    // Takes a spawned thing out of the world, listed or not. A script reload forgets the list
    // but leaves the spawns, and a bodyguard kept through one is still ours to take.
    public static Outcome Take(Actor actor) =>
        Take(Spawns.Find(spawn => ReferenceEquals(spawn.Actor, actor)) ?? new Spawn { Actor = actor, Label = "" });

    // People go back the way the game takes them, which takes their brain and weapon too.
    static Outcome Take(Spawn spawn)
    {
        Spawns.Remove(spawn);
        _listDue = true;
        if (!Alive(spawn))
        {
            return Outcome.Unchanged("ALREADY GONE");
        }

        Bodyguards.Leaving(spawn.Actor);
        if (spawn.Actor is RBMPawnAI body)
        {
            if (body.CurrWeapon is { IsValid: true })
            {
                body.DisappearDisarm();
            }

            var brain = body.Controller;
            body.DestroyOrReturnToPool(true, false);
            if (body.IsValid && !body.bDeleteMe)
            {
                body.Destroy();
                if (brain is { IsValid: true, bDeleteMe: false })
                {
                    brain.Destroy();
                }
            }
        }
        else
        {
            // A hacked vehicle leaves the combat manager's friendly list first, as the game's own
            // hacked drones do before they go.
            if (spawn.Actor is RVehicleNPC { IsHackedFriendly: true } hacked)
            {
                World.Info?.VehicleCombatManager?.SetVehicleAsFriendly(hacked, false);
            }

            spawn.Actor.Destroy();
        }

        return Outcome.Done("DELETED");
    }
}
