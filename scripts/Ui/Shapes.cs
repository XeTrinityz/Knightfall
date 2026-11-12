#nullable enable

namespace Knightfall;

// Round shapes made of the game's triangles: rings, wedges, discs, spokes and ticks. Angles are in
// degrees, 0 at the top going clockwise. Triangles are always drawn solid, so a see-through look
// is mixed against the colour under it, which the caller keeps in Under, and Opacity fades them
// toward it the same way.
static class Shapes
{
    // No piece of an arc spans more than this, so curves stay round.
    const float Smooth = 5f;

    public static Rgba Under = new(0, 0, 0);
    public static float Opacity = 1f;

    static Rgba Tint(Rgba color, float alpha) => Rgba.Mix(Under, color with { A = 255 }, alpha * Opacity);

    static (float X, float Y) At(float radius, float degrees)
    {
        var (sin, cos) = MathF.SinCos(degrees * MathF.PI / 180f);
        return (sin * radius, -cos * radius);
    }

    // A band between two radii, from one angle to another.
    public static void Arc(float inner, float outer, float from, float to, Rgba color, float alpha = 1f)
    {
        if (to < from || outer <= inner)
        {
            return;
        }

        var tint = Tint(color, alpha);
        var pieces = Math.Max(1, (int)MathF.Ceiling((to - from) / Smooth));
        var step = (to - from) / pieces;
        for (var i = 0; i < pieces; i++)
        {
            var a = from + step * i;
            var b = a + step;
            var (ax, ay) = At(inner, a);
            var (bx, by) = At(outer, a);
            var (cx, cy) = At(outer, b);
            var (dx, dy) = At(inner, b);
            Draw.Quad(ax, ay, bx, by, cx, cy, dx, dy, tint);
        }
    }

    public static void Ring(float inner, float outer, Rgba color, float alpha = 1f) => Arc(inner, outer, 0f, 360f, color, alpha);

    public static void Disc(float radius, Rgba color, float alpha = 1f) => Arc(0f, radius, 0f, 360f, color, alpha);

    // A straight bar out from the middle, at an angle.
    public static void Spoke(float degrees, float inner, float outer, float thick, Rgba color, float alpha = 1f)
    {
        var (dx, dy) = At(1f, degrees);
        var (sx, sy) = (-dy * thick / 2f, dx * thick / 2f);
        var (ax, ay) = (dx * inner, dy * inner);
        var (bx, by) = (dx * outer, dy * outer);
        Draw.Quad(ax - sx, ay - sy, bx - sx, by - sy, bx + sx, by + sy, ax + sx, ay + sy, Tint(color, alpha));
    }

    public static void Ticks(float inner, float outer, int count, float turn, float thick, Rgba color, float alpha = 1f)
    {
        for (var i = 0; i < count; i++)
        {
            Spoke(turn + 360f * i / count, inner, outer, thick, color, alpha);
        }
    }

    // A dashed band, for slots with nothing in them.
    public static void Dashes(float inner, float outer, float from, float to, float dash, float gap, Rgba color, float alpha = 1f)
    {
        for (var at = from; at < to; at += dash + gap)
        {
            Arc(inner, outer, at, MathF.Min(at + dash, to), color, alpha);
        }
    }

    // A wedge-shaped arrow pointing out at an angle, its tip at the radius.
    public static void Notch(float degrees, float radius, float size, Rgba color, float alpha = 1f)
    {
        var (tx, ty) = At(radius, degrees);
        var (lx, ly) = At(radius - size, degrees - size * 0.6f * 180f / MathF.PI / radius);
        var (rx, ry) = At(radius - size, degrees + size * 0.6f * 180f / MathF.PI / radius);
        Draw.Triangle(tx, ty, lx, ly, rx, ry, Tint(color, alpha));
    }
}
