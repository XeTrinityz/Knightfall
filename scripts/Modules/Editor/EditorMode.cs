#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// EDITOR > MODE. Edit the world from the free camera without the menu: fly about, click what you
// want and change it with single keys. Batman waits where he is, as with FREE CAMERA.
static class EditorMode
{
    const float Metre = Teleport.UnitsPerMetre;

    // Held longer than this, a click grabs instead of selecting.
    const float HoldToGrab = 0.2f;

    // How fast W and S, or the d-pad, push and pull what's held, per second.
    const float PushSpeed = 6f * Metre;

    const float SkyReach = 50000f;

    // Kept from the game while the mode is on. FREE CAMERA keeps the pad and its own fly keys.
    static readonly Keys[] EditKeys =
    {
        Keys.LButton, Keys.RButton, Keys.Q, Keys.E, Keys.Z, Keys.X, Keys.F, Keys.G, Keys.C, Keys.U, Keys.R, Keys.Delete, Keys.Back, Keys.W, Keys.S,
    };

    static readonly bool[] KeyTaken = new bool[EditKeys.Length];
    static readonly Dictionary<int, bool> Was = new();

    static bool _started;
    static bool _turnedFreeOn;
    static long _pressedAt;
    static bool _pressing;
    static bool _grabbing;
    static bool _turning;

    // A hold tries to grab once, when it has lasted long enough.
    static bool _grabTried;

    // A hold on a gyro handle only drags it once the view moves. Held still this long, it grabs
    // the object instead, since the handles cover most of a small object.
    static bool _turned;
    const float StillToGrab = 0.35f;
    static long _lastAt;

    static GuideLine[]? _keyboardGuide;
    static GuideLine[]? _padGuide;
    static Pad.Style _padGuideStyle;
    static readonly GuideLine KeyboardShow = new(new[] { "H" }, "SHOW CONTROLS");
    static GuideLine? _padShow;
    static Pad.Style _padShowStyle;

    public static readonly Toggle On = new(
        "editor.mode.on", "EDITOR MODE",
        "Edit from the free camera, without the menu. Batman waits where he is. The controls show on screen.",
        saved: false)
    {
        Tick = Hold,
        Restore = Stop,
    };

    public static readonly Toggle Land = new(
        "editor.mode.land", "LAND WHERE YOU FLEW",
        "When EDITOR MODE ends, Batman is put on the ground under the camera, instead of staying where he was.")
    {
        Preference = true,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("EDITOR MODE", On, FreeCamera.Speed, Land),
    };

    // Whether the mode is flying right now, for the reticle and the aim preview.
    public static bool Active => On.On && _started && FreeCamera.Flying;

    static void Hold()
    {
        if (!_started)
        {
            Start();
        }

        // FREE CAMERA switched off by hand ends the mode too.
        if (!FreeCamera.Free.On)
        {
            On.Step(-1);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var seconds = _lastAt == 0 ? 0f : MathF.Min((float)((now - _lastAt) / (double)Stopwatch.Frequency), 0.1f);
        _lastAt = now;
        if (Menu.Showing || World.Paused)
        {
            ForgetPresses();
            return;
        }

        if (!_turning)
        {
            Gyro.Look(Editor.Selected);
        }

        Click(Keyboard.IsDown(Keys.LButton) || Pad.IsDown(Pad.Button.A), now);
        if (_turning && Editor.Selected is { } dragged && World.Controller is { } controller)
        {
            Gyro.Drag(dragged, controller);
            _turned = Gyro.Committed;
        }
        if (Pressed(Keys.RButton, Pad.Button.B))
        {
            LetGoOfGrab();
            if (Editor.Selected is not null)
            {
                Editor.Select(null);
                Toasts.Show("EDITOR MODE", "SELECTION CLEARED", OutcomeKind.Unchanged);
            }
        }

        if (Pressed(Keys.Q, Pad.Button.LB))
        {
            Step(Moving.Turn, -1);
        }

        if (Pressed(Keys.E, Pad.Button.RB))
        {
            Step(Moving.Turn, 1);
        }

        // While holding something, W and S, or the d-pad, push and pull it.
        FreeCamera.HoldsForward = _grabbing;
        if (_grabbing)
        {
            var push = (Keyboard.IsDown(Keys.W) || Pad.IsDown(Pad.Button.DPadUp) ? 1f : 0f)
                - (Keyboard.IsDown(Keys.S) || Pad.IsDown(Pad.Button.DPadDown) ? 1f : 0f);
            Moving.Push(push * PushSpeed * seconds);
            Pressed(0, Pad.Button.DPadUp);
            Pressed(0, Pad.Button.DPadDown);

            // Up and down push and pull on the pad, so left and right size what's held.
            if (Pressed(0, Pad.Button.DPadLeft))
            {
                Size(-1);
            }

            if (Pressed(0, Pad.Button.DPadRight))
            {
                Size(1);
            }
        }
        else
        {
            if (Pressed(0, Pad.Button.DPadDown))
            {
                Size(-1);
            }

            if (Pressed(0, Pad.Button.DPadUp))
            {
                Size(1);
            }
        }

        if (Pressed(Keys.Z, 0))
        {
            Size(-1);
        }

        if (Pressed(Keys.X, 0))
        {
            Size(1);
        }

        if (Pressed(Keys.F, Pad.Button.X))
        {
            Flip(Change.Freeze);
        }

        if (Pressed(Keys.G, Pad.Button.Y))
        {
            Toasts.Run(Moving.Drop);
        }

        if (Pressed(Keys.C, 0))
        {
            Toasts.Run(Moving.AtCrosshair);
        }

        if (Pressed(Keys.U, 0))
        {
            Toasts.Run(Moving.Upright);
        }

        if (Pressed(Keys.Back, 0))
        {
            LetGoOfGrab();
            Toasts.Run(Editor.Undo);
        }

        if (Pressed(Keys.Delete, _grabbing ? 0 : Pad.Button.DPadLeft))
        {
            LetGoOfGrab();
            Flip(Change.Remove);
        }

        if (Pressed(Keys.R, _grabbing ? 0 : Pad.Button.DPadRight))
        {
            LetGoOfGrab();
            Toasts.Run(Editor.RestoreSelected);
        }
    }

    // A tap selects what's in the middle of the screen. Held, it grabs that, or the selection
    // when nothing is there, until it's let go.
    static void Click(bool down, long now)
    {
        if (down && !_pressing)
        {
            _pressing = true;
            _grabTried = false;
            _pressedAt = now;

            // On a gyro handle, the click drags the handle instead.
            if (Editor.Selected is { } selected && World.Controller is { } controller && Gyro.Begin(selected, controller))
            {
                _turning = true;
                _turned = false;
                FreeCamera.HoldsLook = true;
            }

            return;
        }

        if (_turning)
        {
            if (!down)
            {
                _pressing = false;
                StopTurning();
                return;
            }

            if (_turned || (now - _pressedAt) / (double)Stopwatch.Frequency < StillToGrab)
            {
                return;
            }

            // Held still on a handle: let it go and grab the object.
            _turning = false;
            FreeCamera.HoldsLook = false;
            Gyro.Cancel(World.Controller);
            if (Editor.Selected is not null)
            {
                _grabbing = true;
                if (!Moving.Grab.On)
                {
                    Moving.Grab.Step(1);
                }
            }

            return;
        }

        if (down && _pressing && !_grabbing && (now - _pressedAt) / (double)Stopwatch.Frequency >= HoldToGrab)
        {
            // The world is looked over once. A hold that found nothing then goes by the aim
            // preview, which is kept up to date anyway.
            var first = !_grabTried;
            _grabTried = true;
            var before = Editor.Selected;
            if ((first ? Editor.Aimed() : Editor.Preview) is { } aimed)
            {
                Editor.Select(aimed);
            }

            // Something that can't be carried says why once, and the camera keeps its forward key.
            if (Editor.Selected is { } wanted)
            {
                if (Moving.Stuck(wanted) is not { } why)
                {
                    _grabbing = true;
                    if (!Moving.Grab.On)
                    {
                        Moving.Grab.Step(1);
                    }
                }
                else if (first || !ReferenceEquals(before, wanted))
                {
                    Toasts.Show(Moving.Grab.Plain, why, OutcomeKind.Failed, Moving.Grab);
                }
            }

            return;
        }

        if (!down && _pressing)
        {
            _pressing = false;
            if (_grabbing)
            {
                LetGoOfGrab();
            }
            else if (Editor.Aimed() is { } aimed)
            {
                Editor.Select(aimed);
                Toasts.Show("SELECTED", Things.NameOf(aimed));
            }
        }
    }

    // A single-key change that can't act says why, under the row's own name. One that can says
    // what it did.
    static bool Allowed(Feature row, string? why)
    {
        if (why is null)
        {
            return true;
        }

        Toasts.Show(row.Plain, why, OutcomeKind.Failed, row);
        return false;
    }

    static void Step(Slider row, int direction)
    {
        if (Allowed(row, row.Blocked))
        {
            row.Step(direction);
            Toasts.Used(row);
        }
    }

    static void Size(int direction) => Step(Change.Size, direction);

    static void Flip(Toggle row)
    {
        if (Allowed(row, row.Blocked))
        {
            row.Press();
            Toasts.Used(row);
        }
    }

    static void StopTurning()
    {
        _turning = false;
        FreeCamera.HoldsLook = false;
        Gyro.End(Editor.Selected);
    }

    static void LetGoOfGrab()
    {
        _grabbing = false;
        FreeCamera.HoldsForward = false;
        if (Moving.Grab.On)
        {
            Moving.Grab.Step(-1);
        }
    }

    // True the moment a key or button goes down. 0 means no key or no button.
    static bool Pressed(Keys key, Pad.Button button)
    {
        var down = (key != 0 && Keyboard.IsDown(key)) || (button != 0 && Pad.IsDown(button));
        var id = (int)key | ((int)button << 16);
        Was.TryGetValue(id, out var was);
        Was[id] = down;
        return down && !was;
    }

    // Under the menu nothing counts, and nothing held then fires once it closes.
    static void ForgetPresses()
    {
        foreach (var id in Was.Keys.ToArray())
        {
            Was[id] = true;
        }

        _pressing = false;
        if (_turning)
        {
            StopTurning();
        }
    }

    static void Start()
    {
        _started = true;
        FreeCamera.ForceFly = true;
        _turnedFreeOn = !FreeCamera.Free.On;
        if (_turnedFreeOn)
        {
            FreeCamera.Free.Step(1);
        }

        ForgetPresses();
    }

    static void Stop()
    {
        if (_turning)
        {
            StopTurning();
        }

        LetGoOfGrab();
        Keep(false);
        for (var i = 0; i < EditKeys.Length; i++)
        {
            if (KeyTaken[i])
            {
                Swallowed.Give(EditKeys[i]);
                KeyTaken[i] = false;
            }
        }

        if (_started && Land.On)
        {
            LandUnderCamera();
        }

        FreeCamera.ForceFly = false;
        if (_turnedFreeOn && FreeCamera.Free.On)
        {
            FreeCamera.Free.Step(-1);
        }

        _started = false;
        _turnedFreeOn = false;
        _lastAt = 0;
        Was.Clear();
    }

    static void LandUnderCamera()
    {
        if (World.Controller is not { } controller || World.Player is not { } you || World.Driving is not null)
        {
            return;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        if (Teleport.Floor(you, eye, eye.Z, eye.Z - SkyReach, out var floor))
        {
            Teleport.Hop(you, floor, new Rotator(0f, view.Yaw, 0f), true);
        }
    }

    // Every drawn frame, since the game tick stops in a pause. The keys go back to the game
    // while it's paused or the menu is open, so its own menus and the map still take clicks.
    public static void Poll() => Keep(On.On && _started && !Menu.Showing && !World.Paused);

    // A key only changes hands while it's up, or the game would see it stuck down.
    static void Keep(bool keep)
    {
        for (var i = 0; i < EditKeys.Length; i++)
        {
            if (KeyTaken[i] != keep && !Keyboard.IsDown(EditKeys[i]))
            {
                if (keep)
                {
                    Swallowed.Take(EditKeys[i]);
                }
                else
                {
                    Swallowed.Give(EditKeys[i]);
                }

                KeyTaken[i] = keep;
            }
        }
    }

    // The on-screen controls while the mode flies. False lets FREE CAMERA show its own.
    public static bool Guide(GuideView view)
    {
        if (!Active)
        {
            return false;
        }

        view.Title = "EDITOR MODE";
        view.Lines.Clear();
        if (FreeCamera.GuideHidden)
        {
            if (FreeCamera.OnPad && (_padShow is null || _padShowStyle != Pad.Look))
            {
                _padShowStyle = Pad.Look;
                _padShow = new GuideLine(new[] { Pad.Describe(Pad.Button.RightStick) }, "SHOW CONTROLS");
            }

            view.Lines.Add(FreeCamera.OnPad ? _padShow!.Value : KeyboardShow);
            return true;
        }

        if (FreeCamera.OnPad)
        {
            if (_padGuide is null || _padGuideStyle != Pad.Look)
            {
                _padGuideStyle = Pad.Look;
                _padGuide = new GuideLine[]
                {
                    new(new[] { "L STICK" }, "FLY"),
                    new(new[] { Pad.Describe(Pad.Button.RT), Pad.Describe(Pad.Button.LT) }, "UP AND DOWN"),
                    new(new[] { Pad.Describe(Pad.Button.A) }, "SELECT, HOLD TO GRAB"),
                    new(new[] { "HOLD " + Pad.Describe(Pad.Button.A) }, "ON THE GYRO, DRAG IT"),
                    new(new[] { Pad.Describe(Pad.Button.DPadUp), Pad.Describe(Pad.Button.DPadDown) }, "SIZE, OR PUSH AND PULL"),
                    new(new[] { Pad.Describe(Pad.Button.DPadLeft), Pad.Describe(Pad.Button.DPadRight) }, "SIZE WHAT YOU HOLD"),
                    new(new[] { Pad.Describe(Pad.Button.LB), Pad.Describe(Pad.Button.RB) }, "TURN"),
                    new(new[] { Pad.Describe(Pad.Button.X) }, "FREEZE"),
                    new(new[] { Pad.Describe(Pad.Button.Y) }, "DROP TO GROUND"),
                    new(new[] { Pad.Describe(Pad.Button.DPadLeft) }, "REMOVE"),
                    new(new[] { Pad.Describe(Pad.Button.DPadRight) }, "RESTORE IT"),
                    new(new[] { Pad.Describe(Pad.Button.B) }, "DESELECT"),
                    new(new[] { Pad.Describe(Pad.Button.RightStick) }, "HIDE CONTROLS"),
                };
            }

            view.Lines.AddRange(_padGuide);
            return true;
        }

        _keyboardGuide ??= new GuideLine[]
        {
            new(new[] { "W", "A", "S", "D" }, "FLY"),
            new(new[] { "SPACE", "CTRL" }, "UP AND DOWN"),
            new(new[] { "CLICK" }, "SELECT, HOLD TO GRAB"),
            new(new[] { "HOLD CLICK" }, "ON THE GYRO, DRAG IT"),
            new(new[] { "W", "S" }, "PUSH AND PULL WHAT YOU HOLD"),
            new(new[] { "Q", "E" }, "TURN"),
            new(new[] { "Z", "X" }, "SIZE"),
            new(new[] { "F" }, "FREEZE"),
            new(new[] { "G" }, "DROP TO GROUND"),
            new(new[] { "C" }, "PLACE AT CROSSHAIR"),
            new(new[] { "U" }, "STAND UPRIGHT"),
            new(new[] { "DEL" }, "REMOVE"),
            new(new[] { "BKSP" }, "UNDO"),
            new(new[] { "R" }, "RESTORE IT"),
            new(new[] { "RIGHT CLICK" }, "DESELECT"),
            new(new[] { "H" }, "HIDE CONTROLS"),
        };
        view.Lines.AddRange(_keyboardGuide);
        return true;
    }
}
