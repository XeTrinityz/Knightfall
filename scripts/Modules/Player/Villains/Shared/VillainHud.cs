#nullable enable

using System.Diagnostics;

namespace Knightfall;

// A played villain's own HUD in the bottom left. It slides up and fades in and out the same for
// every villain, and each villain draws the rest itself.
static class VillainHud
{
    // Coming and going it slides up this far and fades over this long.
    const float ShowSeconds = 0.25f;
    const float Slide = 24f;

    // The villain's drawing, given how far below its place it still is as it slides in, and
    // the seconds since the last frame.
    static Action<float, float>? _drawing;
    static float _shown;
    static long _drawnAt;

    // How far it has faded in, from 0 to 1, for a part of a HUD that fades on its own as well.
    public static float Shown { get; private set; }

    // The HUD to draw, or none. Set every tick by whichever villain is being played.
    public static void Show(Action<float, float>? drawing)
    {
        _drawing = drawing;
    }

    // It goes away with the game's own HUD: in photo mode, and whenever the game or FREE CAMERA
    // hides the HUD.
    static bool GameHudShown() =>
        World.Controller is not { IsValid: true } controller
        || (controller.myHUD is not { IsValid: true, bShowHUD: false } && !controller.IsPhotoMode());

    public static void Render(BmSDK.Engine.Canvas canvas)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _drawnAt == 0 ? 0f : MathF.Min((float)((now - _drawnAt) / (double)Stopwatch.Frequency), 0.1f);
        _drawnAt = now;

        var drawing = _drawing;
        var wanted = drawing is not null && !World.Paused && GameHudShown() ? 1f : 0f;
        _shown = Math.Clamp(_shown + (wanted > _shown ? 1f : -1f) * seconds / ShowSeconds, 0f, 1f);
        if (_shown <= 0f || drawing is null || !Draw.Begin(canvas))
        {
            return;
        }

        try
        {
            var eased = 1f - (1f - _shown) * (1f - _shown);
            Shown = eased;
            Draw.Fade(eased);
            Shapes.Opacity = eased;
            drawing((1f - eased) * Slide, seconds);
        }
        finally
        {
            Draw.Fade(1f);
            Shapes.Opacity = 1f;
            Draw.Place(0f, 0f, 1f);
            Draw.End();
        }
    }
}
