#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// The Scarecrow's HUD, Dr. Crane's own kit. His gas mask's lenses glow with each breath, quicker
// while he moves or works. Under them a tray of his instruments, one per move: the injector's
// syringe, whose plunger drives home as the needle lands, three fear darts that fill as they come
// back, the toxin canister with its pressure gauge, and an old set that hisses static until his
// broadcast is ready and shows it live while he is on air. Below, his case notes on every thug he
// has dosed, each with a heart trace that races in terror, slows as it wears off and flatlines
// red when the subject breaks. The seams are stitched like his mask.
static partial class Scarecrow
{
    static readonly Rgba Ink = new(10, 12, 8, 236);
    static readonly Rgba Solid = new(7, 9, 6);
    static readonly Rgba TileFill = new(14, 17, 11);
    static readonly Rgba LitFill = new(26, 34, 16);
    static readonly Rgba Rule = new(56, 66, 40);
    static readonly Rgba Words = new(228, 232, 204);
    static readonly Rgba Dim = new(140, 148, 118);
    static readonly Rgba Chip = new(70, 80, 52);
    static readonly Rgba Dark = new(32, 40, 22);
    static readonly Rgba Green = new(152, 204, 72);
    static readonly Rgba GreenDim = new(70, 94, 36);
    static readonly Rgba GreenGlow = new(204, 240, 110);
    static readonly Rgba Amber = new(236, 166, 62);
    static readonly Rgba Blood = new(196, 60, 46);
    static readonly Rgba Glass = new(190, 205, 180, 40);

    // Laid out on the 1080-high screen.
    const float HudLeft = 36f;
    const float HudBottom = 36f;
    const float HudWidth = 392f;
    const float HeaderHeight = 40f;
    const float Inset = 9f;
    const float TrayHeight = 66f;
    const float LabelHeight = 30f;
    const float ColumnGap = 6f;
    const float CaptionHeight = 16f;
    const float CaseRow = 15f;
    const int CaseRows = 4;

    // A tool in use pulses this often. A new mode flashes its tag.
    const float PulseSeconds = 1.3f;
    const float FlashSeconds = 0.35f;

    // One breath at rest, and while he moves or works.
    const float BreathSlow = 3.6f;
    const float BreathFast = 1.7f;

    // The plunger drives home this fast and draws back this fast.
    const float PlungeRate = 7f;
    const float DrawBackRate = 1.4f;

    // A thug who drops this soon after a collapse broke, and his case stays up this long after.
    const float BrokenWithin = 0.5f;
    const float BrokenHolds = 5f;

    // Heartbeats a second for a subject in terror and one whose fear is wearing off, and how
    // many beats each trace shows.
    const float TerrorRate = 2.4f;
    const float WaningRate = 1.2f;
    const float BeatsShown = 3f;

    // The static on the set changes this often.
    const float StaticRate = 20f;

    // A heartbeat as points along one beat: its phase and how far up the pen is.
    static readonly (float Phase, float Value)[] Wave =
    {
        (0f, 0f), (0.06f, 0f), (0.09f, 0.15f), (0.12f, 0f), (0.14f, 0f), (0.16f, -0.15f), (0.18f, 1f),
        (0.21f, -0.45f), (0.24f, 0f), (0.32f, 0f), (0.37f, 0.22f), (0.44f, 0f), (1f, 0f),
    };

    sealed class Case
    {
        public required nint Thug;
        public required int Number;
        public float Ended = -1f;
        public bool Broken;
    }

    static readonly List<Case> Cases = new();
    static int _caseCount;

    static float _breath;
    static float _plunger = 1f;
    static string _mode = "";
    static long _modeAt;

    static readonly string[] Pairs = Enumerable.Range(0, 100).Select(n => n.ToString("00")).ToArray();
    static readonly string[] Seconds = Enumerable.Range(0, 61).Select(n => n + " S").ToArray();
    static readonly string[] CaseNumbers = Enumerable.Range(0, 1000).Select(n => $"NO.{n:000}").ToArray();
    static readonly string[] DartCounts = Enumerable.Range(0, DartsMost + 1).Select(n => $"{n} / {DartsMost}").ToArray();

    static void ShowHud()
    {
        VillainHud.Show(Hud.On ? DrawHud : null);
    }

    static void DrawHud(float rise, float seconds)
    {
        FileCases();
        var now = Stopwatch.GetTimestamp();
        var time = (float)(now / (double)Stopwatch.Frequency);
        _breath = (_breath + seconds / (Acting || _speed > StillSpeed ? BreathFast : BreathSlow)) % 1f;
        var target = _move == Move.Needle && _hit ? 0.1f : 1f;
        _plunger = target < _plunger ? MathF.Max(target, _plunger - seconds * PlungeRate) : MathF.Min(target, _plunger + seconds * DrawBackRate);

        var height = HeaderHeight + Inset + TrayHeight + LabelHeight + Inset + CaptionHeight + CaseRow * CaseRows + Inset;
        var x = HudLeft;
        var y = Draw.ScreenHeight - HudBottom - height + rise;
        var right = x + HudWidth;
        Shapes.Under = Solid;

        Draw.Rect(x, y, HudWidth, height, Ink);
        Draw.Frame(x, y, HudWidth, height, 1f, Rule);
        Header(x, y, right, now);
        Seam(x + 1f, right - 1f, y + HeaderHeight);

        var row = y + HeaderHeight + Inset;
        var width = (HudWidth - Inset * 2f - ColumnGap * 3f) / 4f;
        var pulse = 0.5f + 0.5f * MathF.Sin(time * MathF.Tau / PulseSeconds);
        var cx = x + Inset;

        var dosing = _move == Move.Needle;
        Column(cx, row, width, "NEEDLE", Hotkeys.KeyText(NeedleKey), dosing, dosing ? "DOSING" : "READY", dosing ? GreenGlow : Green, pulse);
        Syringe(cx + width / 2f, row, dosing);

        cx += width + ColumnGap;
        var loaded = Math.Clamp((int)MathF.Floor(_darts), 0, DartsMost);
        Column(cx, row, width, "FEAR DART", Hotkeys.KeyText(DartKey), _move == Move.Dart, DartCounts[loaded], loaded > 0 ? Green : Dim, pulse);
        Darts(cx + width / 2f, row);

        cx += width + ColumnGap;
        var gassing = _move == Move.Toxin || World.Now < _cloudUntil;
        var toxinReady = Readiness(_nextToxin, ToxinWait);
        Column(cx, row, width, "FEAR TOXIN", Hotkeys.KeyText(ToxinKey), gassing,
            gassing ? "GASSING" : toxinReady < 1f ? Wait(_nextToxin) : "READY", gassing ? Amber : toxinReady < 1f ? Dim : Green, pulse);
        Canister(cx + width / 2f, row, gassing, toxinReady, time);

        cx += width + ColumnGap;
        var airReady = OnAir ? 1f : Readiness(_nextBroadcast, BroadcastWait);
        Column(cx, row, width, "BROADCAST", Hotkeys.KeyText(BroadcastKey), OnAir,
            OnAir ? "ON AIR" : airReady < 1f ? Wait(_nextBroadcast) : "READY", OnAir ? Blood : airReady < 1f ? Dim : Green, pulse);
        Set(cx + width / 2f, row, airReady, time);

        row += TrayHeight + LabelHeight + Inset;
        Seam(x + 1f, right - 1f, row);
        Notes(x + Inset, row + 4f, right - Inset, time);
    }

    static string Wait(float readyAt) => Seconds[Math.Clamp((int)MathF.Ceiling(readyAt - World.Now), 0, Seconds.Length - 1)];

    // His mask, his name and what he is doing.
    static void Header(float x, float y, float right, long now)
    {
        var glow = 0.5f - 0.5f * MathF.Cos(_breath * MathF.Tau);
        Lens(x + 22f, y + 17f, glow);
        Lens(x + 44f, y + 17f, glow);
        Dot(x + 33f, y + 31f, 5f, Chip);
        Hoop(x + 33f, y + 31f, 2f, 3f, Rgba.Mix(Dark, GreenDim, glow));

        Draw.Tracked("SCARECROW", x + 64f, y + 7f, 15f, 3f, Words);
        Draw.Tracked("DR. JONATHAN CRANE", x + 64f, y + 24f, 8f, 1.5f, Dim);

        var mode = Mode();
        if (mode != _mode)
        {
            _mode = mode;
            _modeAt = now;
        }

        var colour = OnAir ? Blood : _move == Move.None ? Green : Amber;
        var flash = 1f - Math.Clamp((float)((now - _modeAt) / (double)Stopwatch.Frequency) / FlashSeconds, 0f, 1f);
        var tagWidth = Draw.TrackedWidth(mode, 11f, 2.5f) + 14f;
        var tagX = right - Inset - tagWidth;
        if (flash > 0f)
        {
            Draw.Rect(tagX, y + 11f, tagWidth, 17f, colour with { A = (byte)(flash * 120f) });
        }

        Draw.Frame(tagX, y + 11f, tagWidth, 17f, 1f, colour);
        Draw.Tracked(mode, tagX + 7f, y + 14f, 11f, 2.5f, colour);
    }

    // One eyepiece of his mask, lit as he breathes in.
    static void Lens(float x, float y, float glow)
    {
        Dot(x, y, 10f, Chip);
        Dot(x, y, 7.5f, Rgba.Mix(Dark, GreenGlow, 0.15f + 0.65f * glow));
        Dot(x - 2.5f, y - 2.5f, 1.6f, Words with { A = 160 });
    }

    static string Mode() => _move switch
    {
        Move.Needle => "DOSING",
        Move.Dart => "DART",
        Move.Toxin => "VENTING",
        _ when OnAir => "ON AIR",
        _ when World.Now < _cloudUntil => "GASSING",
        _ => _speed > OwnFades ? "STALKING" : _speed > StillSpeed ? "CREEPING" : "WATCHING",
    };

    // A slot on the tray. Under the instrument, inside the same box, the move's name, its key
    // and how it stands.
    static void Column(float x, float y, float width, string name, string key, bool lit, string status, Rgba statusColour, float pulse)
    {
        const float height = TrayHeight + LabelHeight;
        if (lit)
        {
            Draw.Rect(x - 2f, y - 2f, width + 4f, height + 4f, Green with { A = (byte)(40f + 60f * pulse) });
            Draw.Rect(x, y, width, height, LitFill);
        }
        else
        {
            Draw.Rect(x, y, width, height, TileFill);
        }

        var edge = lit ? Green : Rule;
        Draw.Rect(x + 1f, y + TrayHeight, width - 2f, 1f, edge with { A = 120 });
        Draw.Frame(x, y, width, height, 1f, edge);

        Draw.Tracked(name, x + (width - Draw.TrackedWidth(name, 8.5f, 1.5f)) / 2f, y + TrayHeight + 4f, 8.5f, 1.5f, lit ? GreenGlow : Words);
        var keyRight = x + 5f;
        if (key.Length > 0)
        {
            var keyWidth = Draw.TextWidth(key, 8.5f) + 8f;
            Draw.Rect(x + 5f, y + TrayHeight + 16f, keyWidth, 11f, lit ? Green : Chip);
            Draw.Text(key, x + 9f, y + TrayHeight + 16.5f, 8.5f, lit ? Solid : Words);
            keyRight = x + 5f + keyWidth + 4f;
        }

        Draw.TextRightFit(status, x + width - 5f, y + TrayHeight + 16.5f, 8.5f, statusColour, keyRight);
    }

    // The injector's syringe. The plunger drives home as the needle lands and draws back after.
    static void Syringe(float cx, float y, bool lit)
    {
        const float barrel = 12f;
        var top = y + 26f;
        var bottom = y + 50f;
        var plunger = bottom - _plunger * (bottom - top);

        Draw.Rect(cx - 1f, plunger - 14f, 2f, 14f, Dim);
        Hoop(cx, plunger - 17f, 2.2f, 3.8f, Dim);
        Draw.Rect(cx - 10f, top - 2f, 20f, 2f, Chip);
        Draw.Rect(cx - barrel / 2f, top, barrel, bottom - top, Glass);
        if (bottom - plunger > 3f)
        {
            Draw.Rect(cx - barrel / 2f + 1f, plunger + 2f, barrel - 2f, bottom - plunger - 2f, lit ? GreenGlow : Green);
        }

        Draw.Rect(cx - barrel / 2f + 1f, plunger, barrel - 2f, 2.5f, Words);
        Draw.Frame(cx - barrel / 2f, top, barrel, bottom - top, 1f, lit ? Green : Rule);
        for (var i = 1; i < 4; i++)
        {
            Draw.Rect(cx + barrel / 2f - (i == 2 ? 5f : 3f), top + (bottom - top) * i / 4f, i == 2 ? 4f : 2f, 1f, Dim);
        }

        Draw.Rect(cx - 3f, bottom, 6f, 4f, Chip);
        Draw.Rect(cx - 0.75f, bottom + 4f, 1.5f, 9f, Words);
        if (lit)
        {
            Dot(cx, bottom + 13f, 1.8f, GreenGlow);
        }
    }

    // His three fear darts, each filling as it comes back.
    static void Darts(float cx, float y)
    {
        for (var i = 0; i < DartsMost; i++)
        {
            var dx = cx + (i - (DartsMost - 1) / 2f) * 18f;
            var fill = Math.Clamp(_darts - i, 0f, 1f);
            var top = y + 22f;
            var bottom = y + 46f;

            Draw.Rect(dx - 0.75f, y + 11f, 1.5f, 11f, Dim);
            Draw.Triangle(dx - 0.75f, y + 13f, dx - 0.75f, y + 21f, dx - 6f, y + 10f, fill >= 1f ? Chip : Dark);
            Draw.Triangle(dx + 0.75f, y + 13f, dx + 6f, y + 10f, dx + 0.75f, y + 21f, fill >= 1f ? Chip : Dark);
            Draw.Rect(dx - 3.5f, top, 7f, bottom - top, Glass);
            if (fill > 0f)
            {
                var surface = bottom - fill * (bottom - top);
                Draw.Rect(dx - 2.5f, surface, 5f, bottom - surface, fill >= 1f ? Green : GreenDim);
                if (fill >= 1f)
                {
                    Draw.Rect(dx - 2.5f, surface, 5f, 1.5f, GreenGlow);
                }
            }

            Draw.Frame(dx - 3.5f, top, 7f, bottom - top, 1f, fill >= 1f ? Rule : Dark);
            Draw.Rect(dx - 0.6f, bottom, 1.2f, 9f, fill >= 1f ? Words : Dim);
        }
    }

    // The toxin canister on his back and its pressure gauge, full when he can vent again. While
    // the gas is out it drains as the cloud thins and the gauge blinks amber.
    static void Canister(float cx, float y, bool gassing, float ready, float time)
    {
        var level = gassing ? Math.Clamp((_cloudUntil - World.Now) / CloudSeconds, 0f, 1f) : ready;
        var warn = gassing && time % 0.8f < 0.4f;

        var gy = y + 17f;
        Dot(cx, gy, 12.5f, warn ? Amber : Rule);
        Dot(cx, gy, 11f, Solid);
        Draw.Place(cx, gy, 1f);
        Shapes.Arc(8f, 10f, -120f, 120f, Dark);
        Shapes.Arc(8f, 10f, -120f, -120f + 240f * level, gassing ? Amber : level >= 1f ? Green : GreenDim);
        for (var i = 0; i < 5; i++)
        {
            Shapes.Spoke(-120f + 60f * i, 6f, 7.5f, 1f, Dim);
        }

        Shapes.Spoke(-120f + 240f * level, 0f, 9f, 1.5f, Words);
        Shapes.Disc(2f, Chip);
        Draw.Place(0f, 0f, 1f);

        Draw.Rect(cx - 1.5f, gy + 12f, 3f, 4f, Chip);
        var top = y + 36f;
        var bottom = y + 59f;
        Draw.Rect(cx - 15f, top - 3f, 30f, 3f, Chip);
        Draw.Rect(cx - 13f, top, 26f, bottom - top, Glass);
        var surface = bottom - level * (bottom - top);
        if (level > 0f)
        {
            Draw.Rect(cx - 12f, surface, 24f, bottom - surface, level >= 1f && !gassing ? Green : GreenDim);
            Draw.Rect(cx - 12f, surface, 24f, 1.5f, GreenGlow);
        }

        if (!gassing && level > 0.1f && level < 1f)
        {
            for (var i = 0; i < 3; i++)
            {
                var phase = (time * 0.5f + i / 3f) % 1f;
                var by = bottom - 3f - phase * (bottom - 3f - surface);
                Draw.Rect(cx - 7f + i * 6f, by, 2f, 2f, GreenGlow with { A = (byte)(220f * (1f - phase)) });
            }
        }

        Draw.Frame(cx - 13f, top, 26f, bottom - top, 1f, Rule);
        Draw.Rect(cx - 15f, bottom, 30f, 3f, Chip);
    }

    // The old set his broadcast takes over. Static until it is ready, clearing as it gets close,
    // then it waits on standby. On air it shows the broadcast live.
    static void Set(float cx, float y, float ready, float time)
    {
        const float sw = 52f;
        const float sh = 32f;
        var sx = cx - sw / 2f;
        var sy = y + 24f;

        Stroke(cx, y + 20f, cx - 14f, y + 6f, 1.2f, Dim);
        Stroke(cx, y + 20f, cx + 16f, y + 4f, 1.2f, Dim);
        Dot(cx - 14f, y + 6f, 1.6f, Dim);
        Dot(cx + 16f, y + 4f, 1.6f, Dim);
        Draw.Rect(sx - 4f, sy - 4f, sw + 8f, sh + 8f, Dark);
        Draw.Frame(sx - 4f, sy - 4f, sw + 8f, sh + 8f, 1f, Rule);
        Draw.Rect(sx - 2f, sy + sh + 4f, 6f, 2f, Chip);
        Draw.Rect(sx + sw - 4f, sy + sh + 4f, 6f, 2f, Chip);
        Draw.Rect(sx, sy, sw, sh, Solid);

        if (OnAir && _feedTexture is { IsValid: true } feed && feed.SizeX > 0 && feed.SizeY > 0)
        {
            // The feed is wider than the screen, so its sides are cut off.
            var across = MathF.Min(feed.SizeX, feed.SizeY * sw / sh);
            Draw.Glow(feed, sx, sy, sw, sh, (feed.SizeX - across) / 2f, 0f, across, feed.SizeY, Words);
            Draw.Rect(sx, sy + time * 18f % (sh - 2f), sw, 2f, Words with { A = 40 });
            if (time % 1f < 0.6f)
            {
                Dot(sx + 5f, sy + 5f, 2.4f, Blood);
            }
        }
        else if (ready < 1f)
        {
            const float cell = 4f;
            var frame = (int)(time * StaticRate);
            for (var row = 0; row < (int)(sh / cell); row++)
            {
                for (var col = 0; col < (int)(sw / cell); col++)
                {
                    var bright = Noise(frame, row * 32 + col) * (1f - ready * 0.8f);
                    Draw.Rect(sx + col * cell, sy + row * cell, cell, cell, Rgba.Mix(Solid, Words, bright * 0.7f));
                }
            }
        }
        else
        {
            Draw.Rect(sx, sy, sw, sh, GreenDim with { A = 60 });
            Draw.Rect(sx, sy + time * 12f % (sh - 2f), sw, 2f, Green with { A = 50 });
            if (time % 1.6f < 1.1f)
            {
                Draw.Tracked("STANDBY", cx - Draw.TrackedWidth("STANDBY", 7f, 1f) / 2f, sy + sh / 2f - 4f, 7f, 1f, Green);
            }
        }

        for (var line = sy + 1f; line < sy + sh; line += 3f)
        {
            Draw.Rect(sx, line, sw, 1f, Solid with { A = 70 });
        }
    }

    // Every thug he has dosed, newest first. A trace races while the subject is in terror and
    // slows once it wears off. One who collapsed flatlines in red for a while.
    static void Notes(float x, float y, float right, float time)
    {
        Draw.Tracked("CASE NOTES", x, y + 1f, 7.5f, 1.5f, Dim);
        var reach = Draw.TextRight(Pairs[Math.Clamp(_near, 0, Pairs.Length - 1)], right, y, 9f, _near > 0 ? Words : Dim);
        var reachLabel = reach - Draw.TrackedWidth("IN REACH", 7.5f, 1.5f) - 5f;
        Draw.Tracked("IN REACH", reachLabel, y + 1f, 7.5f, 1.5f, Dim);
        var broken = Since(_collapsedAt) < FlashSeconds ? Blood : _collapsed > 0 ? Words : Dim;
        var brokenAt = Draw.TextRight(Pairs[Math.Clamp(_collapsed, 0, Pairs.Length - 1)], reachLabel - 14f, y, 9f, broken);
        Draw.Tracked("BROKEN", brokenAt - Draw.TrackedWidth("BROKEN", 7.5f, 1.5f) - 5f, y + 1f, 7.5f, 1.5f, Dim);

        var traceLeft = x + 48f;
        var traceRight = right - 66f;
        for (var slot = 0; slot < CaseRows; slot++)
        {
            var ry = y + CaptionHeight + slot * CaseRow;
            var mid = ry + 6f;
            var index = Cases.Count - 1 - slot;
            if (index < 0)
            {
                Draw.Text("NO.---", x, ry, 8.5f, Chip);
                for (var dx = traceLeft; dx < traceRight; dx += 8f)
                {
                    Draw.Rect(dx, mid, 4f, 1f, Dark);
                }

                continue;
            }

            var file = Cases[index];
            if (file.Broken)
            {
                if (World.Now - file.Ended < FlashSeconds)
                {
                    Draw.Rect(x - 3f, ry - 2f, right - x + 6f, CaseRow - 1f, Blood with { A = 90 });
                }

                Draw.Text(CaseNumbers[file.Number], x, ry, 8.5f, Dim);
                Draw.Rect(traceLeft, mid, traceRight - traceLeft, 1.2f, Blood);
                Draw.TextRight("BROKEN", right, ry, 8.5f, Blood);
                continue;
            }

            var terror = Dosed.TryGetValue(file.Thug, out var dose) && Panicked(dose.Thug);
            var rate = terror ? TerrorRate : WaningRate;
            var colour = terror ? Green : GreenDim;
            Draw.Text(CaseNumbers[file.Number], x, ry, 8.5f, terror ? Words : Dim);
            Trace(traceLeft, traceRight, mid, terror ? 6f : 3.5f, time * rate + file.Number * 0.37f, colour);
            Draw.TextRight(terror ? "TERROR" : "WANING", right, ry, 8.5f, terror ? GreenGlow : Dim);
        }
    }

    // Opens a case for every thug newly dosed and closes the ones no longer dosed. A thug gone
    // just as one collapsed is the one who broke.
    static void FileCases()
    {
        foreach (var key in Dosed.Keys)
        {
            if (!Open(key))
            {
                _caseCount = _caseCount % (CaseNumbers.Length - 1) + 1;
                Cases.Add(new Case { Thug = key, Number = _caseCount });
            }
        }

        for (var i = Cases.Count - 1; i >= 0; i--)
        {
            var file = Cases[i];
            if (file.Ended < 0f)
            {
                if (!Dosed.ContainsKey(file.Thug))
                {
                    file.Ended = World.Now;
                    file.Broken = Since(_collapsedAt) < BrokenWithin;
                }
            }
            else if (!file.Broken || World.Now - file.Ended > BrokenHolds || World.Now < file.Ended)
            {
                Cases.RemoveAt(i);
            }
        }
    }

    static bool Open(nint thug)
    {
        foreach (var file in Cases)
        {
            if (file.Thug == thug && file.Ended < 0f)
            {
                return true;
            }
        }

        return false;
    }

    // A heart trace, its newest beat at the right. Head counts beats, so its whole part picks the
    // beat and the rest is how far into it the pen is.
    static void Trace(float left, float right, float mid, float height, float head, Rgba colour)
    {
        var width = right - left;
        var lastX = 0f;
        var lastY = 0f;
        var started = false;
        for (var beat = (int)MathF.Floor(head - BeatsShown); beat <= (int)MathF.Floor(head); beat++)
        {
            foreach (var (phase, value) in Wave)
            {
                var px = right - (head - (beat + phase)) / BeatsShown * width;
                var py = mid - value * height;
                if (started)
                {
                    Segment(lastX, lastY, px, py, left, right, colour);
                }

                lastX = px;
                lastY = py;
                started = true;
            }
        }

        var now = head - MathF.Floor(head);
        for (var i = 1; i < Wave.Length; i++)
        {
            if (now <= Wave[i].Phase)
            {
                var (p0, v0) = Wave[i - 1];
                var (p1, v1) = Wave[i];
                var value = v0 + (v1 - v0) * (now - p0) / (p1 - p0);
                Dot(right, mid - value * height, 1.8f, GreenGlow);
                break;
            }
        }
    }

    // The part of a line inside left to right.
    static void Segment(float x0, float y0, float x1, float y1, float left, float right, Rgba colour)
    {
        if (x1 <= x0 || x1 <= left || x0 >= right)
        {
            return;
        }

        if (x0 < left)
        {
            y0 += (y1 - y0) * (left - x0) / (x1 - x0);
            x0 = left;
        }

        if (x1 > right)
        {
            y1 = y0 + (y1 - y0) * (right - x0) / (x1 - x0);
            x1 = right;
        }

        Stroke(x0, y0, x1, y1, 1.2f, colour);
    }

    // A rule with stitches across it, like the seams of his mask.
    static void Seam(float x0, float x1, float y)
    {
        Draw.Rect(x0, y, x1 - x0, 1f, Rule);
        for (var sx = x0 + 8f; sx < x1 - 4f; sx += 12f)
        {
            Stroke(sx - 2f, y - 3f, sx + 2f, y + 4f, 1.2f, Chip);
        }
    }

    static void Dot(float x, float y, float radius, Rgba colour)
    {
        Draw.Place(x, y, 1f);
        Shapes.Disc(radius, colour);
        Draw.Place(0f, 0f, 1f);
    }

    static void Hoop(float x, float y, float inner, float outer, Rgba colour)
    {
        Draw.Place(x, y, 1f);
        Shapes.Ring(inner, outer, colour);
        Draw.Place(0f, 0f, 1f);
    }

    // The same 0 to 1 for the same two numbers, for static that holds still between frames.
    static float Noise(int a, int b)
    {
        var h = unchecked((uint)(a * 374761393 + b * 668265263));
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        return (h ^ (h >> 16)) % 1024u / 1023f;
    }

    // From 0 as a move is used to 1 once it can be used again.
    static float Readiness(float readyAt, float wait) => Math.Clamp(1f - (readyAt - World.Now) / wait, 0f, 1f);

    static float Since(float at) => World.Now >= at ? World.Now - at : float.MaxValue;

    // A line of some thickness, as a thin quad.
    static void Stroke(float x0, float y0, float x1, float y1, float thick, Rgba color)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.01f)
        {
            return;
        }

        var nx = -dy / length * thick / 2f;
        var ny = dx / length * thick / 2f;
        Draw.Quad(x0 + nx, y0 + ny, x1 + nx, y1 + ny, x1 - nx, y1 - ny, x0 - nx, y0 - ny, color);
    }
}
