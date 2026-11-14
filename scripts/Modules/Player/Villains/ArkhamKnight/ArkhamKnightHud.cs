#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// The Arkham Knight's HUD, a visor panel cut like his helmet, in teal with his militia's red.
// Along the top his mark and the range to his laser's mark. Then his two weapons on their keys,
// with a bolt ring that spins while the rifle is worked. Along the bottom his drone strike and his
// militia side by side, each with its recharge and pips: the strike's six shells, and one for each
// of his militia still standing. A light sweep crosses the glass now and then.
static partial class ArkhamKnight
{
    static readonly Rgba Teal = new(82, 227, 214);
    static readonly Rgba TealDim = new(30, 84, 80);
    static readonly Rgba TealFaint = new(16, 46, 46);
    static readonly Rgba Red = new(224, 65, 74);
    static readonly Rgba RedDim = new(70, 26, 30);
    static readonly Rgba Solid = new(8, 22, 26);
    static readonly Rgba Bright = new(233, 255, 253);
    static readonly Rgba Muted = new(111, 156, 154);

    // Laid out on the 1080-high screen.
    const float HudLeft = 36f;
    const float HudBottom = 36f;
    const float HudWidth = 400f;
    const float HudHeight = 156f;
    const float Cut = 18f;
    const float Pad = 20f;
    const float TabWidth = 118f;
    const float TabHeight = 42f;

    const float SweepSeconds = 3.4f;
    const float SweepWidth = 60f;
    const float BlinkSeconds = 1.2f;
    const float BoltSpinSeconds = 0.6f;
    const float HitFlash = 0.25f;

    // The squad is counted this often.
    const float CountEvery = 0.5f;
    const int SquadPips = 12;

    static readonly string[] Ranges = Enumerable.Range(0, 1000).Select(n => n.ToString("000")).ToArray();
    static readonly string[] Waits = Enumerable.Range(0, 61).Select(n => n + " S").ToArray();
    static readonly string[] Ready = Enumerable.Range(0, SquadPips + 1).Select(n => "READY · " + n).ToArray();

    static int _standing;
    static float _nextSquadCount;

    static void ShowHud()
    {
        if (!Hud.On)
        {
            VillainHud.Show(Aim.On ? DrawCrosshair : null);
            return;
        }

        if (World.Now >= _nextSquadCount)
        {
            _nextSquadCount = World.Now + CountEvery;
            _standing = 0;
            foreach (var body in Brought)
            {
                if (body.IsValid && !body.bDeleteMe && body.Health > 0 && Crowd.Standing(body) && Bodyguards.Is(body))
                {
                    _standing++;
                }
            }
        }

        VillainHud.Show(DrawHud);
    }

    static void DrawHud(float rise, float seconds)
    {
        var time = (float)(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        var x = HudLeft;
        var y = Draw.ScreenHeight - HudBottom - HudHeight + rise;

        Panel(x, y, time);
        Header(x, y, time);
        Weapons(x + Pad, y + 56f, time);

        var half = (HudWidth - Pad * 2f - 16f) / 2f;
        StrikeGauge(x + Pad, y + HudHeight - 44f, half);
        MilitiaGauge(x + Pad + half + 16f, y + HudHeight - 44f, half, time);
        Reticle();
    }

    static void DrawCrosshair(float rise, float seconds) => Reticle();

    // At the middle of the screen, where the camera's line runs, while he aims. The rifle's is a
    // cross with a gap and a dot, the pistols' a pair of brackets. It goes red on a thug and
    // flashes an X when a round strikes one.
    static void Reticle()
    {
        if (!Aim.On || Arms.Raised < 0.5f)
        {
            return;
        }

        var cx = Draw.ScreenWidth / 2f;
        var cy = Draw.ScreenHeight / 2f;
        var color = _onThug ? Red : Teal;
        Draw.Rect(cx - 1.5f, cy - 1.5f, 3f, 3f, color);
        if (_pistols)
        {
            Bracket(cx - 14f, cy, -1f, color);
            Bracket(cx + 14f, cy, 1f, color);
        }
        else
        {
            Draw.Rect(cx - 22f, cy - 0.75f, 14f, 1.5f, color);
            Draw.Rect(cx + 8f, cy - 0.75f, 14f, 1.5f, color);
            Draw.Rect(cx - 0.75f, cy + 8f, 1.5f, 14f, color);
            Draw.Rect(cx - 0.75f, cy - 18f, 1.5f, 10f, color);
            Draw.Rect(cx - 5f, cy + 26f, 10f, 1.5f, TealDim);
            Draw.Rect(cx - 3f, cy + 32f, 6f, 1.5f, TealDim);
        }

        var since = World.Now - _hitAt;
        if (since >= 0f && since < HitFlash)
        {
            var hit = Rgba.Mix(Red, Bright, 1f - since / HitFlash);
            Draw.Line(cx - 12f, cy - 12f, cx - 6f, cy - 6f, hit);
            Draw.Line(cx + 12f, cy - 12f, cx + 6f, cy - 6f, hit);
            Draw.Line(cx - 12f, cy + 12f, cx - 6f, cy + 6f, hit);
            Draw.Line(cx + 12f, cy + 12f, cx + 6f, cy + 6f, hit);
        }
    }

    // One side of the pistols' crosshair, opening toward the middle.
    static void Bracket(float x, float cy, float side, Rgba color)
    {
        Draw.Rect(side < 0f ? x - 1.5f : x, cy - 10f, 1.5f, 20f, color);
        Draw.Rect(side < 0f ? x - 1.5f : x - 5f, cy - 10f, 6.5f, 1.5f, color);
        Draw.Rect(side < 0f ? x - 1.5f : x - 5f, cy + 8.5f, 6.5f, 1.5f, color);
    }

    // The glass, cut at two corners like his helmet, with a sweep of light crossing it.
    static void Panel(float x, float y, float time)
    {
        var w = HudWidth;
        var h = HudHeight;
        // The game draws slanted shapes solid, so the glass is solid throughout to match.
        Draw.Rect(x, y + Cut, w, h - Cut * 2f, Solid);
        Draw.Quad(x, y + Cut, x + Cut, y, x + w, y, x + w, y + Cut, Solid);
        Draw.Quad(x, y + h - Cut, x + w, y + h - Cut, x + w - Cut, y + h, x, y + h, Solid);

        var sweep = (time % SweepSeconds) / SweepSeconds;
        var sx = x - SweepWidth + sweep * (w + SweepWidth);
        var left = MathF.Max(sx, x + 1f);
        var right = MathF.Min(sx + SweepWidth, x + w - 1f);
        if (right > left)
        {
            Draw.Rect(left, y + Cut, right - left, h - Cut * 2f, Teal with { A = 9 });
        }

        Draw.Line(x, y + Cut, x + Cut, y, TealDim);
        Draw.Line(x + Cut, y, x + w, y, TealDim);
        Draw.Line(x + w, y, x + w, y + h - Cut, TealDim);
        Draw.Line(x + w, y + h - Cut, x + w - Cut, y + h, TealDim);
        Draw.Line(x + w - Cut, y + h, x, y + h, TealDim);
        Draw.Line(x, y + h, x, y + Cut, TealDim);
    }

    static void Header(float x, float y, float time)
    {
        Emblem(x + Pad + 13f, y + 27f);
        var after = Draw.Tracked("KNIGHT", x + Pad + 36f, y + 17f, 16f, 4f, Teal);
        var lit = (time % BlinkSeconds) < BlinkSeconds * 0.6f;
        Draw.Rect(after + 12f, y + 23f, 6f, 6f, lit ? Red : RedDim);
        Draw.Tracked("LIVE", after + 22f, y + 20f, 10f, 1.5f, lit ? Red : RedDim);

        var right = x + HudWidth - Pad;
        Draw.TextRight("MARK RANGE", right, y + 12f, 10f, Muted);
        var range = _markAt is { } mark && World.Player is { IsValid: true } player
            ? Ranges[Math.Clamp((int)MathF.Round(System.Numerics.Vector3.Distance(mark, player.Location) / Teleport.UnitsPerMetre), 0, Ranges.Length - 1)]
            : "---";
        var unit = Draw.TextRight(" M", right, y + 34f, 13f, Teal);
        Draw.TextRight(range, unit, y + 24f, 28f, Bright);
    }

    // A hexagon split by a chevron, his mark on the glass.
    static void Emblem(float cx, float cy)
    {
        const float r = 12f;
        Span<(float X, float Y)> points = stackalloc (float, float)[6];
        for (var i = 0; i < 6; i++)
        {
            var (sin, cos) = MathF.SinCos((i * 60f - 30f) * MathF.PI / 180f);
            points[i] = (cx + cos * r, cy + sin * r);
        }

        for (var i = 0; i < 6; i++)
        {
            var (ax, ay) = points[i];
            var (bx, by) = points[(i + 1) % 6];
            Draw.Line(ax, ay, bx, by, Teal);
        }

        Draw.Line(cx - 6f, cy - 3f, cx, cy + 1f, Teal);
        Draw.Line(cx, cy + 1f, cx + 6f, cy - 3f, Teal);
        Draw.Line(cx, cy + 1f, cx, cy + 7f, Teal);
    }

    static void Weapons(float x, float y, float time)
    {
        Tab(x, y, "SNIPER", Hotkeys.KeyText(RifleKey), !_pistols);
        Tab(x + TabWidth + 8f, y, "TWIN PISTOLS", Hotkeys.KeyText(PistolsKey), _pistols);

        // The ring spins while the rifle's bolt is worked and sits full when it is ready.
        var cx = x + TabWidth * 2f + 16f + 21f;
        var cy = y + 21f;
        Shapes.Under = Solid;
        Draw.Place(cx, cy, 1f);
        Shapes.Ring(13f, 16f, TealFaint);
        var cycling = !_pistols && (World.Now < _nextShot || _boltAt >= 0f);
        if (cycling)
        {
            var turn = (time % BoltSpinSeconds) / BoltSpinSeconds * 360f;
            Shapes.Arc(13f, 16f, turn, turn + 140f, Teal);
        }
        else
        {
            Shapes.Ring(13f, 16f, _pistols ? TealDim : Teal);
        }

        Draw.Place(0f, 0f, 1f);
        var label = _pistols ? "DUAL" : cycling ? "BOLT" : "READY";
        Draw.Text(label, cx - Draw.TextWidth(label, 8f) / 2f, cy - 5f, 8f, cycling || !_pistols ? Teal : Muted);
    }

    // A weapon's tab, its key in the corner, notched at the bottom right.
    static void Tab(float x, float y, string name, string key, bool active)
    {
        const float notch = 12f;
        var edge = active ? Teal : TealDim;
        if (active)
        {
            var fill = Rgba.Mix(Solid, Teal, 0.16f);
            Draw.Rect(x, y, TabWidth, TabHeight - notch, fill);
            Draw.Quad(x, y + TabHeight - notch, x + TabWidth, y + TabHeight - notch, x + TabWidth - notch, y + TabHeight, x, y + TabHeight, fill);
        }

        Draw.Line(x, y, x + TabWidth, y, edge);
        Draw.Line(x + TabWidth, y, x + TabWidth, y + TabHeight - notch, edge);
        Draw.Line(x + TabWidth, y + TabHeight - notch, x + TabWidth - notch, y + TabHeight, edge);
        Draw.Line(x + TabWidth - notch, y + TabHeight, x, y + TabHeight, edge);
        Draw.Line(x, y + TabHeight, x, y, edge);

        var keyEnd = Draw.Tracked(key, x + 10f, y + 4f, 9f, 0f, active ? Teal : Muted);
        if (active)
        {
            Draw.Tracked("· ACTIVE", keyEnd + 4f, y + 4f, 9f, 0f, Teal);
        }

        Draw.Tracked(name, x + 10f, y + 18f, 15f, 2f, active ? Bright : Muted);
    }

    // The strike's recharge, and its shells: all six lit when it is ready, one going out as each
    // falls during a barrage.
    static void StrikeGauge(float x, float y, float width)
    {
        var wait = _nextStrike - World.Now;
        var ready = wait <= 0f;
        Draw.Tracked("DRONE STRIKE", x, y, 11f, 2f, Red);
        Draw.TextRight(ready ? "READY" : Waits[Math.Clamp((int)MathF.Ceiling(wait), 0, Waits.Length - 1)], x + width, y, 11f, ready ? Bright : Muted);

        var fill = ready ? 1f : Math.Clamp(1f - wait / StrikeEvery, 0f, 1f);
        Draw.Rect(x, y + 17f, width, 6f, RedDim);
        Draw.Rect(x, y + 17f, width * fill, 6f, Red);

        var left = Incoming.Count > 0 ? Incoming.Count : ready ? Shells : 0;
        for (var i = 0; i < Shells; i++)
        {
            Draw.Rect(x + i * 13f, y + 27f, 10f, 4f, i < left ? Red : RedDim);
        }
    }

    // The militia's recharge or the Humvee on its way, then a pip for each of his militia still
    // standing.
    static void MilitiaGauge(float x, float y, float width, float time)
    {
        var wait = _nextCall - World.Now;
        var ready = wait <= 0f;
        var status = Rides.Count > 0 ? "EN ROUTE" : ready ? Ready[Math.Clamp(_standing, 0, SquadPips)] : Waits[Math.Clamp((int)MathF.Ceiling(wait), 0, Waits.Length - 1)];
        Draw.Tracked("MILITIA", x, y, 11f, 2f, Teal);
        Draw.TextRight(status, x + width, y, 11f, ready || Rides.Count > 0 ? Bright : Muted);

        var fill = ready ? 1f : Math.Clamp(1f - wait / CallEvery, 0f, 1f);
        Draw.Rect(x, y + 17f, width, 6f, TealFaint);
        Draw.Rect(x, y + 17f, width * fill, 6f, Teal);

        // A pip waiting for the next squad pulses while a Humvee is on its way.
        var pulse = 0.5f + 0.5f * MathF.Sin(time * MathF.Tau / 1.4f);
        for (var i = 0; i < SquadPips; i++)
        {
            var px = x + i * 13f;
            if (px + 9f > x + width)
            {
                break;
            }

            if (i < _standing)
            {
                Draw.Rect(px, y + 26f, 9f, 9f, Teal);
            }
            else
            {
                Draw.Frame(px, y + 26f, 9f, 9f, 1f, i == _standing && Rides.Count > 0 ? Rgba.Mix(TealDim, Teal, pulse) : TealDim);
            }
        }
    }
}
