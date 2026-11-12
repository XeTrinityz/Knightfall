#nullable enable

namespace Knightfall;

// The speedometer as a hologram the Batmobile projects beside itself. It is one design for every
// theme, in the theme's colours, since a solid panel standing in the street looks out of place.
// It is laid out flat here and Hologram warps it onto the panel in the world.
static class HoloDash
{
    public const float Width = 220f;
    public const float Height = 150f;

    const float DialX = Width / 2f;
    const float DialY = 82f;
    const float DialRadius = 60f;
    const int Dots = 41;

    // The dial runs clockwise from lower left, over the top, to lower right.
    const float DialFrom = 210f;
    const float DialSweep = 240f;

    static readonly Rgba White = new(255, 255, 255);

    public static void Render(DashView view, Rgba accent, Rgba armed)
    {
        var hot = view.Hot ? armed : accent;
        var bright = Rgba.Mix(accent, White, 0.7f);

        // A projection is never quite steady.
        Draw.Fade(view.Shown * (0.9f + 0.1f * MathF.Sin(view.Time * 9f)));

        Draw.GradientDown(0f, 0f, Width, Height, accent with { A = 58 }, accent with { A = 16 });
        for (var y = 2f; y < Height - 1f; y += 4f)
        {
            Draw.Rect(1f, y, Width - 2f, 1f, accent with { A = 18 });
        }

        Kit.Sheen(1f, 1f, Width - 2f, Height - 2f, view.Time, accent with { A = 30 });
        Draw.Frame(0f, 0f, Width, Height, 1f, accent with { A = 120 });
        Draw.Frame(-1f, -1f, Width + 2f, Height + 2f, 1f, accent with { A = 40 });
        Kit.Corners(0f, 0f, Width, Height, 14f, bright);

        if (view.Mode.Length > 0)
        {
            var width = Draw.TrackedWidth(view.Mode, 10f, 3f);
            Draw.Tracked(view.Mode, (Width - width) / 2f, 8f, 10f, 3f, hot);
        }

        Dial(view.SpeedFill, view.Hot ? armed : bright, accent);

        // A soft halo round the figures, the way projected light bleeds.
        var speed = Draw.TextWidth(view.Speed, 42f);
        var left = DialX - speed / 2f;
        var top = DialY - 30f;
        Draw.Text(view.Speed, left - 1f, top, 42f, accent with { A = 90 });
        Draw.Text(view.Speed, left + 1f, top, 42f, accent with { A = 90 });
        Draw.Text(view.Speed, left, top, 42f, bright);
        var units = Draw.TrackedWidth(view.Units, 10f, 3f);
        Draw.Tracked(view.Units, DialX - units / 2f, DialY + 16f, 10f, 3f, accent);

        var armour = view.LowArmour ? Kit.Failed : accent;
        Bar(12f, 100f, "ARMOUR", view.Armour, view.ArmourText, armour, accent);
        Bar(120f, 208f, "POWER", view.Power, view.PowerText, armed, accent);

        Draw.Fade(1f);
    }

    // A ring of dots that light up with the speed. Every fifth one is a little larger.
    static void Dial(float fill, Rgba lit, Rgba accent)
    {
        var count = (int)MathF.Round(fill * (Dots - 1));
        for (var i = 0; i < Dots; i++)
        {
            var angle = (DialFrom - DialSweep * i / (Dots - 1)) * MathF.PI / 180f;
            var size = i % 5 == 0 ? 6f : 4f;
            var x = DialX + MathF.Cos(angle) * DialRadius - size / 2f;
            var y = DialY - MathF.Sin(angle) * DialRadius - size / 2f;
            Draw.Rect(x, y, size, size, i <= count && fill > 0f ? lit : accent with { A = 70 });
        }
    }

    static void Bar(float left, float right, string label, float fill, string value, Rgba lit, Rgba accent)
    {
        const float top = 118f;
        Draw.Tracked(label, left, top, 9f, 2f, accent);
        Draw.TextRight(value, right, top - 2f, 12f, lit);
        Draw.Rect(left, top + 14f, right - left, 3f, accent with { A = 70 });
        Draw.Rect(left, top + 14f, (right - left) * Math.Clamp(fill, 0f, 1f), 3f, lit);
    }
}
