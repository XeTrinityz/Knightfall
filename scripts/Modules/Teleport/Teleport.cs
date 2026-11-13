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

    // Real seconds, so slow motion doesn't stretch them.
    const float LoadSeconds = 15f;
    const float FadeOut = 0.3f;
    const float FadeBack = 0.4f;

    // Collision can come in a moment after its district shows.
    const float FloorSeconds = 2f;

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

    // A target in the water lands on the nearest dry ground, looked for this far out in metres.
    static readonly float[] ShoreRings = { 25f, 50f, 100f, 150f, 200f, 300f };
    public const int Spokes = 12;

    // A pin on the map has a made-up height, and districts are boxes, so its district is looked
    // for at a few heights.
    static readonly float[] PinHeights = { 0f, 1500f, 4000f, 8000f, 15000f };

    // Game seconds between looks at what the rows need, for their locks.
    const float LookEvery = 0.25f;

    static SeqAct_Teleport? _action;
    static Vector3 _destination;
    static Rotator _facing;

    static Stage _stage;
    static long _stageAt;
    static long _visibleAt;
    static int _epoch;
    static Command? _by;
    static Pawn? _mover;
    static Actor.EPhysics _physics;
    static bool _held;
    static Vector3 _target;
    static bool _exact;
    static FName _level;
    static FName _leftLevel;
    static RLevelVolume? _leftVolume;
    static Vector3 _from;
    static Vector3? _back;

    static float _lookedAt = -1f;

    // Why a far teleport can't start where the player is, or null when it can.
    public static string? Away { get; private set; }

    public static bool HasPin { get; private set; }

    public static bool HasObjective { get; private set; }

    public static bool HasCar { get; private set; }

    public static readonly Command ToWaypoint = new(
        "teleport.waypoint.to_waypoint", "TELEPORT TO WAYPOINT",
        "Takes you to the waypoint you set on the map, in the Batmobile too. A pin dropped on the map lands you on the highest ground there, usually a roof.",
        () => Go(ToWaypoint!, Waypoint(), "NO WAYPOINT"))
    {
        Block = static () => Away ?? (HasPin ? null : "NO WAYPOINT"),
    };

    public static readonly Command ToObjective = new(
        "teleport.waypoint.to_objective", "TELEPORT TO OBJECTIVE",
        "Takes you to the current mission marker. For one inside a building you land at its way in.",
        () => Go(ToObjective!, Objective(), "NO MARKER"))
    {
        Block = static () => Away ?? (HasObjective ? null : "NO MARKER"),
    };

    public static readonly Command GoBack = new(
        "teleport.waypoint.go_back", "GO BACK",
        "Takes you back to where you were before the last teleport.",
        () => Go(GoBack!, _back is { } back ? (back, true) : null, "NOWHERE YET"))
    {
        Block = static () => Away ?? (_back is null ? "NOWHERE YET" : null),
    };

    public static readonly Slider NoFadeRange = new(
        "teleport.waypoint.no_fade_range", "NO FADE RANGE",
        "A teleport shorter than this, onto ground that's already there, happens straight away with no fade.",
        min: 0, max: 500, step: 25, value: 100, unit: " M", zero: "OFF")
    {
        Preference = true,
    };

    public static readonly Slider ArrivalHeight = new(
        "teleport.waypoint.arrival_height", "ARRIVAL HEIGHT",
        "Arrive this far above the ground and drop in, ready to glide. Under a roof you arrive on the floor. The Batmobile always lands on its wheels.",
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

    public static RGameRI? Ri => Game.TryGetWorldInfo(out var world) && world.GRI is RGameRI { IsValid: true } ri ? ri : null;

    static Outcome Go(Command command, (Vector3 At, bool Exact)? target, string missing) =>
        target is { } goal ? Start(command, goal.At, goal.Exact) : Outcome.Failed(missing);

    // The map waypoint. A side mission with no map icon sets a hidden one far outside the city,
    // which isn't somewhere to go.
    public static (Vector3 At, bool Exact)? Waypoint() =>
        World.Info?.PersistentData is { IsValid: true } data && data.CustomWaypointName.ToString() == "HiddenFake" ? null : Markers.Waypoint();

    // The game's own way to a marker. For one inside a building that is its entrance.
    static (Vector3 At, bool Exact)? Objective()
    {
        if (Markers.Objective() is not { } marker)
        {
            return null;
        }

        // Without a player Start says so.
        return World.Controller is { } controller && Mover is { } mover
            ? (controller.GetOverworldNavigationTarget(marker.At, mover.Location), marker.Exact)
            : marker;
    }

    // The city's shift from where the map has it. Only interiors move it. Places from the map add
    // it, and places kept for later take it off.
    public static Vector3 CityOffset => World.Controller is { } controller ? controller.GetCurrentCityXOffset() : Vector3.Zero;

    // Interiors like the GCPD are separate maps that only the game's own doors leave. The game
    // asks the same before it shows the waypoint.
    static string? Outside(RGameRI ri) => !World.InCity ? "NOT IN THE CITY" : !ri.IsOverworldGameplay() ? "INDOORS" : null;

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

        if (Outside(ri) is { } away)
        {
            return Outcome.Failed(away);
        }

        var car = mover as RVehicleBatmobileBase;
        var volumes = info.LevelVolumeList;
        var there = District(ri, goal.At, goal.Exact, car is not null);
        if (there < 0 || there >= volumes.Count)
        {
            return Outcome.Failed("NOT IN THE CITY");
        }

        // The district's Batmobile flags are only about where the car can be called to, so they
        // aren't asked here.
        var district = volumes[there];

        // Batman stays where he is while the car is driven by remote, and his district has to
        // stay loaded. The game's own rescue doesn't change the level then either.
        if (car is { bRemoteDrive: true, bFakeRemoteDrive: false } && !district.IsVisible)
        {
            return Outcome.Failed("REMOTE CONTROL");
        }

        _by = command;
        _mover = mover;
        _target = goal.At;
        _exact = goal.Exact;
        _level = district.LevelName;
        _leftLevel = ri.CurrentLevel;
        _leftVolume = ri.CurrentLevelVolume;
        _from = mover.Location;

        var flat = new Vector2(_target.X - _from.X, _target.Y - _from.Y).Length();
        if (district.IsVisible && flat <= NoFadeRange.Value * UnitsPerMetre && Place() is { } placed)
        {
            return placed;
        }

        ri.SetFadeType(false, FadeOut, false, false, 0f, 0f, 0f, 0f);
        Enter(Stage.Darkening);
        return Outcome.Unchanged("GOING");
    }

    // A short move inside ground that's already loaded, made straight away. On the floor stands
    // the mover on that point. Otherwise the point is where the player's middle goes, and they
    // keep falling or gliding as before. Keeping motion leaves the player's pose and speed alone.
    public static Outcome Hop(Pawn mover, Vector3 at, Rotator facing, bool onFloor, bool keepMotion = false)
    {
        if (Refusal(mover) is { } refused)
        {
            return refused;
        }

        var from = mover.Location;
        var velocity = mover.Velocity;
        var heading = Flat(mover.Rotation.ToDirection());
        if (!Put(mover, onFloor ? at + new Vector3(0f, 0f, Lift(mover)) : at, facing, !keepMotion))
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

    // The floors round a point that pass the test, ring by ring going out. Rings are in metres.
    // Each spot is looked at from the sky, so what's found there is always out in the open.
    public static IEnumerable<Vector3> FloorsAround(Actor tracer, Vector3 centre, float[] rings, int spokes, Func<Vector3, bool> test)
    {
        foreach (var ring in rings)
        {
            for (var i = 0; i < spokes; i++)
            {
                var angle = MathF.Tau * i / spokes;
                var at = centre + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * ring * UnitsPerMetre;
                if (Floor(tracer, at, Sky, Deep, out var floor) && test(floor))
                {
                    yield return floor;
                }
            }
        }
    }

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

        if (controller.bCinematicMode
            || mover is RVehicleBatmobileBase { bControlledBySeqAct: true } or RVehicleBatmobileBase { MatineeControlRefCount: not 0 }
            || (mover is RPawnCharacter character && character.IsControlledByMatinee()))
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
        // A new world took the trip's player, fade and level with it.
        if (_epoch != World.Epoch)
        {
            _epoch = World.Epoch;
            _back = null;
            if (_stage != Stage.Idle)
            {
                _stage = Stage.Idle;
                _held = false;
                _mover = null;
                _by = null;
                _leftVolume = null;
            }
        }

        Look();
        if (_stage == Stage.Idle)
        {
            return;
        }

        var waited = Seconds(_stageAt);
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
                    Fail(ri, Outcome.Failed("LOST"));
                }
                else if (!ri.IsLevelVisible(_level, false))
                {
                    if (waited > LoadSeconds)
                    {
                        Log.Info($"Teleport gave up waiting for {_level} to load.");
                        Fail(ri, Outcome.Failed("DIDN'T LOAD"));
                    }
                }
                else
                {
                    Arrive(ri);
                }

                break;

            case Stage.Brightening when waited >= FadeBack:
                _stage = Stage.Idle;
                break;
        }
    }

    // The district shows. Its floor may take a moment longer.
    static void Arrive(RGameRI ri)
    {
        var first = _visibleAt == 0;
        if (first)
        {
            _visibleAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        var since = Seconds(_visibleAt);
        if (Place() is not { } placed)
        {
            if (since > FloorSeconds)
            {
                Log.Info($"Teleport found no floor in {_level} {FloorSeconds:0.#} s after it showed.");
                Fail(ri, Outcome.Failed("NO GROUND"));
            }

            return;
        }

        if (placed.Kind != OutcomeKind.Done)
        {
            Fail(ri, placed);
            return;
        }

        if (!first)
        {
            Log.Info($"Teleport found the floor in {_level} {since:0.00} s after it showed.");
        }

        Finish(placed);
    }

    static float Seconds(long since) => (float)System.Diagnostics.Stopwatch.GetElapsedTime(since).TotalSeconds;

    // What the rows' locks show. Looked at a few times a second, since a lock is asked every frame.
    static void Look()
    {
        if (MathF.Abs(World.Now - _lookedAt) < LookEvery)
        {
            return;
        }

        _lookedAt = World.Now;
        Away = Ri is { } ri ? Outside(ri) : null;
        HasPin = Waypoint() is not null;
        HasObjective = Markers.Objective() is not null;
        HasCar = Car.Find() is not null;
    }

    static void Enter(Stage stage)
    {
        _stage = stage;
        _stageAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _visibleAt = 0;
    }

    // A trip that can't end where it was going puts back the level the player left. Changing the
    // level empties the current level volume. The player never left that volume, so the game
    // doesn't fill it again, and without it every district lookup fails.
    static void Fail(RGameRI ri, Outcome outcome)
    {
        PutBack(ri);
        Finish(outcome);
    }

    static void PutBack(RGameRI ri)
    {
        if (World.Controller is not { } owner)
        {
            return;
        }

        ri.SetCurrentLevel(_leftLevel, owner, true);
        if (_leftVolume is { IsValid: true } volume && ri.CurrentLevelVolume is null)
        {
            ri.CurrentLevelVolume = volume;
        }
    }

    static void Finish(Outcome outcome)
    {
        Let();
        Ri?.SetFadeType(true, FadeBack, false, false, 0f, 0f, 0f, 0f);
        Enter(Stage.Brightening);
        _by?.Report(outcome);
        _mover = null;
        _leftVolume = null;
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

    // Finds the floor and moves the player there. Null while the ground isn't loaded yet.
    static Outcome? Place()
    {
        if (_mover is not { IsValid: true } mover)
        {
            return Outcome.Failed("LOST");
        }

        if (!Ground(mover, _target, _exact, out var floor))
        {
            return null;
        }

        if (!Dry(floor))
        {
            if (_exact || !Shore(mover, out floor))
            {
                return Outcome.Failed("WATER");
            }
        }

        var lift = Lift(mover);
        if (mover is not RVehicleBatmobileBase && ArrivalHeight.Value > 0)
        {
            var high = lift + (float)ArrivalHeight.Value * UnitsPerMetre;
            if (mover.FastTrace(floor + new Vector3(0f, 0f, high), floor + new Vector3(0f, 0f, lift), Vector3.Zero, false))
            {
                lift = high;
            }
        }

        var way = new Vector3(_target.X - _from.X, _target.Y - _from.Y, 0f);
        var facing = FaceTravel.On && way.LengthSquared() > 1f ? Rotator.FromDirection(way) : new Rotator(0f, mover.Rotation.Yaw, 0f);
        if (!Put(mover, floor + new Vector3(0f, 0f, lift), facing, true))
        {
            return Outcome.Failed("NO ROOM");
        }

        _back = _from;
        Let();

        // What the game does after each of its own teleports on foot. The car tidies up itself.
        if (mover is not RVehicle && World.Controller is { } controller)
        {
            mover.Velocity = Vector3.Zero;
            (controller.PlayerCamera as R3rdPersonCamera)?.ResetCamera(default, true, true);
            controller.OnPlayerHasBeenMoved();
        }

        return Outcome.Done("ARRIVED");
    }

    static bool Shore(Pawn mover, out Vector3 floor)
    {
        foreach (var dry in FloorsAround(mover, _target, ShoreRings, Spokes, Dry))
        {
            floor = dry;
            return true;
        }

        floor = default;
        return false;
    }

    // Somewhere a pawn doesn't fit refuses the move, so a little higher is tried. The car is
    // always put where it's sent.
    static bool Put(Pawn mover, Vector3 point, Rotator facing, bool stop)
    {
        _facing = facing;
        var action = Action(stop);
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
    // game holds on to it. Stopping all movement also stands the player up.
    static SeqAct_Teleport Action(bool stop)
    {
        if (_action is not { IsValid: true })
        {
            _action = Borrowed.Keep(GameObject.ConstructObject<SeqAct_Teleport>(), Lender);
        }

        _action.bUpdateRotation = true;
        _action.bStopAllMovement = stop;
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
            if (_stage == Stage.Loading && Ri is { } ri)
            {
                PutBack(ri);
            }

            Let();
            Ri?.SetFadeType(true, FadeBack, false, false, 0f, 0f, 0f, 0f);
            _stage = Stage.Idle;
        }

        _mover = null;
        _leftVolume = null;
        _action = null;
        Borrowed.Let(Lender);
    }
}
