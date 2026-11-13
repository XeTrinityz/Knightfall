#nullable enable

using System.Numerics;
using BmSDK.BmGame;
using PickupMode = BmSDK.BmGame.RVehicleBatmobileBase.EBatmobilePickupMode;
using SelfDrive = BmSDK.BmGame.RVehicle.ESelfDriveMode;

namespace Knightfall;

// The Batmobile drives itself the way it does when it is called, along the game's road network.
// The game only plans a route for an empty car, so the driver is cleared for that one call.
static class Autopilot
{
    // 30 m, 100 m and 300 m. Only "no road near the destination" gets better with a wider search.
    static readonly float[] RoadSearch = { 3000f, 10000f, 30000f };
    const float AnyDistance = 1000000f;
    const float ExactHeightRange = 2000f;
    const float ArrivedWithin = 4000f;
    const float StartGraceSeconds = 1f;

    // Self-drive lets go 15 to 20 m short of the end without braking, so the car is slowed to a
    // stop over the room it has left.
    const float BrakeMin = 800f;
    const float BrakeMax = 3000f;
    const float BrakeRoomMin = 300f;
    const float StoppedSpeed = 60f;
    const float BrakeSeconds = 4f;

    static readonly Held<RVehicleBatmobileBase, bool> BoostFlag = new("autopilot boost", car => car.SelfDriveAllowBoost, (car, on) => car.SelfDriveAllowBoost = on);

    static RVehicleBatmobileBase? _driving;
    static Command? _startedBy;
    static Vector3 _goal;
    static float _since;
    static RVehicleBatmobileBase? _braking;
    static float _brakeSince;

    public static readonly Command ToWaypoint = new(
        "batmobile.driving.autopilot_waypoint", "AUTOPILOT TO WAYPOINT",
        "The Batmobile drives you to your map waypoint along the roads. Press again to take the wheel back.",
        () => Toggle(ToWaypoint!, Markers.Waypoint))
    {
        Block = Car.NotDriving,
    };

    public static readonly Command ToObjective = new(
        "batmobile.driving.autopilot_objective", "AUTOPILOT TO OBJECTIVE",
        "The Batmobile drives you to the current mission marker along the roads. Press again to take the wheel back.",
        () => Toggle(ToObjective!, Markers.Objective))
    {
        Block = Car.NotDriving,
    };

    public static readonly Command Stop = new(
        "batmobile.driving.autopilot_stop", "STOP AUTOPILOT",
        "Takes the wheel back from the autopilot.",
        () => Active ? Halt(Outcome.Done("STOPPED")) : Outcome.Unchanged("NOT ON"))
    {
        Block = static () => Active ? null : "AUTOPILOT IS OFF",
    };

    public static readonly Toggle CanBoost = new(
        "batmobile.driving.autopilot_boost", "AUTOPILOT CAN BOOST",
        "Lets the autopilot use the afterburner on the straights.")
    {
        Preference = true,
    };

    public static bool Active => _driving is { IsValid: true };

    public static bool Braking => _braking is not null;

    // How far the car still has to go, along the ground.
    public static float Remaining(Vector3 from) => Flat(_goal - from).Length();

    static Outcome Toggle(Command command, Func<(Vector3 At, bool Exact)?> target)
    {
        if (Active)
        {
            return Halt(Outcome.Done("STOPPED"));
        }

        var car = World.Driving;
        if (car is null || World.Controller is not { } controller)
        {
            return Outcome.Failed("NOT IN CAR");
        }

        if (car.bControlledBySeqAct || car.SelfDriveMode != SelfDrive.SELFDRIVE_None)
        {
            return Outcome.Failed("BUSY");
        }

        if (car.IsInBattleMode())
        {
            return Outcome.Failed("BATTLE MODE");
        }

        if (target() is not { } goal)
        {
            return Outcome.Failed(command == ToWaypoint ? "NO WAYPOINT" : "NO MARKER");
        }

        var start = Plan(car, controller, goal, out var end);
        if (start < 0)
        {
            Log.Info($"Autopilot could not plan a route, the game said {start}.");
            return Outcome.Failed("NO ROUTE");
        }

        controller.ExecuteBatmobileRoute(start, end, PickupMode.BPM_None, false);
        if (car.SelfDriveMode == SelfDrive.SELFDRIVE_None)
        {
            return Outcome.Failed("REFUSED");
        }

        // The same mark the game's own scripted drives use, so the driving camera and the fire
        // button leave the car alone.
        car.bControlledBySeqAct = true;
        _braking = null;
        _driving = car;
        _startedBy = command;
        _goal = end;
        _since = World.Now;
        return Outcome.Done("DRIVING");
    }

    static int Plan(RVehicleBatmobileBase car, RPlayerController controller, (Vector3 At, bool Exact) goal, out Vector3 end)
    {
        // A pin dropped on the map has a made-up height, so any height will do for it.
        var height = goal.Exact ? ExactHeightRange : AnyDistance;
        var start = -1;
        end = default;

        var driver = car.Driver;
        try
        {
            car.Driver = null!;
            foreach (var search in RoadSearch)
            {
                start = controller.PlanBatmobileRoute(goal.At, false, AnyDistance, out end, search, height, false, Vector3.Zero, null!);
                if (start != -1)
                {
                    break;
                }
            }
        }
        finally
        {
            car.Driver = driver;
        }

        return start;
    }

    public static void Tick()
    {
        if (_braking is not null)
        {
            Brake();
        }

        if (_driving is null)
        {
            return;
        }

        var car = _driving;
        if (!car.IsValid || World.Driving != car)
        {
            Halt(Outcome.Unchanged("STOPPED"));
            return;
        }

        if (CanBoost.On)
        {
            BoostFlag.Hold(car, true);
        }
        else
        {
            BoostFlag.Release();
        }

        if (car.SelfDriveMode == SelfDrive.SELFDRIVE_None && World.Now - _since > StartGraceSeconds)
        {
            var arrived = Flat(car.Location - _goal).Length() < ArrivedWithin;
            Halt(arrived ? Outcome.Done("ARRIVED") : Outcome.Failed("FELL SHORT"));
            if (arrived)
            {
                _braking = car;
                _brakeSince = World.Now;
                car.EmergencyBrake();
            }

            return;
        }

        if (car.IsInBattleMode())
        {
            Halt(Outcome.Unchanged("BATTLE MODE"));
        }
    }

    static void Brake()
    {
        var car = _braking!;
        if (!car.IsValid || World.Driving != car || World.Now - _brakeSince > BrakeSeconds || car.CollisionComponent is not { IsValid: true } body)
        {
            _braking = null;
            return;
        }

        var flat = Flat(car.Velocity);
        var speed = flat.Length();
        if (speed < StoppedSpeed)
        {
            _braking = null;
            return;
        }

        var ahead = Vector3.Dot(flat, Flat(_goal - car.Location)) > 0f;
        var room = MathF.Max(Flat(car.Location - _goal).Length(), BrakeRoomMin);
        var slowing = ahead ? Math.Clamp(speed * speed / (2f * room), BrakeMin, BrakeMax) : BrakeMax;
        body.SetRBLinearVelocity(-flat / speed * MathF.Min(slowing * World.Delta, speed), true);
    }

    static Outcome Halt(Outcome outcome)
    {
        var car = _driving;
        var command = _startedBy;
        _driving = null;
        _startedBy = null;
        BoostFlag.Release();

        if (car is { IsValid: true })
        {
            if (car.SelfDriveMode != SelfDrive.SELFDRIVE_None)
            {
                car.CancelSelfDrive();
            }

            car.bControlledBySeqAct = false;
        }

        command?.Report(outcome);
        return outcome;
    }

    public static void Release()
    {
        _braking = null;
        if (_driving is not null)
        {
            Halt(Outcome.Unchanged("STOPPED"));
        }
    }

    static Vector3 Flat(Vector3 vector) => new(vector.X, vector.Y, 0f);
}
