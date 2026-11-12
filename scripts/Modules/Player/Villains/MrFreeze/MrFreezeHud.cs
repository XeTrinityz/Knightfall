#nullable enable

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// Mr. Freeze's HUD, his cryo suit's own display in ice blue. A coolant tube runs down its side,
// bubbling as it refills. Beside his name, Nora's vitals beat in a capsule window next to the
// suit's temperature, which climbs while he fires. Thugs he has frozen fill in as ice diamonds,
// and his beam and ice spire are tiles: the one in use glows, one that can't fire yet is hatched.
// Along the bottom a log types out what he and his suit say.
static partial class MrFreeze
{
    static readonly Rgba Ice = new(143, 216, 255);
    static readonly Rgba Frost = new(201, 240, 255);
    static readonly Rgba Coolant = new(58, 166, 217);
    static readonly Rgba Ink = new(7, 15, 21, 237);
    static readonly Rgba Solid = new(6, 18, 26);
    static readonly Rgba TileFill = new(10, 22, 29);
    static readonly Rgba LitFill = new(14, 37, 51);
    static readonly Rgba Line = new(36, 70, 90);
    static readonly Rgba Text = new(228, 246, 255);
    static readonly Rgba Dim = new(127, 157, 176);
    static readonly Rgba Chip = new(61, 91, 107);
    static readonly Rgba Dark = new(23, 50, 63);
    static readonly Rgba Amber = new(255, 179, 71);

    // Laid out on the 1080-high screen.
    const float HudLeft = 36f;
    const float HudBottom = 36f;
    const float HudWidth = 360f;
    const float TubeColumn = 46f;
    const float TubeWidth = 18f;
    const float HeaderHeight = 34f;
    const float Inset = 9f;
    const float VitalsHeight = 46f;
    const float TempWidth = 70f;
    const float FrozenHeight = 12f;
    const float TileHeight = 36f;
    const float TileGap = 6f;
    const float TickerHeight = 22f;

    // How fast the log types, and how long a line stays bright.
    const float TypeRate = 38f;
    const float LineHolds = 5f;

    // A tile in use pulses this often. A new mode flashes its tag.
    const float PulseSeconds = 1.3f;
    const float FlashSeconds = 0.35f;

    // His suit sits at the first while coolant is full and warms to the second as it runs dry.
    const float SuitCold = -42f;
    const float SuitWarm = -12f;

    // Nora's heart beats about this fast, drifting a little. This many beats fit across her
    // window. The trace is this thick, its spike this tall, and the oldest of it fades this much.
    // The sweep leaves this gap ahead of it, and the grid lines are this far apart.
    const int NoraBpm = 31;
    const int NoraDrift = 2;
    const float BeatsShown = 2.5f;
    const float TraceThick = 1.6f;
    const float TraceHigh = 15f;
    const float TraceFade = 0.75f;
    const float SweepGap = 10f;
    const float GridStep = 12f;

    // Enemies are counted this close, this often.
    const float NearReach = 1500f;
    const float CountEvery = 0.5f;
    static float _nextCount;
    static int _near;
    static float _speed;

    // Nora's heartbeats counted as they pass, so a change of pace never jumps the sweep.
    static float _noraBeats;

    // Thugs he has frozen while he has been on.
    static int _frozen;

    static string[] _line = Array.Empty<string>();
    static float _lineAt = -100f;
    static int _logged;
    static string _mode = "";
    static long _modeAt;

    // One heartbeat, as points along it from 0 to 1, high is up.
    static readonly (float At, float High)[] Heartbeat =
    {
        (0f, 0f), (0.10f, 0f), (0.14f, 0.15f), (0.18f, 0f), (0.30f, 0f), (0.32f, -0.12f), (0.35f, 1f), (0.38f, -0.35f), (0.41f, 0f),
        (0.55f, 0f), (0.62f, 0.25f), (0.70f, 0f), (1f, 0f),
    };

    static readonly string[] Temps = Enumerable.Range(0, 451).Select(n => (-45f + n / 10f).ToString("0.0", CultureInfo.InvariantCulture)).ToArray();
    static readonly string[] Percents = Enumerable.Range(0, 101).Select(n => n + "%").ToArray();
    static readonly string[] Seconds = Enumerable.Range(0, 61).Select(n => n + " S").ToArray();
    static readonly string[] Pairs = Enumerable.Range(0, 100).Select(n => n.ToString("00")).ToArray();
    static readonly string[] Counts = Enumerable.Range(0, 1000).Select(n => n.ToString("00")).ToArray();
    static readonly string[] Logs = Enumerable.Range(0, 100).Select(n => $"LOG {n:00} >").ToArray();
    static readonly string[] Beats = Enumerable.Range(0, 60).Select(n => $"STABLE · {n} BPM").ToArray();

    static void Record(string[] typed)
    {
        _line = typed;
        _lineAt = World.Now;
        _logged++;
    }

    static void ResetSuit()
    {
        _coolant = 1f;
        _venting = false;
        _warnedLow = false;
        _frozen = 0;
        _line = Array.Empty<string>();
        _logged = 0;
    }

    static void ShowHud(RPawnPlayer player)
    {
        if (!Hud.On)
        {
            VillainHud.Show(null);
            return;
        }

        _speed = new Vector2(player.Velocity.X, player.Velocity.Y).Length();
        if (World.Now >= _nextCount)
        {
            _nextCount = World.Now + CountEvery;
            _near = 0;
            foreach (var thug in Game.FindObjects<RPawnVillain>())
            {
                if (thug.IsValid && !thug.IsClassDefaultObject && Crowd.IsEnemy(thug) && Crowd.Standing(thug)
                    && Vector3.DistanceSquared(thug.Location, player.Location) <= NearReach * NearReach)
                {
                    _near++;
                }
            }
        }

        VillainHud.Show(DrawHud);
    }

    static void DrawHud(float rise, float seconds)
    {
        var now = Stopwatch.GetTimestamp();
        var time = (float)(now / (double)Stopwatch.Frequency);
        var height = HeaderHeight + Inset + VitalsHeight + Inset + FrozenHeight + Inset + TileHeight + Inset + TickerHeight;
        var x = HudLeft;
        var y = Draw.ScreenHeight - HudBottom - height + rise;
        Shapes.Under = Solid;

        Draw.Rect(x, y, HudWidth, height, Ink);
        Draw.Frame(x, y, HudWidth, height, 1f, Line);

        Tube(x, y, height, time);
        var left = x + TubeColumn;
        var right = x + HudWidth - Inset;
        Draw.Rect(left, y, 1f, height, Line);

        Header(left, y, right, now);
        var row = y + HeaderHeight + Inset;
        Vitals(left + Inset, row, right - TempWidth - Inset, time, seconds);
        Suit(right - TempWidth, row);

        row += VitalsHeight + Inset;
        FrozenRow(left + Inset, row, right);

        row += FrozenHeight + Inset;
        var pulse = 0.5f + 0.5f * MathF.Sin(time * MathF.Tau / PulseSeconds);
        var pad = Controls.LastDevice == Device.Pad;
        var tileWidth = (right - left - Inset - TileGap) / 2f;
        var beamCorner = _venting ? "VENTING" : _firing ? "FIRING" : "";
        Tile(left + Inset, row, tileWidth, "CRYO BEAM", pad ? Pad.Describe(Pad.Button.RT) : "LMB", _firing, _venting,
            _coolant, _coolant <= LowCoolant, beamCorner, pulse);
        var ready = BlastReady;
        var spiring = Spiring;
        var spireCorner = !spiring && ready < 1f ? Seconds[Math.Clamp((int)MathF.Ceiling(_nextBlast - World.Now), 0, Seconds.Length - 1)] : "";
        Tile(left + Inset + tileWidth + TileGap, row, tileWidth, "ICE SPIRE", pad ? Pad.Describe(Pad.Button.RB) : "MMB", spiring, !spiring && ready < 1f,
            spiring ? 1f : ready, false, spireCorner, pulse);

        Ticker(left, y + height - TickerHeight, right, time, pad);
    }

    // The ice spire shows as in use from its crack until a moment after it bursts.
    static bool Spiring => _blastPending || World.Now < _nextBlast - BlastWait + BlastDelay + 0.6f;

    // The coolant tube, a capsule filled to the coolant left. It bubbles while it refills and
    // its outline blinks amber while the suit vents.
    static void Tube(float x, float y, float height, float time)
    {
        var label = "COOLANT";
        var labelWidth = Draw.TrackedWidth(label, 7f, 1f);
        Draw.Tracked(label, x + (TubeColumn - labelWidth) / 2f, y + 6f, 7f, 1f, Dim);

        var tx = x + (TubeColumn - TubeWidth) / 2f;
        var ty = y + 20f;
        var th = height - 44f;
        var radius = TubeWidth / 2f;
        var outline = _venting && time % 0.8f < 0.4f ? Amber : Ice;
        Capsule(tx, ty, TubeWidth, th, outline, Dark);

        var level = Math.Clamp(_coolant, 0f, 1f);
        var liquid = _venting ? Rgba.Mix(Coolant, Dark, 0.5f) : Coolant;
        var surface = ty + th - level * th;
        var capTop = ty + th - radius;
        if (level > 0f)
        {
            if (surface >= capTop)
            {
                Draw.Rect(tx + 3f, surface, TubeWidth - 6f, ty + th - 2f - surface, liquid);
            }
            else
            {
                Draw.Place(tx + radius, capTop, 1f);
                Shapes.Arc(0f, radius - 1.5f, 90f, 270f, liquid);
                Draw.Place(0f, 0f, 1f);
                var top = MathF.Max(surface, ty + radius);
                Draw.Rect(tx + 1.5f, top, TubeWidth - 3f, capTop - top, liquid);
                if (surface < ty + radius)
                {
                    Draw.Place(tx + radius, ty + radius, 1f);
                    Shapes.Arc(0f, radius - 1.5f, 270f, 450f, liquid);
                    Draw.Place(0f, 0f, 1f);
                }
            }

            Draw.Rect(tx + 3f, MathF.Max(surface, ty + 2f), TubeWidth - 6f, 1.5f, Frost);
        }

        // Bubbles rise while it refills.
        if (!_firing && level < 1f && level > 0.05f)
        {
            for (var i = 0; i < 3; i++)
            {
                var phase = (time * 0.45f + i / 3f) % 1f;
                var by = ty + th - 4f - phase * (ty + th - 4f - surface);
                Draw.Rect(tx + 5f + i * 3f, by, 2f, 2f, Frost with { A = (byte)(220f * (1f - phase)) });
            }
        }

        // Quarter marks, and the amber line where the suit starts warning.
        for (var i = 1; i < 4; i++)
        {
            var mark = ty + th * i / 4f;
            Draw.Rect(tx + TubeWidth - (i == 2 ? 7f : 5f), mark, i == 2 ? 6f : 4f, 1f, Ice);
        }

        Draw.Rect(tx + 1f, ty + th * (1f - LowCoolant), TubeWidth - 2f, 1f, Amber);

        var shown = Percents[Math.Clamp((int)MathF.Round(level * 100f), 0, 100)];
        var shownWidth = Draw.TextWidth(shown, 10f);
        Draw.Text(shown, x + (TubeColumn - shownWidth) / 2f, y + height - 19f, 10f, _coolant <= LowCoolant ? Amber : Frost);
    }

    // A rounded box, upright or lying down, with an outline and a fill.
    static void Capsule(float x, float y, float width, float height, Rgba outline, Rgba fill)
    {
        if (height >= width)
        {
            var r = width / 2f;
            Draw.Rect(x + 1f, y + r, width - 2f, height - r * 2f, fill);
            Draw.Rect(x, y + r, 1f, height - r * 2f, outline);
            Draw.Rect(x + width - 1f, y + r, 1f, height - r * 2f, outline);
            Draw.Place(x + r, y + r, 1f);
            Shapes.Arc(0f, r - 1f, 270f, 450f, fill);
            Shapes.Arc(r - 1f, r, 270f, 450f, outline);
            Draw.Place(x + r, y + height - r, 1f);
            Shapes.Arc(0f, r - 1f, 90f, 270f, fill);
            Shapes.Arc(r - 1f, r, 90f, 270f, outline);
        }
        else
        {
            var r = height / 2f;
            Draw.Rect(x + r, y + 1f, width - r * 2f, height - 2f, fill);
            Draw.Rect(x + r, y, width - r * 2f, 1f, outline);
            Draw.Rect(x + r, y + height - 1f, width - r * 2f, 1f, outline);
            Draw.Place(x + r, y + r, 1f);
            Shapes.Arc(0f, r - 1f, 180f, 360f, fill);
            Shapes.Arc(r - 1f, r, 180f, 360f, outline);
            Draw.Place(x + width - r, y + r, 1f);
            Shapes.Arc(0f, r - 1f, 0f, 180f, fill);
            Shapes.Arc(r - 1f, r, 0f, 180f, outline);
        }

        Draw.Place(0f, 0f, 1f);
    }

    static void Header(float x, float y, float right, long now)
    {
        Draw.Tracked("MR. FREEZE", x + Inset, y + 5f, 15f, 3f, Text);
        Draw.Tracked("CRYO SUIT · DR. V. FRIES", x + Inset, y + 21f, 8f, 1.5f, Dim);

        var mode = Mode();
        if (mode != _mode)
        {
            _mode = mode;
            _modeAt = now;
        }

        var colour = _venting ? Amber : Frost;
        var flash = 1f - Math.Clamp((float)((now - _modeAt) / (double)Stopwatch.Frequency) / FlashSeconds, 0f, 1f);
        var tagWidth = Draw.TrackedWidth(mode, 11f, 2.5f) + 14f;
        var tagX = right - tagWidth;
        if (flash > 0f)
        {
            Draw.Rect(tagX, y + 9f, tagWidth, 17f, colour with { A = (byte)(flash * 120f) });
        }

        Draw.Frame(tagX, y + 9f, tagWidth, 17f, 1f, colour);
        Draw.Tracked(mode, tagX + 7f, y + 12f, 11f, 2.5f, colour);
        Draw.Rect(x, y + HeaderHeight, right + Inset - x, 1f, Line);
    }

    static string Mode()
    {
        if (_venting)
        {
            return "VENTING";
        }

        if (_firing)
        {
            return "BEAM";
        }

        if (Spiring)
        {
            return "SPIRE";
        }

        if (Aim.On)
        {
            return "AIMING";
        }

        return _speed < 40f ? "STANDING" : _speed < 300f ? "WALKING" : "RUNNING";
    }

    // Nora's chamber monitor. Her heartbeat is swept across it like a hospital monitor: the
    // newest trace is bright at the sweep, and last lap's fades out just ahead of it. Each beat
    // is drawn through its own points, so the spike keeps its full height.
    static void Vitals(float x, float y, float right, float time, float delta)
    {
        var width = right - x;
        var r = VitalsHeight / 2f;
        Capsule(x, y, width, VitalsHeight, Ice, Solid);

        var bpm = NoraBpm + (int)MathF.Round(NoraDrift * MathF.Sin(time * 0.05f));
        var beat = 60f / bpm;
        var baseline = y + VitalsHeight * 0.6f;
        var from = x + r * 0.45f;
        var to = right - r * 0.45f;
        var span = to - from;

        for (var gx = from + GridStep; gx < to; gx += GridStep)
        {
            Draw.Rect(gx, y + 5f, 1f, VitalsHeight - 10f, Line with { A = 80 });
        }

        Draw.Rect(from, baseline, span, 1f, Line with { A = 140 });

        var perBeat = span / BeatsShown;
        _noraBeats = (_noraBeats + delta / beat) % 1000f;
        var beats = _noraBeats;
        var sweep = from + beats * perBeat % span;
        Trace(from, sweep, beats - (sweep - from) / perBeat, perBeat, baseline, sweep, span);
        if (sweep + SweepGap < to)
        {
            Trace(sweep + SweepGap, to, beats - (span - SweepGap) / perBeat, perBeat, baseline, sweep, span);
        }

        Draw.Place(sweep, baseline - Wave(beats - MathF.Floor(beats)) * TraceHigh, 1f);
        Shapes.Disc(2.2f, Frost);
        Draw.Place(0f, 0f, 1f);

        // The labels sit on a backing, so the trace runs under them cleanly.
        var since = beats - MathF.Floor(beats);
        var glow = since < 0.15f ? 1f : MathF.Max(0.35f, 1f - (since - 0.15f) * 1.5f);
        var name = "NORA FRIES";
        var nameWidth = Draw.TrackedWidth(name, 7.5f, 1.5f);
        Draw.Rect(x + r - 4f, y + 4f, nameWidth + 18f, 11f, Solid with { A = 230 });
        Draw.Place(x + r + 1f, y + 9.5f, 1f);
        Shapes.Disc(2.5f, Ice, glow);
        Draw.Place(0f, 0f, 1f);
        Draw.Tracked(name, x + r + 7f, y + 5f, 7.5f, 1.5f, Dim);

        var reading = Beats[Math.Clamp(bpm, 0, Beats.Length - 1)];
        var readingWidth = Draw.TextWidth(reading, 8.5f);
        Draw.Rect(right - r - readingWidth - 4f, y + VitalsHeight - 14f, readingWidth + 8f, 11f, Solid with { A = 230 });
        Draw.TextRight(reading, right - r, y + VitalsHeight - 14f, 8.5f, Frost);
    }

    // The trace between two points across the window, starting at a point in the heartbeat. It
    // fades with how long ago the sweep drew it.
    static void Trace(float x0, float x1, float phase0, float perBeat, float baseline, float sweep, float span)
    {
        if (x1 - x0 < 1f)
        {
            return;
        }

        var phase1 = phase0 + (x1 - x0) / perBeat;
        var lastX = x0;
        var lastY = baseline - Wave(phase0 - MathF.Floor(phase0)) * TraceHigh;
        for (var k = MathF.Floor(phase0); k < phase1; k++)
        {
            foreach (var (at, high) in Heartbeat)
            {
                var point = k + at;
                if (point <= phase0 || point >= phase1)
                {
                    continue;
                }

                var px = x0 + (point - phase0) * perBeat;
                var py = baseline - high * TraceHigh;
                Stroke(lastX, lastY, px, py, sweep, span);
                lastX = px;
                lastY = py;
            }
        }

        Stroke(lastX, lastY, x1, baseline - Wave(phase1 - MathF.Floor(phase1)) * TraceHigh, sweep, span);
    }

    // A thick piece of the trace, its brightness from how far behind the sweep it is.
    static void Stroke(float ax, float ay, float bx, float by, float sweep, float span)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.01f)
        {
            return;
        }

        var behind = sweep - (ax + bx) / 2f;
        var age = (behind < 0f ? behind + span : behind) / span;
        var colour = Rgba.Mix(Solid, Ice, 1f - TraceFade * age);
        var nx = -dy / length * TraceThick / 2f;
        var ny = dx / length * TraceThick / 2f;
        Draw.Quad(ax + nx, ay + ny, bx + nx, by + ny, bx - nx, by - ny, ax - nx, ay - ny, colour);
    }

    static float Wave(float phase)
    {
        for (var i = 1; i < Heartbeat.Length; i++)
        {
            var (at, high) = Heartbeat[i];
            if (phase <= at)
            {
                var (was, wasHigh) = Heartbeat[i - 1];
                return wasHigh + (high - wasHigh) * ((phase - was) / MathF.Max(at - was, 0.0001f));
            }
        }

        return 0f;
    }

    // The suit's temperature, which climbs as coolant runs out.
    static void Suit(float x, float y)
    {
        Draw.Tracked("SUIT", x, y + 1f, 7.5f, 1.5f, Dim);
        var temp = SuitCold + (SuitWarm - SuitCold) * (1f - Math.Clamp(_coolant, 0f, 1f));
        var shown = Temps[Math.Clamp((int)MathF.Round((temp + 45f) * 10f), 0, Temps.Length - 1)];
        var end = Draw.Tracked(shown, x, y + 11f, 17f, 0.5f, Text);
        Draw.Text("°C", end + 2f, y + 15f, 9f, Ice);

        var refilling = !_firing && _coolant < 1f;
        var (trend, colour) = _firing ? ("RISING", Amber) : refilling ? ("COOLING", Ice) : ("STEADY", Dim);
        var tx = x;
        var ty = y + 32f;
        if (_firing)
        {
            Draw.Triangle(tx, ty + 6f, tx + 6f, ty + 6f, tx + 3f, ty + 1f, colour);
            tx += 9f;
        }
        else if (refilling)
        {
            Draw.Triangle(tx, ty + 1f, tx + 6f, ty + 1f, tx + 3f, ty + 6f, colour);
            tx += 9f;
        }

        Draw.Tracked(trend, tx, ty, 7.5f, 1f, colour);
    }

    // Thugs he has frozen as ice diamonds, ten to a row, and the enemies near.
    static void FrozenRow(float x, float y, float right)
    {
        var end = Draw.Tracked("FROZEN", x, y + 1f, 7.5f, 1.5f, Dim);
        var dx = end + 10f;
        const float Size = 4f;
        for (var i = 0; i < 10; i++)
        {
            var cx = dx + i * 12f;
            var cy = y + 6f;
            var lit = i < _frozen;
            Draw.Quad(cx, cy - Size, cx + Size, cy, cx, cy + Size, cx - Size, cy, lit ? Ice : Dark);
            if (!lit)
            {
                Draw.Quad(cx, cy - Size + 1.5f, cx + Size - 1.5f, cy, cx, cy + Size - 1.5f, cx - Size + 1.5f, cy, Solid);
            }
        }

        Draw.Text(Counts[Math.Clamp(_frozen, 0, Counts.Length - 1)], dx + 10f * 12f, y - 1f, 9.5f, Text);
        var near = Pairs[Math.Clamp(_near, 0, Pairs.Length - 1)];
        var nearEnd = Draw.TextRight(near, right, y - 1f, 9.5f, Text);
        Draw.Tracked("NEAR", nearEnd - Draw.TrackedWidth("NEAR", 7.5f, 1.5f) - 5f, y + 1f, 7.5f, 1.5f, Dim);
    }

    // One move. In use it glows and pulses. When it can't fire it is hatched. Its bar shows how
    // ready it is, amber when it is running low.
    static void Tile(float x, float y, float width, string name, string key, bool lit, bool waiting, float bar, bool low, string corner, float pulse)
    {
        if (lit)
        {
            Draw.Rect(x - 2f, y - 2f, width + 4f, TileHeight + 4f, Ice with { A = (byte)(40f + 60f * pulse) });
            Draw.Rect(x, y, width, TileHeight, LitFill);
            Draw.Frame(x, y, width, TileHeight, 1f, Ice);
        }
        else
        {
            Draw.Rect(x, y, width, TileHeight, TileFill);
            if (waiting)
            {
                Draw.Hatch(x, y, width, TileHeight, 6f, Dark);
            }

            Draw.Frame(x, y, width, TileHeight, 1f, Line);
        }

        var keyWidth = Draw.TextWidth(key, 9f) + 8f;
        Draw.Rect(x + 6f, y + 5f, keyWidth, 12f, lit ? Ice : Chip);
        Draw.Text(key, x + 10f, y + 6f, 9f, lit ? Solid : Text);
        if (corner.Length > 0)
        {
            Draw.TextRight(corner, x + width - 6f, y + 6f, 8.5f, _venting && waiting ? Amber : Frost);
        }

        Draw.Tracked(name, x + 6f, y + 19f, 12f, 1.5f, lit ? Frost : waiting ? Dim : Text);

        Draw.Rect(x + 6f, y + TileHeight - 5f, width - 12f, 2f, Dark);
        var filled = Math.Clamp(bar, 0f, 1f);
        if (filled > 0f)
        {
            Draw.Rect(x + 6f, y + TileHeight - 5f, (width - 12f) * filled, 2f, low ? Amber : lit ? Ice : Ice with { A = 130 });
        }
    }

    // His log, typed out with a blinking caret. Once it has been shown a while it dims, and
    // the aim key shows beside it.
    static void Ticker(float x, float y, float right, float time, bool pad)
    {
        Draw.Rect(x, y, right + Inset - x, 1f, Line);
        var since = World.Now - _lineAt;
        var number = Logs[Math.Clamp(_logged, 0, 99)];
        var end = Draw.Tracked(number, x + Inset, y + 7f, 8.5f, 0.5f, Dim);
        var text = _line.Length > 0 ? _line[Math.Clamp((int)(since * TypeRate), 0, _line.Length - 1)] : "";
        var typed = _line.Length > 0 && text.Length < _line[^1].Length;
        var fresh = typed || since < LineHolds;
        var after = Draw.Tracked(text, end + 6f, y + 6f, 9.5f, 0.5f, fresh ? Frost : Dim);
        if (typed || (time % 1f) < 0.5f)
        {
            Draw.Rect(after + 3f, y + 6f, 6f, 11f, fresh ? Frost : Dim);
        }

        if (!fresh)
        {
            Draw.TextRight(pad ? Pad.Hint("{0} HOLD · AIM", Pad.Button.LT) : "RMB HOLD · AIM", right, y + 7f, 8f, Dim);
        }
    }
}
