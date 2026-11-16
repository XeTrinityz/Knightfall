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

    // Armed means the vehicle has a fight of its own. Civilian cars don't, so they can't be put
    // on your side.
    sealed record Recipe(string Key, string Label, string Group, RVehicleNPC Template, bool Armed);

    // A spawned ally and what it was told to do. A fighting one waits parked until the combat
    // manager hands it an enemy, since with none a tank or drone aims at you instead.
    sealed class Ally
    {
        public required RVehicleNPC Vehicle;
        public required string Behaviour;
    }

    static readonly List<Ally> Allies = new();

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
        "HOSTILE vehicles are the militia's. ON YOUR SIDE hacks them like the Batmobile's drone hacker, with no self-destruct, so they stay yours until deleted. Only vehicles that can fight can be on your side. With nothing to fight, those set to fight wait parked.",
        new[] { ("hostile", "HOSTILE"), ("ally", "ON YOUR SIDE") })
    {
        Preference = true,
    };

    public static readonly Choice Behaviour = new(
        "spawner.vehicles.behaviour", "BEHAVIOUR",
        "THEIR OWN is what the vehicle does by itself. ATTACK starts a fight. GUARD holds and shoots. PARKED stays still. Cars can also WANDER or RUN AWAY, which need a road close by.",
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
        Block = static () => Side.Index == 1 && _recipes.Find(recipe => recipe.Key == Vehicle.CurrentId) is { Armed: false } ? Unarmed : null,
    };

    const string Unarmed = "CAN'T FIGHT FOR YOU";

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

        if (Allies.Count > 0)
        {
            KeepAllies();
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
        if (ally && !picked.Armed)
        {
            return Outcome.Failed(Unarmed);
        }

        // Written before the native call, so a crash inside it names what.
        Log.Info($"Spawner: spawning {spots.Count} x {picked.Label} ({picked.Key}, {Side.CurrentId}, {behaviour}).");
        var made = 0;
        foreach (var spot in spots)
        {
            // The actor's own spawn refuses a spot the vehicle won't fit, unlike a forced one.
            if (mover.Spawn(template.Class, null, default, spot.Floor + new Vector3(0f, 0f, lift), spot.Facing, template, false)
                is not RVehicleNPC vehicle || !vehicle.IsAlive)
            {
                continue;
            }

            SetUp(vehicle);
            if (ally)
            {
                Befriend(vehicle, behaviour);
            }

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

    // What the drone hacker does to each target, then its self-destruct is taken away. Done the
    // moment it spawns and parked straight away, which also stops the vehicle's own start, so it
    // never has a moment as an enemy.
    static void Befriend(RVehicleNPC vehicle, string behaviour)
    {
        vehicle.EndDroneHack();
        vehicle.SwitchAllegiance(Forever);
        vehicle.ClearTimer(SelfDestruct);
        Park(vehicle);
        Allies.Add(new Ally { Vehicle = vehicle, Behaviour = behaviour });
    }

    static void Park(RVehicleNPC vehicle) => vehicle.GotoBehaviourClass(RVehicleBehaviour_Idle.StaticClass());

    static bool Fights(string behaviour) => behaviour is "own" or "attack" or "guard";

    // The vehicle starts its own behaviour on a short timer after it begins play, which would undo
    // anything set before, so this waits for that to have run. A fighting ally is left parked for
    // KeepAllies to send in.
    static void Start(RVehicleNPC vehicle, Spawned.Spawn spawn, string behaviour, bool ally)
    {
        if (!vehicle.IsAlive || vehicle.HasDied)
        {
            return;
        }

        if (!ally || !Fights(behaviour))
        {
            Steer(vehicle, behaviour);
            Roadless(vehicle, behaviour);
        }

        if (!ally)
        {
            return;
        }

        spawn.Ready = null;
        spawn.StartAt = World.Now + CheckAfter;
        spawn.Start = () => Check(vehicle);
    }

    // WANDER and RUN AWAY need a road within about 20 m. Without one the game falls back on the
    // vehicle's default behaviour, and a spawned one has none, so it would sit there with no AI.
    static bool Roadless(RVehicleNPC vehicle, string behaviour)
    {
        if (behaviour is not ("wander" or "runaway") || vehicle.CurrentBehaviour is { IsValid: true })
        {
            return false;
        }

        Park(vehicle);
        Spawn.Report(Outcome.Failed("NO ROAD NEARBY, PARKED"));
        return true;
    }

    // Every tick. With no enemy from the combat manager a fighting ally waits parked, and goes in
    // when one turns up. One that drove off into a fight by itself, like a wandering tank meeting
    // the Batmobile, is put back to what it was told.
    static void KeepAllies()
    {
        for (var i = Allies.Count - 1; i >= 0; i--)
        {
            var ally = Allies[i];
            var vehicle = ally.Vehicle;
            if (!vehicle.IsAlive || vehicle.HasDied || !Spawned.Has(vehicle))
            {
                Allies.RemoveAt(i);
                continue;
            }

            if (Spawned.Settling(vehicle))
            {
                continue;
            }

            var enemy = vehicle.CombatManagerTargetOverride is { IsValid: true } target && target.IsAlive;
            var parked = vehicle.CurrentBehaviour is RVehicleBehaviour_Idle;

            // A vehicle that sees you plays its spotted sound when what it's doing can be broken
            // off for a fight. The behaviour is the spawn's own object, so this goes with it.
            if (!enemy && vehicle.CurrentBehaviour is { IsValid: true, CanBeInteruptedByCombat: true } calm)
            {
                calm.CanBeInteruptedByCombat = false;
            }
            if (Fights(ally.Behaviour))
            {
                if (enemy && parked)
                {
                    Steer(vehicle, ally.Behaviour == "own" ? "attack" : ally.Behaviour);
                }
                else if (!enemy && !parked)
                {
                    Park(vehicle);
                }

                continue;
            }

            if (enemy || Doing(vehicle.CurrentBehaviour, ally.Behaviour))
            {
                continue;
            }

            if (ally.Behaviour == "parked")
            {
                Park(vehicle);
                continue;
            }

            Steer(vehicle, ally.Behaviour);
            if (Roadless(vehicle, ally.Behaviour))
            {
                ally.Behaviour = "parked";
            }
        }
    }

    // Wander is a kind of run away, so run away means exactly that.
    static bool Doing(RVehicleBehaviour? current, string behaviour) => behaviour switch
    {
        "parked" => current is RVehicleBehaviour_Idle,
        "wander" => current is RVehicleBehaviour_Wander,
        "runaway" => current is RVehicleBehaviour_RunAway and not RVehicleBehaviour_Wander,
        _ => true,
    };

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

    // The vehicle combat manager blows up every friendly vehicle once no enemy vehicle is left to
    // fight, which is how the game ends a drone hack. Our own allies are kept out of that.
    [Redirect(typeof(RVehicleNPC), nameof(RVehicleNPC.DroneHackedSuicide))]
    static void DroneHackedSuicide(RVehicleNPC self)
    {
        try
        {
            if (self.IsHackedFriendly && Spawned.Has(self))
            {
                return;
            }

            self.DroneHackedSuicide();
        }
        catch (Exception error)
        {
            Log.Error("spawner.vehicles.suicide", "The friendly vehicle self-destruct check failed", error);
        }
    }

    // Each tick the combat manager asks every vehicle whether it's fighting its target, and those
    // that are join the fight, which starts the combat music and the commander's lines. With no
    // enemy to give an ally, its target falls back to you, so an ally only says yes about the
    // enemy it was handed.
    [Redirect(typeof(RVehicleTank), nameof(RVehicleTank.InCombat))]
    static bool TankInCombat(RVehicleTank self, Actor Target) => AllyInCombat(self, Target) ?? self.InCombat(Target);

    [Redirect(typeof(RVehicleFlying), nameof(RVehicleFlying.InCombat))]
    static bool DroneInCombat(RVehicleFlying self, Actor Target) => AllyInCombat(self, Target) ?? self.InCombat(Target);

    // A tank or drone with no enemy handed to it targets you, and its turret tracks you with the
    // lock-on sound even while parked. With no target the game turns the turret back to the front.
    [Redirect(typeof(RVehicleTank), nameof(RVehicleTank.GetTargetActor))]
    static Actor TankTarget(RVehicleTank self) => Unaimed(self) ? null! : self.GetTargetActor();

    [Redirect(typeof(RVehicleFlying), nameof(RVehicleFlying.GetTargetActor))]
    static Actor DroneTarget(RVehicleFlying self) => Unaimed(self) ? null! : self.GetTargetActor();

    static bool Unaimed(RVehicleNPC self)
    {
        try
        {
            if (Allies.Count == 0 || !Allies.Exists(ally => ReferenceEquals(ally.Vehicle, self)))
            {
                return false;
            }

            return self.CombatManagerTargetOverride is not { IsValid: true } enemy || !enemy.IsAlive;
        }
        catch (Exception error)
        {
            Log.Error("spawner.vehicles.target", "The ally target check failed", error);
            return false;
        }
    }

    // False when an ally is asked about anything but its enemy, null to let the game answer.
    static bool? AllyInCombat(RVehicleNPC self, Actor? target)
    {
        try
        {
            if (Allies.Count == 0 || !Allies.Exists(ally => ReferenceEquals(ally.Vehicle, self)))
            {
                return null;
            }

            var enemy = self.CombatManagerTargetOverride;
            return enemy is { IsValid: true } && enemy.IsAlive && ReferenceEquals(enemy, target) ? null : false;
        }
        catch (Exception error)
        {
            Log.Error("spawner.vehicles.in_combat", "The ally fight check failed", error);
            return null;
        }
    }

    // The game's switch to friendly can leave a vehicle with no AI at all, which froze tanks in
    // Oracle. Such an ally is parked, so it still has one.
    static void Check(RVehicleNPC vehicle)
    {
        if (!vehicle.IsAlive || vehicle.HasDied || vehicle.CurrentBehaviour is { IsValid: true })
        {
            return;
        }

        Log.Warn("spawner.vehicles.hack." + vehicle.Ptr, $"ON YOUR SIDE left {vehicle.Class.Name} with no AI, so it was parked.");
        Park(vehicle);
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

        var armed = template.DefaultCombatBehaviour is { IsValid: true } || template.DefaultCombatBehaviourInstance is { IsValid: true };
        return new Recipe(path, People.Pretty(template.Name.ToString() ?? "VEHICLE"), GroupOf(template), template, armed);
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
