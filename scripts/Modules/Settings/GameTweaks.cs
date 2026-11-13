#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// SETTINGS > GAME. Engine switches and render settings the game's own options don't reach. They
// are how the game is set up, like the look of the menu, so TURN EVERYTHING OFF leaves them alone.
static class GameTweaks
{
    // A render change waits until the slider has stopped moving, since each one rewrites the
    // game's settings file.
    const double Settle = 0.4;

    // A gap this long between ticks means the game was paused, and its graphics menu may have put
    // its own values back.
    const double PausedGap = 0.5;

    const float Uncapped = 1000f;

    // The game ships with pause on focus loss turned on in its ini. The engine reads it every time
    // the window loses focus, so holding it off is enough.
    static readonly Held<GameEngine, bool> FocusPause = new(
        "pause on focus loss", engine => engine.bPauseOnLossOfFocus, (engine, on) => engine.bPauseOnLossOfFocus = on);

    static readonly Held<GameEngine, bool> PadUnfocused = new(
        "controller without focus", engine => engine.AllowControllerInputWithoutFocus, (engine, on) => engine.AllowControllerInputWithoutFocus = on);

    // Frame smoothing caps the frame rate alongside MaxFPS, so both follow the cap.
    static readonly Held<GameEngine, bool> Smoothing = new(
        "frame smoothing", engine => engine.bSmoothFrameRate, (engine, on) => engine.bSmoothFrameRate = on);

    static readonly Held<GameEngine, float> SmoothCap = new(
        "smoothed frame cap", engine => engine.MaxSmoothedFrameRate, (engine, cap) => engine.MaxSmoothedFrameRate = cap);

    static readonly float[] Caps = { 0f, 120f, 144f, 165f, 240f, Uncapped };

    static int _capSet = -1;
    static double _detailSet = -1;
    static long _lastTick;

    public static readonly Toggle KeepRunning = new(
        "settings.game.keep_running", "KEEP RUNNING IN BACKGROUND",
        "The game doesn't pause or open its pause menu when you click or alt-tab to another window.")
    {
        Preference = true,
        Tick = () => Hold(FocusPause, false),
        Restore = FocusPause.Release,
    };

    public static readonly Toggle PadInBackground = new(
        "settings.game.pad_background", "CONTROLLER IN BACKGROUND",
        "A controller keeps playing while another window is in front. Best with KEEP RUNNING IN BACKGROUND on.")
    {
        Preference = true,
        Tick = () => Hold(PadUnfocused, true),
        Restore = PadUnfocused.Release,
    };

    public static readonly Choice FrameCap = new(
        "settings.game.frame_cap", "FRAME RATE CAP",
        "Frame rate limits past the game's own 30, 60 and 90. GAME leaves the limit from the game's graphics menu.",
        new[] { ("game", "GAME"), ("120", "120"), ("144", "144"), ("165", "165"), ("240", "240"), ("uncapped", "UNCAPPED") })
    {
        Preference = true,
        Tick = HoldCap,
        Restore = ReleaseCap,
    };

    public static readonly Slider CharacterDetail = new(
        "settings.game.character_detail", "CHARACTER DETAIL",
        "Keeps people at their detailed models from further away. The game uses 0.8X. Higher costs frame rate in crowds.",
        min: 0, max: 4, step: 0.25, value: 0, unit: "X", zero: "GAME")
    {
        Preference = true,
        Tick = HoldDetail,
        Restore = ReleaseDetail,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("WINDOW", KeepRunning, PadInBackground),
        new("PERFORMANCE", FrameCap, CharacterDetail, Buildings.Detail),
    };

    static void Hold<T>(Held<GameEngine, T> held, T value)
    {
        if (Game.TryGetEngine(out var engine))
        {
            held.Hold(engine, value);
        }
    }

    // Once a tick for the render rows. A long gap since the last one means the game was paused,
    // so the render values are sent again.
    static bool CameBack()
    {
        var now = Stopwatch.GetTimestamp();
        var gap = _lastTick == 0 ? 0 : (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;
        return gap > PausedGap;
    }

    static bool Settled(Feature feature) =>
        (Stopwatch.GetTimestamp() - feature.TouchedAt) / (double)Stopwatch.Frequency >= Settle;

    static void HoldCap()
    {
        var cap = Caps[Math.Clamp(FrameCap.Index, 0, Caps.Length - 1)];
        if (cap >= Uncapped)
        {
            Hold(Smoothing, false);
            SmoothCap.Release();
        }
        else
        {
            Hold(SmoothCap, cap);
            Smoothing.Release();
        }

        if (CameBack())
        {
            _capSet = -1;
            _detailSet = -1;
        }

        if (_capSet != FrameCap.Index && Settled(FrameCap) && SystemValues.Set("MaxFPS", cap))
        {
            _capSet = FrameCap.Index;
        }
    }

    static void ReleaseCap()
    {
        SystemValues.Restore("MaxFPS");
        Smoothing.Release();
        SmoothCap.Release();
        _capSet = -1;
    }

    static void HoldDetail()
    {
        if (FrameCap.IsDefault && CameBack())
        {
            _detailSet = -1;
        }

        if (_detailSet != CharacterDetail.Value && Settled(CharacterDetail)
            && SystemValues.Set("SkeletalMeshDisplayFactorScale", (float)CharacterDetail.Value))
        {
            _detailSet = CharacterDetail.Value;
        }
    }

    static void ReleaseDetail()
    {
        SystemValues.Restore("SkeletalMeshDisplayFactorScale");
        _detailSet = -1;
    }
}
