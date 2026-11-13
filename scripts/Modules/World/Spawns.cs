#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// WORLD > CITY, the SPAWN rows. Each one asks the game's own spawner for what it already sends
// out by itself, without its waiting.
static class Spawns
{
    // Each mob thug gets ten random street spots around the car, so narrow streets miss often.
    const int MobTries = 8;
    const int MobWanted = 3;

    // How long a marked car stays marked once it gets away.
    const float MarkSeconds = 60f;
    const float MarkRange = 3000f;

    // How long a chase's cars are waited for after asking.
    const float LookSeconds = 3f;

    // A new car is given this long before it is called a chase or a thug car, so the spawner
    // has finished setting it up.
    const float SettleSeconds = 0.5f;

    // The spawner that was asked for a car, while its cars are still looked for. The cars it
    // already had out are remembered, so the new one is told from them whatever order the
    // spawner keeps its list in.
    static RPopulationManager? _asked;
    static readonly HashSet<nint> Known = new();
    static RVehicleNPC? _came;
    static float _cameAt;
    static float _askedAt;

    public static readonly Command Mob = new(
        "world.city.thug_mob", "THUG MOB ON BATMOBILE",
        "A gang of thugs comes out of the streets around the parked Batmobile and goes for it, as they do when you leave it alone. Use its thug zapper or fight them off.",
        SendMob)
    {
        Block = Emptied,
    };

    public static readonly Command Getaway = new(
        "world.city.getaway_car", "SPAWN GETAWAY CAR",
        "Sends a thug car onto the roads near you and marks it. When the island has a police chase to give, the game makes it one, with the police after the car.",
        SendGetaway)
    {
        Block = Emptied,
    };

    public static readonly Command Apc = new(
        "world.city.spawn_apc", "SPAWN APC",
        "Sends out one of the militia's armoured APCs that you hunt down with the Batmobile, and marks it. Only once the APC hunt is open in your story.",
        SendApc)
    {
        Block = Emptied,
    };

    public static readonly Catalog.Section Section = new("SPAWN", Mob, Getaway, Apc);

    // EMPTY STREETS switches the spawner off, so these rows wait for it.
    static string? Emptied() => Streets.Empty.On ? "STREETS ARE EMPTY" : null;

    // Why the spawner can't be asked now, or null with the population manager to ask.
    static Outcome? Refusal(out RPopulationManager people)
    {
        people = null!;
        if (World.Info is not { } game)
        {
            return Outcome.NoPlayer;
        }

        if (game.IsChallengeMode())
        {
            return Outcome.Failed("NOT IN CHALLENGES");
        }

        if (game.PopulationManager is not { IsValid: true } found)
        {
            return Outcome.Failed("NO CITY HERE");
        }

        if (found.bForceDisable)
        {
            return Outcome.Failed("STREETS ARE EMPTY");
        }

        people = found;
        return null;
    }

    static Outcome SendMob()
    {
        if (Refusal(out var people) is { } refused)
        {
            return refused;
        }

        if (Car.Find() is not { } car)
        {
            return Outcome.Failed("NO BATMOBILE NEARBY");
        }

        if (car.Controller is not null)
        {
            return Outcome.Failed("GET OUT FIRST");
        }

        // The game treats a car that moved in the last 20 seconds as a threat, and thugs run
        // from it. Its own mob waits for that to pass.
        car.LastMobileTime = -100f;

        var known = new HashSet<nint>();
        foreach (var thug in people.CurrentBatmobileMobThugs)
        {
            if (thug is { IsValid: true })
            {
                known.Add(thug.Ptr);
            }
        }

        var came = 0;
        for (var i = 0; i < MobTries && came < MobWanted; i++)
        {
            people.SpawnBatmobileMob(car);
            came = 0;
            foreach (var thug in people.CurrentBatmobileMobThugs)
            {
                if (thug is { IsValid: true, bDeleteMe: false } && !known.Contains(thug.Ptr))
                {
                    came++;
                }
            }
        }

        if (came == 0)
        {
            return Outcome.Failed("NO STREET FOR THEM");
        }

        // What the game does when it forces thugs into a fight, so they hold their ground.
        foreach (var thug in people.CurrentBatmobileMobThugs)
        {
            if (thug is RPawnVillain { IsValid: true, bDeleteMe: false } villain && !known.Contains(villain.Ptr))
            {
                villain.bIgnoreBatmobileThreat = true;
                if (villain.AIController?.RiotBrain is { IsValid: true } brain)
                {
                    brain.bRiotWantsToFight = true;
                    brain.bRiotHiding = false;
                    brain.bForcedToFleeByKismet = false;
                }
            }
        }

        car.MakeReadyForThugZapper();
        people.LastBatmobileMobTime = World.Now;
        return Outcome.Done($"{came} COMING");
    }

    // The spawner can't be asked for a police chase. It only answers for a thug car, and swaps
    // that for a chase itself when the island has one to give and none is running. It also says
    // yes when nothing was free, so success is a new car in its own list.
    static Outcome SendGetaway()
    {
        if (Refusal(out var people) is { } refused)
        {
            return refused;
        }

        if (_asked is not null)
        {
            return Outcome.Unchanged("STILL LOOKING");
        }

        var fighting = World.Info?.VehicleCombatManager is { IsValid: true } vehicles
            ? vehicles.GetCurrentEncounterType()
            : RVehicleNPC.EVehicleCombatEncounterType.EVCE_NoEncounter;

        Known.Clear();
        foreach (var scenario in people.VehiclePopulation)
        {
            foreach (var car in scenario.Vehicles)
            {
                if (car is { IsValid: true })
                {
                    Known.Add(car.Ptr);
                }
            }
        }

        _asked = people;
        _came = null;
        _askedAt = World.Now;
        people.SpawnVehicleScenario(RGameInfo.VehicleScenarioType.VehicleScenario_Thug, fighting, false);
        return Outcome.Done("LOOKING");
    }

    // The game spawns the cars over the next few frames, so they're looked for a moment after
    // asking.
    public static void Watch()
    {
        if (_asked is not { } asked)
        {
            return;
        }

        // A load takes the spawner and its cars with it.
        if (!ReferenceEquals(World.Info?.PopulationManager, asked))
        {
            Forget();
            return;
        }

        if (_came is null)
        {
            if (NewCar(asked, null, out _) is { } car)
            {
                _came = car;
                _cameAt = World.Now;
            }
            else if (MathF.Abs(World.Now - _askedAt) >= LookSeconds)
            {
                Settle(Outcome.Failed("NONE FREE HERE YET"));
            }

            return;
        }

        if (MathF.Abs(World.Now - _cameAt) < SettleSeconds)
        {
            return;
        }

        if (NewCar(asked, _came, out var chase) is { } settled)
        {
            Mark(settled);
            Settle(Outcome.Done(chase ? "CHASE ON, MARKED" : "THUG CAR, MARKED"));
        }
        else
        {
            Settle(Outcome.Failed("IT GOT AWAY"));
        }
    }

    public static void Forget()
    {
        _asked = null;
        _came = null;
        Known.Clear();
    }

    static void Settle(Outcome outcome)
    {
        Forget();
        Getaway.Report(outcome);
    }

    static Outcome SendApc()
    {
        if (Refusal(out var people) is { } refused)
        {
            return refused;
        }

        // The game's own command. It also marks an APC that is already out.
        if (people.ForceSpawnAPC() != RGameInfo.EDistrict.EDISTRICT_None)
        {
            return Outcome.Done("MARKED");
        }

        if (!people.APCSideStoryEnabled())
        {
            return Outcome.Failed("APC HUNT NOT OPEN");
        }

        var left = 0;
        for (var island = RGameInfo.EDistrict.EDISTRICT_CityX; island <= RGameInfo.EDistrict.EDISTRICT_CityZ; island++)
        {
            left += Math.Max(0, people.GetAPCThreatForDistrictID((int)island, island));
        }

        return Outcome.Failed(left == 0 ? "NO APCS LEFT" : "NOT READY YET");
    }

    // The fleeing car that wasn't out when the spawner was asked, and whether it is in a police
    // chase. Police cars are the friendly ones. With a car to look for, only that car counts.
    static RVehicleNPC? NewCar(RPopulationManager people, RVehicleNPC? only, out bool chase)
    {
        chase = false;
        foreach (var scenario in people.VehiclePopulation)
        {
            var kind = scenario.ScenarioType;
            if (kind is not (RGameInfo.VehicleScenarioType.VehicleScenario_Thug or RGameInfo.VehicleScenarioType.VehicleScenario_CopChase))
            {
                continue;
            }

            foreach (var car in scenario.Vehicles)
            {
                if (car is { IsValid: true, bDeleteMe: false, IsFriendly: false }
                    && (only is null ? !Known.Contains(car.Ptr) : ReferenceEquals(car, only)))
                {
                    chase = kind == RGameInfo.VehicleScenarioType.VehicleScenario_CopChase;
                    return car;
                }
            }
        }

        return null;
    }

    static void Mark(RVehicle car)
    {
        if (World.Controller?.CurrentForensicsDevice is { IsValid: true } tracker)
        {
            tracker.TrackVehicle(car, true, true, MarkRange, false, "", MarkSeconds);
        }
    }
}
