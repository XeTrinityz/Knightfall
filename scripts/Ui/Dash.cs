#nullable enable

using System.Diagnostics;
using BmSDK.Engine;

namespace Knightfall;

// The driving readout. It works out when it shows and how far it has faded in, and the current
// theme draws it. On the car it is drawn onto a panel in the world by Hologram.
static class Dash
{
    const float InSeconds = 0.25f;
    const float OutSeconds = 0.18f;
    const double LoopSeconds = 3600;

    static readonly DashView View = new();
    static long _lastFrame;
    static float _seconds;
    static float _progress;

    // Whether the panel on the car was in sight last frame. Only known while drawing.
    static bool _inSight = true;

    public static void Update()
    {
        var now = Stopwatch.GetTimestamp();
        _seconds = _lastFrame == 0 ? 0f : MathF.Min((float)((now - _lastFrame) / (double)Stopwatch.Frequency), 0.1f);
        _lastFrame = now;
        View.Time = (float)(now / (double)Stopwatch.Frequency % LoopSeconds);

        var car = World.Paused ? null : Dashboard.Showing();
        if (car is not null)
        {
            Dashboard.Fill(View, car, _seconds);
        }

        if (car is not null && (View.Spot != DashSpot.OnCar || _inSight))
        {
            _progress = MathF.Min(1f, _progress + _seconds / InSeconds);
            var left = 1f - _progress;
            View.Shown = 1f - left * left * left;
        }
        else
        {
            // Straight away in the pause menu, which would otherwise draw over a fading readout.
            _progress = World.Paused ? 0f : MathF.Max(0f, _progress - _seconds / OutSeconds);
            View.Shown = _progress * _progress;
        }
    }

    public static void Render(Canvas canvas)
    {
        var onCar = View.Spot == DashSpot.OnCar && World.Driving is not null;
        if ((View.Shown <= 0f && !onCar) || !Draw.Begin(canvas))
        {
            return;
        }

        try
        {
            // Checked even while hidden, so it knows when to come back.
            if (onCar && World.Controller is { } controller && World.Driving is { } car)
            {
                controller.GetPlayerViewPoint(out var eye, out var turn);
                var fieldOfView = controller.PlayerCamera is { IsValid: true } camera ? camera.GetFOVAngle() : controller.GetFOVAngle();
                Hologram.Frame(canvas, eye, turn, fieldOfView);
                _inSight = !Hologram.Available || Hologram.Seen(View.Panel, HoloDash.Width, HoloDash.Height, car);
            }

            if (View.Shown <= 0f)
            {
                return;
            }

            // On the car it is one hologram in the theme's colours. Elsewhere the theme draws it.
            var theme = Theme.Current;
            if (onCar && Hologram.Available)
            {
                var accent = Look.AccentOr(theme.Accent);
                Hologram.Beams(View.Panel, View.Emitter, View.Shown, HoloDash.Width, HoloDash.Height, accent);
                if (Hologram.Place(View.Panel, HoloDash.Width, HoloDash.Height))
                {
                    HoloDash.Render(View, accent, theme.Armed);
                }
            }
            else
            {
                theme.RenderDash(View);
            }
        }
        finally
        {
            Hologram.End();
            Draw.End();
        }
    }
}
