#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// The Scarecrow's HUD, the console of his fear experiments. A canister of toxin runs down its
// side and refills after he vents. Beside his name a polygraph pen traces the fear in the thugs
// around him, jumping as each is dosed and kicking down as one drops, with their pulse next to
// it. While he is on air the trace gives way to his broadcast, live. A row of subjects shows who
// is calm and who is in a panic. His four moves are tiles on their keys, and along the bottom a
// log types out what he says.
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
    const float HudWidth = 372f;
    const float TankColumn = 46f;
    const float TankWidth = 18f;
    const float HeaderHeight = 34f;
    const float Inset = 9f;
    const float MonitorHeight = 52f;
    const float PulseWidth = 74f;
    const float SubjectsHeight = 12f;
    const float TileHeight = 34f;
    const float TileGap = 6f;
    const float TickerHeight = 22f;

    // How fast the log types, and how long a line stays bright.
    const float TypeRate = 34f;
    const float LineHolds = 5f;

    // A tile in use pulses this often. A new mode flashes its tag.
    const float PulseSeconds = 1.3f;
    const float FlashSeconds = 0.35f;

    // The polygraph keeps this many readings, taken this often, and swings this far. A dose
    // kicks the pen up and a collapse throws it down.
    const int Readings = 120;
    const float ReadEvery = 1f / 30f;
    const float PenSwing = 18f;
    const float Kick = 1f;

    // Their pulse at rest and how much each thug in a panic adds.
    const int RestingPulse = 72;
    const int PulsePerPanic = 17;

    const int SubjectSlots = 10;

    static readonly float[] Pen = new float[Readings];
    static int _penAt;
    static float _penClock;
    static float _penKick;
    static int _penDosesSeen;
    static int _penDropsSeen;
    static float _penPhase;

    static string[] _line = Array.Empty<string>();
    static float _lineAt = -100f;
    static int _logged;
    static string _lastSpoken = "";
    static string _mode = "";
    static long _modeAt;

    static readonly Dictionary<string, string[]> TypedLines = new();
    static readonly string[] Percents = Enumerable.Range(0, 101).Select(n => n + "%").ToArray();
    static readonly string[] Panics = Enumerable.Range(0, 101).Select(n => "PANIC " + n + "%").ToArray();
    static readonly string[] Pulses = Enumerable.Range(0, 250).Select(n => n.ToString()).ToArray();
    static readonly string[] Pairs = Enumerable.Range(0, 100).Select(n => n.ToString("00")).ToArray();
    static readonly string[] Seconds = Enumerable.Range(0, 61).Select(n => n + " S").ToArray();
    static readonly string[] Logs = Enumerable.Range(0, 100).Select(n => $"LOG {n:00} >").ToArray();
    static readonly string[] DartCounts = Enumerable.Range(0, DartsMost + 1).Select(n => $"{n} / {DartsMost}").ToArray();

    // The words of his lines, for the log.
    static readonly Dictionary<string, string> Spoken = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Scenes_Tv_Scarecrow_City_Ch07_Gas_B_01"] = "Gotham is mine. Do you see, world? The nightmare has truly begun.",
        ["Scenes_Tv_Scarecrow_City_Ch07_Pre_Gas_I_01"] = "People of Gotham. I'm going to give you something. A gift unlike anything you've experienced before.",
        ["Scenes_Tv_Scarecrow_City_Ch01_B_01"] = "I have waited for this night. Yearned for it. When I'm finished, the world will be forever changed.",
        ["Scenes_Tv_Scarecrow_City_Ch05_D_01"] = "When I'm done with Gotham - and with you - I will have barely begun. Terror will spread far.",
        ["Scenes_Tv_Scarecrow_City_Ch07_Pre_Gas_F_01"] = "It is time for you to bear witness to Gotham's end. Her destruction will be mirrored in your defeat.",
        ["Scenes_Tv_Scarecrow_City_Ch07_Post_Gas_J_01"] = "I gave you all a gift. Stripped away your pretenses and let you be the frightened beasts you all are inside.",
        ["Scenes_Tv_Scarecrow_City_Ch09_J_01"] = "Do not let cowardice be your epitaph. Accept your fate and face me.",
        ["Scenes_Tv_Scarecrow_City_Ch03_Army_01"] = "Are you scared, petty criminals of Gotham? Of the tanks rolling across the roads. The armed men patrolling your streets?",
        ["Scenes_Tv_Scarecrow_City_Ch01_H_01"] = "Look at your city. Abandoned in terror by those that fled, torn apart by those who had nowhere to go.",
    };

    // A line for the log, typed a letter at a time.
    static void Said(string words)
    {
        if (!TypedLines.TryGetValue(words, out var typed))
        {
            typed = Enumerable.Range(1, words.Length).Select(n => words[..n]).ToArray();
            TypedLines[words] = typed;
        }

        _line = typed;
        _lineAt = World.Now;
        _logged = (_logged + 1) % 100;
    }

    static void ShowHud()
    {
        if (!Hud.On)
        {
            VillainHud.Show(null);
            return;
        }

        // A line he just spoke goes into the log.
        var spoken = Speech.LastLine;
        if (spoken != _lastSpoken)
        {
            _lastSpoken = spoken;
            if (Spoken.TryGetValue(spoken[(spoken.LastIndexOf('.') + 1)..], out var words))
            {
                Said(words);
            }
        }

        VillainHud.Show(DrawHud);
    }

    static void DrawHud(float rise, float seconds)
    {
        var now = Stopwatch.GetTimestamp();
        var time = (float)(now / (double)Stopwatch.Frequency);
        var height = HeaderHeight + Inset + MonitorHeight + Inset + SubjectsHeight + Inset + TileHeight * 2f + TileGap + Inset + TickerHeight;
        var x = HudLeft;
        var y = Draw.ScreenHeight - HudBottom - height + rise;
        Shapes.Under = Solid;

        Draw.Rect(x, y, HudWidth, height, Ink);
        Draw.Frame(x, y, HudWidth, height, 1f, Rule);
        Tank(x, y, height, time);
        var left = x + TankColumn;
        var right = x + HudWidth - Inset;
        Draw.Rect(left, y, 1f, height, Rule);

        Header(left, y, right, now);
        var row = y + HeaderHeight + Inset;
        Monitor(left + Inset, row, right - PulseWidth - Inset, time, seconds);
        PulseReading(right - PulseWidth, row);

        row += MonitorHeight + Inset;
        Subjects(left + Inset, row, right, time);

        row += SubjectsHeight + Inset;
        var pulse = 0.5f + 0.5f * MathF.Sin(time * MathF.Tau / PulseSeconds);
        var tileWidth = (right - left - Inset - TileGap) / 2f;
        var firstX = left + Inset;
        var secondX = firstX + tileWidth + TileGap;

        Tile(firstX, row, tileWidth, "NEEDLE", Hotkeys.KeyText(NeedleKey), _move == Move.Needle, false, 1f, "", pulse);
        Tile(secondX, row, tileWidth, "FEAR DART", Hotkeys.KeyText(DartKey), _move == Move.Dart, _darts < 1f, _darts / DartsMost,
            DartCounts[Math.Clamp((int)MathF.Floor(_darts), 0, DartsMost)], pulse);

        row += TileHeight + TileGap;
        var gassing = _move == Move.Toxin || World.Now < _cloudUntil;
        var toxinReady = Readiness(_nextToxin, ToxinWait);
        Tile(firstX, row, tileWidth, "FEAR TOXIN", Hotkeys.KeyText(ToxinKey), gassing, !gassing && toxinReady < 1f, gassing ? 1f : toxinReady,
            gassing ? "GASSING" : toxinReady < 1f ? Wait(_nextToxin) : "", pulse);
        var airReady = OnAir ? 1f : Readiness(_nextBroadcast, BroadcastWait);
        Tile(secondX, row, tileWidth, "BROADCAST", Hotkeys.KeyText(BroadcastKey), OnAir, !OnAir && airReady < 1f, airReady,
            OnAir ? "ON AIR" : airReady < 1f ? Wait(_nextBroadcast) : "", pulse);

        Ticker(left, y + height - TickerHeight, right, time);
    }

    static string Wait(float readyAt) => Seconds[Math.Clamp((int)MathF.Ceiling(readyAt - World.Now), 0, Seconds.Length - 1)];

    // The toxin canister, filled to how ready his next vent is. It bubbles as it refills, and
    // while the gas is out it drains as the cloud thins and its outline blinks amber.
    static void Tank(float x, float y, float height, float time)
    {
        const string label = "TOXIN";
        Draw.Tracked(label, x + (TankColumn - Draw.TrackedWidth(label, 7f, 1f)) / 2f, y + 6f, 7f, 1f, Dim);

        var tx = x + (TankColumn - TankWidth) / 2f;
        var ty = y + 24f;
        var th = height - 50f;
        var gassing = _move == Move.Toxin || World.Now < _cloudUntil;
        var level = gassing ? Math.Clamp((_cloudUntil - World.Now) / CloudSeconds, 0f, 1f) : Readiness(_nextToxin, ToxinWait);

        Draw.Rect(tx - 3f, ty - 5f, TankWidth + 6f, 5f, Chip);
        Draw.Rect(tx + TankWidth / 2f - 2f, ty - 9f, 4f, 4f, Chip);
        Draw.Rect(tx, ty, TankWidth, th, Glass);
        var surface = ty + th - level * th;
        if (level > 0f)
        {
            Draw.Rect(tx + 2f, surface, TankWidth - 4f, ty + th - surface - 1f, level >= 1f && !gassing ? Green : GreenDim);
            Draw.Rect(tx + 2f, surface, TankWidth - 4f, 1.5f, GreenGlow);
        }

        if (!gassing && level > 0.05f && level < 1f)
        {
            for (var i = 0; i < 3; i++)
            {
                var phase = (time * 0.5f + i / 3f) % 1f;
                var by = ty + th - 4f - phase * (ty + th - 4f - surface);
                Draw.Rect(tx + 4f + i * 4f, by, 2f, 2f, GreenGlow with { A = (byte)(220f * (1f - phase)) });
            }
        }

        Draw.Frame(tx, ty, TankWidth, th, 1f, gassing && time % 0.8f < 0.4f ? Amber : Rule);
        for (var i = 1; i < 4; i++)
        {
            Draw.Rect(tx + TankWidth - (i == 2 ? 7f : 5f), ty + th * i / 4f, i == 2 ? 6f : 4f, 1f, Dim);
        }

        Draw.Rect(tx - 3f, ty + th, TankWidth + 6f, 4f, Chip);
        var shown = Percents[Math.Clamp((int)MathF.Round(level * 100f), 0, 100)];
        Draw.Text(shown, x + (TankColumn - Draw.TextWidth(shown, 10f)) / 2f, y + height - 19f, 10f, level >= 1f && !gassing ? Green : Words);
    }

    static void Header(float x, float y, float right, long now)
    {
        Draw.Tracked("DR. J. CRANE", x + Inset, y + 5f, 15f, 3f, Words);
        Draw.Tracked("FEAR RESPONSE STUDY", x + Inset, y + 21f, 8f, 1.5f, Dim);

        var mode = Mode();
        if (mode != _mode)
        {
            _mode = mode;
            _modeAt = now;
        }

        var colour = OnAir ? Blood : _move == Move.None ? Green : Amber;
        var flash = 1f - Math.Clamp((float)((now - _modeAt) / (double)Stopwatch.Frequency) / FlashSeconds, 0f, 1f);
        var tagWidth = Draw.TrackedWidth(mode, 11f, 2.5f) + 14f;
        var tagX = right - tagWidth;
        if (flash > 0f)
        {
            Draw.Rect(tagX, y + 9f, tagWidth, 17f, colour with { A = (byte)(flash * 120f) });
        }

        Draw.Frame(tagX, y + 9f, tagWidth, 17f, 1f, colour);
        Draw.Tracked(mode, tagX + 7f, y + 12f, 11f, 2.5f, colour);
        Draw.Rect(x, y + HeaderHeight, right + Inset - x, 1f, Rule);
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

    // The polygraph window. Its pen writes at the right and the paper runs left, wavering more
    // the more of them are afraid. On air, it shows his broadcast instead.
    static void Monitor(float x, float y, float right, float time, float delta)
    {
        var width = right - x;
        Draw.Rect(x, y, width, MonitorHeight, Solid);
        Draw.Frame(x, y, width, MonitorHeight, 1f, Rule);
        Record(delta);

        if (OnAir && _feedTexture is { IsValid: true } feed && feed.SizeX > 0 && feed.SizeY > 0)
        {
            Feed(x, y, width, feed, time);
            return;
        }

        for (var gx = x + 12f; gx < right - 2f; gx += 12f)
        {
            Draw.Rect(gx, y + 3f, 1f, MonitorHeight - 6f, Rule with { A = 70 });
        }

        var baseline = y + MonitorHeight * 0.58f;
        Draw.Rect(x + 3f, baseline, width - 6f, 1f, Rule with { A = 140 });

        var step = (width - 10f) / (Readings - 1);
        var lastX = 0f;
        var lastY = 0f;
        for (var i = 0; i < Readings; i++)
        {
            var px = x + 5f + i * step;
            var py = baseline - Pen[(_penAt + i) % Readings] * PenSwing;
            if (i > 0)
            {
                Stroke(lastX, lastY, px, py, 1.5f, Rgba.Mix(Green, Solid, (1f - i / (float)(Readings - 1)) * 0.7f));
            }

            lastX = px;
            lastY = py;
        }

        Draw.Rect(lastX - 1f, y + 4f, 2f, MonitorHeight - 8f, Dim with { A = 120 });
        Draw.Place(lastX, lastY, 1f);
        Shapes.Disc(2.4f, GreenGlow);
        Draw.Place(0f, 0f, 1f);

        Draw.Rect(x + 4f, y + 4f, Draw.TrackedWidth("GROUP FEAR", 7.5f, 1.5f) + 6f, 11f, Solid with { A = 230 });
        Draw.Tracked("GROUP FEAR", x + 7f, y + 5f, 7.5f, 1.5f, Dim);
        var panic = _afraid > 0 ? _afraid / (float)Math.Max(_near, _afraid) : 0f;
        var reading = Panics[Math.Clamp((int)MathF.Round(panic * 100f), 0, 100)];
        var readingWidth = Draw.TextWidth(reading, 8.5f);
        Draw.Rect(right - readingWidth - 10f, y + MonitorHeight - 15f, readingWidth + 8f, 11f, Solid with { A = 230 });
        Draw.TextRight(reading, right - 6f, y + MonitorHeight - 15f, 8.5f, panic > 0.5f ? Amber : Green);
    }

    // The pen's next readings: a tremor that grows with the fear, and the kicks.
    static void Record(float delta)
    {
        if (_doses != _penDosesSeen)
        {
            _penDosesSeen = _doses;
            _penKick = Kick;
        }

        if (_collapsed != _penDropsSeen)
        {
            _penDropsSeen = _collapsed;
            _penKick = -Kick;
        }

        _penClock = MathF.Min(_penClock + delta, 1f);
        while (_penClock >= ReadEvery)
        {
            _penClock -= ReadEvery;
            var fear = Math.Clamp(_afraid / 4f, 0f, 1f);
            _penPhase = (_penPhase + ReadEvery * (0.7f + 2.5f * fear)) % 1000f;
            var tremor = (0.12f + 0.5f * fear) * MathF.Sin(_penPhase * MathF.Tau) + (0.05f + 0.25f * fear) * MathF.Sin(_penPhase * 17.3f);
            Pen[_penAt] = Math.Clamp(tremor + _penKick, -1.2f, 1.2f);
            _penAt = (_penAt + 1) % Readings;
            _penKick *= 0.82f;
        }
    }

    // His broadcast as it plays, with a blinking light and a band of interference rolling down.
    static void Feed(float x, float y, float width, TextureRenderTarget2D feed, float time)
    {
        var aspect = feed.SizeX / (float)feed.SizeY;
        var shownWidth = MathF.Min(width - 4f, (MonitorHeight - 4f) * aspect);
        var shownX = x + (width - shownWidth) / 2f;
        Draw.Glow(feed, shownX, y + 2f, shownWidth, MonitorHeight - 4f, 0f, 0f, feed.SizeX, feed.SizeY, Words);
        for (var line = y + 3f; line < y + MonitorHeight - 2f; line += 3f)
        {
            Draw.Rect(shownX, line, shownWidth, 1f, Solid with { A = 70 });
        }

        Draw.Rect(shownX, y + 2f + time * 22f % (MonitorHeight - 6f), shownWidth, 2f, Words with { A = 40 });
        if (time % 1f < 0.6f)
        {
            Draw.Place(x + 9f, y + 10f, 1f);
            Shapes.Disc(3f, Blood);
            Draw.Place(0f, 0f, 1f);
        }

        Draw.Tracked("ON AIR", x + 15f, y + 5f, 7.5f, 1.5f, Blood);
    }

    // Their pulse, climbing with every thug in a panic.
    static void PulseReading(float x, float y)
    {
        Draw.Tracked("PULSE", x, y + 1f, 7.5f, 1.5f, Dim);
        var bpm = Math.Min(RestingPulse + PulsePerPanic * _afraid, Pulses.Length - 1);
        var end = Draw.Tracked(Pulses[bpm], x, y + 11f, 17f, 0.5f, _afraid > 0 ? Amber : Words);
        Draw.Text("BPM", end + 3f, y + 17f, 8f, Dim);
        var (trend, colour) = _afraid > 0 ? ("RACING", Amber) : _near > 0 ? ("STEADY", Green) : ("NO SUBJECTS", Dim);
        Draw.Tracked(trend, x, y + 34f, 7.5f, 1f, colour);
    }

    // The thugs close by: green while in a panic, pale while calm. Then those he has dropped.
    static void Subjects(float x, float y, float right, float time)
    {
        var end = Draw.Tracked("SUBJECTS", x, y + 1f, 7.5f, 1.5f, Dim);
        var dx = end + 10f;
        var afraid = Math.Min(_afraid, SubjectSlots);
        var calm = Math.Min(Math.Max(_near - _afraid, 0), SubjectSlots - afraid);
        var beat = 0.6f + 0.4f * MathF.Sin(time * 9f);
        for (var i = 0; i < SubjectSlots; i++)
        {
            Draw.Place(dx + i * 12f, y + 6f, 1f);
            if (i < afraid)
            {
                Shapes.Disc(4f, Rgba.Mix(GreenDim, GreenGlow, beat));
            }
            else
            {
                Shapes.Ring(2.6f, 4f, i < afraid + calm ? Words : Dark);
            }
        }

        Draw.Place(0f, 0f, 1f);
        var down = Pairs[Math.Clamp(_collapsed, 0, Pairs.Length - 1)];
        var downEnd = Draw.TextRight(down, right, y - 1f, 9.5f, Since(_collapsedAt) < FlashSeconds ? Blood : Words);
        Draw.Tracked("DOWN", downEnd - Draw.TrackedWidth("DOWN", 7.5f, 1.5f) - 5f, y + 1f, 7.5f, 1.5f, Dim);
    }

    // One move. In use it glows and pulses. When it can't be used yet it is hatched. Its bar
    // shows how ready it is.
    static void Tile(float x, float y, float width, string name, string key, bool lit, bool waiting, float bar, string corner, float pulse)
    {
        if (lit)
        {
            Draw.Rect(x - 2f, y - 2f, width + 4f, TileHeight + 4f, Green with { A = (byte)(40f + 60f * pulse) });
            Draw.Rect(x, y, width, TileHeight, LitFill);
            Draw.Frame(x, y, width, TileHeight, 1f, Green);
        }
        else
        {
            Draw.Rect(x, y, width, TileHeight, TileFill);
            if (waiting)
            {
                Draw.Hatch(x, y, width, TileHeight, 6f, Dark);
            }

            Draw.Frame(x, y, width, TileHeight, 1f, Rule);
        }

        if (key.Length > 0)
        {
            var keyWidth = Draw.TextWidth(key, 9f) + 8f;
            Draw.Rect(x + 6f, y + 5f, keyWidth, 12f, lit ? Green : Chip);
            Draw.Text(key, x + 10f, y + 6f, 9f, lit ? Solid : Words);
        }

        if (corner.Length > 0)
        {
            Draw.TextRight(corner, x + width - 6f, y + 6f, 8.5f, corner == "ON AIR" ? Blood : waiting ? Dim : Green);
        }

        Draw.Tracked(name, x + 6f, y + 19f, 11f, 1.5f, lit ? GreenGlow : waiting ? Dim : Words);
        Draw.Rect(x + 6f, y + TileHeight - 5f, width - 12f, 2f, Dark);
        var filled = Math.Clamp(bar, 0f, 1f);
        if (filled > 0f)
        {
            Draw.Rect(x + 6f, y + TileHeight - 5f, (width - 12f) * filled, 2f, lit ? Green : Green with { A = 130 });
        }
    }

    // What he last said, typed with a blinking caret. A long line shows its newest words. Once it
    // has been shown a while it dims.
    static void Ticker(float x, float y, float right, float time)
    {
        Draw.Rect(x, y, right + Inset - x, 1f, Rule);
        var since = World.Now - _lineAt;
        var end = Draw.Tracked(Logs[_logged], x + Inset, y + 7f, 8.5f, 0.5f, Dim);
        var text = _line.Length > 0 ? _line[Math.Clamp((int)(since * TypeRate), 0, _line.Length - 1)] : "";
        var typing = _line.Length > 0 && text.Length < _line[^1].Length;
        var colour = typing || since < LineHolds ? Words : Dim;
        var from = end + 6f;
        var last = right - 12f;
        var width = Draw.TrackedWidth(text, 9.5f, 0.5f);
        var after = from + width;
        if (after <= last)
        {
            Draw.Tracked(text, from, y + 6f, 9.5f, 0.5f, colour);
        }
        else
        {
            Draw.TrackedTail(text, last, y + 6f, 9.5f, 0.5f, colour, from);
            after = last;
        }

        if (typing || (time % 1f) < 0.5f)
        {
            Draw.Rect(after + 3f, y + 6f, 6f, 11f, colour);
        }
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
