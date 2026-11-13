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

    // How handing someone a weapon went.
    public enum Handed
    {
        Given,
        Already,
        Refused,
        NoModel,
    }

    static List<Kind> _kinds = new();
    static RGameInfo? _world;
    static bool _shown;

    public static readonly Choice Weapon = new(
        "spawner.weapons.weapon", "WEAPON",
        "Every weapon the game has loaded here with its model: guns, blades, shields and throwable crates. Select drops one on the ground.",
        new[] { ("", "OPEN THE MENU IN GAME") }, saved: false)
    {
        Pressed = static () => Drop!.Press(),
        PressName = "DROP",
    };

    public static readonly Command RefreshList = new(
        "spawner.weapons.refresh", "REFRESH LIST",
        "Reads again which weapons are loaded here, without leaving the tab. Coming back to the tab reads it too.",
        () =>
        {
            if (World.Info is null)
            {
                return Outcome.NoPlayer;
            }

            Refresh();
            return _kinds.Count > 0 ? Outcome.Done($"{_kinds.Count} LISTED") : Outcome.Unchanged("NONE LOADED HERE");
        });

    public static readonly Command Drop = new(
        "spawner.weapons.drop", "DROP ON THE GROUND",
        "Lays the weapon above on the ground, HOW MANY times, the way PLACE says. Thugs nearby pick them up and use them.",
        DropPicked);

    public static readonly Command GiveAimed = new(
        "spawner.weapons.give_aimed", "GIVE TO WHO I AIM AT",
        "Puts the weapon above in the hands of the person in the middle of your screen, spawned or not.",
        () => Editor.Aimed(null, static kind => kind is Things.Kind.Enemy or Things.Kind.Friendly) is RBMPawnAI body ? GiveTo(body) : Outcome.Failed("AIM AT A PERSON"));

    public static readonly Command Give = new(
        "spawner.weapons.give", "GIVE TO PICKED SPAWN",
        "Puts the weapon above in the hands of the person picked in SPAWNED. When the pick there isn't a person, your newest spawned person gets it.",
        () => Spawned.Person() is { } body ? GiveTo(body) : Outcome.Failed("NO SPAWNED PEOPLE"))
    {
        Block = static () => Spawned.AnyPerson ? null : "NO SPAWNED PEOPLE",
    };

    public static readonly Command GiveSelected = new(
        "spawner.weapons.give_selected", "GIVE TO SELECTED",
        "Puts the weapon above in the hands of the person the editor has selected.",
        () => Editor.Selected is RBMPawnAI body ? GiveTo(body) : Outcome.Failed("SELECT A PERSON"))
    {
        Block = static () => Editor.Selected is RBMPawnAI ? null : "SELECT A PERSON",
    };

    public static readonly Command GiveGuards = new(
        "spawner.weapons.give_bodyguards", "GIVE TO BODYGUARDS",
        "Puts the weapon above in the hands of every bodyguard still standing.",
        ArmGuards)
    {
        Block = static () => Bodyguards.All.Count == 0 ? "NO BODYGUARDS" : null,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("WHICH", Weapon, RefreshList),
        new("USE", Drop, GiveAimed, Give, GiveSelected, GiveGuards),
        Placing.Section,
    };

    // Runs every tick. The list is read each time the tab comes up, since what's loaded changes
    // as you move.
    public static void Tick()
    {
        var shown = Menu.Shows(Weapon);
        if (shown && !_shown)
        {
            Refresh();
        }

        _shown = shown;
    }

    // Every loaded class that's a real weapon a person can hold. One whose model isn't loaded
    // here would be invisible in their hands, so it's left out.
    public static List<Kind> Find()
    {
        var root = RBMWeapon.StaticClass();
        var found = new SortedDictionary<string, Kind>(StringComparer.Ordinal);
        foreach (var type in Game.FindObjects<Class>())
        {
            var name = type.IsValid ? type.Name.ToString() ?? "" : "";
            if (!name.StartsWith("RBMWeapon", StringComparison.Ordinal) || name.EndsWith("Base", StringComparison.Ordinal)
                || Abstract.Contains(name) || Refused.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))
                || !People.Is(type, root) || type.DefaultObject is not RBMWeapon made || !HasModel(made.Mesh))
            {
                continue;
            }

            var label = People.Pretty(name["RBMWeapon".Length..]);
            found.TryAdd(label, new Kind(name, label, type));
        }

        return found.Values.ToList();
    }

    // Swaps what they hold for this, the way the game arms people. One that turns out to have no
    // model is taken back, since an invisible weapon is no use to anyone.
    public static Handed Hand(RBMPawnAI body, Class weapon)
    {
        if (body.CurrWeapon is { IsValid: true } held)
        {
            if (ReferenceEquals(held.Class, weapon))
            {
                return Handed.Already;
            }

            body.DisappearDisarm();
        }

        body.SpawnWeapon(weapon, true, false);
        if (body.CurrWeapon is not { IsValid: true } got || !ReferenceEquals(got.Class, weapon))
        {
            return Handed.Refused;
        }

        if (!HasModel(got.Mesh))
        {
            body.DisappearDisarm();
            return Handed.NoModel;
        }

        return Handed.Given;
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

    // The weapon picked in WEAPON, or why there's none to use. A hotkey can ask before the tab
    // has read the list.
    static Outcome? NoWeapon(out Kind kind)
    {
        kind = null!;
        if (!ReferenceEquals(_world, World.Info) || _kinds.Count == 0)
        {
            Refresh();
        }

        if (_kinds.Find(listed => listed.Key == Weapon.CurrentId) is not { } picked)
        {
            return Outcome.Failed("PICK A WEAPON FIRST");
        }

        if (!picked.Class.IsValid)
        {
            Refresh();
            return Outcome.Failed("GONE, PICK AGAIN");
        }

        kind = picked;
        return null;
    }

    static Outcome DropPicked()
    {
        if (NoWeapon(out var kind) is { } none)
        {
            return none;
        }

        if (Placing.Plan(0f, out _, out var spots) is { } refused)
        {
            return refused;
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

    static Outcome GiveTo(RBMPawnAI body)
    {
        if (NoWeapon(out var kind) is { } none)
        {
            return none;
        }

        if (!Crowd.Standing(body))
        {
            return Outcome.Failed("THEY'RE DOWN");
        }

        return Hand(body, kind.Class) switch
        {
            Handed.Given => Outcome.Done("GIVEN"),
            Handed.Already => Outcome.Unchanged("ALREADY HOLDING IT"),
            Handed.NoModel => Outcome.Failed("NOT LOADED HERE"),
            _ => Outcome.Failed("THEY CAN'T HOLD IT"),
        };
    }

    static Outcome ArmGuards()
    {
        if (Bodyguards.All.Count == 0)
        {
            return Outcome.Failed("NO BODYGUARDS");
        }

        if (NoWeapon(out var kind) is { } none)
        {
            return none;
        }

        var standing = 0;
        var armed = 0;
        foreach (var guard in Bodyguards.All)
        {
            if (!Fight.Standing(guard.Body))
            {
                continue;
            }

            standing++;
            if (Hand(guard.Body, kind.Class) is Handed.Given or Handed.Already)
            {
                armed++;
            }
        }

        return armed > 0 ? Outcome.Done($"{armed} ARMED") : Outcome.Failed(standing == 0 ? "NONE STANDING" : "THEY CAN'T HOLD IT");
    }

    static bool HasModel(SkeletalMeshComponent? part) => part is { IsValid: true } && part.SkeletalMesh is { IsValid: true };
}
