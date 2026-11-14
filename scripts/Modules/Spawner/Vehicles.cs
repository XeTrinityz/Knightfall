#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SPAWNER > VEHICLES. Any vehicle template the game has loaded, built the way the game builds
// its own chase car: spawned from the template, then given something to do. What's loaded changes
// as you move, so the list is read each time the tab comes up.
static class Vehicles
{
    // Vehicles need more room than people, both from you and from each other.
    const float Room = 800f;

    const float GroundLift = 250f;
    const float AirLift = 1000f;

    // The game raises this on its chase car so it keeps driving far from you.
    const float Anywhere = 1e17f;

    // A hack with its self-destruct timer cleared lasts until deleted. The long time is only a
    // backstop in case the clear doesn't take.
    const float Forever = 1e7f;

    // How long after the hack the vehicle's AI is checked.
    const float CheckAfter = 1f;

    static readonly FName OwnStart = new("SwitchToAssignedBehaviour");
    static readonly FName SelfDestruct = new("DroneHackedSuicide");

    // Abstract templates, mission vehicles and the Batmobiles.
    static readonly string[] Refused = { "_BASE", "Boss.", "Static.", "Interrogation", "GordonChase", "Batmobile" };

    sealed record Recipe(string Key, string Label, string Group, RVehicleNPC Template);

    static List<Recipe> _recipes = new();
    static RGameInfo? _world;
    static string _listedGroup = "";
    static bool _shown;

    public static readonly Choice Group = new(
        "spawner.vehicles.group", "GROUP",
        "Which kind of vehicle VEHICLE lists.",
        new[] { ("", "EVERYTHING") }, saved: false);

    public static readonly Choice Vehicle = new(
        "spawner.vehicles.vehicle", "VEHICLE",
        "Every vehicle the game has loaded. Militia tanks and drones are loaded across the city, cars depend on where you are. Select spawns it.",
        new[] { ("", "OPEN THE MENU IN GAME") }, saved: false)
    {
        Pressed = static () => Spawn!.Press(),
        PressName = "SPAWN",
    };

    public static readonly Command RefreshList = new(
        "spawner.vehicles.refresh", "REFRESH LIST",
        "Reads again which vehicles can be spawned here, without leaving the tab. Coming back to the tab reads it too.",
        () =>
        {
            if (World.Info is null)
            {
                return Outcome.NoPlayer;
            }

            Refresh();
            return _recipes.Count > 0 ? Outcome.Done($"{_recipes.Count} LISTED") : Outcome.Unchanged("NONE LOADED HERE");
        });

    public static readonly Choice Side = new(
        "spawner.vehicles.side", "SIDE",
        "HOSTILE vehicles are the militia's. ON YOUR SIDE hacks them like the Batmobile's drone hacker, with no self-destruct, so they stay yours until deleted.",
        new[] { ("hostile", "HOSTILE"), ("ally", "ON YOUR SIDE") })
    {
        Preference = true,
    };

    public static readonly Choice Behaviour = new(
        "spawner.vehicles.behaviour", "BEHAVIOUR",
        "THEIR OWN is what the vehicle does by itself. ATTACK starts a fight. GUARD holds and shoots. PARKED stays still. Cars can also WANDER or RUN AWAY.",
        new[] { ("own", "THEIR OWN"), ("attack", "ATTACK"), ("guard", "GUARD"), ("parked", "PARKED"), ("wander", "WANDER"), ("runaway", "RUN AWAY") })
    {
        Preference = true,
    };

    public static readonly Slider Health = new(
        "spawner.vehicles.health", "HEALTH",
        "How much health they get, times their normal amount.",
        min: 0.25, max: 10, step: 0.25, value: 1, unit: "X")
    {
        Preference = true,
    };

    public static readonly Command Spawn = new(
        "spawner.vehicles.spawn", "SPAWN VEHICLE",
        "Spawns the vehicle picked in VEHICLE, HOW MANY times, the way this tab and PLACE say. Drones appear in the air.",
        SpawnPicked)
    {
        Preview = static signs => Placing.Show(signs, Room, "SPAWN"),
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("WHICH", Group, Vehicle, RefreshList),
        new("HOW", Side, Behaviour, Health, Spawn),
        Placing.Section,
    };

    // Runs every tick. The list is read when the tab comes up, and VEHICLE follows GROUP as soon
    // as it changes.
    public static void Tick()
    {
        var shown = Menu.Shows(Vehicle);
        if (shown && !_shown)
        {
            Refresh();
        }

        _shown = shown;
        if (Group.CurrentId != _listedGroup)
        {
            ListVehicles();
        }
    }

    static void Refresh()
    {
        if (World.Info is null)
        {
            return;
        }

        _world = World.Info;
        _recipes = Recipes();

        var groups = _recipes.Select(recipe => recipe.Group).Distinct().ToArray();
        var group = Group.CurrentId;
        Group.SetOptions(new[] { ("", "EVERYTHING") }.Concat(groups.Select(name => (name, name))).ToArray(), Math.Max(0, Array.IndexOf(groups, group) + 1));
        ListVehicles();
    }

    static void ListVehicles()
    {
        var group = Group.CurrentId;
        _listedGroup = group;
        var shown = _recipes.Where(recipe => group.Length == 0 || recipe.Group == group).ToList();
        if (shown.Count == 0)
        {
            Vehicle.SetOptions(new[] { ("", World.Info is null ? "OPEN THE MENU IN GAME" : "NONE LOADED HERE") }, 0);
            return;
        }

        var picked = Vehicle.CurrentId;
        var options = shown.Select(recipe => (recipe.Key, group.Length == 0 ? $"{recipe.Label}  ({recipe.Group})" : recipe.Label)).ToArray();
        Vehicle.SetOptions(options, Math.Max(0, Array.FindIndex(options, option => option.Key == picked)));
    }

    static Outcome SpawnPicked()
    {
        // A hotkey can ask before the tab has read the list, or after GROUP changed.
        if (!ReferenceEquals(_world, World.Info) || _recipes.Count == 0)
        {
            Refresh();
        }

        ListVehicles();
        if (_recipes.Find(recipe => recipe.Key == Vehicle.CurrentId) is not { } picked)
        {
            return Outcome.Failed("PICK A VEHICLE FIRST");
        }

        if (!picked.Template.IsValid)
        {
            Refresh();
            return Outcome.Failed("GONE, PICK AGAIN");
        }

        return Build(picked);
    }

    // Another of a vehicle already in the world, built from the template it came from.
    public static Outcome Copy(RVehicleNPC vehicle)
    {
        var template = IsTemplate(vehicle) ? vehicle : vehicle.ObjectArchetype as RVehicleNPC;
        if (template is not { IsValid: true } || !IsTemplate(template) || Make(template) is not { } recipe)
        {
            return Outcome.Failed("CAN'T COPY THAT ONE");
        }

        return Build(recipe);
    }

    static Outcome Build(Recipe picked)
    {
        if (Placing.Plan(Room, out var mover, out var spots) is { } refused)
        {
            return refused;
        }

        var template = picked.Template;
        var lift = template is RVehicleFlying ? AirLift : GroundLift;
        var behaviour = Behaviour.CurrentId;
        var ally = Side.Index == 1;

        // Written before the native call, so a crash inside it names what.
        Log.Info($"Spawner: spawning {spots.Count} x {picked.Label} ({picked.Key}, {Side.CurrentId}, {behaviour}).");
        var made = 0;
        foreach (var spot in spots)
        {
            // The actor's own spawn refuses a spot the vehicle won't fit, unlike a forced one.
            if (mover.Spawn(template.Class, null, default, spot.Floor + new Vector3(0f, 0f, lift), spot.Facing, template, false)
                is not RVehicleNPC vehicle || !vehicle.IsValid || vehicle.bDeleteMe)
            {
                continue;
            }

            SetUp(vehicle);
            var spawn = Spawned.Add(vehicle, picked.Label, passive: false);
            spawn.Ready = () => !vehicle.IsTimerActive(OwnStart);
            spawn.Start = () => Start(vehicle, spawn, behaviour, ally);
            made++;
        }

        return made > 0 ? Outcome.Done($"{made} SPAWNED") : Outcome.Failed("NO ROOM THERE");
    }

    static void SetUp(RVehicleNPC vehicle)
    {
        vehicle.BehaviourDeactivateRange = Anywhere;
        if (!Health.IsDefault)
        {
            var full = Math.Max(1, (int)MathF.Round((vehicle.HealthMax > 0 ? vehicle.HealthMax : Math.Max(1, vehicle.Health)) * (float)Health.Value));
            vehicle.HealthMax = full;
            vehicle.Health = full;
        }
    }

    // The vehicle starts its own behaviour on a short timer after it begins play, which would undo
    // anything set before, so this waits for that to have run.
    static void Start(RVehicleNPC vehicle, Spawned.Spawn spawn, string behaviour, bool ally)
    {
        if (!vehicle.IsValid || vehicle.bDeleteMe || vehicle.HasDied)
        {
            return;
        }

        Steer(vehicle, behaviour);
        if (!ally)
        {
            return;
        }

        // What the drone hacker does to each target, then its self-destruct is taken away.
        var before = vehicle.CurrentBehaviour;
        vehicle.EndDroneHack();
        vehicle.SwitchAllegiance(Forever);
        vehicle.ClearTimer(SelfDestruct);

        spawn.Ready = null;
        spawn.StartAt = World.Now + CheckAfter;
        spawn.Start = () => Check(vehicle, before);
    }

    static void Steer(RVehicleNPC vehicle, string behaviour)
    {
        switch (behaviour)
        {
            case "attack":
                vehicle.GotoCombatBehaviour();
                break;
            case "guard":
                vehicle.GotoGuardBehaviour();
                break;
            case "parked":
                vehicle.GotoBehaviourClass(RVehicleBehaviour_Idle.StaticClass());
                break;
            case "wander":
                vehicle.GotoBehaviourClass(RVehicleBehaviour_Wander.StaticClass());
                break;
            case "runaway":
                vehicle.GotoBehaviourClass(RVehicleBehaviour_RunAway.StaticClass());
                break;
        }
    }

    // The game's switch to friendly swaps the AI, and on some behaviours it leaves none at all,
    // which froze tanks in Oracle. That one case gets its own AI back and stays hostile.
    static void Check(RVehicleNPC vehicle, RVehicleBehaviour? before)
    {
        if (!vehicle.IsValid || vehicle.bDeleteMe || vehicle.HasDied || vehicle.CurrentBehaviour is { IsValid: true })
        {
            return;
        }

        Log.Warn("spawner.vehicles.hack." + vehicle.Ptr, $"ON YOUR SIDE left {vehicle.Class.Name} with no AI, so it was put back hostile.");
        vehicle.ClearTimer(SelfDestruct);
        World.Info?.VehicleCombatManager?.SetVehicleAsFriendly(vehicle, false);
        if (before is { IsValid: true })
        {
            vehicle.GotoBehaviour(before);
        }
        else
        {
            vehicle.SwitchToAssignedBehaviour();
        }
    }

    static List<Recipe> Recipes()
    {
        // The same template is often cooked into several packages, so one of each name is kept.
        var found = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
        foreach (var vehicle in Game.FindObjects<RVehicleNPC>())
        {
            if (!vehicle.IsValid || vehicle.IsClassDefaultObject)
            {
                continue;
            }

            var template = IsTemplate(vehicle) ? vehicle : vehicle.ObjectArchetype as RVehicleNPC;
            if (template is not { IsValid: true } || !IsTemplate(template) || Make(template) is not { } recipe)
            {
                continue;
            }

            found.TryAdd(recipe.Label + "@" + recipe.Group, recipe);
        }

        var list = found.Values.ToList();
        list.Sort((a, b) =>
        {
            var order = string.CompareOrdinal(a.Group, b.Group);
            return order != 0 ? order : string.CompareOrdinal(a.Label, b.Label);
        });
        return list;
    }

    // A template lives in its package, a vehicle in the world lives in a level.
    static bool IsTemplate(RVehicleNPC vehicle) => !vehicle.IsClassDefaultObject && vehicle.Outer is { IsValid: true } outer && outer is not Level;

    static Recipe? Make(RVehicleNPC template)
    {
        if (template is RVehicleStaticTank or RVehicleNPCBatmobile or RVehicleHushBase or RVehicleBatmobilePredictor)
        {
            return null;
        }

        var path = template.GetPathName(null);
        if (Refused.Any(word => path.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new Recipe(path, People.Pretty(template.Name.ToString() ?? "VEHICLE"), GroupOf(template), template);
    }

    static string GroupOf(RVehicleNPC template) => template switch
    {
        RVehicleHeavyTank => "HEAVY TANKS",
        RVehicleTank => "TANKS",
        RVehicleFlying => "DRONES",
        RVehicleWalkerBase => "WALKERS",
        RVehicleCar => "CARS AND VANS",
        _ => "OTHER",
    };
}
