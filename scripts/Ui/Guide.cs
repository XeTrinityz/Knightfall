#nullable enable

using System.Diagnostics;
using BmSDK.Engine;

namespace Knightfall;

// A line of a key guide: the keys, and what they do.
readonly record struct GuideLine(string[] Keys, string Label);

// What a key guide shows. Whatever needs one fills it, and the theme draws it.
sealed class GuideView
{
    public float Shown;
    public float Time;
    public string Title = "";
    public readonly List<GuideLine> Lines = new();
}

// The on-screen key guide, for modes with controls of their own like the free camera. It works
// out when it shows and how far it has faded in, and the current theme draws it. It hides while
// the menu is open, which sits in the same place.
static class Guide
{
    const float InSeconds = 0.2f;
    const float OutSeconds = 0.15f;
    const double LoopSeconds = 3600;

    static readonly GuideView View = new();
    static long _lastFrame;
    static float _progress;

    public static void Update()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _lastFrame == 0 ? 0f : MathF.Min((float)((now - _lastFrame) / (double)Stopwatch.Frequency), 0.1f);
        _lastFrame = now;
        View.Time = (float)(now / (double)Stopwatch.Frequency % LoopSeconds);

        // Left as it was while fading out, so it fades with what it showed.
        var showing = !World.Paused && !Menu.Showing && (EditorMode.Guide(View) || FreeCamera.Guide(View));
        _progress = showing
            ? MathF.Min(1f, _progress + seconds / InSeconds)
            : World.Paused ? 0f : MathF.Max(0f, _progress - seconds / OutSeconds);
        View.Shown = _progress * (2f - _progress);
    }

    public static void Render(Canvas canvas)
    {
        if (View.Shown <= 0f || View.Lines.Count == 0 || !Draw.Begin(canvas))
        {
            return;
        }

        try
        {
            Theme.Current.RenderGuide(View);
        }
        finally
        {
            Draw.End();
        }
    }
}
