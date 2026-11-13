#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// CAMERA > FREE CAMERA. The camera leaves the player and goes where it's sent. While flying the
// player's move and button input are switched off with the game's own switches, so Batman stands
// still. The camera turns with the player's own view, which the game still turns with your look
// controls, sensitivity and inversion.
static class FreeCamera
{
    const float Metre = 100f;

    const float SteepestPitch = 89f;

    // The game stops the player's view pitch at the camera state's limits, and then there is no
    // more turn to read. While flying it is kept within this of level, so it never gets there.
    const float Level = 15f;

    // How quickly the camera picks up and loses speed, per second.
    const float Glide = 8f;
    const float FastFactor = 4f;
    const float LongestStep = 0.1f;

    // SMOOTHING divides these. Turning eases at this rate per second at level 1.
    const float LookEase = 12f;

    // Where WATCH and FOLLOW look, above the player's middle.
    const float WatchLift = 60f;

    // How quickly FOLLOW catches up, per second, and how far behind it jumps instead, like after
    // a teleport.
    const float FollowEase = 10f;
    const float FollowJump = 50f * Metre;

    // Zoom and roll from the keys while flying, in degrees per second.
    const float ZoomRate = 30f;
    const float RollRate = 45f;
    const float NarrowestZoom = 5f;
    const float WidestZoom = 150f;

    static readonly Held<HUD, bool> HudShown = new("hud", hud => hud.bShowHUD, (hud, shown) => hud.bShowHUD = shown);
    static bool _hudHeld;

    static bool _placed;
    static Vector3 _at;
    static float _yaw;
    static float _pitch;
    static Vector3 _velocity;

    // Where the look controls point the camera, which SMOOTHING eases toward, and the player's
    // view last frame, so LOOK SPEED can scale how far it turned.
    static float _lookYaw;
    static float _lookPitch;
    static bool _lookKnown;
    static float _seenYaw;
    static float _seenPitch;
    static long _lastAt;

    // The player's view pitch from before flying levelled it, given back when the camera stops.
    static RPlayerController? _pitchFor;
    static float _pitchWas;

    // FOLLOW keeps this far from you. Taken again when the mode changes or the camera is placed.
    static Vector3 _offset;
    static bool _offsetKnown;
    static Kind _kindWas;

    static float _zoom;
    static float _roll;

    static RPlayerController? _inputOffFor;

    // The game's own button switch doesn't reach every button, so the ones flying uses, and the
    // ones that would make Batman act, are kept from the game too. Start and Back stay, for the
    // pause menu and the map.
    static readonly Keys[] FlyKeys = { Keys.Space, Keys.ControlKey, Keys.ShiftKey, Keys.H, Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Home };

    // Any of these says you're flying with the keyboard.
    static readonly Keys[] KeyboardSigns = { Keys.W, Keys.A, Keys.S, Keys.D, Keys.Space, Keys.ControlKey, Keys.ShiftKey, Keys.H, Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Home };
    const float StickSign = 0.3f;

    static bool _onPad;
    static bool _guideHidden;
    static bool _hideWas;
    static bool _hidePadWas;
    static bool _padAlone;
    static GuideLine[]? _keyboardGuide;
    static GuideLine[]? _padGuide;
    static Pad.Style _padGuideStyle;
    static GuideLine[]? _keyboardShow;
    static GuideLine[]? _padShow;
    static readonly Pad.Button[] FlyButtons =
    {
        Pad.Button.A, Pad.Button.B, Pad.Button.X, Pad.Button.Y, Pad.Button.LB, Pad.Button.RB, Pad.Button.LT, Pad.Button.RT,
        Pad.Button.LeftStick, Pad.Button.RightStick, Pad.Button.DPadUp, Pad.Button.DPadDown, Pad.Button.DPadLeft, Pad.Button.DPadRight,
    };

    static readonly bool[] KeyTaken = new bool[FlyKeys.Length];
    static readonly bool[] ButtonTaken = new bool[FlyButtons.Length];

    // While true, the player's move controls are off because of the free camera.
    public static bool TakesMoves => _inputOffFor is not null;

    // EDITOR MODE flies whatever FREE CAMERA MODE says, and takes W and S while it carries
    // something, to push it away and pull it in.
    public static bool ForceFly;
    public static bool HoldsForward;

    // The editor's gyro holds the view still while a handle is dragged, and reads the look input
    // itself.
    public static bool HoldsLook;

    public static bool Flying => _placed && Free.On && Current == Kind.Fly;

    // True while the free camera has the view, so work on the view before it can be skipped.
    public static bool HasView => _placed && Free.On;

    public static bool GuideHidden => _guideHidden;

    public static bool OnPad => _onPad;

    public static readonly Toggle Free = new(
        "camera.free_camera.on", "FREE CAMERA",
        "Lets the camera go. In FLY you steer it while Batman stays where he is, and its controls show on screen.",
        saved: false)
    {
        Restore = Stop,
    };

    // In the same order as Mode's options.
    enum Kind
    {
        Fly,
        Stay,
        Watch,
        Follow,
    }

    public static readonly Choice Mode = new(
        "camera.free_camera.mode", "FREE CAMERA MODE",
        "FLY moves the camera while Batman waits. STAY leaves it where it is and you play on. WATCH leaves it where it is and turns it to follow you. FOLLOW keeps it beside you as you go and turns it to follow you.",
        new[] { ("fly", "FLY"), ("stay", "STAY"), ("watch", "WATCH"), ("follow", "FOLLOW") })
    {
        Preference = true,
    };

    public static readonly Slider Speed = new(
        "camera.free_camera.speed", "FLY SPEED",
        "How fast the camera flies.",
        min: 1, max: 60, step: 1, value: 10, unit: " M/S")
    {
        Preference = true,
    };

    public static readonly Slider LookSpeed = new(
        "camera.free_camera.look_speed", "LOOK SPEED",
        "How fast the camera turns while flying, on top of the game's own look sensitivity.",
        min: 0.25, max: 2, step: 0.25, value: 1, unit: "X")
    {
        Preference = true,
    };

    public static readonly Slider Smoothing = new(
        "camera.free_camera.smoothing", "SMOOTHING",
        "Eases the flying camera's moves and turns, for smooth shots. Higher is smoother and slower to follow.",
        min: 0, max: 5, step: 1, value: 0, zero: "OFF")
    {
        Preference = true,
    };

    public static readonly Toggle HideHud = new(
        "camera.free_camera.hide_hud", "HIDE HUD",
        "Hides the game's HUD and the speedometer, for screenshots. This menu still shows.")
    {
        Restore = HudShown.Release,
    };

    public static readonly Slider Zoom = new(
        "camera.free_camera.zoom", "FREE CAMERA ZOOM",
        "Zooms the free camera in, or out above zero, and leaves FIELD OF VIEW alone. While flying the Up and Down keys or the D-pad zoom. Home puts it back.",
        min: -60, max: 40, step: 1, value: 0, unit: " DEG", zero: "GAME", saved: false)
    {
        Changed = static () => _zoom = (float)Zoom!.Value,
    };

    public static readonly Slider Roll = new(
        "camera.free_camera.roll", "FREE CAMERA ROLL",
        "Leans the free camera. While flying the Left and Right keys or the D-pad lean it. Home levels it.",
        min: -180, max: 180, step: 1, value: 0, unit: " DEG", zero: "LEVEL", saved: false)
    {
        Wraps = true,
        Changed = static () => _roll = (float)Roll!.Value,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("FREE CAMERA", Free, Mode),
        new("FLYING", Speed, LookSpeed, Smoothing),
        new("PICTURE", HideHud, Zoom, Roll),
        new("WORLD", WorldTime.Freeze) { Borrowed = true },
    };

    static Kind Current => ForceFly ? Kind.Fly : (Kind)Mode.Index;

    // Called from the camera hook with the view the game worked out. Scenes keep the game's own
    // camera, and the free one carries on from where it was after. Input is held and given back
    // here and not in the game tick, which stops under FREEZE WORLD.
    public static void Fit(ref View view, RPlayerController controller)
    {
        if (!Free.On)
        {
            if (_placed || _inputOffFor is not null)
            {
                Stop();
            }

            return;
        }

        Hold(controller);
        if (controller.bCinematicMode || Switcher.InScene(controller))
        {
            _lastAt = 0;
            return;
        }

        if (!_placed)
        {
            _placed = true;
            _at = view.Location;
            _yaw = view.Rotation.Yaw;
            _pitch = Math.Clamp(Normal(view.Rotation.Pitch), -SteepestPitch, SteepestPitch);
            _velocity = Vector3.Zero;
            _lookYaw = _yaw;
            _lookPitch = _pitch;
            _lookKnown = false;
            _offsetKnown = false;
            Shape(0f, 0f);
        }

        // Real time, so slow motion doesn't slow the camera.
        var now = Stopwatch.GetTimestamp();
        var seconds = _lastAt == 0 ? 0f : MathF.Min((float)((now - _lastAt) / (double)Stopwatch.Frequency), LongestStep);
        _lastAt = now;

        var kind = Current;
        if (kind != _kindWas)
        {
            _kindWas = kind;
            _offsetKnown = false;
        }

        if (kind != Kind.Fly)
        {
            _lookKnown = false;
        }

        switch (kind)
        {
            case Kind.Fly:
                Fly(controller, seconds);
                break;

            case Kind.Watch when Teleport.Mover is { } pawn:
                Face(pawn);
                break;

            case Kind.Follow when Teleport.Mover is { } pawn:
                if (!_offsetKnown)
                {
                    _offset = _at - pawn.Location;
                    _offsetKnown = true;
                }

                var wanted = pawn.Location + _offset;
                _at = (wanted - _at).LengthSquared() > FollowJump * FollowJump ? wanted : _at + (wanted - _at) * (1f - MathF.Exp(-FollowEase * seconds));
                Face(pawn);
                break;
        }

        view.Location = _at;

        // The roll is kept, so CAMERA TILT still leans it.
        view.Rotation = new Rotator(_pitch, _yaw, view.Rotation.Roll + _roll);
        if (_zoom != 0f)
        {
            view.FOV = Math.Clamp(view.FOV + _zoom, NarrowestZoom, WidestZoom);
        }
    }

    static void Face(Pawn pawn)
    {
        var toward = pawn.Location + new Vector3(0f, 0f, WatchLift) - _at;
        if (toward.LengthSquared() > 1f)
        {
            var facing = Rotator.FromDirection(toward);
            _yaw = facing.Yaw;
            _pitch = Math.Clamp(Normal(facing.Pitch), -SteepestPitch, SteepestPitch);
        }
    }

    static void Fly(RPlayerController controller, float seconds)
    {
        var look = controller.Rotation;
        if (!HoldsLook)
        {
            if (_lookKnown)
            {
                var speed = (float)LookSpeed.Value;
                _lookYaw += Normal(look.Yaw - _seenYaw) * speed;
                _lookPitch = Math.Clamp(_lookPitch + Normal(look.Pitch - _seenPitch) * speed, -SteepestPitch, SteepestPitch);
            }
            else
            {
                _lookYaw = look.Yaw;
                _lookPitch = Math.Clamp(Normal(look.Pitch), -SteepestPitch, SteepestPitch);
            }

            // The gyro reads the view while it drags, so the camera only eases when it's free.
            var level = (float)Smoothing.Value;
            var ease = level <= 0f ? 1f : 1f - MathF.Exp(-LookEase / level * seconds);
            var behind = Normal(_lookYaw - _yaw);
            _yaw = Normal(_yaw + behind * ease);
            _lookYaw = _yaw + behind * (1f - ease);
            _pitch += (_lookPitch - _pitch) * ease;
        }

        _seenYaw = look.Yaw;
        _seenPitch = look.Pitch;
        _lookKnown = true;

        if (!HoldsLook && MathF.Abs(Normal(look.Pitch)) > Level)
        {
            if (_pitchFor is null)
            {
                _pitchFor = controller;
                _pitchWas = look.Pitch;
            }

            controller.SetRotation(new Rotator(0f, look.Yaw, look.Roll));
            _seenPitch = 0f;
        }

        // The menu switches pages with the triggers and opens with a stick click, so those wait
        // while it shows. It doesn't use the keys or the sticks.
        var menu = Menu.Showing;

        // The game's raw stick values survive the move switch. The keys are read here as well,
        // in case the game only counts the sticks there.
        var input = controller.PlayerInput;
        var ahead = (input?.RawJoyUp ?? 0f) + (HoldsForward ? 0f : Axis(Keys.W, Keys.S));
        var side = (input?.RawJoyRight ?? 0f) + Axis(Keys.D, Keys.A);
        var rise = Axis(Keys.Space, Keys.ControlKey) + (menu ? 0f : PadAxis(Pad.Button.RT, Pad.Button.LT));

        var forward = new Rotator(_pitch, _yaw, 0f).ToDirection();
        var flat = new Rotator(0f, _yaw, 0f).ToDirection();
        var right = new Vector3(-flat.Y, flat.X, 0f);
        var move = forward * Math.Clamp(ahead, -1f, 1f) + right * Math.Clamp(side, -1f, 1f) + Vector3.UnitZ * Math.Clamp(rise, -1f, 1f);
        if (move.LengthSquared() > 1f)
        {
            move = Vector3.Normalize(move);
        }

        var fast = Keyboard.IsDown(Keys.ShiftKey) || (!menu && Pad.IsDown(Pad.Button.LeftStick)) ? FastFactor : 1f;
        var wanted = move * (float)Speed.Value * Metre * fast;
        _velocity += (wanted - _velocity) * (1f - MathF.Exp(-Glide / (1f + (float)Smoothing.Value) * seconds));
        _at += _velocity * seconds;

        // EDITOR MODE has the D-pad for its own work.
        if (menu || EditorMode.Active)
        {
            return;
        }

        if (Keyboard.IsDown(Keys.Home))
        {
            Shape(0f, 0f);
            return;
        }

        var lean = Math.Clamp(Axis(Keys.Right, Keys.Left) + PadAxis(Pad.Button.DPadRight, Pad.Button.DPadLeft), -1f, 1f);
        var narrow = Math.Clamp(Axis(Keys.Up, Keys.Down) + PadAxis(Pad.Button.DPadUp, Pad.Button.DPadDown), -1f, 1f);
        if (lean != 0f || narrow != 0f)
        {
            Shape(Math.Clamp(_zoom - narrow * ZoomRate * seconds, (float)Zoom.Min, (float)Zoom.Max), Normal(_roll + lean * RollRate * seconds));
        }
    }

    // Sets the zoom and roll, and shows them on their rows.
    static void Shape(float zoom, float roll)
    {
        if (zoom != _zoom)
        {
            _zoom = zoom;
            Zoom.Show(zoom);
        }

        if (roll != _roll)
        {
            _roll = roll;
            Roll.Show(roll);
        }
    }

    // Every frame, so a tap is never missed. H or clicking the right stick hides the guide, and
    // whichever was used last decides which keys it shows.
    public static void Poll()
    {
        HoldHud();

        var flying = _placed && Free.On && Current == Kind.Fly;
        var hide = Keyboard.IsDown(Keys.H);
        var hidePad = Pad.IsDown(Pad.Button.RightStick);

        // The stick click counts on release, and only when it was pressed on its own. Opening
        // and closing the menu with a chord that has it in leaves the guide as it was.
        if (hidePad && !_hidePadWas)
        {
            _padAlone = true;
        }

        if (hidePad && (Pad.Held & Controls.ButtonFor(Act.Open) & ~Pad.Button.RightStick) != Pad.Button.None)
        {
            _padAlone = false;
        }

        var padTapped = !hidePad && _hidePadWas && _padAlone;
        if (flying && !Menu.Showing && ((hide && !_hideWas) || padTapped))
        {
            _guideHidden = !_guideHidden;
        }

        _hideWas = hide;
        _hidePadWas = hidePad;

        // Here and not in the game tick, which stops in a pause. The keys and buttons go back to
        // the game then, so its own menus and the map still work.
        Keep(Free.On && Current == Kind.Fly && !World.Paused);
        if (!flying)
        {
            _onPad = Controls.LastDevice == Device.Pad;
            return;
        }

        foreach (var key in KeyboardSigns)
        {
            if (Keyboard.IsDown(key))
            {
                _onPad = false;
                return;
            }
        }

        var input = World.Controller?.PlayerInput;
        var stick = input is not null && (MathF.Abs(input.RawJoyUp) > StickSign || MathF.Abs(input.RawJoyRight) > StickSign);
        foreach (var button in FlyButtons)
        {
            stick |= Pad.IsDown(button);
        }

        _onPad |= stick;
    }

    // Fills the on-screen guide while flying. False when it shouldn't show.
    public static bool Guide(GuideView view)
    {
        if (!_placed || !Free.On || Current != Kind.Fly)
        {
            return false;
        }

        GuideLine[] lines;
        if (_onPad)
        {
            if (_padGuide is null || _padGuideStyle != Pad.Look)
            {
                _padGuideStyle = Pad.Look;
                var hide = Pad.Describe(Pad.Button.RightStick);
                _padGuide = new GuideLine[]
                {
                    new(new[] { "L STICK" }, "MOVE"),
                    new(new[] { "R STICK" }, "LOOK"),
                    new(new[] { Pad.Describe(Pad.Button.RT) }, "UP"),
                    new(new[] { Pad.Describe(Pad.Button.LT) }, "DOWN"),
                    new(new[] { Pad.Describe(Pad.Button.LeftStick) }, "FASTER"),
                    new(new[] { Pad.Describe(Pad.Button.DPadLeft), Pad.Describe(Pad.Button.DPadRight) }, "ROLL"),
                    new(new[] { Pad.Describe(Pad.Button.DPadUp), Pad.Describe(Pad.Button.DPadDown) }, "ZOOM"),
                    new(new[] { hide }, "HIDE CONTROLS"),
                };
                _padShow = new GuideLine[] { new(new[] { hide }, "SHOW CONTROLS") };
            }

            lines = _guideHidden ? _padShow! : _padGuide;
        }
        else
        {
            _keyboardGuide ??= new GuideLine[]
            {
                new(new[] { "W", "A", "S", "D" }, "MOVE"),
                new(new[] { "MOUSE" }, "LOOK"),
                new(new[] { "SPACE" }, "UP"),
                new(new[] { "CTRL" }, "DOWN"),
                new(new[] { "SHIFT" }, "FASTER"),
                new(new[] { "LEFT", "RIGHT" }, "ROLL"),
                new(new[] { "UP", "DOWN" }, "ZOOM"),
                new(new[] { "HOME" }, "RESET ZOOM AND ROLL"),
                new(new[] { "H" }, "HIDE CONTROLS"),
            };
            _keyboardShow ??= new GuideLine[] { new(new[] { "H" }, "SHOW CONTROLS") };
            lines = _guideHidden ? _keyboardShow : _keyboardGuide;
        }

        view.Title = "FREE CAMERA";
        view.Lines.Clear();
        view.Lines.AddRange(lines);
        return true;
    }

    static float Axis(Keys plus, Keys minus) => (Keyboard.IsDown(plus) ? 1f : 0f) - (Keyboard.IsDown(minus) ? 1f : 0f);

    static float PadAxis(Pad.Button plus, Pad.Button minus) => (Pad.IsDown(plus) ? 1f : 0f) - (Pad.IsDown(minus) ? 1f : 0f);

    // Move and button input are kept from the player while flying, so Batman stands still and
    // Space doesn't make him jump. Both switches are counts, so each is given back once.
    static void Hold(RPlayerController controller)
    {
        var want = Current == Kind.Fly ? controller : null;
        if (ReferenceEquals(want, _inputOffFor))
        {
            return;
        }

        LetInput();
        if (want is not null)
        {
            want.IgnoreMoveInput(true);
            want.IgnoreButtonInput(true);
            _inputOffFor = want;
        }
    }

    // A key or button only changes hands while it is up, or the game would see it stuck down.
    static void Keep(bool keep)
    {
        for (var i = 0; i < FlyKeys.Length; i++)
        {
            if (KeyTaken[i] != keep && !Keyboard.IsDown(FlyKeys[i]))
            {
                Hand(i, keep, key: true);
            }
        }

        for (var i = 0; i < FlyButtons.Length; i++)
        {
            if (ButtonTaken[i] != keep && !Pad.IsDown(FlyButtons[i]))
            {
                Hand(i, keep, key: false);
            }
        }
    }

    static void Hand(int index, bool take, bool key)
    {
        if (key)
        {
            if (take)
            {
                Swallowed.Take(FlyKeys[index]);
            }
            else
            {
                Swallowed.Give(FlyKeys[index]);
            }

            KeyTaken[index] = take;
            return;
        }

        if (take)
        {
            Swallowed.Take(FlyButtons[index]);
        }
        else
        {
            Swallowed.Give(FlyButtons[index]);
        }

        ButtonTaken[index] = take;
    }

    // Runs from the camera hook and from the restore, so it has to be safe to run twice.
    static void Stop()
    {
        // Given back straight away when it's over, held or not.
        for (var i = 0; i < FlyKeys.Length; i++)
        {
            if (KeyTaken[i])
            {
                Hand(i, false, key: true);
            }
        }

        for (var i = 0; i < FlyButtons.Length; i++)
        {
            if (ButtonTaken[i])
            {
                Hand(i, false, key: false);
            }
        }

        LetInput();
        if (_pitchFor is { IsValid: true } owner)
        {
            var turn = owner.Rotation;
            owner.SetRotation(new Rotator(_pitchWas, turn.Yaw, turn.Roll));
        }

        _pitchFor = null;
        _placed = false;
        _lastAt = 0;
    }

    static void LetInput()
    {
        if (_inputOffFor is { IsValid: true } controller)
        {
            controller.IgnoreMoveInput(false);
            controller.IgnoreButtonInput(false);
        }

        _inputOffFor = null;
    }

    // From the draw callback and not the game tick, so it works under FREEZE WORLD too.
    static void HoldHud()
    {
        if (HideHud.On)
        {
            if (World.Controller?.myHUD is { IsValid: true } hud)
            {
                HudShown.Hold(hud, false);
                _hudHeld = true;
            }

            return;
        }

        if (_hudHeld)
        {
            HudShown.Release();
            _hudHeld = false;
        }
    }

    static float Normal(float degrees)
    {
        degrees %= 360f;
        return degrees > 180f ? degrees - 360f : degrees < -180f ? degrees + 360f : degrees;
    }
}
