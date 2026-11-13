#nullable enable

namespace Knightfall;

// Small controls and effects every theme can use. A theme still decides where they go and what
// colours they get.
static class Kit
{
    public const float SwitchWidth = 30f;
    public const float SwitchHeight = 14f;

    public static readonly Rgba Failed = new(240, 92, 80);

    // The colour a command's result is shown in. Each theme passes its own for the other two.
    public static Rgba Tone(OutcomeKind kind, Rgba done, Rgba unchanged) => kind switch
    {
        OutcomeKind.Done => done,
        OutcomeKind.Failed => Failed,
        _ => unchanged,
    };

    static readonly string[] Numbers = Enumerable.Range(0, 100).Select(number => number.ToString("00")).ToArray();
    static string? _version;
    static string _clock = "";
    static int _clockMinute = -1;

    // "01", "02" and so on, made once.
    public static string Number(int number) => Numbers[Math.Clamp(number, 0, 99)];

    public static string Version => _version ??= ("V" + Kernel.Version).ToUpperInvariant();

    // The time of day. The text is only rebuilt when the minute changes.
    public static string Clock
    {
        get
        {
            var now = DateTime.Now;
            var minute = now.Hour * 60 + now.Minute;
            if (minute != _clockMinute)
            {
                _clockMinute = minute;
                _clock = now.ToString("HH:mm");
            }

            return _clock;
        }
    }

    // How far a row has come in, 0 to 1. Each row starts a moment after the one above it.
    public static float Cascade(float sinceArrived, int index)
    {
        const float stagger = 0.028f;
        const float seconds = 0.2f;
        var left = 1f - Math.Clamp((sinceArrived - index * stagger) / seconds, 0f, 1f);
        return 1f - left * left * left;
    }

    // A slide switch. The knob sits right when on, with a soft halo round it.
    public static void Switch(float x, float y, bool on, Rgba accent, Rgba off)
    {
        const float knob = 11f;

        if (on)
        {
            Draw.Rect(x - 2f, y - 2f, SwitchWidth + 4f, SwitchHeight + 4f, accent with { A = 22 });
            Draw.Rect(x + 1f, y + 1f, SwitchWidth - 2f, SwitchHeight - 2f, accent with { A = 52 });
            Draw.Frame(x, y, SwitchWidth, SwitchHeight, 1f, accent);
            Draw.Rect(x + SwitchWidth - 3f - knob, y + 3f, knob, SwitchHeight - 6f, accent);
        }
        else
        {
            Draw.Frame(x, y, SwitchWidth, SwitchHeight, 1f, off);
            Draw.Rect(x + 3f, y + 3f, knob, SwitchHeight - 6f, off);
        }
    }

    // A row of blocks that fill up with the value, with a small tick under the default.
    public static void Meter(float x, float y, float width, float height, int blocks, float fill, float mark, Rgba lit, Rgba empty, Rgba tick)
    {
        const float gap = 2f;
        var block = (width - gap * (blocks - 1)) / blocks;
        var count = (int)MathF.Round(fill * blocks);

        for (var i = 0; i < blocks; i++)
        {
            Draw.Rect(x + i * (block + gap), y, block, height, i < count ? lit : empty);
        }

        Draw.Rect(x + mark * width - 0.5f, y + height + 2f, 1f, 3f, tick);
    }

    // One short dash per choice, with the current one lit. Drawn leftwards from the right edge. A
    // long list, like the skins, becomes a track with a marker so it keeps the same width.
    public static void Dashes(float right, float y, int count, int index, Rgba lit, Rgba empty)
    {
        const float dash = 8f;
        const float gap = 2f;
        const int most = 10;

        if (count > most)
        {
            const float track = most * dash + (most - 1) * gap;
            var marker = MathF.Max(4f, track / count);
            var left = right - track;
            Draw.Rect(left, y, track, 2f, empty);
            Draw.Rect(left + (track - marker) * index / (count - 1), y, marker, 2f, lit);
            return;
        }

        var x = right - count * dash - (count - 1) * gap;

        for (var i = 0; i < count; i++)
        {
            Draw.Rect(x + i * (dash + gap), y, dash, 2f, i == index ? lit : empty);
        }
    }

    public const float MoreWidth = 14f;

    static readonly string[] Digits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };

    // A small arrow with a number beside it, for tabs hidden off one end of the strip. The arrow
    // points the way they lie and the number is how many. It is MoreWidth wide, centred on middle.
    public static void More(float x, float middle, int count, bool left, Rgba arrow, Rgba number)
    {
        const float size = 9f;
        const int columns = 4;

        var text = Digits[Math.Clamp(count, 0, 9)];
        var arrowLeft = left ? x : x + MoreWidth - columns;
        for (var i = 0; i < columns; i++)
        {
            var half = 0.5f + (left ? i : columns - 1 - i);
            Draw.Rect(arrowLeft + i, middle - half, 1f, 2f * half, arrow);
        }

        if (left)
        {
            Draw.Text(text, x + columns + 3f, middle - size / 2f, size, number);
        }
        else
        {
            Draw.TextRight(text, x + MoreWidth - columns - 3f, middle - size / 2f, size, number);
        }
    }

    // The game's loading icon, a bat that spins. It is a row of square frames side by side.
    const string SpinnerPath = "BmFonts.Loading_DVDIcon";
    const double SpinnerRetrySeconds = 2;

    static BmSDK.Engine.Texture2D? _spinner;
    static DateTime _spinnerRetry;

    // Draws the spinning bat in a size by size square. The loading screen plays it at 14 frames
    // a second. Returns false if the game hasn't got the texture loaded, so the caller can draw
    // something else.
    public static bool SpinningBat(float x, float y, float size, float time, float fps, Rgba color)
    {
        if (_spinner is null || !_spinner.IsValid)
        {
            _spinner = null;
            if (DateTime.UtcNow < _spinnerRetry)
            {
                return false;
            }

            // Looking it up is a call into the game, so a missing texture is only asked about now and then.
            _spinnerRetry = DateTime.UtcNow.AddSeconds(SpinnerRetrySeconds);
            _spinner = Game.FindObject<BmSDK.Engine.Texture2D>(SpinnerPath);
            if (_spinner is null)
            {
                return false;
            }
        }

        var frameSize = _spinner.SizeY;
        if (frameSize <= 0)
        {
            return false;
        }

        var frames = Math.Max(1, _spinner.SizeX / frameSize);
        var frame = (int)(time * fps) % frames;
        Draw.Glow(_spinner, x, y, size, size, frame * frameSize, 0f, frameSize, frameSize, color);
        return true;
    }

    // The bat, as rows of blocks. It is built from boxes and not triangles so it can fade.
    // Wing tips rising to points, a head with two ears, and a scalloped lower edge.
    static readonly string[] BatRows =
    {
        ".X.........X...X.........X.",
        "XXX........XXXXX........XXX",
        "XXXX.......XXXXX.......XXXX",
        "XXXXX.....XXXXXXX.....XXXXX",
        "XXXXXXX..XXXXXXXXX..XXXXXXX",
        "XXXXXXXXXXXXXXXXXXXXXXXXXXX",
        "XXXXXXXXXXXXXXXXXXXXXXXXXXX",
        ".XXXXXXXXXXXXXXXXXXXXXXXXX.",
        "..XXXX.XXXXXXXXXXXXX.XXXX..",
        ".....X...XXXXXXXXX...X.....",
        "............XXX............",
        ".............X.............",
    };

    static (int Row, int Start, int Length)[]? _batRuns;

    public const int BatColumns = 27;
    public const int BatRowCount = 12;

    // Each unbroken run of blocks in a row is drawn as one box.
    public static void Bat(float x, float y, float block, Rgba color)
    {
        _batRuns ??= BuildBatRuns();
        foreach (var run in _batRuns)
        {
            Draw.Rect(x + run.Start * block, y + run.Row * block, run.Length * block, block, color);
        }
    }

    static (int Row, int Start, int Length)[] BuildBatRuns()
    {
        var runs = new List<(int, int, int)>();
        for (var row = 0; row < BatRows.Length; row++)
        {
            var line = BatRows[row];
            var start = -1;
            for (var column = 0; column <= line.Length; column++)
            {
                var filled = column < line.Length && line[column] == 'X';
                if (filled && start < 0)
                {
                    start = column;
                }
                else if (!filled && start >= 0)
                {
                    runs.Add((row, start, column - start));
                    start = -1;
                }
            }
        }

        return runs.ToArray();
    }

    // Three little bars that rise and fall, like a signal that is live. 12 wide, 11 high.
    public static void Signal(float x, float y, float time, Rgba color)
    {
        const float height = 11f;
        for (var i = 0; i < 3; i++)
        {
            var level = 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin(time * (3.1f + i * 1.3f) + i * 2.1f));
            var bar = MathF.Max(2f, height * level);
            Draw.Rect(x + i * 4.5f, y + height - bar, 3f, bar, color);
        }
    }

    // A bright dash that runs along a line every few seconds and fades out behind itself.
    public static void Pulse(float x, float y, float width, float time, Rgba color)
    {
        const float every = 3.6f;
        const float travel = 1.1f;
        const float length = 90f;

        var phase = time % every;
        if (phase > travel)
        {
            return;
        }

        var head = x + (width + length) * (phase / travel);
        var from = MathF.Max(x, head - length);
        var to = MathF.Min(x + width, head);
        if (to > from)
        {
            Draw.GradientAcross(from, y, to - from, 1f, color with { A = 0 }, color);
        }
    }

    // A faint band of light that crosses a row now and then.
    public static void Sheen(float x, float y, float width, float height, float time, Rgba color)
    {
        const float every = 3.2f;
        const float travel = 0.75f;
        const float band = 46f;

        var phase = time % every;
        if (phase > travel)
        {
            return;
        }

        var left = x + (width + band) * (phase / travel) - band;
        var from = MathF.Max(x, left);
        var to = MathF.Min(x + width, left + band);
        if (to > from)
        {
            Draw.Rect(from, y, to - from, height, color);
        }
    }

    // A small padlock, as tall as the size. Returns its width.
    public static float Padlock(float x, float y, float size, Rgba color)
    {
        var width = MathF.Round(size * 0.8f);
        var body = MathF.Round(size * 0.55f);
        var loop = MathF.Round(width * 0.6f);
        Draw.Frame(x + (width - loop) / 2f, y, loop, size - body + 1f, 1f, color);
        Draw.Rect(x, y + size - body, width, body, color);
        return width;
    }

    // What a row that can't be used right now shows in place of its value: a padlock and the
    // reason, ending at the right. Returns where it starts.
    public static float Locked(string why, float right, float y, float size, Rgba mark, Rgba text, float leftmost)
    {
        var gap = MathF.Round(size * 0.6f);
        var room = MathF.Round(size * 0.8f) + gap;
        var left = Draw.TextRightFit(why, right, y, size, text, leftmost + room);
        Padlock(left - room, y, size, mark);
        return left - room;
    }

    // A key name in a small outlined box, centred on y. Returns its width.
    public static float Cap(string text, float x, float y, float size, Rgba frame, Rgba color)
    {
        var height = size + 7f;
        var width = MathF.Max(height, Draw.TextWidth(text, size) + size);
        Draw.Frame(x, y - height / 2f, width, height, 1f, frame);
        Draw.Text(text, x + (width - Draw.TextWidth(text, size)) / 2f, y - size / 2f, size, color);
        return width;
    }

    public static float CapWidth(string text, float size)
    {
        return MathF.Max(size + 7f, Draw.TextWidth(text, size) + size);
    }

    const float HintKeyGap = 4f;
    const float HintLabelGap = 7f;
    const float HintLineGap = 18f;
    const float HintSpacing = 1.5f;

    // One row of key hints centred on middle, each line its keys in caps and then what they do.
    public static void HintRow(IReadOnlyList<GuideLine> lines, float middle, float y, float size, Rgba frame, Rgba key, Rgba label)
    {
        var total = 0f;
        foreach (var line in lines)
        {
            foreach (var name in line.Keys)
            {
                total += CapWidth(name, size) + HintKeyGap;
            }

            total += HintLabelGap - HintKeyGap + Draw.TrackedWidth(line.Label, size, HintSpacing) + HintLineGap;
        }

        var x = middle - (total - HintLineGap) / 2f;
        foreach (var line in lines)
        {
            foreach (var name in line.Keys)
            {
                x += Cap(name, x, y, size, frame, key) + HintKeyGap;
            }

            x = Draw.Tracked(line.Label, x + HintLabelGap - HintKeyGap, y - size / 2f, size, HintSpacing, label) + HintLineGap;
        }
    }

    // A point on a circle round 0,0. Degrees go clockwise from the top.
    public static (float X, float Y) Around(float degrees, float radius)
    {
        var (sin, cos) = MathF.SinCos(degrees * MathF.PI / 180f);
        return (sin * radius, -cos * radius);
    }

    // The shorter way round from one direction to another, in degrees.
    public static float Apart(float from, float to)
    {
        var apart = (from - to) % 360f;
        return apart > 180f ? apart - 360f : apart < -180f ? apart + 360f : apart;
    }

    // Fast at first and settling gently, for 0 to 1.
    public static float EaseOut(float t)
    {
        var left = 1f - Math.Clamp(t, 0f, 1f);
        return 1f - left * left * left;
    }

    static readonly byte[] ShadowStrengths = { 60, 34, 16, 6 };

    // Soft dark rings round a box, so it lifts off the game behind it. A box with its top left or
    // bottom right corner cut off passes the size of the cut, and the rings stop short of it.
    public static void Shadow(float x, float y, float width, float height, float cutTopLeft, float cutBottomRight)
    {
        for (var i = 0; i < ShadowStrengths.Length; i++)
        {
            var grow = i + 1f;
            var color = new Rgba(0, 0, 0, ShadowStrengths[i]);
            var left = x - grow;
            var top = y - grow + 1f;
            var right = x + width + grow;
            var bottom = y + height + grow + 1f;

            Draw.Rect(left + cutTopLeft, top, right - left - cutTopLeft, 1f, color);
            Draw.Rect(left, bottom - 1f, right - left - cutBottomRight, 1f, color);
            Draw.Rect(left, top + 1f + cutTopLeft, 1f, bottom - top - 2f - cutTopLeft, color);
            Draw.Rect(right - 1f, top + 1f, 1f, bottom - top - 2f - cutBottomRight, color);
        }
    }

    // Small L-shaped marks on the corners of a box.
    public static void Corners(float x, float y, float width, float height, float length, Rgba color)
    {
        const float thick = 2f;
        Draw.Rect(x, y, length, thick, color);
        Draw.Rect(x, y, thick, length, color);
        Draw.Rect(x + width - length, y, length, thick, color);
        Draw.Rect(x + width - thick, y, thick, length, color);
        Draw.Rect(x, y + height - thick, length, thick, color);
        Draw.Rect(x, y + height - length, thick, length, color);
        Draw.Rect(x + width - length, y + height - thick, length, thick, color);
        Draw.Rect(x + width - thick, y + height - length, thick, length, color);
    }
}
