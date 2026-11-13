#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// EDITOR > MOVE. Puts the selected object somewhere new, steps it about, or carries it in front
// of the camera. Everything here is noted first, so UNDO and RESTORE can put it back.
static class Moving
{
    const float Metre = Teleport.UnitsPerMetre;
    const float Reach = 30000f;
    const int Layers = 8;
    const float StepPast = 60f;

    // Room left between you and what you bring or go to.
    const float Gap = 150f;

    const float GrabNearest = 150f;
    const float ThrowFastest = 4000f;

    // A surface at least this upright is a floor, anything steeper a wall.
    const float Floorish = 0.7f;

    const float FloorReach = 5000f;

    static bool _holding;
    static Actor? _held;
    static Vector3 _offset;
    static float _distance;
    static float _yawOffset;
    static float _heldPitch;
    static float _heldRoll;
    static Vector3 _last;
    static float _lastAt;
    static Vector3 _velocity;
    static object? _paused;

    // Set once what's held is pushed or pulled, which then wins over GRAB DISTANCE.
    static bool _reached;

    // How far the step rows have shifted what's held off the middle of the view, sideways and up.
    static float _side;
    static float _rise;

    // What GRAB is carrying, if anything.
    public static Actor? Held => _holding ? _held : null;

    // What the rows that move the selection show while it can't be moved. It sits above the rows,
    // which take it as they are made.
    static readonly Func<string?> Unmovable = static () => Stuck(Editor.Selected);

    public static readonly Command GoTo = new(
        "editor.move.go_to", "GO TO IT",
        "Puts you beside the selected object, facing it.",
        GoToSelected)
    {
        Block = static () => Editor.Selected is null ? "NOTHING SELECTED" : null,
    };

    public static readonly Command Bring = new(
        "editor.move.bring", "BRING IT TO ME",
        "Puts the selected object on the ground in front of you.",
        BringSelected)
    {
        Block = Unmovable,
    };

    public static readonly Command AtCrosshair = new(
        "editor.move.crosshair", "PLACE AT CROSSHAIR",
        "Puts the selected object where you're aiming. It stands on a floor, sits against a wall or hangs under a ceiling.",
        PlaceAtCrosshair)
    {
        Block = Unmovable,
    };

    public static readonly Command Drop = new(
        "editor.move.drop", "DROP TO GROUND",
        "Sets the selected object down on the floor beneath it.",
        DropSelected)
    {
        Block = Unmovable,
    };

    public static readonly Toggle Grab = new(
        "editor.move.grab", "GRAB",
        "The selected object floats in front of the camera and follows your view. The STEP and TURN rows shift it while you hold it. Turn it off to let go, and turn quickly as you do to throw it.",
        saved: false)
    {
        Block = Unmovable,
    };

    public static readonly Slider GrabDistance = new(
        "editor.move.grab_distance", "GRAB DISTANCE",
        "How far in front of the camera a grabbed object floats. KEEP holds it as far away as it was.",
        min: 0, max: 30, step: 1, value: 0, unit: " M", zero: "KEEP")
    {
        Preference = true,
    };

    public static readonly Slider Forward = Step("forward", "MOVE FORWARD", "Moves the selected object the way the camera looks, or back toward it below zero.");
    public static readonly Slider Right = Step("right", "MOVE RIGHT", "Moves the selected object to the camera's right, or left below zero.");
    public static readonly Slider Up = Step("up", "MOVE UP", "Lifts the selected object, or lowers it below zero.");
    public static readonly Slider Turn = Angle("turn", "TURN", "Turns the selected object left or right.");
    public static readonly Slider Tilt = Angle("tilt", "TILT", "Tips the selected object forward or back.");
    public static readonly Slider Roll = Angle("roll", "ROLL", "Rolls the selected object onto its side.");

    public static readonly Command Upright = new(
        "editor.move.upright", "STAND UPRIGHT",
        "Takes the tilt and roll off the selected object, so it stands straight. Its turn stays.",
        StandUpright)
    {
        Block = Unmovable,
    };

    static readonly Slider[] Steps = { Forward, Right, Up, Turn, Tilt, Roll };
    static readonly double[] Shown = new double[6];

    public static readonly Catalog.Section[] Sections =
    {
        new("PUT", GoTo, Bring, AtCrosshair, Drop),
        new("GRAB", Grab, GrabDistance),
        new("STEP", Forward, Right, Up),
        new("TURN", Turn, Tilt, Roll, Upright),
        new("UNDO", Editor.Undo, Editor.RestoreSelected),
    };

    // The STEP and TURN rows show how far they have taken the selected object since it was first
    // touched. A turn goes all the way round.
    static Slider Step(string id, string label, string help) =>
        new("editor.move." + id, label, help + " Each step is a quarter of a metre.", min: -50, max: 50, step: 0.25, value: 0, unit: " M", saved: false)
        {
            Preference = true,
            Block = Unmovable,
        };

    static Slider Angle(string id, string label, string help) =>
        new("editor.move." + id, label, help, min: -180, max: 180, step: 15, value: 0, unit: " DEG", saved: false)
        {
            Preference = true,
            Wraps = true,
            Block = Unmovable,
        };

    // A new selection, or none. The step rows show its totals and any grab lets go.
    public static void Selected(Actor? actor)
    {
        LetGo();
        if (Grab.On)
        {
            Grab.Reset();
        }

        Reshow(actor);
        Change.Selected(actor);
    }

    // The step rows catch up with what's been done to it.
    public static void Reshow(Actor? actor)
    {
        var edit = actor is null ? null : Edits.Find(actor);
        Span<float> totals = stackalloc float[6];
        if (edit is not null)
        {
            (totals[0], totals[1], totals[2]) = (edit.Moved.X, edit.Moved.Y, edit.Moved.Z);
            (totals[3], totals[4], totals[5]) = (edit.Turned.X, edit.Turned.Y, edit.Turned.Z);
        }

        for (var i = 0; i < Steps.Length; i++)
        {
            Steps[i].Show(totals[i]);
            Shown[i] = Steps[i].Value;
        }
    }

    // Why the selection can't be moved, or null when it can.
    public static string? Stuck(Actor? actor)
    {
        if (actor is null)
        {
            return "NOTHING SELECTED";
        }

        return Things.Fixed(actor) ?? (Edits.Find(actor) is { Removed: true } ? "IT'S REMOVED" : null);
    }

    public static void Tick()
    {
        var actor = Editor.Selected;
        for (var i = 0; i < Steps.Length; i++)
        {
            var value = Steps[i].Value;
            if (value == Shown[i])
            {
                continue;
            }

            // A row that can't act goes back to what it showed, and says why.
            if (Stuck(actor) is { } why)
            {
                Steps[i].Show(Shown[i]);
                Toasts.Show(Steps[i].Plain, why, OutcomeKind.Failed, Steps[i]);
                continue;
            }

            if (World.Controller is not { } controller)
            {
                Steps[i].Show(Shown[i]);
                continue;
            }

            var change = (float)(value - Shown[i]);
            Shown[i] = value;
            if (i < 3)
            {
                controller.GetPlayerViewPoint(out _, out var view);
                Edits.Touch(actor!, "MOVE");
                Shift(actor!, i, change, view);
            }
            else
            {
                Edits.Touch(actor!, "TURN");
                Spin(actor!, i, change);
            }
        }

        // A pause holds everything where it is.
        if (World.Paused)
        {
            return;
        }

        if (Grab.On)
        {
            Carry();
        }
        else if (_holding)
        {
            LetGo(throwIt: true);
        }
    }

    static bool Carrying(Actor actor) => _holding && ReferenceEquals(_held, actor);

    // One of the three STEP rows. What's being carried is placed from the view each tick, so
    // there a step pushes it away or shifts where it's held.
    static void Shift(Actor actor, int row, float change, Rotator view)
    {
        var edit = Edits.Begin(actor);
        switch (row)
        {
            case 0:
                edit.Moved.X += change;
                break;
            case 1:
                edit.Moved.Y += change;
                break;
            default:
                edit.Moved.Z += change;
                break;
        }

        if (Carrying(actor))
        {
            switch (row)
            {
                case 0:
                    Push(change * Metre);
                    break;
                case 1:
                    _side += change * Metre;
                    break;
                default:
                    _rise += change * Metre;
                    break;
            }

            return;
        }

        var ahead = Teleport.Flat(view.ToDirection());
        var way = row switch
        {
            0 => ahead,
            1 => new Vector3(-ahead.Y, ahead.X, 0f),
            _ => Vector3.UnitZ,
        };
        Edits.Put(actor, actor.Location + way * change * Metre, actor.Rotation);
    }

    // One of the three TURN rows, by its place in the list. What's being carried keeps the turn
    // it's given, since the carry sets its facing each tick.
    static void Spin(Actor actor, int row, float change)
    {
        var edit = Edits.Begin(actor);
        switch (row)
        {
            case 3:
                edit.Turned.X = Wrap(edit.Turned.X + change);
                break;
            case 4:
                edit.Turned.Y = Wrap(edit.Turned.Y + change);
                break;
            default:
                edit.Turned.Z = Wrap(edit.Turned.Z + change);
                break;
        }

        if (Carrying(actor))
        {
            switch (row)
            {
                case 3:
                    _yawOffset += change;
                    break;
                case 4:
                    _heldPitch += change;
                    break;
                default:
                    _heldRoll += change;
                    break;
            }

            return;
        }

        var turn = actor.Rotation;
        turn = row switch
        {
            3 => new Rotator(turn.Pitch, turn.Yaw + change, turn.Roll),
            4 => new Rotator(turn.Pitch + change, turn.Yaw, turn.Roll),
            _ => new Rotator(turn.Pitch, turn.Yaw, turn.Roll + change),
        };
        Edits.Put(actor, actor.Location, turn);
    }

    static float Wrap(float degrees)
    {
        degrees %= 360f;
        return degrees > 180f ? degrees - 360f : degrees <= -180f ? degrees + 360f : degrees;
    }

    // Held where the camera looks. People are paused while held so their AI doesn't fight it.
    static void Carry()
    {
        var actor = Editor.Selected;
        if (Stuck(actor) is { } why)
        {
            Toasts.Show(Grab.Plain, why, OutcomeKind.Failed, Grab);
            Grab.Reset();
            return;
        }

        if (World.Controller is not { } controller)
        {
            Grab.Reset();
            return;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        if (!Carrying(actor!))
        {
            LetGo();
            Edits.Touch(actor!, "GRAB");
            Things.Box(actor!, out var middle, out _);
            _offset = middle - actor!.Location;
            _distance = MathF.Max(GrabNearest, Vector3.Distance(eye, middle));
            _yawOffset = actor.Rotation.Yaw - view.Yaw;
            _heldPitch = actor.Rotation.Pitch;
            _heldRoll = actor.Rotation.Roll;
            _velocity = Vector3.Zero;
            _last = actor.Location;
            _lastAt = World.Now;
            _paused = actor is RBMPawnAI body ? Freezer.Pause(body) : null;
            _reached = false;
            (_side, _rise) = (0f, 0f);
            _held = actor;
            _holding = true;
        }

        var distance = !_reached && GrabDistance.Value > 0 ? (float)GrabDistance.Value * Metre : _distance;
        var ahead = Teleport.Flat(view.ToDirection());
        var at = eye + view.ToDirection() * distance + new Vector3(-ahead.Y, ahead.X, 0f) * _side + Vector3.UnitZ * _rise - _offset;
        var seconds = World.Now - _lastAt;
        if (seconds > 0f)
        {
            _velocity = Vector3.Lerp(_velocity, (at - _last) / seconds, 0.35f);
        }

        _last = at;
        _lastAt = World.Now;
        Edits.Put(actor!, at, new Rotator(_heldPitch, view.Yaw + _yawOffset, _heldRoll));
    }

    // Pushes what's held further from the camera, or pulls it in below zero.
    public static void Push(float change)
    {
        if (!_holding)
        {
            return;
        }

        if (!_reached && GrabDistance.Value > 0)
        {
            _distance = (float)GrabDistance.Value * Metre;
        }

        _reached = true;
        _distance = MathF.Max(GrabNearest, _distance + change);
    }

    // Lets go of anything held. A throw carries on at the speed it was being swung.
    public static void LetGo(bool throwIt = false)
    {
        if (!_holding)
        {
            return;
        }

        _holding = false;
        var held = _held;
        _held = null;
        if (_paused is { } paused)
        {
            Freezer.Unpause(paused);
            _paused = null;
        }

        if (!Things.Alive(held))
        {
            return;
        }

        if (Edits.Find(held!) is { Still: true })
        {
            return;
        }

        var velocity = throwIt ? _velocity : Vector3.Zero;
        if (velocity.Length() > ThrowFastest)
        {
            velocity = Vector3.Normalize(velocity) * ThrowFastest;
        }

        Edits.Throw(held!, velocity);
    }

    // How far its pivot is above the bottom of its box.
    static float Lift(Actor actor)
    {
        Things.Box(actor, out var middle, out var half);
        return actor.Location.Z - (middle.Z - half.Z);
    }

    static Outcome Movable(out Actor actor)
    {
        actor = Editor.Selected!;
        return Stuck(actor) is { } why ? Outcome.Failed(why) : Outcome.Done();
    }

    // The carry would take it straight back, so a grab lets go first.
    static Outcome Moved(Actor actor, Vector3 at)
    {
        if (Carrying(actor))
        {
            LetGo();
            Grab.Reset();
        }

        Edits.Touch(actor, "MOVE");
        Edits.Put(actor, at, actor.Rotation);
        return Outcome.Done("MOVED");
    }

    // Lifts it clear and rolls it over, for FLIP OVER.
    public static void Flip(Actor actor, float lift)
    {
        Edits.Touch(actor, "FLIP");
        if (!Carrying(actor))
        {
            Edits.Put(actor, actor.Location + Vector3.UnitZ * lift, actor.Rotation);
        }

        Spin(actor, 5, 180f);
        Reshow(actor);
    }

    static Outcome StandUpright()
    {
        if (Movable(out var actor) is { Kind: OutcomeKind.Failed } refused)
        {
            return refused;
        }

        var carried = Carrying(actor);
        var pitch = Wrap(carried ? _heldPitch : actor.Rotation.Pitch);
        var roll = Wrap(carried ? _heldRoll : actor.Rotation.Roll);
        if (MathF.Abs(pitch) < 0.5f && MathF.Abs(roll) < 0.5f)
        {
            return Outcome.Unchanged("ALREADY UPRIGHT");
        }

        Edits.Touch(actor, "TURN");
        Spin(actor, 4, -pitch);
        Spin(actor, 5, -roll);
        Reshow(actor);
        return Outcome.Done("UPRIGHT");
    }

    static Outcome GoToSelected()
    {
        if (Editor.Selected is not { } actor)
        {
            return Outcome.Failed("NOTHING SELECTED");
        }

        if (Teleport.Mover is not { } mover)
        {
            return Outcome.NoPlayer;
        }

        Things.Box(actor, out var middle, out var half);
        var toward = Teleport.Flat(middle - mover.Location);
        var beside = middle - toward * (MathF.Max(half.X, half.Y) + Gap);
        var facing = new Rotator(0f, MathF.Atan2(toward.Y, toward.X) * 180f / MathF.PI, 0f);
        return Teleport.Floor(mover, beside, beside.Z + 500f, beside.Z - FloorReach, out var floor)
            ? Teleport.Hop(mover, floor, facing, true)
            : Teleport.Hop(mover, beside, facing, false);
    }

    static Outcome BringSelected()
    {
        if (Movable(out var actor) is { Kind: OutcomeKind.Failed } refused)
        {
            return refused;
        }

        if (Teleport.Mover is not { } mover)
        {
            return Outcome.NoPlayer;
        }

        Things.Box(actor, out var middle, out var half);
        var ahead = Teleport.Flat(mover.Rotation.ToDirection());
        var spot = mover.Location + ahead * (MathF.Max(half.X, half.Y) + Gap + 100f);
        if (!Teleport.Floor(mover, spot, spot.Z + 300f, spot.Z - FloorReach, out var floor))
        {
            return Outcome.Failed("NO GROUND THERE");
        }

        var across = middle - actor.Location;
        return Moved(actor, new Vector3(floor.X - across.X, floor.Y - across.Y, floor.Z + Lift(actor) + 2f));
    }

    static Outcome PlaceAtCrosshair()
    {
        if (Movable(out var actor) is { Kind: OutcomeKind.Failed } refused)
        {
            return refused;
        }

        if (World.Controller is not { } controller || Teleport.Mover is not { } mover)
        {
            return Outcome.NoPlayer;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        var way = view.ToDirection();
        var start = eye;
        for (var i = 0; i < Layers; i++)
        {
            var hit = mover.Trace(out var spot, out var normal, eye + way * Reach, start, Component.ECollisionFilter.COF_TraceWorld, Vector3.Zero, out _, 0);
            if (hit is null)
            {
                break;
            }

            if (hit is Volume || ReferenceEquals(hit, actor) || ReferenceEquals(hit, World.Player) || ReferenceEquals(hit, World.Driving))
            {
                start = spot + way * StepPast;
                continue;
            }

            Things.Box(actor, out var middle, out var half);
            var across = middle - actor.Location;
            if (normal.Z >= Floorish)
            {
                return Moved(actor, new Vector3(spot.X - across.X, spot.Y - across.Y, spot.Z + Lift(actor) + 2f));
            }

            // Against a wall or under a ceiling, by how far its box reaches that way.
            return Moved(actor, spot + normal * (Vector3.Dot(Vector3.Abs(normal), half) + 5f) - across);
        }

        return Outcome.Failed("AIM AT SOMETHING");
    }

    static Outcome DropSelected()
    {
        if (Movable(out var actor) is { Kind: OutcomeKind.Failed } refused)
        {
            return refused;
        }

        // Looked for from its middle down, so one sunk into the ground comes back up onto it.
        Things.Box(actor, out var middle, out var half);
        var bottom = middle.Z - half.Z;
        if (!Teleport.Floor(actor, middle, middle.Z, bottom - FloorReach, out var floor))
        {
            return Outcome.Failed("NO GROUND BELOW");
        }

        return Moved(actor, actor.Location with { Z = floor.Z + Lift(actor) + 2f });
    }
}
