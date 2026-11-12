#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// The mech's HUD, a puzzle box in Riddler's green. Its moves are puzzle tiles: the one in use
// glows, and the others are hatched while it plays or while they build up again. Beside them
// its gait, the enemies close by as question marks, and how many it has knocked down. Along the
// bottom a ticker types out Riddler's taunts.
static partial class RiddlerMech
{
    static readonly Rgba Green = new(92, 214, 84);
    static readonly Rgba Lime = new(196, 255, 150);
    static readonly Rgba Ink = new(8, 13, 9, 235);
    static readonly Rgba Solid = new(8, 17, 10);
    static readonly Rgba TileFill = new(15, 22, 16);
    static readonly Rgba LitFill = new(19, 36, 15);
    static readonly Rgba Line = new(44, 74, 42);
    static readonly Rgba Text = new(226, 244, 222);
    static readonly Rgba Dim = new(140, 164, 136);
    static readonly Rgba Chip = new(86, 112, 79);
    static readonly Rgba Dark = new(31, 50, 29);

    // Laid out on the 1080-high screen.
    const float HudLeft = 36f;
    const float HudBottom = 36f;
    const float HudWidth = 340f;
    const float HeaderHeight = 32f;
    const float Inset = 9f;
    const float TileWidth = 80f;
    const float TileHeight = 46f;
    const float TileGap = 6f;
    const float TickerHeight = 22f;

    // How fast the ticker types, how long a line stays bright, and how often Riddler fills a
    // quiet moment with a riddle.
    const float TypeRate = 38f;
    const float LineHolds = 5f;
    const float QuietRiddle = 16f;

    // A tile in use pulses this often. A new mode flashes its tag.
    const float PulseSeconds = 1.4f;
    const float FlashSeconds = 0.35f;

    // Enemies are counted this close, this often.
    const float NearReach = 1500f;
    const float CountEvery = 0.5f;
    static float _nextCount;
    static int _near;

    // Thugs it has knocked down while it has been on.
    static int _solved;

    static string[] _line = Array.Empty<string>();
    static float _lineAt = -100f;
    static float _quietFrom;
    static int _nextRiddle;
    static string _mode = "";
    static long _modeAt;

    // Riddler's lines, each typed out a letter at a time. Each one is cut into its typed lengths
    // once, so the ticker only ever draws a fixed set of texts.
    static readonly string[][] Riddles = Typed(
        "RIDDLE ME THIS: WHAT WALKS ON TWO LEGS AND STILL OUTRUNS YOU?",
        "RIDDLE ME THIS: WHAT HAS CLAWS, A GENIUS AND NO PATIENCE?",
        "A GENIUS NEEDS NO PARTNER. HE NEEDS A BIGGER MACHINE.",
        "STEP CLOSER. THE ANSWER IS RIGHT IN FRONT OF YOU.");

    static readonly string[][] SmashLines = Typed(
        "WHAT FALLS FASTER THAN A THUG? ANOTHER THUG.",
        "FLATTENED. EXACTLY AS PREDICTED.",
        "TOO SLOW. TOO SIMPLE. TOO EASY.");

    static readonly string[][] BeamLines = Typed(
        "THE ANSWER WAS A BEAM OF LIGHT. NOBODY GUESSED.",
        "ILLUMINATING, ISN'T IT?",
        "SEE? I TOLD YOU I WAS BRILLIANT.");

    static readonly string[][] SurgeLines = Typed(
        "SHOCKING, ISN'T IT? I DID WARN YOU.",
        "EVERY PUZZLE HAS A SOLUTION. YOU ARE IT.",
        "DOWN YOU GO. ALL OF YOU. AT ONCE.");

    static readonly string[][] SurgeQuiet = Typed(
        "POWER LEVELS NOMINAL. AUDIENCE ABSENT.");

    static readonly string[][] HopLines = Typed(
        "A TACTICAL RETREAT IS STILL A VICTORY.",
        "YOU MISSED. I ALWAYS KNOW WHERE YOU WILL STRIKE.");

    static readonly string[] Speeds = Enumerable.Range(0, 121).Select(n => $"{n / 10f:0.0} M/S").ToArray();
    static readonly string[] Seconds = Enumerable.Range(0, 61).Select(n => n + " S").ToArray();
    static readonly string[] Percents = Enumerable.Range(0, 101).Select(n => n + "%").ToArray();
    static readonly string[] Pairs = Enumerable.Range(0, 100).Select(n => n.ToString("00")).ToArray();
    static readonly string[] Solved = Enumerable.Range(0, 1000).Select(n => n == 1 ? "1 THUG" : $"{n} THUGS").ToArray();

    static string[][] Typed(params string[] lines) =>
        lines.Select(line => Enumerable.Range(0, line.Length + 1).Select(n => "> " + line[..n]).ToArray()).ToArray();

    static void Say(string[][] lines)
    {
        _line = lines[Random.Shared.Next(lines.Length)];
        _lineAt = World.Now;
        _quietFrom = World.Now;
    }

    static void ShowHud(RPawnPlayer player)
    {
        if (!Hud.On)
        {
            VillainHud.Show(null);
            return;
        }

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

        if (Voiced)
        {
            if (World.Now - _quietFrom >= VoiceQuiet)
            {
                _quietFrom = World.Now;
                Speak(TauntVoice, Riddles, always: true);
            }
        }
        else if (World.Now - _quietFrom >= QuietRiddle)
        {
            _line = Riddles[_nextRiddle++ % Riddles.Length];
            _lineAt = World.Now;
            _quietFrom = World.Now;
        }

        VillainHud.Show(DrawHud);
    }

    static void DrawHud(float rise, float seconds)
    {
        var now = Stopwatch.GetTimestamp();
        var time = (float)(now / (double)Stopwatch.Frequency);
        var height = HeaderHeight + Inset + TileHeight * 2f + TileGap + Inset + TickerHeight;
        var x = HudLeft;
        var y = Draw.ScreenHeight - HudBottom - height + rise;

        Draw.Rect(x, y, HudWidth, height, Ink);
        Draw.Frame(x, y, HudWidth, height, 1f, Line);

        Header(x, y, now);

        var body = y + HeaderHeight + Inset;
        var pulse = 0.5f + 0.5f * MathF.Sin(time * MathF.Tau / PulseSeconds);
        var beaming = _move is Move.Charge or Move.Fire;
        var ready = BeamReady;
        var beamBar = beaming ? Charge : ready;
        var beamCorner = _move == Move.Charge ? Percents[(int)MathF.Round(Charge * 100f)] : "";
        Tile(x + Inset, body, "SMASH", Hotkeys.KeyText(SmashKey), _move == Move.Smash, Acting && _move != Move.Smash, 1f, "", pulse);
        Tile(x + Inset + TileWidth + TileGap, body, "BEAM", Hotkeys.KeyText(BeamKey), beaming, (Acting && !beaming) || ready < 1f, beamBar, beamCorner, pulse);
        Tile(x + Inset, body + TileHeight + TileGap, "HOP BACK", Hotkeys.KeyText(HopKey) + " x2", _move == Move.Hop, Acting && _move != Move.Hop, 1f, "", pulse);
        var surging = _move == Move.Surge;
        var charged = SurgeReady;
        var surgeCorner = !surging && charged < 1f ? Seconds[Math.Clamp((int)MathF.Ceiling(_nextSurge - World.Now), 0, Seconds.Length - 1)] : "";
        Tile(x + Inset + TileWidth + TileGap, body + TileHeight + TileGap, "SURGE", Hotkeys.KeyText(SurgeKey), surging, (Acting && !surging) || charged < 1f, surging ? 1f : charged, surgeCorner, pulse);

        Readings(x + Inset + TileWidth * 2f + TileGap + 12f, body, x + HudWidth - Inset);
        Ticker(x, y + height - TickerHeight, time);
    }

    static void Header(float x, float y, long now)
    {
        Draw.Rect(x, y, HeaderHeight, HeaderHeight, Green);
        Mark(x + 10f, y + 6f, Solid);

        Draw.Tracked("RIDDLER MECH", x + HeaderHeight + 9f, y + 4f, 15f, 3f, Text);
        Draw.Tracked("PUZZLE 07 · PILOT E. NYGMA", x + HeaderHeight + 9f, y + 20f, 8f, 1.5f, Dim);

        var mode = Mode();
        if (mode != _mode)
        {
            _mode = mode;
            _modeAt = now;
        }

        var flash = 1f - Math.Clamp((float)((now - _modeAt) / (double)Stopwatch.Frequency) / FlashSeconds, 0f, 1f);
        var tagWidth = Draw.TrackedWidth(mode, 11f, 2.5f) + 14f;
        var tagX = x + HudWidth - Inset - tagWidth;
        if (flash > 0f)
        {
            Draw.Rect(tagX, y + 8f, tagWidth, 17f, Lime with { A = (byte)(flash * 120f) });
        }

        Draw.Frame(tagX, y + 8f, tagWidth, 17f, 1f, Lime);
        Draw.Tracked(mode, tagX + 7f, y + 11f, 11f, 2.5f, Lime);
        Draw.Rect(x, y + HeaderHeight, HudWidth, 1f, Line);
    }

    // Riddler's question mark built from blocks, 12 wide and 20 tall. The game's font draws a
    // large one that looks like a P.
    static void Mark(float x, float y, Rgba colour)
    {
        Draw.Rect(x, y, 12f, 4f, colour);
        Draw.Rect(x, y, 4f, 6f, colour);
        Draw.Rect(x + 8f, y, 4f, 10f, colour);
        Draw.Rect(x + 4f, y + 8f, 8f, 4f, colour);
        Draw.Rect(x + 4f, y + 8f, 4f, 6f, colour);
        Draw.Rect(x + 4f, y + 16f, 4f, 4f, colour);
    }

    static string Mode() => _move switch
    {
        Move.Smash => "SMASH",
        Move.Hop => "HOP BACK",
        Move.Charge => "CHARGING",
        Move.Fire => "FIRING",
        Move.Surge => "SURGE",
        _ => _speed < StandSpeed ? "STANDING" : _speed < (WalkSpeed + RunSpeed) / 2f ? "WALKING" : "RUNNING",
    };

    // One move. In use it glows and pulses. Waiting on another move or cooling down it is
    // hatched. Its bar shows how ready it is, or how charged.
    static void Tile(float x, float y, string name, string key, bool lit, bool waiting, float bar, string corner, float pulse)
    {
        if (lit)
        {
            Draw.Rect(x - 2f, y - 2f, TileWidth + 4f, TileHeight + 4f, Green with { A = (byte)(40f + 60f * pulse) });
            Draw.Rect(x, y, TileWidth, TileHeight, LitFill);
            Draw.Frame(x, y, TileWidth, TileHeight, 1f, Green);
        }
        else
        {
            Draw.Rect(x, y, TileWidth, TileHeight, TileFill);
            if (waiting)
            {
                Draw.Hatch(x, y, TileWidth, TileHeight, 6f, Dark);
            }

            Draw.Frame(x, y, TileWidth, TileHeight, 1f, Line);
        }

        var keyWidth = Draw.TextWidth(key, 9f) + 8f;
        Draw.Rect(x + 6f, y + 5f, keyWidth, 12f, lit ? Green : Chip);
        Draw.Text(key, x + 10f, y + 6f, 9f, lit ? Solid : Text);
        if (corner.Length > 0)
        {
            Draw.TextRight(corner, x + TileWidth - 6f, y + 6f, 9f, Lime);
        }
        else if (lit)
        {
            Draw.TextRight("?", x + TileWidth - 6f, y + 2f, 15f, Green);
        }

        Draw.Tracked(name, x + 6f, y + 20f, 13f, 1.5f, lit ? Lime : waiting ? Dim : Text);

        Draw.Rect(x + 6f, y + TileHeight - 6f, TileWidth - 12f, 3f, Dark);
        var filled = waiting && bar >= 1f ? 0f : Math.Clamp(bar, 0f, 1f);
        if (filled > 0f)
        {
            Draw.Rect(x + 6f, y + TileHeight - 6f, (TileWidth - 12f) * filled, 3f, lit ? Green : Green with { A = 130 });
        }
    }

    static void Readings(float x, float y, float right)
    {
        // Its gait and speed, with a block bar to its run.
        var speed = _speed / Teleport.UnitsPerMetre;
        var gait = _speed < StandSpeed ? "GAIT · STAND" : _speed < (WalkSpeed + RunSpeed) / 2f ? "GAIT · WALK" : "GAIT · RUN";
        Draw.Tracked(gait, x, y, 9f, 1.5f, Dim);
        Draw.TextRight(Speeds[Math.Clamp((int)MathF.Round(speed * 10f), 0, Speeds.Length - 1)], right, y - 1f, 10f, Text);
        var blocks = 10;
        var gap = 3f;
        var block = (right - x - gap * (blocks - 1)) / blocks;
        var filled = speed / 8f * blocks;
        for (var i = 0; i < blocks; i++)
        {
            Draw.Rect(x + i * (block + gap), y + 13f, block, 6f, i < filled ? Green : Dark);
        }

        // The enemies close by, one question mark each.
        Draw.Tracked("SUSPECTS NEAR", x, y + 30f, 9f, 1.5f, Dim);
        Draw.TextRight(Pairs[Math.Clamp(_near, 0, Pairs.Length - 1)], right, y + 29f, 10f, Text);
        var markStep = (right - x) / 10f;
        for (var i = 0; i < 10; i++)
        {
            Draw.Text("?", x + i * markStep, y + 42f, 13f, i < _near ? Green : Dark);
        }

        // Thugs it has knocked down.
        Draw.Tracked("SOLVED", x, y + 66f, 9f, 1.5f, Dim);
        Draw.TextRight(Solved[Math.Clamp(_solved, 0, Solved.Length - 1)], right, y + 65f, 10f, Text);
        Draw.Rect(x, y + 81f, right - x, 1f, Line);
    }

    // Riddler's line, typed out with a blinking caret. Once it has been shown a while it dims.
    static void Ticker(float x, float y, float time)
    {
        Draw.Rect(x, y, HudWidth, 1f, Line);
        var since = World.Now - _lineAt;
        var text = "> ";
        if (_line.Length > 0)
        {
            text = _line[Math.Clamp((int)(since * TypeRate), 0, _line.Length - 1)];
        }

        var typed = text.Length < (_line.Length > 0 ? _line[^1].Length : 2);
        var colour = typed || since < LineHolds ? Lime : Dim;
        var end = Draw.Tracked(text, x + Inset, y + 6f, 10f, 0.5f, colour);
        if (typed || (time % 1f) < 0.5f)
        {
            Draw.Rect(end + 3f, y + 6f, 6f, 11f, Lime);
        }
    }
}
