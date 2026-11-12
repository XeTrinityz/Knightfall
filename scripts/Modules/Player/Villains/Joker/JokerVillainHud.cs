#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// The Joker's HUD: his own playing card, tilted and swaying, with his grin on it, dealt beside a
// slim strip. The strip shows his shells as cartridges that kick when he pumps, his laughing gas
// as a green vial that bubbles as it fills, a row of HAs for the thugs he has put in stitches,
// and a log along the bottom typing out what he says.
static partial class JokerVillain
{
    static readonly Rgba Ink = new(16, 10, 22, 240);
    static readonly Rgba Solid = new(10, 6, 14);
    static readonly Rgba Edge = new(74, 43, 99);
    static readonly Rgba Green = new(123, 209, 74);
    static readonly Rgba GreenDeep = new(47, 143, 58);
    static readonly Rgba VialFill = new(76, 175, 58);
    static readonly Rgba VialBack = new(16, 32, 15);
    static readonly Rgba Bubble = new(200, 245, 168);
    static readonly Rgba Purple = new(106, 44, 156);
    static readonly Rgba CardEdge = new(91, 42, 134);
    static readonly Rgba Cream = new(244, 239, 228);
    static readonly Rgba Lilac = new(201, 191, 216);
    static readonly Rgba Red = new(179, 18, 43);
    static readonly Rgba Brass = new(217, 164, 65);
    static readonly Rgba Gold = new(242, 193, 78);
    static readonly Rgba Text = new(241, 236, 226);
    static readonly Rgba Dim = new(165, 156, 181);
    static readonly Rgba Empty = new(93, 74, 112);
    static readonly Rgba Shadow = new(0, 0, 0, 150);

    // Laid out on the 1080-high screen. The card overlaps the strip's left edge.
    const float HudLeft = 36f;
    const float HudBottom = 36f;
    const float CardWidth = 88f;
    const float CardHeight = 124f;
    const float CardOverlap = 12f;
    const float StripWidth = 252f;
    const float StripHeight = 118f;
    const float StripPad = 20f;
    const float Inset = 9f;

    // The card sways this many degrees either side of its tilt, this often.
    const float CardTilt = -4f;
    const float CardSway = 1f;
    const float SwaySeconds = 4f;

    // Shells share the row's width and are this tall, and kick back this far as he pumps.
    const float ShellHeight = 9f;
    const float ShellGap = 4f;
    const float PumpKick = 4f;

    // The log types this fast and stays bright this long. A new mode flashes its tag.
    const float TypeRate = 38f;
    const float LineHolds = 5f;
    const float FlashSeconds = 0.35f;
    const float BangSeconds = 0.35f;

    // At most this many HAs are drawn, the newest in gold.
    const int MostHas = 7;

    static string[] _line = Array.Empty<string>();
    static float _lineAt = -100f;
    static string _mode = "";
    static long _modeAt;

    static readonly string[] Loaded = Enumerable.Range(0, Shells + 1).Select(n => $"{n} / {Shells}").ToArray();
    static readonly string[] Pumped = Enumerable.Range(0, Shells + 1).Select(n => $"{n} / {Shells} · PUMP").ToArray();
    static readonly string[] Percents = Enumerable.Range(0, 101).Select(n => n + "%").ToArray();
    static readonly string[] Counts = Enumerable.Range(0, 1000).Select(n => n.ToString("00")).ToArray();

    static void Record(string[] typed)
    {
        _line = typed;
        _lineAt = World.Now;
    }

    static void ResetHud()
    {
        _line = Array.Empty<string>();
        _lineAt = -100f;
    }

    static void ShowHud() => VillainHud.Show(Hud.On ? DrawHud : null);

    static void DrawHud(float rise, float seconds)
    {
        var now = Stopwatch.GetTimestamp();
        var time = (float)(now / (double)Stopwatch.Frequency);
        var x = HudLeft + CardWidth - CardOverlap;
        var y = Draw.ScreenHeight - HudBottom - StripHeight + rise;
        Shapes.Under = Solid;

        Draw.Rect(x, y, StripWidth, StripHeight, Ink);
        Draw.Frame(x, y, StripWidth, StripHeight, 1f, Edge);
        Draw.Rect(x, y, 3f, StripHeight, GreenDeep);

        var left = x + StripPad;
        var right = x + StripWidth - Inset;
        Header(left, y + 6f, right, now);
        ShellRow(left, y + 28f, right, time);
        GasRow(left, y + 55f, right, time);
        Stitches(left, y + 80f, right);
        Ticker(left, y + StripHeight - 20f, right, time);

        Card(HudLeft + CardWidth / 2f, y + StripHeight + 6f - CardHeight / 2f, time);
    }

    static void Header(float x, float y, float right, long now)
    {
        var end = Draw.Tracked("THE JOKER", x, y + 1f, 13f, 3f, Text);

        var mode = Mode();
        if (mode != _mode)
        {
            _mode = mode;
            _modeAt = now;
        }

        var (back, fore) = mode switch
        {
            "BANG!" => (Red, Cream),
            "RELOADING" => (Gold, Solid),
            "GAS!" or "AIMING" => (Green, Solid),
            _ => (Edge, Text),
        };

        var tagWidth = Draw.TrackedWidth(mode, 9f, 2f) + 12f;
        var tagX = right - tagWidth;
        var flash = 1f - Math.Clamp((float)((now - _modeAt) / (double)Stopwatch.Frequency) / FlashSeconds, 0f, 1f);
        Draw.Rect(tagX, y, tagWidth, 15f, flash > 0f ? Rgba.Mix(back, Cream, flash * 0.6f) : back);
        Draw.Tracked(mode, tagX + 6f, y + 3f, 9f, 2f, fore);
        if (tagX - end > 20f)
        {
            Draw.Rect(end + 10f, y + 8f, tagX - end - 20f, 1f, Edge);
        }
    }

    static string Mode()
    {
        if (Reloading)
        {
            return "RELOADING";
        }

        if (GasFlying)
        {
            return "GAS!";
        }

        if (World.Now - _firedAt < BangSeconds)
        {
            return "BANG!";
        }

        if (Pumping)
        {
            return "PUMP";
        }

        return Aim.On ? "AIMING" : "AT LARGE";
    }

    // His shells as cartridges, brass ends and red hulls, empty ones dashed. They kick back as he
    // pumps, and fill in one by one as he reloads.
    static void ShellRow(float x, float y, float right, float time)
    {
        Draw.Tracked("SHELLS", x, y, 8f, 2f, Dim);
        var told = Reloading ? "RELOADING" : Pumping ? Pumped[Math.Clamp(_shells, 0, Shells)] : Loaded[Math.Clamp(_shells, 0, Shells)];
        Draw.Tracked(told, right - Draw.TrackedWidth(told, 8f, 1.5f), y, 8f, 1.5f, Gold);

        var shown = Reloading ? (int)MathF.Floor(ReloadDone * Shells) : _shells;
        var kick = Pumping ? PumpKick * MathF.Sin(Math.Clamp((_pumpAt - World.Now) / PumpAfter, 0f, 1f) * MathF.PI) : 0f;
        var sx = x - kick;
        var sy = y + 12f;
        var shellWidth = (right - x - ShellGap * (Shells - 1)) / Shells;
        for (var i = 0; i < Shells; i++)
        {
            var at = sx + i * (shellWidth + ShellGap);
            if (i < shown)
            {
                Draw.Rect(at, sy, 7f, ShellHeight, Brass);
                Draw.Rect(at + 7f, sy, shellWidth - 7f, ShellHeight, Red);
                Draw.Rect(at + 7f, sy, shellWidth - 7f, 1f, Rgba.Mix(Red, Cream, 0.35f));
            }
            else
            {
                Dashed(at, sy, shellWidth, ShellHeight, Empty);
            }
        }
    }

    // A box drawn in short dashes, for a shell that isn't there.
    static void Dashed(float x, float y, float width, float height, Rgba color)
    {
        for (var at = x; at < x + width; at += 4f)
        {
            var length = MathF.Min(2f, x + width - at);
            Draw.Rect(at, y, length, 1f, color);
            Draw.Rect(at, y + height - 1f, length, 1f, color);
        }

        for (var at = y; at < y + height; at += 4f)
        {
            var length = MathF.Min(2f, y + height - at);
            Draw.Rect(x, at, 1f, length, color);
            Draw.Rect(x + width - 1f, at, 1f, length, color);
        }
    }

    // His laughing gas as a green vial, emptied when he fires, filling as the next canister gets
    // ready and bubbling all the while.
    static void GasRow(float x, float y, float right, float time)
    {
        Draw.Tracked("LAUGHING GAS", x, y, 8f, 2f, Dim);
        var ready = GasReady;
        var told = GasMissing ? "NEEDS HARLEY PACK" : GasFlying ? "IN THE AIR" : ready >= 1f ? "READY" : Percents[Math.Clamp((int)(ready * 100f), 0, 100)];
        Draw.Tracked(told, right - Draw.TrackedWidth(told, 8f, 1.5f), y, 8f, 1.5f, GasMissing ? Dim : Green);

        var vx = x;
        var vy = y + 12f;
        var width = right - x;
        const float height = 9f;
        Draw.Rect(vx, vy, width, height, VialBack);
        if (GasMissing)
        {
            Draw.Hatch(vx, vy, width, height, 5f, Empty);
        }
        else
        {
            var level = ready;
            Draw.Rect(vx, vy, width * level, height, VialFill);
            if (level > 0.05f)
            {
                for (var i = 0; i < 3; i++)
                {
                    var phase = (time * 0.6f + i / 3f) % 1f;
                    var bx = vx + width * level * (0.25f + i * 0.25f);
                    Draw.Rect(bx, vy + height - 3f - phase * (height - 3f), 2f, 2f, Bubble with { A = (byte)(230f * (1f - phase)) });
                }

                if (level < 1f)
                {
                    Draw.Rect(vx + width * level - 1f, vy - 2f, 2f, height + 4f, Bubble);
                }
            }
        }

        Draw.Frame(vx, vy, width, height, 1f, GreenDeep);
    }

    // A HA for each thug he has put down, the newest in gold, and the count.
    static void Stitches(float x, float y, float right)
    {
        var end = Draw.Tracked("IN STITCHES", x, y + 2f, 8f, 2f, Dim);
        var count = Downed.Count;
        var count3 = Counts[Math.Clamp(count, 0, Counts.Length - 1)];
        var countX = right - Draw.TrackedWidth(count3, 14f, 1f);
        Draw.Tracked(count3, countX, y - 2f, 14f, 1f, Text);

        var at = end + 10f;
        var has = Math.Min(count, MostHas);
        for (var i = 0; i < has; i++)
        {
            var colour = i == has - 1 ? Gold : Red;
            var next = Draw.Tracked("HA", at, y + 1f, 9f, 0.5f, colour) + 4f;
            if (next > countX - 6f)
            {
                break;
            }

            at = next;
        }
    }

    // What he said last, typed in. A line too long to fit runs on to the left as it types.
    static void Ticker(float x, float y, float right, float time)
    {
        Draw.Rect(x, y - 4f, right - x, 1f, Edge);
        var start = Draw.Tracked(">", x, y + 2f, 9f, 0f, Green) + 6f;
        var since = World.Now - _lineAt;
        var text = _line.Length > 0 ? _line[Math.Clamp((int)(since * TypeRate), 0, _line.Length - 1)] : "";
        var typing = _line.Length > 0 && text.Length < _line[^1].Length;
        var fresh = typing || since < LineHolds;
        var colour = fresh ? Lilac : Dim;

        if (text.Length == 0)
        {
            var hint = Controls.LastDevice == Device.Pad
                ? Pad.Hint("{0} SHOOT · {1} GAS", Pad.Button.RT, Pad.Button.RB)
                : "LMB SHOOT · MMB GAS";
            Draw.Tracked(hint, start, y + 2f, 8f, 1f, Dim);
            return;
        }

        var caretRoom = 10f;
        float after;
        if (start + Draw.TrackedWidth(text, 8.5f, 0.5f) <= right - caretRoom)
        {
            after = Draw.Tracked(text, start, y + 1f, 8.5f, 0.5f, colour);
        }
        else
        {
            after = right - caretRoom;
            Draw.TrackedTail(text, after, y + 1f, 8.5f, 0.5f, colour, start);
        }

        if (typing || (time % 1f) < 0.5f)
        {
            Draw.Rect(after + 3f, y + 1f, 5f, 10f, fresh ? Green : Dim);
        }
    }

    // His card, turned about its middle: a purple edge, a cream face with a lilac frame, a J in
    // each corner, green brows and a red grin full of teeth that stretches now and then.
    static void Card(float cx, float cy, float time)
    {
        var degrees = CardTilt + CardSway * MathF.Sin(time * MathF.Tau / SwaySeconds);
        var turn = degrees * MathF.PI / 180f;
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

        void Stroke(float ax, float ay, float bx, float by, Rgba color)
        {
            var a = At(ax, ay);
            var b = At(bx, by);
            Draw.Line(a.X, a.Y, b.X, b.Y, color);
        }

        var hw = CardWidth / 2f;
        var hh = CardHeight / 2f;
        Box(-hw, -hh, hw, hh, Shadow, 3f, 5f);
        Box(-hw, -hh, hw, hh, CardEdge);
        Box(-hw + 2f, -hh + 2f, hw - 2f, hh - 2f, Cream);

        // The inner frame.
        const float fx = 13f;
        const float fy = 17f;
        Stroke(-hw + fx, -hh + fy, hw - fx, -hh + fy, Lilac);
        Stroke(hw - fx, -hh + fy, hw - fx, hh - fy, Lilac);
        Stroke(hw - fx, hh - fy, -hw + fx, hh - fy, Lilac);
        Stroke(-hw + fx, hh - fy, -hw + fx, -hh + fy, Lilac);

        // A J in each corner. Letters can't turn, so they sit upright at the turned corners.
        var topLeft = At(-hw + 6f, -hh + 4f);
        Draw.Tracked("J", topLeft.X - 2f, topLeft.Y - 2f, 14f, 0f, Purple);
        var bottomRight = At(hw - 9f, hh - 7f);
        Draw.Tracked("J", bottomRight.X - 8f, bottomRight.Y - 16f, 14f, 0f, GreenDeep);

        // Brows.
        void Wedge(float ax, float ay, float bx, float by, float qx, float qy)
        {
            var a = At(ax, ay);
            var b = At(bx, by);
            var c = At(qx, qy);
            Draw.Triangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, GreenDeep);
        }

        Wedge(-25f, -27f, -11f, -33f, -8f, -24f);
        Wedge(25f, -27f, 11f, -33f, 8f, -24f);
        Stroke(-15f, -14f, 0f, -17f, Purple);
        Stroke(0f, -17f, 15f, -14f, Purple);

        // The grin, a red crescent between two curves, breathing wider every few seconds.
        var stretch = 1f + 0.06f * (0.5f + 0.5f * MathF.Sin(time * MathF.Tau / 2.4f));
        const int steps = 12;
        const float mouthY = -4f;
        var half = 26f * stretch;
        float Upper(float u) => mouthY + 7f * (1f - u * u);
        float Lower(float u) => mouthY + 19f * (1f - u * u);
        for (var i = 0; i < steps; i++)
        {
            var u0 = -1f + 2f * i / steps;
            var u1 = -1f + 2f * (i + 1) / steps;
            var a = At(u0 * half, Upper(u0));
            var b = At(u1 * half, Upper(u1));
            var c = At(u1 * half, Lower(u1));
            var d = At(u0 * half, Lower(u0));
            Draw.Quad(a.X, a.Y, b.X, b.Y, c.X, c.Y, d.X, d.Y, Red);
        }

        // Teeth, a zigzag along the top of the grin.
        const int teeth = 9;
        for (var i = 0; i < teeth; i++)
        {
            var u0 = -0.8f + 1.6f * i / teeth;
            var u1 = -0.8f + 1.6f * (i + 0.5f) / teeth;
            var u2 = -0.8f + 1.6f * (i + 1) / teeth;
            var gap = Lower(u1) - Upper(u1);
            Stroke(u0 * half, Upper(u0) + 1f, u1 * half, Upper(u1) + gap * 0.55f, Cream);
            Stroke(u1 * half, Upper(u1) + gap * 0.55f, u2 * half, Upper(u2) + 1f, Cream);
        }

        var label = "WILD CARD";
        var labelAt = At(0f, 30f);
        Draw.Tracked(label, labelAt.X - Draw.TrackedWidth(label, 6.5f, 1.5f) / 2f, labelAt.Y - 3f, 6.5f, 1.5f, Purple);
    }
}
