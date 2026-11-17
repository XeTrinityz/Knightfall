#nullable enable

using System.Diagnostics;

namespace Knightfall;

// The WAVES counter in the top right corner. A ring with a segment for each enemy of the wave,
// which goes dark when they're down, the wave number inside it, and a status block beside it.
// During the break the ring fills back up as a countdown. One design for every theme.
static class WaveHud
{
    const float Edge = 56f;
    const float Top = 50f;
    const float ShowSeconds = 0.3f;

    // The ring's backing disc, the segment band and the hairline inside it.
    const float Disc = 72f;
    const float BandOuter = 64f;
    const float BandInner = 50f;
    const float Inner = 41f;

    // Ticks in the countdown ring.
    const int Ticks = 24;

    // How long INCOMING stays after a wave is sent.
    const float IncomingFor = 1.6f;

    const float CaptionSize = 15f;
    const float StatusSize = 26f;
    const float FootSize = 16f;
    const float NumberSize = 44f;
    const float LabelSize = 12f;

    static readonly Rgba White = new(255, 255, 255);
    static readonly Rgba Pale = new(206, 228, 240);
    static readonly Rgba Dim = new(130, 160, 180);
    static readonly Rgba Ghost = new(84, 110, 130);
    static readonly Rgba Live = new(250, 184, 78);
    static readonly Rgba Back = new(6, 15, 26);
    static readonly Rgba Hair = new(206, 228, 240, 80);
    static readonly Rgba Shade = new(3, 9, 18, 130);

    // The room kept between the counter and the menu when it moves out of the way.
    const float Clear = 14f;

    // How quickly it glides out of the menu's way and back, per second.
    const float MoveEase = 12f;

    // The counter's width from the status block's shade to the ring's right edge.
    const float Wide = 236f + 18f + Disc * 2f;

    static long _drawnAt;
    static float _shown;
    static float _moveX;
    static float _moveY;

    static bool GameHudShown() =>
        World.Controller is not { IsValid: true } controller
        || (controller.myHUD is not { IsValid: true, bShowHUD: false } && !controller.IsPhotoMode());

    public static void Render(BmSDK.Engine.Canvas canvas)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _drawnAt == 0 ? 0f : MathF.Min((float)((now - _drawnAt) / (double)Stopwatch.Frequency), 0.1f);
        _drawnAt = now;

        var wanted = Waves.On.On && !World.Paused && GameHudShown() && World.Controller is { bCinematicMode: false } ? 1f : 0f;
        _shown = Math.Clamp(_shown + (wanted > _shown ? 1f : -1f) * seconds / ShowSeconds, 0f, 1f);
        if (_shown <= 0f || !Draw.Begin(canvas))
        {
            return;
        }

        var under = Shapes.Under;
        try
        {
            var eased = 1f - (1f - _shown) * (1f - _shown);
            Draw.Fade(eased);

            // The ring's triangles can't fade, so it slides in from off the edge.
            Dodge(seconds);
            Layout((1f - eased) * (Disc * 2f + Edge + 40f));
        }
        finally
        {
            Shapes.Under = under;
            Draw.Fade(1f);
            Draw.Place(0f, 0f, 1f);
            Draw.End();
        }
    }

    static void Layout(float slide)
    {
        var number = Waves.Number;
        var nextIn = Waves.NextIn;
        var incoming = number > 0 && World.Now - Waves.SentAt < IncomingFor;
        var resting = !incoming && nextIn >= 0f;
        var cleared = resting && number > 0;
        var left = Waves.Left;
        var total = Math.Max(Waves.Total, left);

        var cx = Draw.ScreenWidth - Edge - Disc + slide + _moveX;
        var cy = Top + Disc + _moveY;

        Ring(cx, cy, incoming, resting, nextIn, left, total);

        // The number inside the ring.
        var label = "WAVE";
        Draw.Tracked(label, cx - Draw.TrackedWidth(label, LabelSize, 3f) / 2f, cy - 30f, LabelSize, 3f, Ghost);
        var shown = Math.Max(number, 1).ToString("00");
        Draw.Text(shown, cx - Draw.TextWidth(shown, NumberSize) / 2f, cy - 16f, NumberSize, incoming ? Live : White);

        // The status block to its left, on a soft shade so it reads over bright streets.
        var right = cx - Disc - 18f;
        Draw.Rect(right - 236f, cy - 52f, 248f, 104f, Shade);

        var caption = cleared ? "WAVE CLEARED" : "WAVES";
        Draw.Tracked(caption, right - Draw.TrackedWidth(caption, CaptionSize, 4f), cy - 44f, CaptionSize, 4f, Ghost);

        var status = incoming ? $"INCOMING {Waves.Planned}"
            : resting ? $"{(cleared ? "NEXT" : "FIRST WAVE")} IN {MathF.Ceiling(nextIn):0}"
            : $"{left} OF {total} LEFT";
        Draw.TextRight(status, right, cy - 24f, StatusSize, incoming || resting ? Live : White);

        Draw.Rect(right - 170f, cy + 10f, 170f, 1f, Hair);
        Draw.TextRight($"DOWN {Waves.Down}   BEST {Waves.Best}", right, cy + 18f, FootSize, Dim);
    }

    // A menu that sits in the top right, like MILITIA, is drawn over this corner. The counter moves
    // to the menu's left while it's open, or below it when there's no room there. The menu's area is
    // the one from the last frame, since the menu is drawn after this.
    static void Dodge(float seconds)
    {
        var x = 0f;
        var y = 0f;
        var right = Draw.ScreenWidth - Edge;
        var home = new Theme.Area(right - Wide, Top, right, Top + Disc * 2f);
        if (Theme.MenuArea is { } menu && menu.Overlaps(home))
        {
            if (menu.Left - Clear - Wide >= Clear)
            {
                x = menu.Left - Clear - right;
            }
            else
            {
                y = menu.Bottom + Clear - Top;
            }
        }

        var glide = 1f - MathF.Exp(-MoveEase * seconds);
        _moveX += (x - _moveX) * glide;
        _moveY += (y - _moveY) * glide;
    }

    static void Ring(float cx, float cy, bool incoming, bool resting, float nextIn, int left, int total)
    {
        Draw.Place(cx, cy, 1f);
        Shapes.Under = Back;
        Shapes.Disc(Disc, Back);
        Shapes.Ring(Disc - 1.5f, Disc, Ghost, 0.7f);

        if (resting)
        {
            var length = Waves.BreakLength;
            var done = length > 0f ? Math.Clamp(1f - nextIn / length, 0f, 1f) : 1f;
            var lit = (int)MathF.Round(done * Ticks);
            const float Step = 360f / Ticks;
            for (var i = 0; i < Ticks; i++)
            {
                Shapes.Arc(BandInner + 4f, BandOuter - 2f, i * Step + 1.5f, (i + 1) * Step - 1.5f, i < lit ? Live : Ghost, i < lit ? 1f : 0.45f);
            }
        }
        else if (total > 0)
        {
            var step = 360f / total;
            var gap = total > 20 ? 0.8f : 2f;
            for (var i = 0; i < total; i++)
            {
                var colour = incoming ? Live : i < left ? Pale : Ghost;
                Shapes.Arc(BandInner, BandOuter, i * step + gap, (i + 1) * step - gap, colour, incoming || i < left ? 1f : 0.45f);
            }
        }

        Shapes.Ring(Inner - 0.75f, Inner + 0.75f, Pale, 0.25f);
        Draw.Place(0f, 0f, 1f);
    }
}
