#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;

namespace Knightfall;

// Poison Ivy's HUD: a herbarium sheet, the kind a botanist presses a plant onto. Her vine is a
// pressed sprig over a ruler that fills as it recharges. Each flytrap is a specimen tag that
// shows the plant as it is: sprouting, hungry, biting or full. A ledger counts the thugs eaten
// and the machines torn apart, and what she says is pinned to the sheet as a note.
static partial class PoisonIvy
{
    static readonly Rgba Paper = new(236, 228, 204);
    static readonly Rgba PaperLight = new(247, 241, 223);
    static readonly Rgba PaperEdge = new(185, 171, 134);
    static readonly Rgba PaperLine = new(207, 195, 162);
    static readonly Rgba Ink = new(43, 36, 24);
    static readonly Rgba Brown = new(107, 95, 69);
    static readonly Rgba Faint = new(138, 124, 90);
    static readonly Rgba Leaf = new(94, 138, 69);
    static readonly Rgba LeafLight = new(122, 168, 92);
    static readonly Rgba Stem = new(77, 107, 56);
    static readonly Rgba Deep = new(45, 74, 42);
    static readonly Rgba Rose = new(168, 36, 63);
    static readonly Rgba Maw = new(143, 42, 62);
    static readonly Rgba Shade = new(0, 0, 0, 150);

    // Laid out on the 1080-high screen.
    const float SheetLeft = 36f;
    const float SheetBottom = 36f;
    const float SheetWidth = 304f;
    const float SheetHeight = 158f;
    const float Margin = 11f;
    const float SprigWidth = 100f;
    const float TagGap = 5f;
    const float TagHeight = 50f;

    // A new mode flashes its stamp. A note stays this long, and drops in and fades over these.
    const float FlashSeconds = 0.35f;
    const float NoteHolds = 5f;
    const float NoteDrops = 0.3f;
    const float NoteFades = 0.5f;
    const float NoteTilt = 3f;

    // The stamp reads this for a moment after she casts or plants.
    const float CastShows = 0.5f;

    // Machines her vine has destroyed while she has been on.
    static int _wrecked;
    static float _plantedAt = -100f;

    static string _stamp = "";
    static long _stampAt;

    static readonly string[] Percents = Enumerable.Range(0, 101).Select(n => n + "%").ToArray();
    static readonly string[] Counts = Enumerable.Range(0, 1000).Select(n => n.ToString("00")).ToArray();
    static readonly string[] Seconds = Enumerable.Range(0, 10).Select(n => n + "s").ToArray();
    static readonly string[] Planted = Enumerable.Range(0, TrapsAtOnce + 1).Select(n => $"SPECIMENS PLANTED · {n} OF {TrapsAtOnce}").ToArray();
    static readonly Dictionary<string, string> Quoted = new();

    static void ShowHud() => VillainHud.Show(Hud.On ? DrawHud : null);

    static void ResetHud()
    {
        _wrecked = 0;
        _plantedAt = -100f;
        _stamp = "";
    }

    static void DrawHud(float rise, float seconds)
    {
        var now = Stopwatch.GetTimestamp();
        var x = SheetLeft;
        var y = Draw.ScreenHeight - SheetBottom - SheetHeight + rise;
        Shapes.Under = Paper;

        Draw.Rect(x + 3f, y + 5f, SheetWidth, SheetHeight, Shade);
        Draw.Rect(x, y, SheetWidth, SheetHeight, Paper);
        Draw.Frame(x, y, SheetWidth, SheetHeight, 1f, PaperEdge);
        Draw.Frame(x + 4f, y + 4f, SheetWidth - 8f, SheetHeight - 8f, 1f, PaperLine);

        var left = x + Margin;
        var right = x + SheetWidth - Margin;
        Heading(left, y + 9f, right, now);
        Sprig(left, y + 40f);
        Specimens(left + SprigWidth + 12f, y + 40f, right);
        Ledger(left + SprigWidth + 12f, y + 40f + 12f + TagHeight + 8f, right);
        Keys(left, y + SheetHeight - 20f);
        Note(x + SheetWidth - 150f, y - 6f);
    }

    static void Heading(float x, float y, float right, long now)
    {
        Draw.Tracked("TOXICODENDRON ISLEYI", x, y, 12f, 1.5f, Deep);
        Draw.Tracked("POISON IVY · GOTHAM BOTANICAL GARDENS", x, y + 16f, 7f, 1.5f, Brown);

        var stamp = Stamp();
        if (stamp != _stamp)
        {
            _stamp = stamp;
            _stampAt = now;
        }

        var width = Draw.TrackedWidth(stamp, 8f, 2f) + 10f;
        var at = right - width;
        var flash = 1f - Math.Clamp((float)((now - _stampAt) / (double)Stopwatch.Frequency) / FlashSeconds, 0f, 1f);
        var ink = flash > 0f ? Rgba.Mix(Rose, Ink, flash * 0.6f) : Rose;
        Draw.Frame(at, y - 1f, width, 14f, 1.5f, ink);
        Draw.Tracked(stamp, at + 5f, y + 2f, 8f, 2f, ink);
    }

    static string Stamp()
    {
        var now = World.Now;
        if (now - _plantedAt < CastShows)
        {
            return "PLANTED";
        }

        if (now < _castUntil - CastSeconds + CastShows)
        {
            return "VINE!";
        }

        return Aim.On ? "AIMING" : "IN BLOOM";
    }

    // A pressed sprig: a stem with leaves along it, over a ruler that fills as the vine recharges.
    static void Sprig(float x, float y)
    {
        Vector2 Along(float u) => new(x + 4f + u * (SprigWidth - 8f), y + 50f - 46f * u * (1.4f - 0.4f * u));
        const int steps = 8;
        for (var i = 0; i < steps; i++)
        {
            var a = Along(i / (float)steps);
            var b = Along((i + 1) / (float)steps);
            Thick(a, b, 2f - i * 0.15f, Stem);
        }

        // Leaves on alternate sides, each a long diamond angled forward off the stem.
        for (var i = 1; i <= 6; i++)
        {
            var u = i / 7f;
            var at = Along(u);
            var ahead = Vector2.Normalize(Along(u + 0.02f) - at);
            var side = i % 2 == 0 ? 1f : -1f;
            var outward = new Vector2(-ahead.Y, ahead.X) * side;
            var tip = at + Vector2.Normalize(outward * 1.6f + ahead) * 13f;
            var mid = at + (tip - at) * 0.45f;
            var across = Vector2.Normalize(new Vector2(tip.Y - at.Y, at.X - tip.X)) * 3.5f;
            Draw.Quad(at.X, at.Y, mid.X + across.X, mid.Y + across.Y, tip.X, tip.Y, mid.X - across.X, mid.Y - across.Y, i % 2 == 0 ? LeafLight : Leaf);
            Draw.Line(at.X, at.Y, mid.X + (tip.X - mid.X) * 0.6f, mid.Y + (tip.Y - mid.Y) * 0.6f, Deep);
        }

        var ready = VineReady;
        var by = y + 60f;
        Draw.Tracked("VINE", x, by, 7f, 1.5f, Brown);
        var told = Percents[(int)MathF.Round(ready * 100f)];
        Draw.Tracked(told, x + SprigWidth - Draw.TrackedWidth(told, 7f, 1f), by, 7f, 1f, ready >= 1f ? Deep : Brown);

        var ry = by + 11f;
        Draw.Rect(x, ry, SprigWidth, 10f, PaperLight);
        var filled = SprigWidth * ready;
        for (var at = 0f; at < filled; at += 8f)
        {
            Draw.Rect(x + at, ry, MathF.Min(4f, filled - at), 10f, Stem);
            Draw.Rect(x + at + 4f, ry, MathF.Max(0f, MathF.Min(4f, filled - at - 4f)), 10f, Leaf);
        }

        for (var at = 10f; at < SprigWidth; at += 10f)
        {
            Draw.Rect(x + at, ry + (at % 50f == 0f ? 0f : 5f), 1f, at % 50f == 0f ? 10f : 5f, Faint);
        }

        Draw.Frame(x, ry, SprigWidth, 10f, 1f, Faint);
        Draw.Tracked("0", x, ry + 13f, 6f, 0f, Faint);
        Draw.Tracked("5", x + SprigWidth / 2f - 2f, ry + 13f, 6f, 0f, Faint);
        Draw.Tracked("10 CM", x + SprigWidth - Draw.TrackedWidth("10 CM", 6f, 0.5f), ry + 13f, 6f, 0.5f, Faint);
    }

    static float VineReady => Math.Clamp(1f - (_nextVine - World.Now) / VineWait, 0f, 1f);

    static float TrapReady => Math.Clamp(1f - (_nextTrap - World.Now) / TrapWait, 0f, 1f);

    // A tag for each flytrap standing, and an empty one for the next seed.
    static void Specimens(float x, float y, float right)
    {
        Draw.Tracked(Planted[Math.Clamp(Traps.Count, 0, TrapsAtOnce)], x, y, 7f, 1.5f, Brown);
        var ty = y + 12f;
        var width = (right - x - TagGap * (TrapsAtOnce - 1)) / TrapsAtOnce;
        for (var i = 0; i < TrapsAtOnce; i++)
        {
            var tx = x + i * (width + TagGap);
            if (i < Traps.Count)
            {
                Tag(tx, ty, width, i, Traps[i]);
            }
            else
            {
                Empty(tx, ty, width, i == Traps.Count);
            }
        }
    }

    static void Tag(float x, float y, float width, int index, Trap trap)
    {
        var now = World.Now;
        var cx = x + width / 2f;
        var gy = y + 21f;
        string state;
        string value;
        Rgba edge;
        if (trap.Sinking)
        {
            state = "WILTING";
            value = "";
            edge = Faint;
        }
        else if (!trap.Grown)
        {
            state = "SPROUT";
            value = Percents[(int)MathF.Round(Math.Clamp((now - trap.Born) / GrowSeconds, 0f, 1f) * 100f)];
            edge = Faint;
        }
        else if (trap.Swallowed)
        {
            state = "FULL";
            value = Seconds[Math.Clamp((int)MathF.Ceiling(DigestSeconds - (now - trap.SwallowedAt)), 0, Seconds.Length - 1)];
            edge = Rose;
        }
        else if (trap.Meal is not null)
        {
            state = "BITING";
            value = "!";
            edge = Rose;
        }
        else
        {
            state = "HUNGRY";
            value = "READY";
            edge = Stem;
        }

        Draw.Rect(x, y, width, TagHeight, PaperLight);
        Draw.Frame(x, y, width, TagHeight, 1f, edge);
        Glyph(cx, gy, trap, now);
        var label = $"NO.{index + 1} {state}";
        Draw.Tracked(label, cx - Draw.TrackedWidth(label, 6f, 0.5f) / 2f, y + 31f, 6f, 0.5f, edge == Faint ? Brown : edge);
        Draw.Tracked(value, cx - Draw.TrackedWidth(value, 8f, 0.5f) / 2f, y + 39f, 8f, 0.5f, Ink);

        // Its time left, draining along the tag's foot.
        if (trap.Grown && !trap.Sinking)
        {
            var left = Math.Clamp(1f - trap.Lived / (float)TrapLife.Value, 0f, 1f);
            Draw.Rect(x + 3f, y + TagHeight - 4f, (width - 6f) * left, 2f, left < 0.25f ? Rose : Stem);
        }
    }

    // The plant as it is: a sprout, a closed pod digesting, jaws snapping shut, or jaws wide open.
    static void Glyph(float cx, float gy, Trap trap, float now)
    {
        if (!trap.Grown || trap.Sinking)
        {
            Sprout(cx, gy);
        }
        else if (trap.Swallowed)
        {
            Pod(cx, gy, 1f - 0.12f * (0.5f + 0.5f * MathF.Sin(now * MathF.Tau / 0.9f)));
        }
        else
        {
            Jaws(cx, gy, trap.Meal is not null ? 0.3f : 1f);
        }
    }

    // An empty slot, dashed. The next one shows how near the next seed is.
    static void Empty(float x, float y, float width, bool next)
    {
        Dashed(x, y, width, TagHeight, Faint);
        var cx = x + width / 2f;
        if (TrapMissing)
        {
            Draw.Tracked("NEEDS", cx - Draw.TrackedWidth("NEEDS", 6f, 0.5f) / 2f, y + 16f, 6f, 0.5f, Rose);
            Draw.Tracked("HARLEY", cx - Draw.TrackedWidth("HARLEY", 6f, 0.5f) / 2f, y + 25f, 6f, 0.5f, Rose);
            Draw.Tracked("PACK", cx - Draw.TrackedWidth("PACK", 6f, 0.5f) / 2f, y + 34f, 6f, 0.5f, Rose);
            return;
        }

        if (!next)
        {
            return;
        }

        var ready = TrapReady;
        var label = ready >= 1f ? "SEED READY" : "NEXT SEED";
        Draw.Tracked(label, cx - Draw.TrackedWidth(label, 6f, 0.5f) / 2f, y + 31f, 6f, 0.5f, ready >= 1f ? Deep : Brown);
        var value = Percents[(int)MathF.Round(ready * 100f)];
        Draw.Tracked(value, cx - Draw.TrackedWidth(value, 8f, 0.5f) / 2f, y + 39f, 8f, 0.5f, Ink);
        Draw.Rect(cx - 1f, y + 14f, 2f, 10f * ready, Stem);
        Draw.Rect(cx - 4f, y + 24f, 8f, 2f, Faint);
    }

    // A full flytrap: a closed red pod, teeth showing at the seam, squeezing as it digests.
    static void Pod(float cx, float cy, float squeeze)
    {
        Draw.Rect(cx - 1f, cy + 2f, 2f, 7f, Stem);
        const int steps = 10;
        var half = 13f;
        for (var i = 0; i < steps; i++)
        {
            var u0 = -1f + 2f * i / steps;
            var u1 = -1f + 2f * (i + 1) / steps;
            float Top(float u) => cy - 9f * squeeze * (1f - u * u);
            float Bottom(float u) => cy + 3f * (1f - u * u);
            Draw.Quad(cx + u0 * half, Top(u0), cx + u1 * half, Top(u1), cx + u1 * half, Bottom(u1), cx + u0 * half, Bottom(u0), Maw);
        }

        for (var i = -2; i <= 2; i++)
        {
            Draw.Line(cx + i * 4f, cy - 1f, cx + i * 4f + 1f, cy - 4f, PaperLight);
        }
    }

    // A hungry flytrap: two jaws open on a stem, with teeth. Open is how wide, 1 for wide open.
    static void Jaws(float cx, float cy, float open)
    {
        Draw.Rect(cx - 1f, cy, 2f, 9f, Stem);
        var spread = 3f + 10f * open;
        for (var side = -1f; side <= 1f; side += 2f)
        {
            // Each jaw is a wedge from the hinge out to its tip, with its back held low.
            var tipX = cx + side * 14f;
            var tipY = cy - spread;
            var backX = cx + side * 13f;
            var backY = cy - spread * 0.2f;
            Draw.Quad(cx, cy, tipX, tipY, backX + side * 1f, (tipY + backY) / 2f, backX, backY, Leaf);
            Draw.Line(cx, cy, tipX, tipY, Deep);
            for (var t = 0.45f; t <= 1f; t += 0.27f)
            {
                var px = cx + (tipX - cx) * t;
                var py = cy + (tipY - cy) * t;
                Draw.Line(px, py, px + side * 1f, py - 3f, Deep);
            }
        }
    }

    // A flytrap still coming up: two small leaves on a short stem.
    static void Sprout(float cx, float cy)
    {
        Draw.Rect(cx - 1f, cy - 2f, 2f, 11f, Stem);
        Draw.Triangle(cx, cy - 1f, cx - 8f, cy - 8f, cx - 6f, cy - 1f, LeafLight);
        Draw.Triangle(cx, cy - 1f, cx + 8f, cy - 8f, cx + 6f, cy - 1f, LeafLight);
    }

    // Thugs eaten and machines torn apart, written in on dotted lines.
    static void Ledger(float x, float y, float right)
    {
        var mid = x + (right - x) / 2f;
        Entry(x, y, mid - 6f, "CONSUMED", Counts[Math.Clamp(_eaten, 0, Counts.Length - 1)], Deep);
        Entry(mid + 6f, y, right, "MACHINES", Counts[Math.Clamp(_wrecked, 0, Counts.Length - 1)], Rose);
    }

    static void Entry(float x, float y, float right, string label, string count, Rgba color)
    {
        Draw.Tracked(label, x, y + 6f, 6.5f, 1.5f, Brown);
        Draw.Tracked(count, right - Draw.TrackedWidth(count, 13f, 0.5f), y, 13f, 0.5f, color);
        for (var at = x; at < right; at += 3f)
        {
            Draw.Rect(at, y + 15f, 1f, 1f, Faint);
        }
    }

    static void Keys(float x, float y)
    {
        var pad = Controls.LastDevice == Device.Pad;
        var after = Chip(pad ? Pad.Describe(Pad.Button.RT) : "LMB", x, y);
        after = Draw.Tracked("VINE", after + 5f, y + 3f, 7f, 1.5f, Brown) + 12f;
        after = Chip(pad ? Pad.Describe(Pad.Button.RB) : "MMB", after, y);
        Draw.Tracked("FLYTRAP", after + 5f, y + 3f, 7f, 1.5f, Brown);
    }

    static float Chip(string key, float x, float y)
    {
        var width = Draw.TrackedWidth(key, 7f, 1f) + 8f;
        Draw.Frame(x, y, width, 13f, 1f, Brown);
        Draw.Tracked(key, x + 4f, y + 3f, 7f, 1f, Ink);
        return x + width;
    }

    // What she said, on a note pinned to the sheet's top edge. It drops in, holds and fades.
    static void Note(float x, float y)
    {
        var since = World.Now - _saidAt;
        if (_saidAt < 0f || _said.Length == 0 || since > NoteHolds + NoteFades)
        {
            return;
        }

        if (!Quoted.TryGetValue(_said, out var quote))
        {
            Quoted[_said] = quote = "\"" + _said + "\"";
        }

        var drop = 1f - Math.Clamp(since / NoteDrops, 0f, 1f);
        var fade = 1f - Math.Clamp((since - NoteHolds) / NoteFades, 0f, 1f);
        var width = Draw.TrackedWidth(quote, 9f, 0.5f) + 18f;
        const float height = 30f;
        var cx = x + 75f;
        var cy = y - height / 2f - 10f * drop;
        var turn = NoteTilt * MathF.PI / 180f;
        var cos = MathF.Cos(turn);
        var sin = MathF.Sin(turn);
        Vector2 At(float lx, float ly) => new(cx + lx * cos - ly * sin, cy + lx * sin + ly * cos);
        void Box(float l, float t, float r, float b, Rgba color, float dx = 0f, float dy = 0f)
        {
            var p0 = At(l, t);
            var p1 = At(r, t);
            var p2 = At(r, b);
            var p3 = At(l, b);
            Draw.Quad(p0.X + dx, p0.Y + dy, p1.X + dx, p1.Y + dy, p2.X + dx, p2.Y + dy, p3.X + dx, p3.Y + dy, color);
        }

        Draw.Fade(fade * VillainHud.Shown);
        Shapes.Opacity = fade * VillainHud.Shown;
        var hw = width / 2f;
        var hh = height / 2f;
        Box(-hw, -hh, hw, hh, Shade, 2f, 4f);
        Box(-hw, -hh, hw, hh, PaperEdge);
        Box(-hw + 1f, -hh + 1f, hw - 1f, hh - 1f, PaperLight);
        Draw.Tracked(quote, cx - (width - 18f) / 2f, cy - 7f, 9f, 0.5f, Deep);
        Draw.Tracked("- P. I.", cx + hw - 32f, cy + 5f, 6f, 1f, Faint);

        var pin = At(0f, -hh);
        Draw.Place(pin.X, pin.Y, 1f);
        Shapes.Under = PaperLight;
        Shapes.Disc(4f, Rose);
        Draw.Place(0f, 0f, 1f);
        Shapes.Under = Paper;
        Draw.Fade(VillainHud.Shown);
        Shapes.Opacity = VillainHud.Shown;
    }

    // A box drawn in short dashes.
    static void Dashed(float x, float y, float width, float height, Rgba color)
    {
        for (var at = x; at < x + width; at += 4f)
        {
            Draw.Rect(at, y, MathF.Min(2f, x + width - at), 1f, color);
            Draw.Rect(at, y + height - 1f, MathF.Min(2f, x + width - at), 1f, color);
        }

        for (var at = y; at < y + height; at += 4f)
        {
            Draw.Rect(x, at, 1f, MathF.Min(2f, y + height - at), color);
            Draw.Rect(x + width - 1f, at, 1f, MathF.Min(2f, y + height - at), color);
        }
    }

    // A line of some thickness, as a thin quad.
    static void Thick(Vector2 a, Vector2 b, float width, Rgba color)
    {
        var way = b - a;
        if (way.LengthSquared() < 1e-4f)
        {
            return;
        }

        var side = Vector2.Normalize(new Vector2(-way.Y, way.X)) * (width / 2f);
        Draw.Quad(a.X + side.X, a.Y + side.Y, b.X + side.X, b.Y + side.Y, b.X - side.X, b.Y - side.Y, a.X - side.X, a.Y - side.Y, color);
    }
}
