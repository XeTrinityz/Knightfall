#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// CAMERA > VIEW. The game's camera works out its view as usual, collision and smoothing and all,
// and these move it after. Scenes keep the game's camera.
static class CameraView
{
    const float Metre = 100f;

    // How quickly the changes ease out going into a scene and back in after, per second.
    const float Ease = 4f;

    // The view's angle stays inside this, in degrees.
    const float Narrowest = 20f;
    const float Widest = 150f;

    // Kept this far off a wall the camera was moved into.
    const float WallGap = 20f;

    // How quickly the camera crosses to the other shoulder, per second.
    const float SwapEase = 6f;

    // How quickly the mirrored side follows the game's own. Slow, so the camera's lag while you
    // run sideways isn't mirrored with it.
    const float SideFollow = 2f;

    // How quickly the black bars slide in and out, per second.
    const float BarsEase = 6f;

    // How quickly the glide rows blend in and out as a glide starts and ends, per second.
    const float GlideEase = 3f;

    // How quickly AIM ZOOM goes in and out as you start and stop aiming, per second.
    const float AimEase = 10f;

    static float _weight;
    static float _glide;
    static float _aim;
    static bool _aiming;
    static int _stateIndex = -1;
    static bool _stateAims;
    static bool _stateHides;
    static float _bars;
    static long _barsAt;
    static float _lastShape = 2.39f;
    static float _mirror;
    static float _gameSide;
    static bool _gameSideKnown;

    public static readonly Slider FieldOfView = new(
        "camera.view.field_of_view", "FIELD OF VIEW",
        "Wider or narrower than the game's view, on foot and in the Batmobile. Aiming, gliding and boosting still zoom the way they do.",
        min: -30, max: 40, step: 5, value: 0, unit: " DEG", zero: "GAME");

    public static readonly Slider AimZoom = new(
        "camera.view.aim_zoom", "AIM ZOOM",
        "Scales the view while you aim a gadget or zoom in. Lower zooms in further, higher is wider.",
        min: 0.5, max: 1.5, step: 0.05, value: 1, unit: "X");

    public static readonly Slider Tilt = new(
        "camera.view.tilt", "CAMERA TILT",
        "Leans the view to the right, or to the left below zero, on foot, in the Batmobile and in the free camera.",
        min: -30, max: 30, step: 2.5, value: 0, unit: " DEG", zero: "LEVEL");

    // How wide the picture is between the bars, in the same order as the choices.
    static readonly float[] BarShapes = { 0f, 2f, 2.39f, 2.76f };

    public static readonly Choice Bars = new(
        "camera.view.black_bars", "BLACK BARS",
        "Black bars above and below the picture, like a film. They cover the HUD's top and bottom edges, so they go well with HIDE HUD.",
        new[] { ("off", "OFF"), ("wide", "2:1"), ("film", "2.39:1"), ("epic", "2.76:1") });

    // The camera's states copy their sway and hip follow from these when they begin, and the run
    // state plays its bob at RunOverlayStrength. Holding the live values too covers the state
    // that's already running.
    static readonly Held<R3rdPersonCamera, float>[] Sway =
    {
        new("camera sway", camera => camera.PelvisSway, (camera, value) => camera.PelvisSway = value),
        new("camera default sway", camera => camera.DefaultPelvisSway, (camera, value) => camera.DefaultPelvisSway = value),
        new("camera run sway", camera => camera.RunStatePelvisSway, (camera, value) => camera.RunStatePelvisSway = value),
        new("camera hip follow", camera => camera.PelvisFollowXY, (camera, value) => camera.PelvisFollowXY = value),
        new("camera hip follow up", camera => camera.PelvisFollowZ, (camera, value) => camera.PelvisFollowZ = value),
        new("camera default hip follow", camera => camera.DefaultPelvisFollowXY, (camera, value) => camera.DefaultPelvisFollowXY = value),
        new("camera default hip follow up", camera => camera.DefaultPelvisFollowZ, (camera, value) => camera.DefaultPelvisFollowZ = value),
        new("camera run hip follow", camera => camera.RunStatePelvisXY, (camera, value) => camera.RunStatePelvisXY = value),
        new("camera run hip follow up", camera => camera.RunStatePelvisZ, (camera, value) => camera.RunStatePelvisZ = value),
        new("camera run bob", camera => camera.RunOverlayStrength, (camera, value) => camera.RunOverlayStrength = value),
    };

    static readonly FName RunBob = new("RunLoop");
    static nint _bobStoppedOn;

    public static readonly Toggle Steady = new(
        "camera.view.steady", "STEADY CAMERA",
        "Stops the camera swaying with Batman's hips and bobbing while he runs.")
    {
        Tick = HoldSteady,
        Restore = ReleaseSteady,
    };

    public static readonly Toggle NoShake = new(
        "camera.view.no_shake", "NO CAMERA SHAKE",
        "No shaking from boosts, landings, blasts or hits.");

    public static readonly Slider Distance = new(
        "camera.view.distance", "CAMERA DISTANCE",
        "How far behind you the camera sits on foot. It still pulls in for walls.",
        min: 0.5, max: 2.5, step: 0.1, value: 1, unit: "X");

    public static readonly Slider Height = new(
        "camera.view.height", "CAMERA HEIGHT",
        "Raises or lowers the camera on foot.",
        min: -1, max: 2, step: 0.1, value: 0, unit: " M", zero: "GAME");

    public static readonly Slider Shoulder = new(
        "camera.view.shoulder", "SHOULDER",
        "Moves the camera to the right of you, or the left below zero.",
        min: -1.5, max: 1.5, step: 0.1, value: 0, unit: " M", zero: "GAME");

    public static readonly Toggle LeftShoulder = new(
        "camera.view.left_shoulder", "LEFT SHOULDER",
        "Puts the camera over your left shoulder instead of the right. SHOULDER then moves it further out to the left. Give it a hotkey to swap mid-fight.");

    public static readonly Slider GlideDistance = new(
        "camera.view.glide_distance", "GLIDE CAMERA DISTANCE",
        "How far behind you the camera sits while you glide, on top of CAMERA DISTANCE.",
        min: 0.5, max: 2.5, step: 0.1, value: 1, unit: "X");

    public static readonly Slider GlideFieldOfView = new(
        "camera.view.glide_field_of_view", "GLIDE FIELD OF VIEW",
        "Widens or narrows the view while you glide, on top of FIELD OF VIEW. Dives still zoom the way they do.",
        min: -20, max: 40, step: 5, value: 0, unit: " DEG", zero: "GAME");

    public static readonly Slider CarFieldOfView = new(
        "camera.view.car_field_of_view", "CAR FIELD OF VIEW",
        "Widens or narrows the view in the Batmobile, on top of FIELD OF VIEW.",
        min: -20, max: 40, step: 5, value: 0, unit: " DEG", zero: "GAME");

    public static readonly Slider CarDistance = new(
        "camera.view.car_distance", "CAR CAMERA DISTANCE",
        "How far behind the Batmobile the camera sits.",
        min: 0.5, max: 2.5, step: 0.1, value: 1, unit: "X");

    public static readonly Slider CarHeight = new(
        "camera.view.car_height", "CAR CAMERA HEIGHT",
        "Raises or lowers the camera behind the Batmobile.",
        min: -1, max: 3, step: 0.1, value: 0, unit: " M", zero: "GAME");

    static readonly Feature[] Rows = { FieldOfView, AimZoom, Tilt, Bars, Steady, NoShake, FirstPerson.OnFoot, FirstPerson.Eye, Distance, Height, Shoulder, LeftShoulder, GlideDistance, GlideFieldOfView, CarFieldOfView, CarDistance, CarHeight };

    public static readonly Command ResetAll = new(
        "camera.view.reset", "RESET CAMERA",
        "Puts every row on this tab back to the game's camera. FREE CAMERA is left alone.",
        static () => Weather.Back(Rows));

    // Each preset sets these rows, and the rest of Shaped go back to the game's.
    sealed record Preset(string Id, string Label, params (Feature Row, double Value)[] Values);

    // After every row it names, so they all exist when it's made.
    static readonly Preset[] Presets =
    {
        new("wide", "WIDE", (FieldOfView, 10), (Distance, 1.1), (GlideFieldOfView, 5), (CarFieldOfView, 5)),
        new("close", "CLOSE", (FieldOfView, -5), (Distance, 0.7), (Height, -0.1), (Shoulder, 0.2)),
        new("shoulder", "OVER THE SHOULDER", (Distance, 0.8), (Height, 0.1), (Shoulder, 0.4), (AimZoom, 0.9)),
        new("cinematic", "CINEMATIC", (FieldOfView, -5), (Distance, 1.3), (Height, 0.2), (Steady, 1), (Bars, 2), (GlideDistance, 1.3)),
        new("action", "ACTION", (FieldOfView, 15), (Distance, 0.9), (Steady, 1), (GlideFieldOfView, 10), (CarFieldOfView, 10)),
        new("high", "HIGH", (Distance, 1.6), (Height, 1.5), (CarHeight, 1)),
    };

    public static readonly Choice PresetPick = new(
        "camera.view.preset", "PRESET",
        "A set of camera rows to start from. APPLY PRESET puts them on.",
        Presets.Select(preset => (preset.Id, preset.Label)).ToArray(),
        saved: false)
    {
        Pressed = static () => ApplyPreset!.Press(),
        PressName = "APPLY",
    };

    public static readonly Command ApplyPreset = new(
        "camera.view.apply_preset", "APPLY PRESET",
        "Sets the camera rows to the picked PRESET. Change any of them after. RESET CAMERA puts them all back.",
        Apply);

    // What a preset shapes. NO CAMERA SHAKE, FIRST PERSON, FIRST PERSON EYE and LEFT SHOULDER are
    // a matter of taste, so a preset leaves them as they are.
    static readonly Feature[] Shaped = { FieldOfView, AimZoom, Tilt, Bars, Steady, Distance, Height, Shoulder, GlideDistance, GlideFieldOfView, CarFieldOfView, CarDistance, CarHeight };

    public static readonly Catalog.Section[] Sections =
    {
        new("PRESET", PresetPick, ApplyPreset, ResetAll),
        new("VIEW", FieldOfView, AimZoom, Tilt, Steady, NoShake, Bars),
        new("FIRST PERSON", FirstPerson.OnFoot, FirstPerson.Eye),
        new("ON FOOT", Distance, Height, Shoulder, LeftShoulder),
        new("GLIDING", GlideDistance, GlideFieldOfView),
        new("BATMOBILE", CarFieldOfView, CarDistance, CarHeight),
    };

    static void HoldSteady()
    {
        if (World.Controller?.PlayerCamera is not R3rdPersonCamera { IsValid: true } camera)
        {
            return;
        }

        foreach (var held in Sway)
        {
            held.Hold(camera, 0f);
        }

        // A bob that's already playing carries on until it's stopped, the way the run state
        // stops it when it ends.
        if (_bobStoppedOn != camera.Ptr && camera.OverlayPlayer is { IsValid: true } overlay)
        {
            overlay.StopAnim(RunBob, 0.3f);
            _bobStoppedOn = camera.Ptr;
        }
    }

    static void ReleaseSteady()
    {
        foreach (var held in Sway)
        {
            held.Release();
        }

        _bobStoppedOn = 0;
    }

    static Outcome Apply()
    {
        var preset = Array.Find(Presets, entry => entry.Id == PresetPick.CurrentId);
        if (preset is null)
        {
            return Outcome.Failed("NO PRESET");
        }

        foreach (var row in Shaped)
        {
            row.Reset();
        }

        foreach (var (row, value) in preset.Values)
        {
            switch (row)
            {
                case Slider slider:
                    slider.SetTo(value);
                    break;
                case Toggle toggle:
                    toggle.SetTo(value != 0);
                    break;
                case Choice choice:
                    choice.SetTo((int)value);
                    break;
            }
        }

        return Outcome.Done(preset.Label);
    }

    // Each shake the game adds is dropped before the camera can play it.
    public static void Before(R3rdPersonCamera camera)
    {
        if (NoShake.On && camera.ScreenShakeModifier is { IsValid: true } shaker && shaker.Shakes.Count > 0)
        {
            shaker.Shakes.Pop(shaker.Shakes.Count);
        }

        _aiming = AimZoom.Value != 1.0 && Aims(camera);
    }

    // The game's camera has its own state for each gadget's aim and for zooming in. The name
    // only changes with the state, so it's read once per change. Aiming from a crouch or cover
    // reuses the plain crouch and cover states, so there the aim button tells.
    static bool Aims(R3rdPersonCamera camera)
    {
        var state = camera.GetStateName();
        if (state.Index != _stateIndex)
        {
            _stateIndex = state.Index;
            var name = state.ToString() ?? "";
            _stateAims = name.Contains("Aim", StringComparison.OrdinalIgnoreCase) || name.Contains("Zoom", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Sniper", StringComparison.OrdinalIgnoreCase) || name.Equals("Batarang", StringComparison.OrdinalIgnoreCase);
            _stateHides = name.Contains("Stealth", StringComparison.OrdinalIgnoreCase) || name.Contains("Cover", StringComparison.OrdinalIgnoreCase)
                || name.Equals("HideCam", StringComparison.OrdinalIgnoreCase);
        }

        return _stateAims || camera.bZoomed || (_stateHides && World.Controller is { bAimGadgetPressed: true });
    }

    public static void Fit(ref View view, RPlayerController controller, float deltaTime, bool replaced)
    {
        var pawn = Teleport.Mover;
        var car = pawn is RVehicleBatmobileBase;
        var glideStep = 1f - MathF.Exp(-GlideEase * MathF.Max(0f, deltaTime));
        _glide += ((pawn is RPawnPlayer { bGliding: true } ? 1f : 0f) - _glide) * glideStep;
        if (_glide < 0.001f)
        {
            _glide = 0f;
        }

        var aimStep = 1f - MathF.Exp(-AimEase * MathF.Max(0f, deltaTime));
        _aim += ((_aiming && !car ? 1f : 0f) - _aim) * aimStep;
        if (_aim < 0.001f)
        {
            _aim = 0f;
        }

        var zoom = 1f + ((float)AimZoom.Value - 1f) * _aim;
        var widen = (float)FieldOfView.Value + (float)GlideFieldOfView.Value * _glide + (car ? (float)CarFieldOfView.Value : 0f);
        var away = (float)(car ? CarDistance.Value : Distance.Value * (1.0 + (GlideDistance.Value - 1.0) * _glide));
        var lift = (float)(car ? CarHeight.Value : Height.Value) * Metre;
        var side = car ? 0f : (float)Shoulder.Value * Metre;

        var swapStep = 1f - MathF.Exp(-SwapEase * MathF.Max(0f, deltaTime));
        _mirror += ((!car && LeftShoulder.On ? 1f : 0f) - _mirror) * swapStep;
        if (_mirror < 0.001f)
        {
            _mirror = 0f;
        }

        var lean = (float)Tilt.Value;
        var changed = widen != 0f || zoom != 1f || lean != 0f || away != 1f || lift != 0f || side != 0f || _mirror > 0f;

        var allowed = changed && pawn is not null && !controller.bCinematicMode && !Switcher.InScene(controller);
        var step = 1f - MathF.Exp(-Ease * MathF.Max(0f, deltaTime));
        _weight += ((allowed ? 1f : 0f) - _weight) * step;
        if (_weight < 0.001f || pawn is null)
        {
            _weight = allowed ? _weight : 0f;
            _gameSideKnown = false;
            return;
        }

        // A game zoom already outside the range is never pulled into it.
        if (widen != 0f || zoom != 1f)
        {
            var game = view.FOV;
            var wanted = (game + widen * _weight) * (1f + (zoom - 1f) * _weight);
            view.FOV = Math.Clamp(wanted, MathF.Min(Narrowest, game), MathF.Max(Widest, game));
        }

        if (lean != 0f)
        {
            view.Rotation = new Rotator(view.Rotation.Pitch, view.Rotation.Yaw, view.Rotation.Roll + lean * _weight);
        }

        // FIRST PERSON or the free camera puts the view somewhere else after this.
        if (replaced)
        {
            _gameSideKnown = false;
            return;
        }

        // Pulled in or out along the line the camera looks down, so what's in the middle of the
        // screen stays there.
        var forward = view.Rotation.ToDirection();
        var at = view.Location;
        var along = Vector3.Dot(pawn.Location - at, forward);
        if (along > 1f)
        {
            at += forward * along * (1f - (1f + (away - 1f) * _weight));
        }

        var yaw = new Rotator(0f, view.Rotation.Yaw, 0f).ToDirection();
        var right = new Vector3(-yaw.Y, yaw.X, 0f);

        // The other shoulder mirrors how far to the side the game itself put the camera.
        if (_mirror > 0f)
        {
            var measured = Vector3.Dot(view.Location - pawn.Location, right);
            var follow = 1f - MathF.Exp(-SideFollow * MathF.Max(0f, deltaTime));
            _gameSide = _gameSideKnown ? _gameSide + (measured - _gameSide) * follow : measured;
            _gameSideKnown = true;
            side = side * (1f - 2f * _mirror) - 2f * _mirror * _gameSide;
        }
        else
        {
            _gameSideKnown = false;
        }

        at += right * side * _weight + new Vector3(0f, 0f, lift * _weight);
        if (at == view.Location)
        {
            return;
        }

        // The game only kept its own spot clear of walls.
        var hit = pawn.Trace(out var spot, out _, at, pawn.Location, Component.ECollisionFilter.COF_TraceWorld, Vector3.Zero, out _, 0);
        if (hit is not null and not Pawn)
        {
            var back = pawn.Location - spot;
            if (back.LengthSquared() <= WallGap * WallGap)
            {
                return;
            }

            at = spot + Vector3.Normalize(back) * WallGap;
        }

        view.Location = at;
    }

    // Drawn under everything else of ours, every frame. Real time, so pausing doesn't hold them
    // halfway.
    public static void RenderBars(Canvas canvas)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _barsAt == 0 ? 0f : MathF.Min((float)((now - _barsAt) / (double)Stopwatch.Frequency), 0.1f);
        _barsAt = now;

        var shape = BarShapes[Math.Clamp(Bars.Index, 0, BarShapes.Length - 1)];
        var wanted = shape > 0f && !World.Paused ? 1f : 0f;
        _bars += (wanted - _bars) * (1f - MathF.Exp(-BarsEase * seconds));
        if (wanted == 0f && _bars < 0.002f)
        {
            _bars = 0f;
            return;
        }

        if (!Draw.Begin(canvas))
        {
            return;
        }

        try
        {
            // The last shape stays while they slide away.
            if (shape > 0f)
            {
                _lastShape = shape;
            }

            var picture = Draw.ScreenWidth / _lastShape;
            var bar = (Draw.ScreenHeight - picture) * 0.5f * _bars;
            if (bar >= 0.5f)
            {
                var black = new Rgba(0, 0, 0);
                Draw.Rect(0f, 0f, Draw.ScreenWidth, bar, black);
                Draw.Rect(0f, Draw.ScreenHeight - bar, Draw.ScreenWidth, bar, black);
            }
        }
        finally
        {
            Draw.End();
        }
    }
}
