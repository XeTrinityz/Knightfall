#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// TELEPORT > WAYPOINT. A far move works like the game's own Batmobile rescue. The screen fades
// out, the target's district becomes the current one and is loaded while the player is held
// still, then the player is moved and the screen fades back in. The move is the game's mission
// teleport, so the player and the Batmobile each tidy up after it the way they do in a mission.
static class Teleport
{
    enum Stage
    {
        Idle,
        Darkening,
        Loading,
        Brightening,
    }

    const string Lender = "teleport";
    public const float UnitsPerMetre = 100f;
    const float LoadSeconds = 15f;
    const float FadeOut = 0.3f;
    const float FadeBack = 0.4f;

    // Above the tallest roof and below the lowest street.
    public const float Sky = 100000f;
    public const float Deep = -50000f;

    // An exact target looks for a floor from a little above it to this far below.
    const float Headroom = 150f;
    const float ExactDepth = 5000f;

    // Invisible blocking boxes over roofs and the sky are passed through, up to this many.
    const int Layers = 12;

    // Steeper than this isn't somewhere to stand.
    public const float Level = 0.6f;

    const float CarLift = 150f;
    const float WaterAbove = 5000f;
    const int Tries = 4;
    const float TryStep = 100f;
    const float ArrivedWithin = 60f;

    // A pin on the map has a made-up height, and districts are boxes, so its district is looked
    // for at a few heights.
    static readonly float[] PinHeights = { 0f, 1500f, 4000f, 8000f, 15000f };

    static SeqAct_Teleport? _action;
    static Vector3 _destination;
    static Rotator _facing;

    static Stage _stage;
    static float _stageAt;
    static Command? _by;
    static Pawn? _mover;
    static Actor.EPhysics _physics;
    static bool _held;
    static Vector3 _target;
    static bool _exact;
    static FName _level;
    static FName _leftLevel;
    static Vector3 _from;
    static Vector3? _back;

    public static readonly Command ToWaypoint = new(
        "teleport.waypoint.to_waypoint", "TELEPORT TO WAYPOINT",
        "Takes you to the waypoint you set on the map, in the Batmobile too. A pin dropped on the map lands you on the highest ground there, usually a roof.",
        () => Go(ToWaypoint!, Markers.Waypoint(), "NO WAYPOINT"));

    public static readonly Command ToObjective = new(
        "teleport.waypoint.to_objective", "TELEPORT TO OBJECTIVE",
        "Takes you to the current mission marker.",
        () => Go(ToObjective!, Markers.Objective(), "NO MARKER"));

    public static readonly Command GoBack = new(
        "teleport.waypoint.go_back", "GO BACK",
        "Takes you back to where you were before the last teleport.",
        () => Go(GoBack!, _back is { } back ? (back, true) : null, "NOWHERE YET"));

    public static readonly Slider NoFadeRange = new(
        "teleport.waypoint.no_fade_range", "NO FADE RANGE",
        "A teleport shorter than this, onto ground that's already there, happens straight away with no fade.",
        min: 0, max: 500, step: 25, value: 100, unit: " M", zero: "OFF")
    {
        Preference = true,
    };

    public static readonly Slider ArrivalHeight = new(
        "teleport.waypoint.arrival_height", "ARRIVAL HEIGHT",
        "Arrive this far above the ground and drop in, ready to glide. The Batmobile always lands on its wheels.",
        min: 0, max: 100, step: 5, value: 0, unit: " M", zero: "GROUND")
    {
        Preference = true,
    };

    public static readonly Toggle FaceTravel = new(
        "teleport.waypoint.face_travel", "FACE TRAVEL DIRECTION",
        "Arrive facing the way you travelled. Off keeps the way you were facing.")
    {
        Preference = true,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("GO TO", ToWaypoint, ToObjective, GoBack),
        new("MARK", Pin.OnSet, Pin.AtCrosshair, Pin.Clear),
        new("ARRIVAL", NoFadeRange, ArrivalHeight, FaceTravel),
    };

    static RGameRI? Ri => Game.TryGetWorldInfo(out var world) && world.GRI is RGameRI { IsValid: true } ri ? ri : null;

    static Outcome Go(Command command, (Vector3 At, bool Exact)? target, string missing) =>
        target is { } goal ? Start(command, goal.At, goal.Exact) : Outcome.Failed(missing);

    // The city's shift from where the map has it. Only interiors move it. Places from the map add
    // it, and places kept for later take it off.
    public static Vector3 CityOffset => World.Controller is { } controller ? controller.GetCurrentCityXOffset() : Vector3.Zero;

    // Exact keeps the target's height, for somewhere indoors or on a lower floor. Otherwise only X
    // and Y count and the player lands on the highest ground there. The command gets the result.
    public static Outcome Start(Command command, Vector3 at, bool exact)
    {
        var goal = (At: at, Exact: exact);
        if (Mover is not { } mover || World.Info is not { } info || Ri is not { } ri)
        {
            return Outcome.NoPlayer;
        }

        if (Refusal(mover) is { } refused)
        {
            return refused;
        }

        var car = mover as RVehicleBatmobileBase;

        // Interiors like the GCPD are separate maps that only the game's own doors leave.
        var volumes = info.LevelVolumeList;
        var here = ri.GetLevelVolumeForPoint(mover.Location, 0f, true, false, car is not null, -1, -1, -1);
        if (here < 0 || here >= volumes.Count)
        {
            return Outcome.Failed("NOT INDOORS");
        }

        var there = District(ri, goal.At, goal.Exact, car is not null);
        if (there < 0 || there >= volumes.Count)
        {
            return Outcome.Failed("NOT IN THE CITY");
        }

        // The district's Batmobile flags are only about where the car can be called to, so they
        // aren't asked here.
        var district = volumes[there];
        _by = command;
        _mover = mover;
        _target = goal.At;
        _exact = goal.Exact;
        _level = district.LevelName;
        _leftLevel = volumes[here].LevelName;
        _from = mover.Location;

        var flat = new Vector2(_target.X - _from.X, _target.Y - _from.Y).Length();
        if (district.IsVisible && flat <= NoFadeRange.Value * UnitsPerMetre && Place())
        {
            return Outcome.Done("ARRIVED");
        }

        ri.SetFadeType(false, FadeOut, false, false, 0f, 0f, 0f, 0f);
        Enter(Stage.Darkening);
        return Outcome.Unchanged("GOING");
    }

    // A short move inside ground that's already loaded, made straight away. On the floor stands
    // the mover on that point. Otherwise the point is where the player's middle goes, and they
    // keep falling or gliding as before.
    public static Outcome Hop(Pawn mover, Vector3 at, Rotator facing, bool onFloor)
    {
        if (Refusal(mover) is { } refused)
        {
            return refused;
        }

        var from = mover.Location;
        var velocity = mover.Velocity;
        var heading = Flat(mover.Rotation.ToDirection());
        if (!Put(mover, onFloor ? at + new Vector3(0f, 0f, Lift(mover)) : at, facing))
        {
            return Outcome.Failed("NO ROOM");
        }

        if (ReferenceEquals(mover, Mover))
        {
            _back = from;
        }

        // The car's clean up after a teleport stops it dead, so its speed is given back along
        // the way it now faces.
        if (mover is RVehicleBatmobileBase && mover.CollisionComponent is { IsValid: true } body && velocity.LengthSquared() > 1f)
        {
            var along = Vector3.Dot(velocity, heading);
            body.SetRBLinearVelocity(Flat(facing.ToDirection()) * along + new Vector3(0f, 0f, velocity.Z), false);
        }

        return Outcome.Done("ARRIVED");
    }

    // The car while driving, otherwise the player.
    public static Pawn? Mover => World.Driving is { IsValid: true } driving ? driving : World.Player is { IsValid: true } player ? player : null;

    // How far above the floor the mover's middle is.
    public static float Lift(Pawn mover) =>
        mover is RVehicleBatmobileBase ? CarLift : (mover.CylinderComponent is { IsValid: true } body ? body.CollisionHeight : 90f) + 5f;

    public static Vector3 Flat(Vector3 direction) =>
        new Vector2(direction.X, direction.Y).LengthSquared() > 1e-6f ? Vector3.Normalize(direction with { Z = 0f }) : Vector3.UnitX;

    // Not at the bottom of the river or the bay. The game's own water trace looks down from high
    // above the floor.
    public static bool Dry(Vector3 floor) =>
        World.Controller is not { } controller || !controller.TraceWaterVolume(out _, floor, floor + new Vector3(0f, 0f, WaterAbove));

    static Outcome? Refusal(Pawn mover)
    {
        if (World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        if (_stage != Stage.Idle)
        {
            return Outcome.Unchanged("ALREADY GOING");
        }

        if (controller.bCinematicMode || mover is RVehicleBatmobileBase { bControlledBySeqAct: true } or RVehicleBatmobileBase { MatineeControlRefCount: not 0 })
        {
            return Outcome.Failed("NOT NOW");
        }

        return Autopilot.Active ? Outcome.Failed("AUTOPILOT ON") : null;
    }

    static int District(RGameRI ri, Vector3 at, bool exact, bool vehicle)
    {
        var found = ri.GetLevelVolumeForPoint(at, 0f, true, false, vehicle, -1, -1, -1);
        if (found >= 0 || exact)
        {
            return found;
        }

        foreach (var height in PinHeights)
        {
            found = ri.GetLevelVolumeForPoint(at with { Z = height }, 0f, true, false, vehicle, -1, -1, -1);
            if (found >= 0)
            {
                return found;
            }
        }

        return -1;
    }

    public static void Tick()
    {
        if (_stage == Stage.Idle)
        {
            return;
        }

        var waited = World.Now - _stageAt;
        var ri = Ri;
        switch (_stage)
        {
            case Stage.Darkening when waited >= FadeOut && ri is not null:
                if (_mover is not { IsValid: true } mover || World.Controller is not { } controller)
                {
                    Finish(Outcome.Failed("LOST"));
                    return;
                }

                Hold(mover);
                ri.SetCurrentLevel(_level, controller, true);
                Enter(Stage.Loading);
                break;

            case Stage.Loading when ri is not null:
                if (_mover is not { IsValid: true })
                {
                    Finish(Outcome.Failed("LOST"));
                }
                else if (ri.IsLevelVisible(_level, false) && Place())
                {
                    Finish(Outcome.Done("ARRIVED"));
                }
                else if (waited > LoadSeconds)
                {
                    Log.Info($"Teleport gave up waiting for {_level} to load.");
                    if (World.Controller is { } owner)
                    {
                        ri.SetCurrentLevel(_leftLevel, owner, true);
                    }

                    Finish(Outcome.Failed("DIDN'T LOAD"));
                }

                break;

            case Stage.Brightening when waited >= FadeBack:
                _stage = Stage.Idle;
                break;
        }
    }

    static void Enter(Stage stage)
    {
        _stage = stage;
        _stageAt = World.Now;
    }

    static void Finish(Outcome outcome)
    {
        Let();
        Ri?.SetFadeType(true, FadeBack, false, false, 0f, 0f, 0f, 0f);
        Enter(Stage.Brightening);
        _by?.Report(outcome);
        _mover = null;
    }

    // Held still while the ground under them may be unloading, so they can't fall out of the world.
    static void Hold(Pawn mover)
    {
        _held = true;
        if (mover is RVehicle vehicle)
        {
            vehicle.SetRigidBodyFixed(true);
            return;
        }

        _physics = mover.Physics;
        mover.Velocity = Vector3.Zero;
        mover.SetPhysics(Actor.EPhysics.PHYS_None, false);
    }

    static void Let()
    {
        if (!_held || _mover is not { IsValid: true } mover)
        {
            _held = false;
            return;
        }

        _held = false;
        if (mover is RVehicle vehicle)
        {
            vehicle.SetRigidBodyFixed(false);
        }
        else
        {
            mover.SetPhysics(_physics == Actor.EPhysics.PHYS_None ? Actor.EPhysics.PHYS_None : Actor.EPhysics.PHYS_Falling, false);
        }
    }

    // Finds the floor and moves the player there. False when the ground isn't loaded yet.
    static bool Place()
    {
        if (_mover is not { IsValid: true } mover || !Ground(mover, _target, _exact, out var floor))
        {
            return false;
        }

        var lift = Lift(mover);
        if (mover is not RVehicleBatmobileBase)
        {
            lift += (float)ArrivalHeight.Value * UnitsPerMetre;
        }

        var way = new Vector3(_target.X - _from.X, _target.Y - _from.Y, 0f);
        var facing = FaceTravel.On && way.LengthSquared() > 1f ? Rotator.FromDirection(way) : new Rotator(0f, mover.Rotation.Yaw, 0f);
        if (!Put(mover, floor + new Vector3(0f, 0f, lift), facing))
        {
            return false;
        }

        _back = _from;
        Let();
        return true;
    }

    // Somewhere a pawn doesn't fit refuses the move, so a little higher is tried. The car is
    // always put where it's sent.
    static bool Put(Pawn mover, Vector3 point, Rotator facing)
    {
        _facing = facing;
        var action = Action();
        var start = mover.Location;
        for (var i = 0; i < Tries; i++)
        {
            _destination = point + new Vector3(0f, 0f, i * TryStep);
            mover.OnTeleport(action);
            if (Vector3.Distance(mover.Location, _destination) < ArrivedWithin || (mover is RVehicle && Vector3.Distance(mover.Location, start) > ArrivedWithin))
            {
                return true;
            }
        }

        return false;
    }

    static bool Ground(Actor tracer, Vector3 at, bool exact, out Vector3 floor) =>
        Floor(tracer, at, exact ? at.Z + Headroom : Sky, exact ? at.Z - ExactDepth : Deep, out floor);

    // The first floor going down from top to bottom at this spot.
    public static bool Floor(Actor tracer, Vector3 at, float top, float bottom, out Vector3 floor)
    {
        for (var i = 0; i < Layers && top > bottom; i++)
        {
            var hit = tracer.Trace(out var spot, out var normal, at with { Z = bottom }, at with { Z = top }, Component.ECollisionFilter.COF_TraceWorld, Vector3.Zero, out _, 0);
            if (hit is null)
            {
                break;
            }

            if (hit is not Volume && hit is not Pawn && normal.Z >= Level)
            {
                floor = spot;
                return true;
            }

            top = spot.Z - 5f;
        }

        floor = default;
        return false;
    }

    // The game's own teleport action, made once. Kept from the clean-up, since nothing in the
    // game holds on to it.
    static SeqAct_Teleport Action()
    {
        if (_action is not { IsValid: true })
        {
            _action = Borrowed.Keep(GameObject.ConstructObject<SeqAct_Teleport>(), Lender);
        }

        _action.bUpdateRotation = true;
        _action.bStopAllMovement = true;
        _action.bDontResetCamera = false;
        _action.bForcePawnIntoCrouch = false;
        _action.bForceResetBatmobileRigidBody = true;
        return _action;
    }

    // Our action has no destination of its own, so it gives ours. Every other teleport is the
    // game's and is answered by the game.
    [Redirect(typeof(SeqAct_Teleport), nameof(SeqAct_Teleport.GetDestination))]
    static bool GetDestination(SeqAct_Teleport self, out Vector3 loc, out Rotator rot, out int bNoDestinationSet)
    {
        if (!ReferenceEquals(self, _action))
        {
            return self.GetDestination(out loc, out rot, out bNoDestinationSet);
        }

        loc = _destination;
        rot = _facing;
        bNoDestinationSet = 0;
        return true;
    }

    // A hot reload or unload mid-teleport mustn't leave the player frozen behind a black screen.
    public static void Release()
    {
        if (_stage != Stage.Idle)
        {
            Let();
            Ri?.SetFadeType(true, FadeBack, false, false, 0f, 0f, 0f, 0f);
            _stage = Stage.Idle;
        }

        _mover = null;
        _action = null;
        Borrowed.Let(Lender);
    }
}
