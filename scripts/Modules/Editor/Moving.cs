#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// EDITOR > MOVE. Puts the selected object somewhere new, steps it about, or carries it in front
// of the camera. Everything here is noted first, so RESTORE can put it back.
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

    // Set once EDITOR MODE pushes or pulls what's held, which then wins over GRAB DISTANCE.
    static bool _reached;

    // What GRAB is carrying, if anything.
    public static Actor? Held => _holding ? _held : null;

    public static readonly Command GoTo = new(
        "editor.move.go_to", "GO TO IT",
        "Puts you beside the selected object, facing it.",
        GoToSelected);

    public static readonly Command Bring = new(
        "editor.move.bring", "BRING IT TO ME",
        "Puts the selected object on the ground in front of you.",
        BringSelected);

    public static readonly Command AtCrosshair = new(
        "editor.move.crosshair", "PLACE AT CROSSHAIR",
        "Puts the selected object where you're aiming, standing on the floor there or against the wall.",
        PlaceAtCrosshair);

    public static readonly Command Drop = new(
        "editor.move.drop", "DROP TO GROUND",
        "Sets the selected object down on the floor beneath it.",
        DropSelected);

    public static readonly Toggle Grab = new(
        "editor.move.grab", "GRAB",
        "The selected object floats in front of the camera and follows your view. Turn it off to let go, and turn quickly as you do to throw it.",
        saved: false);

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
    public static readonly Slider Turn = Spin("turn", "TURN", "Turns the selected object left or right.");
    public static readonly Slider Tilt = Spin("tilt", "TILT", "Tips the selected object forward or back.");
    public static readonly Slider Roll = Spin("roll", "ROLL", "Rolls the selected object onto its side.");

    static readonly Slider[] Steps = { Forward, Right, Up, Turn, Tilt, Roll };
    static readonly double[] Shown = new double[6];

    public static readonly Catalog.Section[] Sections =
    {
        new("PUT", GoTo, Bring, AtCrosshair, Drop),
        new("GRAB", Grab, GrabDistance),
        new("STEP", Forward, Right, Up),
        new("TURN", Turn, Tilt, Roll),
        new("RESTORE", Editor.RestoreSelected),
    };

    // The MOVE and TURN rows show how far the selected object has gone since it was first touched.
    static Slider Step(string id, string label, string help) =>
        new("editor.move." + id, label, help + " Each step is a quarter of a metre.", min: -50, max: 50, step: 0.25, value: 0, unit: " M", saved: false)
        {
            Preference = true,
        };

    static Slider Spin(string id, string label, string help) =>
        new("editor.move." + id, label, help, min: -180, max: 180, step: 15, value: 0, unit: " DEG", saved: false)
        {
            Preference = true,
        };

    // A new selection, or none. The step rows show its totals and any grab lets go.
    public static void Selected(Actor? actor)
    {
        LetGo();
        if (Grab.On)
        {
            Grab.Reset();
        }

        var edit = actor is null ? null : Edits.Find(actor);
        double[] totals = edit is null
            ? new double[6]
            : new double[] { edit.Moved.X, edit.Moved.Y, edit.Moved.Z, edit.Turned.X, edit.Turned.Y, edit.Turned.Z };
        for (var i = 0; i < Steps.Length; i++)
        {
            Steps[i].Show(totals[i]);
            Shown[i] = Steps[i].Value;
        }

        Change.Selected(actor);
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

            if (actor is null || Things.Fixed(actor) is not null || Edits.Find(actor) is { Removed: true }
                || World.Controller is not { } controller)
            {
                Steps[i].Show(Shown[i]);
                continue;
            }

            var change = (float)(value - Shown[i]);
            Shown[i] = value;
            controller.GetPlayerViewPoint(out _, out var view);
            Nudge(actor, i, change, view);
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

    static void Nudge(Actor actor, int row, float change, Rotator view)
    {
        var edit = Edits.Begin(actor);
        edit.Changed = true;

        // What's being carried keeps the turn it's given, since the carry sets its facing each tick.
        if (_holding && ReferenceEquals(_held, actor) && row >= 3)
        {
            switch (row)
            {
                case 3:
                    _yawOffset += change;
                    edit.Turned.X += change;
                    break;
                case 4:
                    _heldPitch += change;
                    edit.Turned.Y += change;
                    break;
                default:
                    _heldRoll += change;
                    edit.Turned.Z += change;
                    break;
            }

            return;
        }

        var ahead = Teleport.Flat(view.ToDirection());
        var right = new Vector3(-ahead.Y, ahead.X, 0f);
        var at = actor.Location;
        var turn = actor.Rotation;
        switch (row)
        {
            case 0:
                at += ahead * change * Metre;
                edit.Moved.X += change;
                break;
            case 1:
                at += right * change * Metre;
                edit.Moved.Y += change;
                break;
            case 2:
                at += Vector3.UnitZ * change * Metre;
                edit.Moved.Z += change;
                break;
            case 3:
                turn = new Rotator(turn.Pitch, turn.Yaw + change, turn.Roll);
                edit.Turned.X += change;
                break;
            case 4:
                turn = new Rotator(turn.Pitch + change, turn.Yaw, turn.Roll);
                edit.Turned.Y += change;
                break;
            default:
                turn = new Rotator(turn.Pitch, turn.Yaw, turn.Roll + change);
                edit.Turned.Z += change;
                break;
        }

        Edits.Put(actor, at, turn);
    }

    // Held where the camera looks. People are paused while held so their AI doesn't fight it.
    static void Carry()
    {
        if (Editor.Selected is not { } actor || Things.Fixed(actor) is not null || Edits.Find(actor) is { Removed: true }
            || World.Controller is not { } controller)
        {
            Grab.Reset();
            return;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        if (!_holding || !ReferenceEquals(_held, actor))
        {
            LetGo();
            Things.Box(actor, out var middle, out _);
            _offset = middle - actor.Location;
            _distance = MathF.Max(GrabNearest, Vector3.Distance(eye, middle));
            _yawOffset = actor.Rotation.Yaw - view.Yaw;
            _heldPitch = actor.Rotation.Pitch;
            _heldRoll = actor.Rotation.Roll;
            _velocity = Vector3.Zero;
            _last = actor.Location;
            _lastAt = World.Now;
            Edits.Begin(actor).Changed = true;
            _paused = actor is RBMPawnAI body ? Freezer.Pause(body) : null;
            _reached = false;
            _held = actor;
            _holding = true;
        }

        var distance = !_reached && GrabDistance.Value > 0 ? (float)GrabDistance.Value * Metre : _distance;
        var at = eye + view.ToDirection() * distance - _offset;
        var seconds = World.Now - _lastAt;
        if (seconds > 0f)
        {
            _velocity = Vector3.Lerp(_velocity, (at - _last) / seconds, 0.35f);
        }

        _last = at;
        _lastAt = World.Now;
        Edits.Put(actor, at, new Rotator(_heldPitch, view.Yaw + _yawOffset, _heldRoll));
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
        actor = null!;
        if (Editor.Selected is not { } selected)
        {
            return Outcome.Failed("NOTHING SELECTED");
        }

        if (Things.Fixed(selected) is { } why)
        {
            return Outcome.Failed(why);
        }

        if (Edits.Find(selected) is { Removed: true })
        {
            return Outcome.Failed("IT'S REMOVED");
        }

        actor = selected;
        return Outcome.Done();
    }

    static Outcome Moved(Actor actor, Vector3 at)
    {
        Edits.Begin(actor).Changed = true;
        Edits.Put(actor, at, actor.Rotation);
        return Outcome.Done("MOVED");
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

            return Moved(actor, spot + normal * (MathF.Max(half.X, half.Y) + 5f) - across);
        }

        return Outcome.Failed("AIM AT SOMETHING");
    }

    static Outcome DropSelected()
    {
        if (Movable(out var actor) is { Kind: OutcomeKind.Failed } refused)
        {
            return refused;
        }

        Things.Box(actor, out var middle, out var half);
        var bottom = middle.Z - half.Z;
        if (!Teleport.Floor(actor, middle, bottom + 20f, bottom - FloorReach, out var floor))
        {
            return Outcome.Failed("NO GROUND BELOW");
        }

        return Moved(actor, actor.Location with { Z = floor.Z + Lift(actor) + 2f });
    }
}
