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

    static RGameInfo.VehicleScenarioType? _asked;
    static int _before;
    static float _askedAt;

    public static readonly Command Mob = new(
        "world.city.thug_mob", "THUG MOB ON BATMOBILE",
        "A gang of thugs comes out of the streets around the parked Batmobile and goes for it, as they do when you leave it alone. Use its thug zapper or fight them off.",
        SendMob);

    public static readonly Command Getaway = new(
        "world.city.getaway_car", "SPAWN GETAWAY CAR",
        "Starts a police chase after a fleeing car on the roads near you and marks the car. Where the island has no chase to give, a thug car comes instead.",
        SendGetaway);

    public static readonly Command Apc = new(
        "world.city.spawn_apc", "SPAWN APC",
        "Sends out one of the militia's armoured APCs that you hunt down with the Batmobile, and marks it. Only once the APC hunt is open in your story.",
        SendApc);

    public static readonly Catalog.Section Section = new("SPAWN", Mob, Getaway, Apc);

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

    // The spawner says yes even when no chase was free here, so success is a new chase in its
    // own list.
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

        Ask(people, RGameInfo.VehicleScenarioType.VehicleScenario_CopChase);
        return Outcome.Done("LOOKING");
    }

    // The game spawns a chase's cars over the next few frames, so they're looked for a moment
    // after asking. A cop chase is asked for first, then a thug car.
    static void Ask(RPopulationManager people, RGameInfo.VehicleScenarioType kind)
    {
        var fighting = World.Info?.VehicleCombatManager is { IsValid: true } vehicles
            ? vehicles.GetCurrentEncounterType()
            : RVehicleNPC.EVehicleCombatEncounterType.EVCE_NoEncounter;

        _asked = kind;
        _before = Running(people, kind);
        _askedAt = World.Now;
        people.SpawnVehicleScenario(kind, fighting, false);
    }

    public static void Watch()
    {
        if (_asked is not { } kind)
        {
            return;
        }

        if (World.Info?.PopulationManager is not { IsValid: true } people)
        {
            Settle(Outcome.Failed("LOST"));
            return;
        }

        if (Newest(people, kind, _before) is { } car)
        {
            Mark(car);
            Settle(Outcome.Done(kind == RGameInfo.VehicleScenarioType.VehicleScenario_CopChase ? "CHASE ON, MARKED" : "THUG CAR, MARKED"));
            return;
        }

        if (MathF.Abs(World.Now - _askedAt) < LookSeconds)
        {
            return;
        }

        if (kind == RGameInfo.VehicleScenarioType.VehicleScenario_CopChase)
        {
            Ask(people, RGameInfo.VehicleScenarioType.VehicleScenario_Thug);
            return;
        }

        Settle(Outcome.Failed("NONE FREE HERE YET"));
    }

    public static void Forget() => _asked = null;

    static void Settle(Outcome outcome)
    {
        _asked = null;
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

    static int Running(RPopulationManager people, RGameInfo.VehicleScenarioType kind)
    {
        var count = 0;
        foreach (var scenario in people.VehiclePopulation)
        {
            if (scenario.ScenarioType == kind)
            {
                count++;
            }
        }

        return count;
    }

    // The fleeing car of the newest chase, or the thug car. Police cars are the friendly ones.
    static RVehicleNPC? Newest(RPopulationManager people, RGameInfo.VehicleScenarioType kind, int before)
    {
        if (Running(people, kind) <= before)
        {
            return null;
        }

        var list = people.VehiclePopulation;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var scenario = list[i];
            if (scenario.ScenarioType != kind)
            {
                continue;
            }

            foreach (var car in scenario.Vehicles)
            {
                if (car is { IsValid: true, bDeleteMe: false, IsFriendly: false })
                {
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
