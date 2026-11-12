#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SPAWNER > WEAPONS, and the weapon list CHARACTERS hands out too. A weapon on the floor is what a
// disarm leaves behind, so thugs pick it up like any other.
static class Arms
{
    const float Lift = 40f;

    static readonly string[] Abstract = { "RBMWeapon", "RBMWeaponMelee", "RBMWeaponRanged", "RBMWeaponHeavyObject" };

    // Batman's own pieces, tutorial copies and ones only a scene can use.
    static readonly string[] Refused = { "Batman", "ChokePoint", "SmashedSegment", "Tutorial", "UnPickupable", "Jokerfied" };

    public sealed record Kind(string Key, string Label, Class Class);

    static List<Kind> _kinds = new();
    static RGameInfo? _world;

    public static readonly Choice Weapon = new(
        "spawner.weapons.weapon", "WEAPON",
        "Every weapon the game has loaded here: guns, blades, shields and throwable crates. One whose model isn't loaded here is refused.",
        new[] { ("", "OPEN THE MENU IN GAME") }, saved: false)
    {
        Opening = () =>
        {
            if (!ReferenceEquals(_world, World.Info) || _kinds.Count == 0)
            {
                Refresh();
            }
        },
    };

    public static readonly Command RefreshList = new(
        "spawner.weapons.refresh", "REFRESH LIST",
        "Reads again which weapons are loaded here.",
        () =>
        {
            Refresh();
            return Outcome.Done($"{_kinds.Count} LISTED");
        });

    public static readonly Command Drop = new(
        "spawner.weapons.drop", "DROP ON THE GROUND",
        "Lays the weapon above on the ground, HOW MANY times, the way PLACE says. Thugs nearby pick them up and use them.",
        DropPicked);

    public static readonly Command Give = new(
        "spawner.weapons.give", "GIVE TO PICKED SPAWN",
        "Puts the weapon above in the hands of the person picked in SPAWNED, or your newest spawned person when nothing is picked there.",
        GivePicked);

    public static readonly Command GiveSelected = new(
        "spawner.weapons.give_selected", "GIVE TO SELECTED",
        "Puts the weapon above in the hands of the person the editor has selected.",
        () => Editor.Selected is RBMPawnAI body ? GiveTo(body) : Outcome.Failed("SELECT A PERSON"));

    public static readonly Catalog.Section[] Sections =
    {
        new("WHICH", Weapon, RefreshList),
        new("USE", Drop, Give, GiveSelected),
        Placing.Section,
    };

    // Every loaded class that's a real weapon a person can hold.
    public static List<Kind> Find()
    {
        var root = RBMWeapon.StaticClass();
        var found = new SortedDictionary<string, Kind>(StringComparer.Ordinal);
        foreach (var type in Game.FindObjects<Class>())
        {
            var name = type.IsValid ? type.Name.ToString() ?? "" : "";
            if (!name.StartsWith("RBMWeapon", StringComparison.Ordinal) || name.EndsWith("Base", StringComparison.Ordinal)
                || Abstract.Contains(name) || Refused.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))
                || !People.Is(type, root))
            {
                continue;
            }

            var label = People.Pretty(name["RBMWeapon".Length..]);
            found.TryAdd(label, new Kind(name, label, type));
        }

        return found.Values.ToList();
    }

    // Swaps what they hold for this, the way the game arms people. False when they already hold one.
    public static bool Hand(RBMPawnAI body, Class weapon)
    {
        if (body.CurrWeapon is { IsValid: true } held)
        {
            if (ReferenceEquals(held.Class, weapon))
            {
                return false;
            }

            body.DisappearDisarm();
        }

        body.SpawnWeapon(weapon, true, false);
        return true;
    }

    static void Refresh()
    {
        if (World.Info is null)
        {
            return;
        }

        _world = World.Info;
        _kinds = Find();
        if (_kinds.Count == 0)
        {
            Weapon.SetOptions(new[] { ("", "NONE LOADED HERE") }, 0);
            return;
        }

        var picked = Weapon.CurrentId;
        var options = _kinds.Select(kind => (kind.Key, kind.Label)).ToArray();
        Weapon.SetOptions(options, Math.Max(0, Array.FindIndex(options, option => option.Key == picked)));
    }

    static Kind? Picked() => _kinds.Find(kind => kind.Key == Weapon.CurrentId);

    static Outcome DropPicked()
    {
        if (Teleport.Mover is not { } mover || World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        if (controller.bCinematicMode)
        {
            return Outcome.Failed("NOT IN A SCENE");
        }

        if (Picked() is not { } kind)
        {
            return Outcome.Failed("PICK A WEAPON FIRST");
        }

        if (!kind.Class.IsValid)
        {
            Refresh();
            return Outcome.Failed("GONE, PICK AGAIN");
        }

        var room = Spawned.Room;
        if (room == 0)
        {
            return Outcome.Failed("AT MOST AT ONCE");
        }

        var spots = Placing.Spots(mover, Math.Min((int)Placing.Count.Value, room));
        if (spots.Count == 0)
        {
            return Outcome.Failed("NO ROOM THERE");
        }

        var made = 0;
        var unloaded = false;
        foreach (var spot in spots)
        {
            if (Game.SpawnActor(kind.Class, default, spot.Floor + new Vector3(0f, 0f, Lift), spot.Facing) is not RBMWeapon weapon)
            {
                continue;
            }

            RBMCombatThrownObject? dropped = null;
            try
            {
                if (HasModel(weapon.Mesh))
                {
                    dropped = weapon.CreateDroppedWeapon(true, Vector3.Zero);
                }
            }
            finally
            {
                // The held form goes back the way a disarm sends it.
                if (weapon.IsValid && !weapon.bDeleteMe)
                {
                    weapon.DestroyOrReturnToPool();
                }
            }

            if (dropped is not { IsValid: true, bDeleteMe: false })
            {
                unloaded = true;
                continue;
            }

            if (!HasModel(dropped.SkeletalMeshComponent))
            {
                dropped.Destroy();
                unloaded = true;
                continue;
            }

            // The game tidies dropped weapons away off screen. These stay until someone takes them.
            dropped.bDestroyWhenOffscreen = false;
            dropped.bCanBeDestroyedWhenOffScreenAndNotInCombat = false;
            Spawned.Add(dropped, kind.Label, passive: false);
            made++;
        }

        if (made > 0)
        {
            return Outcome.Done($"{made} DROPPED");
        }

        return Outcome.Failed(unloaded ? "NOT LOADED HERE" : "THE GAME REFUSED");
    }

    static Outcome GivePicked() => Spawned.Person() is { } body ? GiveTo(body) : Outcome.Failed("PICK A PERSON IN SPAWNED");

    static Outcome GiveTo(RBMPawnAI body)
    {
        if (Picked() is not { } kind)
        {
            return Outcome.Failed("PICK A WEAPON FIRST");
        }

        if (!kind.Class.IsValid)
        {
            Refresh();
            return Outcome.Failed("GONE, PICK AGAIN");
        }

        if (body.Health <= 0 || body is RPawnVillain { bGoodAsDead: true })
        {
            return Outcome.Failed("THEY'RE DOWN");
        }

        if (!Hand(body, kind.Class))
        {
            return Outcome.Unchanged("ALREADY HOLDING IT");
        }

        if (body.CurrWeapon is not { IsValid: true } got || !ReferenceEquals(got.Class, kind.Class))
        {
            return Outcome.Failed("THEY CAN'T HOLD IT");
        }

        // An invisible weapon is no use to anyone, so it's taken back.
        if (!HasModel(got.Mesh))
        {
            body.DisappearDisarm();
            return Outcome.Failed("NOT LOADED HERE");
        }

        return Outcome.Done("GIVEN");
    }

    static bool HasModel(SkeletalMeshComponent? part) => part is { IsValid: true } && part.SkeletalMesh is { IsValid: true };
}
