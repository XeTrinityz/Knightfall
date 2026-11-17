#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// WORLD > CITY, the STREETS rows.
static class Streets
{
    // How high a flipped car is lifted first, so it has room to land on its roof.
    const float FlipLift = 300f;

    // The game's own off switch for street life, the one its debug options use.
    static readonly Held<RPopulationManager, bool> Off = new(
        "street population", people => people.bForceDisable, (people, off) => people.bForceDisable = off);

    // The most street people the spawner keeps out is MaxRiotPawns times one of these, one for
    // in the Batmobile and one for on foot. At 0 no riot or patrol volume can start. Neither is
    // saved.
    static readonly Held<RPopulationManager, float> CrowdInCar = new(
        "street crowd in the car", people => people.BatmobilePawnCountMultiplier, (people, size) => people.BatmobilePawnCountMultiplier = size);

    static readonly Held<RPopulationManager, float> CrowdOnFoot = new(
        "street crowd on foot", people => people.NonBatmobilePawnCountMultiplier, (people, size) => people.NonBatmobilePawnCountMultiplier = size);

    // A wandering group only starts while pooling is on or a population volume is, so with no
    // volume this keeps them off. Only the spawner's volume and wanderer spawns read it.
    static readonly Held<RPopulationManager, bool> Pooling = new(
        "street pawn pooling", people => people.bUsePawnPooling, (people, on) => people.bUsePawnPooling = on);

    // The street life cleared once already, and for which mode, so it isn't cleared every tick.
    static RPopulationManager? _cleared;
    static string? _clearedFor;

    public static readonly ModeToggle Empty = new(
        "world.city.empty_streets", "EMPTY STREETS",
        "Clears the streets and stops new street life coming. Left and right pick what goes. EVERYTHING takes all of it. CARS takes thug cars, police chases, drones, tanks and APCs. ON FOOT takes rioters, patrols and wandering thugs. Mission enemies and militia bases stay. Off lets the city refill.",
        "empty_what",
        new[] { ("all", "EVERYTHING"), ("cars", "CARS"), ("foot", "ON FOOT") })
    {
        Tick = KeepEmpty,
        Restore = Refill,
    };

    public static bool NoCars => Empty.On && Empty.ModeId != "foot";

    public static bool NoPeople => Empty.On && Empty.ModeId != "cars";

    public static readonly Toggle Quiet = new(
        "world.city.no_chatter", "NO STREET CHATTER",
        "Stops the thug talk you overhear around the city, plus the chopper chatter and the Riddler and Scarecrow billboards. Story scenes, radio calls and fight shouts still play.")
    {
        Tick = KeepQuiet,
        Restore = EndQuiet,
    };

    public static readonly Command Flip = new(
        "world.city.flip_enemy_cars", "FLIP ENEMY CARS",
        "Turns every thug car, militia tank and APC around you upside down. Police cars, drones and the Batmobile are left alone.",
        FlipAll);

    public static readonly Catalog.Section Section = new("STREETS", Empty, Quiet, Flip);

    // The game keeps chatter quiet while any object is on its suppression list, one per reason.
    // A class default object never goes away and is never one the game adds, so our entry is
    // easy to find again after a hot reload and taking it off leaves the game's own reasons alone.
    static GameObject QuietToken => RNoChatterVolume.DefaultObject;

    static RGameInfo? _quieted;

    static void KeepQuiet()
    {
        if (World.Info is not { IsValid: true } game || ReferenceEquals(_quieted, game))
        {
            return;
        }

        game.SetChatterSuppression(true, QuietToken);
        _quieted = game;
    }

    static void EndQuiet()
    {
        if (_quieted is { IsValid: true } game)
        {
            game.SetChatterSuppression(false, QuietToken);
        }

        _quieted = null;
    }

    static void KeepEmpty()
    {
        if (World.Info is not { } game || game.IsChallengeMode() || game.PopulationManager is not { IsValid: true } people)
        {
            return;
        }

        var mode = Empty.ModeId;
        var all = mode == "all";
        if (all)
        {
            Off.Hold(people, true);
        }
        else
        {
            Off.Release();
        }

        if (mode == "foot")
        {
            CrowdInCar.Hold(people, 0f);
            CrowdOnFoot.Hold(people, 0f);
            Pooling.Hold(people, false);
        }
        else
        {
            CrowdInCar.Release();
            CrowdOnFoot.Release();
            Pooling.Release();
        }

        if (ReferenceEquals(_cleared, people) && _clearedFor == mode)
        {
            return;
        }

        _cleared = people;
        _clearedFor = mode;
        if (all)
        {
            // The game's own clear-out, which it runs before a Most Wanted mission or AR
            // challenge. It leaves mission enemies alone.
            people.bRemovePopulationOnNextTick = true;
        }
        else if (mode == "cars")
        {
            ClearCars(people);
        }
        else
        {
            ClearPeople(people);
        }
    }

    static void Refill()
    {
        Off.Release();
        CrowdInCar.Release();
        CrowdOnFoot.Release();
        Pooling.Release();
        _cleared = null;
        _clearedFor = null;
    }

    // The car half of the game's RemovePopulation. Destroying a car can call back into the
    // spawner, so the cars are gathered before any goes.
    static void ClearCars(RPopulationManager people)
    {
        var cars = new List<RVehicleNPC>();
        foreach (var scenario in people.VehiclePopulation)
        {
            foreach (var car in scenario.Vehicles)
            {
                if (car is { IsValid: true })
                {
                    cars.Add(car);
                }
            }
        }

        foreach (var car in cars)
        {
            if (car.IsValid)
            {
                car.Destroy();
            }
        }

        // The spawner's tick reads each entry's first car without a check, so none may be left
        // pointing at a destroyed one.
        people.VehiclePopulation.Clear();
    }

    // The on-foot half of the game's RemovePopulation: riot and patrol volumes, wandering groups
    // and Batmobile mobs.
    static void ClearPeople(RPopulationManager people)
    {
        var volumes = new List<ROverworldPopulationVolume>();
        foreach (var volume in people.ActivePopulationVolumes)
        {
            if (volume is { IsValid: true })
            {
                volumes.Add(volume);
            }
        }

        foreach (var volume in volumes)
        {
            volume.DeactivatePopulation();
        }

        people.ActivePopulationVolumes.Clear();

        var walkers = new List<RBMPawnAI>();
        foreach (var group in people.SpawnedWanderers)
        {
            foreach (var pawn in group.SpawnedPawns)
            {
                if (pawn is { IsValid: true })
                {
                    walkers.Add(pawn);
                }
            }
        }

        foreach (var pawn in people.CurrentBatmobileMobThugs)
        {
            if (pawn is { IsValid: true })
            {
                walkers.Add(pawn);
            }
        }

        foreach (var pawn in walkers)
        {
            if (pawn.IsValid)
            {
                pawn.DestroyOrReturnToPool();
            }
        }

        people.SpawnedWanderers.Clear();
        people.ActiveWanderers = 0;
        people.CurrentBatmobileMobThugs.Clear();
    }

    // Every car, drone, tank, APC and chase the city sends out goes through this, asked from the
    // spawner's native tick.
    [Redirect(typeof(RPopulationManager), nameof(RPopulationManager.SpawnVehicleScenario))]
    static bool SpawnVehicleScenario(
        RPopulationManager self,
        RGameInfo.VehicleScenarioType type,
        RVehicleNPC.EVehicleCombatEncounterType encounter,
        bool forceApc)
    {
        try
        {
            if (NoCars)
            {
                return false;
            }
        }
        catch (Exception error)
        {
            Log.Error("world.city.empty_streets.cars", "The street car check failed", error);
        }

        return self.SpawnVehicleScenario(type, encounter, forceApc);
    }

    static Outcome FlipAll()
    {
        if (World.Info is not { } game)
        {
            return Outcome.NoPlayer;
        }

        var flipped = 0;
        var vehicles = game.Vehicles;
        foreach (var vehicle in vehicles)
        {
            if (!Enemy(vehicle))
            {
                continue;
            }

            // BmSDK's Rotator is in degrees. Position and turn go in one call, so neither undoes
            // the other.
            var turned = vehicle.Rotation;
            turned.Roll += 180f;
            vehicle.SetRBTransform(vehicle.Location + new Vector3(0f, 0f, FlipLift), turned, true);
            flipped++;
        }

        if (flipped > 0)
        {
            return Outcome.Done($"{flipped} FLIPPED");
        }

        return Outcome.Unchanged("NONE AROUND");
    }

    // Only computer-driven vehicles, so never your Batmobile. Police cars are the ones the game
    // calls friendly. Drones are left out, since they fly and would only spin.
    static bool Enemy(RVehicle? vehicle) =>
        vehicle is RVehicleNPC { IsAlive: true, IsFriendly: false, IsHackedFriendly: false }
        and not RVehicleFlying
        and not RVehicleNPCBatmobile;
}
