#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// The Arkham Knight's militia. CALL MILITIA brings a militia Humvee in from a road out of sight.
// It drives up to him the way the game's own vehicles drive to someone and stop, and its crew climb
// out calmly, take up the militia's combat rifles and follow him as bodyguards until they fall.
// The Humvee then drives off and is gone.
static partial class ArkhamKnight
{
    const string HumveePath = "Car_Archetypes.Humvee.Humvee";
    const string MilitiaGroup = "MILITIA";

    // The militia in the streets fight with their fists, so each is handed the militia's combat
    // rifle once out.
    const int SquadSize = 4;
    const float CallEvery = 30f;
    const float CrewHealth = 3f;

    // The Humvee comes from a road at least this far off, and stops this near him.
    const float ComeFrom = 4000f;
    const float StopNear = 800f;
    const float SpawnLift = 250f;

    // One that hasn't stopped by then lets its crew out where it is, and they run to him.
    const float DriveFor = 60f;

    // A crew member still seated this long after the first got out isn't coming.
    const float StuckAfter = 8f;

    // The game raises this on its own chase car so it keeps driving far from you.
    const float Anywhere = 1e17f;

    static readonly FName OwnStart = new("SwitchToAssignedBehaviour");
    static readonly FName WaitForNewBehaviour = new("WaitForNewBehaviour");

    sealed class Ride
    {
        public required RVehicleCar Car;
        public RVehicleBehaviour_StopAndSpawnBase? Stop;
        public readonly List<(RPawnVillain Body, string Label, int Health)> Crew = new();
        public bool Seated;
        public bool Stopped;
        public float GiveUpAt;
        public float FirstOutAt = -1f;
    }

    static readonly List<Ride> Rides = new();

    // Everyone his Humvees brought, sent home when he stops being the Arkham Knight.
    static readonly List<RPawnVillain> Brought = new();

    static float _nextCall;

    static Outcome CallMilitia()
    {
        if (!Option.On)
        {
            return Outcome.Failed("ARKHAM KNIGHT IS OFF");
        }

        if (World.Player is not { IsValid: true } player || World.Controller is not { } controller || World.Driving is not null)
        {
            return Outcome.NoPlayer;
        }

        if (controller.bCinematicMode)
        {
            return Outcome.Failed("NOT IN A SCENE");
        }

        if (World.Now < _nextCall)
        {
            return Outcome.Failed($"READY IN {(int)MathF.Ceiling(_nextCall - World.Now)} S");
        }

        if (Bodyguards.Room < SquadSize)
        {
            return Outcome.Failed("TOO MANY BODYGUARDS");
        }

        if (Game.FindObject<RVehicleCar>(HumveePath) is not { IsValid: true } humvee)
        {
            return Outcome.Failed("NO HUMVEE LOADED");
        }

        if (Crewmen().Count == 0)
        {
            return Outcome.Failed("NO MILITIA HERE");
        }

        controller.GetPlayerViewPoint(out var eye, out _);
        if (!RSeqAct_VehicleEnemySpawner.GetNearestAvailableSpawnPointStatic(out var start, out var facing, player, eye, humvee, ComeFrom, 0f, null!))
        {
            return Outcome.Failed("NO ROAD NEAR");
        }

        if (player.Spawn(humvee.Class, null, default, start + new Vector3(0f, 0f, SpawnLift), facing, humvee, false) is not RVehicleCar { IsValid: true, bDeleteMe: false } car)
        {
            return Outcome.Failed("THE GAME REFUSED");
        }

        car.BehaviourDeactivateRange = Anywhere;
        Rides.Add(new Ride { Car = car, GiveUpAt = World.Now + DriveFor });
        _nextCall = World.Now + CallEvery;
        return Outcome.Done("MILITIA ON THE WAY");
    }

    // Militia of a thug's size. The medic is left out as he can't hold a rifle, and the
    // lieutenant doesn't fit a seat.
    static List<string> Crewmen() =>
        People.GroupKeys(MilitiaGroup, RPawnVillainThug.StaticClass()).Except(People.GroupKeys(MilitiaGroup, RPawnVillainThugMedic.StaticClass())).ToList();

    static void Squads()
    {
        Brought.RemoveAll(body => !body.IsValid || body.bDeleteMe);
        if (World.Player is not { IsValid: true } player)
        {
            return;
        }

        for (var i = Rides.Count - 1; i >= 0; i--)
        {
            if (!Run(Rides[i], player))
            {
                Rides.RemoveAt(i);
            }
        }
    }

    // False once the Humvee is done with: everyone out, or nobody to bring.
    static bool Run(Ride ride, RPawnPlayer player)
    {
        var car = ride.Car;
        var alive = car.IsValid && !car.bDeleteMe && !car.HasDied;
        if (!ride.Seated)
        {
            if (!alive)
            {
                return false;
            }

            // The Humvee starts its own behaviour on a short timer after it begins play, which
            // would undo anything set before.
            if (car.IsTimerActive(OwnStart))
            {
                return true;
            }

            Board(ride, player);
            return ride.Crew.Count > 0;
        }

        var driving = alive && car.GetNumPassengers() > 0;
        if (driving && car.bPassengerExitRequested)
        {
            ride.Stopped = true;
        }

        // The Humvee can take up its own driving about after it was sent, so until it stops for
        // them it is sent again.
        if (driving && !ride.Stopped && ride.Stop is { IsValid: true } stop
            && (car.CurrentBehaviour is not { IsValid: true } current || current.Ptr != stop.Ptr))
        {
            Log.Warn("arkham_knight.militia.drive", $"The militia Humvee went to {car.CurrentBehaviour?.Class.Name}, so it was sent to him again.");
            car.GotoBehaviour(stop);
        }

        if (driving && World.Now >= ride.GiveUpAt)
        {
            ride.GiveUpAt = float.MaxValue;
            ride.Stopped = true;
            car.PassengerExitRequested(false, false, false);
        }

        var inside = false;
        for (var i = ride.Crew.Count - 1; i >= 0; i--)
        {
            var (body, label, health) = ride.Crew[i];
            if (!body.IsValid || body.bDeleteMe || body.Health <= 0)
            {
                ride.Crew.RemoveAt(i);
                continue;
            }

            if (!Out(body))
            {
                // One left in a seat long after the rest got out is stuck, so it goes, and the
                // Humvee is free to leave.
                if (ride.FirstOutAt >= 0f && World.Now - ride.FirstOutAt > StuckAfter)
                {
                    Spawned.Take(body);
                    ride.Crew.RemoveAt(i);
                    continue;
                }

                inside = true;
                continue;
            }

            if (ride.FirstOutAt < 0f)
            {
                ride.FirstOutAt = World.Now;
                ride.Stopped = true;
            }

            ride.Crew.RemoveAt(i);
            global::Knightfall.Arms.Hand(body, RBMWeaponCombatRifle.StaticClass());
            if (!Bodyguards.Take(body, label, health))
            {
                Spawned.Take(body);
            }
        }

        if (inside)
        {
            return true;
        }

        if (alive)
        {
            car.GotoBehaviourClass(RVehicleBehaviour_DriveOffAndDespawn.StaticClass());
        }

        return false;
    }

    // His militia take the seats, then the Humvee drives up to him to let them out.
    static void Board(Ride ride, RPawnPlayer player)
    {
        ride.Seated = true;
        var car = ride.Car;
        var keys = Crewmen();
        for (var seat = 0; seat < SquadSize && keys.Count > 0; seat++)
        {
            People.Hire(keys[seat % keys.Count], 1, CrewHealth, (body, label, health) =>
            {
                car.AddPawnToPassengerSlot(ride.Crew.Count, body, false);
                ride.Crew.Add((body, label, health));
                Brought.Add(body);
            }, car);
        }

        if (ride.Crew.Count == 0)
        {
            car.Destroy();
            return;
        }

        car.bSetPassengersHostileOnExit = false;
        car.bPassengersExitNormally = true;
        car.bPassengersExitRunAway = false;
        var stop = Made.Tracked(new RVehicleBehaviour_StopAndSpawnBase(car)
        {
            StopNearActor = player,
            StopNearActorDistance = StopNear,
            DriveAtActor = false,
            SkidToStop = true,
            DisembarkPassengers = true,
            DisembarkedPassengersRunAway = false,
        });
        car.GotoBehaviour(stop);
        ride.Stop = stop;
        Log.Info($"Arkham Knight militia: {ride.Crew.Count} aboard, driving to him.");
    }

    // Out of the Humvee and done climbing down. A calm exit doesn't end by itself. It waits for
    // something new to do, so once it is waiting it is ended here and they are free to follow him.
    static bool Out(RPawnVillain body)
    {
        if (body.Base is Vehicle || body.DrivenVehicle is { IsValid: true } || body.AIController is not { IsValid: true } brain)
        {
            return false;
        }

        if (brain.GetBehaviourOfClass(RBMBehaviour_ExitVehicle.StaticClass(), true) is not { IsValid: true } exit)
        {
            return true;
        }

        if (exit.GetStateName().Index != WaitForNewBehaviour.Index)
        {
            return false;
        }

        brain.InstantExitBehaviour(exit, false);
        return true;
    }

    static void SendSquadsHome()
    {
        foreach (var ride in Rides)
        {
            if (ride.Car is { IsValid: true, bDeleteMe: false } car)
            {
                car.Destroy();
            }
        }

        foreach (var body in Brought)
        {
            if (!body.IsValid || body.bDeleteMe)
            {
                continue;
            }

            if (!Bodyguards.Let(body))
            {
                Spawned.Take(body);
            }
        }

        Rides.Clear();
        Brought.Clear();
        _nextCall = 0f;
    }
}
