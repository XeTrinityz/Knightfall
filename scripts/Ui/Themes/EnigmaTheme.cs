#nullable enable

namespace Knightfall;

// The Riddler's. Dark glass in a frame of green neon tubes that stop short of the corners, the
// way a real sign is bent. Every option has a puzzle tile: a question mark until it is changed,
// then a purple tick. A wire cube turns in the header and a dotted path crawls round whatever is
// picked. It is all boxes and lines, so it can fade and flicker.
sealed class EnigmaTheme : Theme
{
    const float Margin = 40f;
    const float Width = 440f;
    const float Inset = 20f;
    const float TabsMiddle = 74f;
    const float HeaderHeight = 92f;

    // The pages are listed down a narrower panel of their own, off the main one's left side.
    const float ColumnGap = 10f;
    const float ColumnInset = 14f;
    const float ColumnRule = 54f;
    const float PageTop = 62f;
    const float PageHeight = 26f;
    const float PageNameLeft = 40f;
    const float PageSize = 14f;
    const float ColumnMinWidth = 128f;
    const float RowHeight = 30f;

    const float RuleGap = 8f;
    const float TileSize = 26f;
    const float LabelLeft = Inset + 40f;
    const float ValueRight = Width - Inset - 8f;
    const float MeterWidth = 96f;
    const float ValueRoom = 54f;
    const float HelpLineHeight = 18f;
    const float HelpWidth = Width - 2f * Inset - 30f;
    const int MaxLines = 14;
    const int HelpLines = 3;

    // How much of the opening the tubes spend striking before the tiles start to turn over.
    const float StrikeShare = 0.3f;

    // Tubes leave this much dark at each corner.
    const float TubeGap = 8f;

    // A dash and the gap after it, on the path that crawls round the picked row.
    const float AntDash = 8f;
    const float AntPitch = 13f;

    static readonly Rgba PanelTop = new(5, 16, 9, 236);
    static readonly Rgba PanelBottom = new(2, 8, 5, 228);
    static readonly Rgba Glass = new(4, 12, 7);
    static readonly Rgba Tile = new(13, 42, 24);
    static readonly Rgba TileEdge = new(26, 78, 44);
    static readonly Rgba Green = new(74, 255, 124);
    static readonly Rgba White = new(255, 255, 255);
    static readonly Rgba Pale = new(214, 255, 226);
    static readonly Rgba Dim = new(130, 180, 146);
    static readonly Rgba Ghost = new(72, 112, 86);
    static readonly Rgba Ink = new(2, 12, 6);

    // A solved tile is his other colour.
    static readonly Rgba Live = new(206, 116, 255);

    static readonly HintStyle KeyHints = new(Size: 10f, CapHeight: 17f, LineHeight: 22f, Frame: Ghost, Key: Pale, Label: Dim);

    // The help types itself out again each time another row is picked.
    Feature? _helpFor;
    long _helpAt;

    // How far the speedometer's cube has turned. It turns faster the faster the car goes.
    float _spin;
    float _spinAt;

    public override string Id => "enigma";

    public override string Name => "ENIGMA";

    public override Rgba Accent => Green;

    public override Rgba Armed => Live;

    public override void Render(MenuView view)
    {
        var accent = Look.AccentOr(Accent);
        var left = Inset;
        var right = Width - Inset;

        var column = ColumnWidth(view);
        Draw.Place(Margin + (column + ColumnGap) * Look.Zoom + MoveX, Margin + MoveY, Look.Zoom);

        var visible = Math.Clamp(view.Lines.Count, 1, MaxLines);
        var rowsTop = HeaderHeight + RuleGap;
        var rowsBottom = rowsTop + visible * RowHeight;
        var helpTop = rowsBottom + 20f;
        var helpLines = HelpRoom(view, 13f, HelpWidth, HelpLines);
        var hintsTop = helpTop + helpLines * HelpLineHeight + 10f;
        var hintLines = HintLines(view, left, right, KeyHints);
        var statusTop = hintsTop + (hintLines - 1) * KeyHints.LineHeight + KeyHints.CapHeight + 12f;
        var height = statusTop + 32f;

        // The tubes' glow reaches 4 units past each edge.
        var columnHeight = PageTop + view.Pages.Count * PageHeight + 10f;
        var tallest = height + (MaxLines - visible) * RowHeight + (HelpLines - helpLines) * HelpLineHeight;
        Covers(-(column + ColumnGap) - 4f, -4f, Width + 4f, MathF.Max(height, columnHeight) + 4f, MathF.Max(tallest, columnHeight) + 4f);

        // The sign stutters as it strikes, then holds.
        var opened = view.Opened;
        var strike = opened >= StrikeShare || Noise((int)(opened * 46f)) > 0.42f ? 1f : 0.12f;
        var glass = Kit.EaseOut(opened / StrikeShare);
        var body = Math.Clamp((opened - 0.25f) / 0.3f, 0f, 1f);

        Draw.Fade(glass);
        Draw.GradientDown(-(column + ColumnGap), 0f, column, columnHeight, PanelTop, PanelBottom);
        Draw.GradientDown(0f, 0f, Width, height, PanelTop, PanelBottom);
        Draw.Fade(glass * strike);
        Neon(-(column + ColumnGap), 0f, column, columnHeight, accent, view.Time, 20);
        Neon(0f, 0f, Width, height, accent, view.Time, 0);

        Column(view, accent, column, body);
        Draw.Fade(body);
        Header(view, accent);
        Rows(view, accent, rowsTop, visible, body);

        Draw.Fade(body);
        Draw.Rect(left, rowsBottom + RuleGap, right - left, 1f, accent with { A = 76 });
        Help(view, accent, helpTop);
        Draw.Fade(body);
        DrawHints(view, left, right, hintsTop, KeyHints);
        Status(accent, statusTop);
        Draw.Fade(1f);
    }

    // How far a tile at this height has turned over, 0 to 1. They turn row by row down the
    // panel once the tubes are lit. Closing turns them all back together.
    static float Flip(MenuView view, float y)
    {
        return view.Closing ? Math.Clamp(view.Opened * 1.5f, 0f, 1f) : Kit.EaseOut((view.Opened - StrikeShare - y / 700f * 0.35f) / 0.25f);
    }

    static float Noise(int seed)
    {
        unchecked
        {
            var hash = (uint)seed * 2654435761u;
            hash ^= hash >> 15;
            hash *= 2246822519u;
            hash ^= hash >> 13;
            return (hash & 0xFFFF) / 65535f;
        }
    }

    // One neon tube: a bright core with two fainter lines each side, which reads as its glow.
    static void Tube(float x, float y, float width, float height, Rgba color)
    {
        Draw.Rect(x, y, width, height, color);
        var near = color with { A = (byte)(color.A * 0.32f) };
        var far = color with { A = (byte)(color.A * 0.1f) };
        if (width > height)
        {
            Draw.Rect(x, y - 2f, width, 1f, near);
            Draw.Rect(x, y + height + 1f, width, 1f, near);
            Draw.Rect(x, y - 4f, width, 1f, far);
            Draw.Rect(x, y + height + 3f, width, 1f, far);
        }
        else
        {
            Draw.Rect(x - 2f, y, 1f, height, near);
            Draw.Rect(x + width + 1f, y, 1f, height, near);
            Draw.Rect(x - 4f, y, 1f, height, far);
            Draw.Rect(x + width + 3f, y, 1f, height, far);
        }
    }

    // A frame of four tubes with a bright end cap in each corner. Now and then one tube dips for
    // a moment, like a sign that needs its transformer looked at.
    static void Neon(float x, float y, float width, float height, Rgba color, float time, int seed)
    {
        var moment = (int)(time * 14f);
        Rgba Lit(int tube) => Noise(moment * 4 + tube + seed) > 0.988f ? color with { A = 90 } : color;

        Tube(x + TubeGap, y, width - 2f * TubeGap, 2f, Lit(0));
        Tube(x + TubeGap, y + height - 2f, width - 2f * TubeGap, 2f, Lit(1));
        Tube(x, y + TubeGap, 2f, height - 2f * TubeGap, Lit(2));
        Tube(x + width - 2f, y + TubeGap, 2f, height - 2f * TubeGap, Lit(3));

        Draw.Rect(x, y, 4f, 4f, color);
        Draw.Rect(x + width - 4f, y, 4f, 4f, color);
        Draw.Rect(x, y + height - 4f, 4f, 4f, color);
        Draw.Rect(x + width - 4f, y + height - 4f, 4f, 4f, color);
    }

    // A short run along a frame, at a distance round it clockwise from the top left.
    static void FrameRun(float x, float y, float width, float height, float at, float length, float thick, Rgba color)
    {
        var around = 2f * (width + height);
        at = (at % around + around) % around;
        var left = length;
        for (var side = 0; side < 5 && left > 0.01f; side++)
        {
            float piece;
            if (at < width)
            {
                piece = MathF.Min(left, width - at);
                Draw.Rect(x + at, y, piece, thick, color);
            }
            else if (at < width + height)
            {
                var down = at - width;
                piece = MathF.Min(left, height - down);
                Draw.Rect(x + width - thick, y + down, thick, piece, color);
            }
            else if (at < 2f * width + height)
            {
                var back = at - width - height;
                piece = MathF.Min(left, width - back);
                Draw.Rect(x + width - back - piece, y + height - thick, piece, thick, color);
            }
            else
            {
                var up = at - 2f * width - height;
                piece = MathF.Min(left, height - up);
                Draw.Rect(x, y + height - up - piece, thick, piece, color);
            }

            left -= piece;
            at = (at + piece) % around;
        }
    }

    // A dotted path round a box that never stops crawling, like the lit trail on one of his puzzles.
    static void Ants(float x, float y, float width, float height, float time, Rgba color)
    {
        var around = 2f * (width + height);
        var crawl = time * 40f % AntPitch;
        for (var at = crawl; at < around + crawl - AntDash; at += AntPitch)
        {
            FrameRun(x, y, width, height, at, AntDash, 1.5f, color);
        }
    }

    // A tile part of the way through turning over. It is drawn squashed towards its middle line.
    static void Flipped(float x, float y, float width, float height, float turned, Rgba color)
    {
        var shown = height * Math.Clamp(turned, 0f, 1f);
        if (shown >= 0.5f)
        {
            Draw.Rect(x, y + (height - shown) / 2f, width, shown, color);
        }
    }

    // A wire cube seen a little from above, turned by angle. The far edges are fainter.
    static void Cube(float x, float y, float size, float angle, Rgba color)
    {
        const float tilt = 0.5f;
        Span<float> px = stackalloc float[8];
        Span<float> py = stackalloc float[8];
        Span<float> depth = stackalloc float[8];
        var (sin, cos) = MathF.SinCos(angle);
        var (tiltSin, tiltCos) = MathF.SinCos(tilt);
        for (var i = 0; i < 8; i++)
        {
            float cx = (i & 1) != 0 ? 1f : -1f;
            float cy = (i & 2) != 0 ? 1f : -1f;
            float cz = (i & 4) != 0 ? 1f : -1f;
            var turnedX = cx * cos + cz * sin;
            var turnedZ = -cx * sin + cz * cos;
            px[i] = x + turnedX * size;
            py[i] = y + (cy * tiltCos - turnedZ * tiltSin) * size;
            depth[i] = cy * tiltSin + turnedZ * tiltCos;
        }

        var faint = color with { A = (byte)(color.A * 0.35f) };
        for (var i = 0; i < 8; i++)
        {
            for (var bit = 1; bit <= 4; bit <<= 1)
            {
                if ((i & bit) != 0)
                {
                    continue;
                }

                var j = i | bit;
                if (depth[i] + depth[j] < 0f)
                {
                    Draw.Line(px[i], py[i], px[j], py[j], faint);
                }
                else
                {
                    Draw.Line(px[i], py[i], px[j], py[j], color);
                    Draw.Line(px[i] + 0.5f, py[i] + 0.5f, px[j] + 0.5f, py[j] + 0.5f, color);
                }
            }
        }
    }

    // The signs come from the game's own interface art: the white pixel question mark off its
    // target markers, the radar's cancel button and the orange warning tile. A small one in the
    // game's fonts can read as another letter, so where the art isn't loaded they are drawn from
    // blocks instead. Tinted art comes out darker than text of the same colour.
    static readonly Art PixelQuestion = new("HudModuleTargets.Image_I4", 2007f, 1415f, 30f, 50f);
    static readonly Art CancelDisc = new("HudModuleBespokeLocalRadar.Image_I4", 524f, 167f, 48f, 48f);
    static readonly Art WarningTile = new("ChallengeBits.LeaderboardMini_I1", 986f, 30f, 18f, 17f);

    static readonly string[] Question = { ".XXX.", "X...X", "....X", "..XX.", "..X..", ".....", "..X.." };
    static readonly string[] Bang = { "..X..", "..X..", "..X..", "..X..", "..X..", ".....", "..X.." };
    static readonly string[] Cross = { "X...X", "X...X", ".X.X.", "..X..", ".X.X.", "X...X", "X...X" };

    // One of the signs, centred on x and y, each block this big. Each unbroken run of blocks in a
    // row is one box.
    static void Sign(string[] rows, float x, float y, float block, Rgba color)
    {
        var left = x - 2.5f * block;
        var top = y - 3.5f * block;
        for (var row = 0; row < rows.Length; row++)
        {
            var line = rows[row];
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
                    Draw.Rect(left + start * block, top + row * block, (column - start) * block, block, color);
                    start = -1;
                }
            }
        }
    }

    // A question mark this tall, centred on x and y.
    static void QuestionMark(float x, float y, float height, Rgba color)
    {
        if (!PixelQuestion.Centred(x, y, height, color))
        {
            Sign(Question, x, y, height / 7f, color);
        }
    }

    // A tick, three lines thick.
    static void Tick(float x, float y, Rgba color)
    {
        for (var d = -0.5f; d <= 0.5f; d += 0.5f)
        {
            Draw.Line(x - 4f, y + d, x - 1f, y + 3.5f + d, color);
            Draw.Line(x - 1f, y + 3.5f + d, x + 5f, y - 4f + d, color);
        }
    }

    // A row of dots, which is what every rule here is.
    static void Dotted(float left, float right, float y, float shift, Rgba color)
    {
        for (var x = left + shift; x < right - 3f; x += 6f)
        {
            Draw.Rect(x, y, 3f, 1f, color);
        }
    }

    void Header(MenuView view, Rgba accent)
    {
        // The cube spins up whenever the rows change and settles back to a slow turn.
        var rest = Math.Clamp(1f - view.Arrived / 1.1f, 0f, 1f);
        Cube(Inset + 18f, 30f, 12f, view.Time * 0.9f + 9f * rest * rest, accent);
        QuestionMark(Inset + 18f, 30f, 18f, accent);

        Draw.Tracked("KNIGHTFALL", Inset + 50f, 12f, 15f, 5f, White);
        Draw.Tracked("ENIGMA", Inset + 51f, 32f, 9f, 6f, accent);

        StatusChip(view, Width - Inset - StatusChipWidth(), 12f);
        Draw.TextRight(Kit.Clock, Width - Inset, 36f, 10f, Ghost);

        Dotted(Inset, Width - Inset, 54f, 6f - view.Time * 14f % 6f, accent with { A = 150 });
        Tabs(view, accent);
        Draw.Rect(1f, HeaderHeight, Width - 2f, 1f, accent with { A = 90 });
    }

    const float ChipSize = 9f;
    const float ChipSpacing = 2f;

    static float StatusChipWidth()
    {
        var armed = Catalog.Active > 0;
        return Draw.TrackedWidth(armed ? "ARMED" : "STANDBY", ChipSize, ChipSpacing) + (armed ? 28f : 16f);
    }

    // STANDBY while nothing is switched on, ARMED once something is.
    static void StatusChip(MenuView view, float left, float y)
    {
        const float height = 16f;
        var armed = Catalog.Active > 0;
        var width = StatusChipWidth();

        if (armed)
        {
            Draw.Rect(left, y, width, height, Live with { A = 40 });
            Draw.Frame(left, y, width, height, 1f, Live);
            if (view.Time % 1f < 0.6f)
            {
                Draw.Rect(left + 7f, y + 5f, 6f, 6f, Live);
            }

            Draw.Tracked("ARMED", left + 20f, y + 4f, ChipSize, ChipSpacing, Live);
        }
        else
        {
            Draw.Frame(left, y, width, height, 1f, Ghost);
            Draw.Tracked("STANDBY", left + 8f, y + 4f, ChipSize, ChipSpacing, Dim);
        }
    }

    // Wide enough for the longest page name.
    static float ColumnWidth(MenuView view)
    {
        var widest = 0f;
        foreach (var page in view.Pages)
        {
            widest = MathF.Max(widest, Draw.TextWidth(page.Name, PageSize));
        }

        return MathF.Max(ColumnMinWidth, PageNameLeft + widest + ColumnInset);
    }

    // Every page down the column as a tile, numbered, with the keys that turn them at the top.
    // The current one is lit green and turns over when you land on it.
    static void Column(MenuView view, Rgba accent, float width, float body)
    {
        var left = -(width + ColumnGap);
        Draw.Fade(body);
        Draw.Tracked("PAGES", left + ColumnInset, 19f, 10f, 4f, accent);
        var right = left + width - ColumnInset;
        var next = Kit.CapWidth(view.PageNextKey, 10f);
        Kit.Cap(view.PageNextKey, right - next, 23f, 10f, Ghost, Pale);
        Kit.Cap(view.PagePrevKey, right - next - 4f - Kit.CapWidth(view.PagePrevKey, 10f), 23f, 10f, Ghost, Pale);
        Dotted(left + ColumnInset, right, ColumnRule, 0f, accent with { A = 150 });

        for (var i = 0; i < view.Pages.Count; i++)
        {
            var y = PageTop + i * PageHeight;
            var current = i == view.PageIndex;
            var turned = Flip(view, y);
            if (current && view.Arrived < 0.2f)
            {
                turned *= MathF.Abs(1f - 2f * view.Arrived / 0.2f);
            }

            Draw.Fade(1f);
            Flipped(left + 10f, y + 1f, width - 20f, PageHeight - 2f, turned, current ? accent : Tile with { A = 160 });
            if (turned < 0.9f)
            {
                continue;
            }

            Draw.Text(Kit.Number(i + 1), left + 18f, y + 9f, 10f, current ? Ink : Ghost);
            Draw.Text(view.Pages[i].Name, left + PageNameLeft, y + 6f, PageSize, current ? Ink : Dim);
        }
    }

    // The current tab sits in a small neon box that blinks on as you land on it.
    static void Tabs(MenuView view, Rgba accent)
    {
        const float size = 11f;
        const float spacing = 2f;
        const float gap = 20f;
        const float capGap = 14f;
        const float more = Kit.MoreWidth + 12f;
        var tabs = view.Page.Tabs;
        var y = TabsMiddle - 6f;

        var left = Inset + Kit.Cap(view.TabPrevKey, Inset, TabsMiddle, 9f, Ghost, Pale) + capGap;
        var right = Width - Inset - Kit.CapWidth(view.TabNextKey, 9f) - capGap;
        var strip = FitTabs(view, left, right, size, spacing, 0f, gap, more);
        var before = strip.First;
        var after = tabs.Count - strip.Last;

        var x = strip.Start;
        for (var i = strip.First; i < strip.Last; i++)
        {
            var current = i == view.TabIndex;
            var end = Draw.Tracked(tabs[i].Name, x, y, size, spacing, current ? accent : Dim);
            if (current && (view.Arrived > 0.26f || (int)(view.Arrived * 26f) % 2 == 1))
            {
                Tube(x - 4f, TabsMiddle - 11f, end - x + 8f, 1.5f, accent);
                Tube(x - 4f, TabsMiddle + 10f, end - x + 8f, 1.5f, accent);
                Draw.Rect(x - 7f, TabsMiddle - 9f, 1.5f, 19f, accent);
                Draw.Rect(end + 5.5f, TabsMiddle - 9f, 1.5f, 19f, accent);
            }

            x = end + gap;
        }

        // When they all fit, the next key follows the last tab.
        if (before == 0 && after == 0)
        {
            Kit.Cap(view.TabNextKey, x - gap + capGap, TabsMiddle, 9f, Ghost, Pale);
            return;
        }

        // Otherwise the strip runs the full width. The room left over shows the cut off name of
        // the next tab that way, or a small count when not even that fits.
        if (before > 0 && !(after == 0 && Draw.TrackedTail(tabs[strip.First - 1].Name, strip.Start - gap, y, size, spacing, Dim, left)))
        {
            Kit.More(left, TabsMiddle, before, left: true, accent, Dim);
        }

        if (after > 0 && !Draw.TrackedHead(tabs[strip.Last].Name, x, y, size, spacing, Dim, right))
        {
            Kit.More(right - Kit.MoreWidth, TabsMiddle, after, left: false, accent, Dim);
        }

        Kit.Cap(view.TabNextKey, right + capGap, TabsMiddle, 9f, Ghost, Pale);
    }

    void Rows(MenuView view, Rgba accent, float top, int visible, float body)
    {
        var lines = view.Lines;
        if (view.Rows.Count == 0)
        {
            return;
        }

        var (first, last, _) = Scroll(view, visible);
        if (lines.Count > visible)
        {
            Draw.Fade(body);
            More(view, accent, top, visible);
        }

        // The path only crawls round a row once the highlight has come to rest on it.
        var settled = MathF.Abs(view.Glide - view.RowIndex) < 0.5f;

        for (var i = first; i < last; i++)
        {
            // Tiles also turn over one after another when a tab or page turns.
            var y = top + (i - first) * RowHeight;
            var turned = view.Closing ? Flip(view, y) : MathF.Min(Flip(view, y), Kit.EaseOut((view.Arrived - (i - first) * 0.03f) / 0.18f));
            var line = lines[i];
            Draw.Fade(1f);
            if (line.Row is { } row)
            {
                Row(row, y, line.RowIndex == view.RowIndex && settled, turned, accent, view.Now, view.Time);
            }
            else if (turned >= 0.9f)
            {
                Heading(line.Heading!, y, accent);
            }
        }
    }

    // There is no scroll bar. When the list runs on past the rows on show, an arrow bobs at that
    // end. Each one fades in as the list leaves that end.
    static void More(MenuView view, Rgba accent, float top, int visible)
    {
        var bottom = top + visible * RowHeight;
        var smooth = ScrollGlide(view, visible);
        var above = MathF.Min(1f, smooth);
        var below = MathF.Min(1f, view.Lines.Count - visible - smooth);
        var bob = 0.75f + 0.75f * MathF.Sin(view.Time * 4f);

        if (above > 0f)
        {
            Arrow(Width / 2f, top - RuleGap + 2.5f - bob, down: false, accent with { A = (byte)(255 * above) });
        }

        if (below > 0f)
        {
            Arrow(Width / 2f, bottom + 2.5f + bob, down: true, accent with { A = (byte)(255 * below) });
        }
    }

    // An arrow head made of four boxes, so it can fade like everything else.
    static void Arrow(float middle, float y, bool down, Rgba color)
    {
        const int rows = 4;
        for (var i = 0; i < rows; i++)
        {
            var half = 0.5f + (down ? rows - 1 - i : i);
            Draw.Rect(middle - half, y + i, 2f * half, 1f, color);
        }
    }

    // A heading sits a little low, so it reads as part of the rows under it. A small hollow
    // square, its name spaced out, and dots running on to the edge.
    static void Heading(string text, float y, Rgba accent)
    {
        var middle = y + RowHeight / 2f + 3f;
        Draw.Frame(Inset, middle - 4f, 8f, 8f, 1.5f, accent);
        var end = Draw.Tracked(text, Inset + 16f, middle - 5f, 10f, 3f, accent);
        Dotted(end + 10f, Width - Inset, middle, 0f, accent with { A = 130 });
    }

    static void Row(Feature feature, float y, bool selected, float turned, Rgba accent, long now, float time)
    {
        var changed = !feature.Preference && !feature.IsDefault;
        var glow = feature.Glow(now);

        // The puzzle tile turns over once more when its value changes.
        var tileTurned = glow > 0f ? turned * MathF.Abs(2f * glow - 1f) : turned;
        var stripLeft = Inset + TileSize + 4f;
        Flipped(stripLeft, y + 2f, Width - Inset - stripLeft, TileSize, turned, Tile with { A = (byte)(selected ? 235 : 160) });
        if (selected)
        {
            Flipped(stripLeft, y + 2f, Width - Inset - stripLeft, TileSize, turned, accent with { A = 34 });
        }

        Flipped(Inset, y + 2f, TileSize, TileSize, tileTurned, changed ? Live : selected ? accent : Tile);
        if (turned < 0.9f)
        {
            return;
        }

        if (selected)
        {
            Ants(Inset - 4f, y - 1f, Width - 2f * Inset + 8f, RowHeight + 2f, time, accent);
        }

        if (tileTurned > 0.7f)
        {
            if (changed)
            {
                Tick(Inset + TileSize / 2f, y + RowHeight / 2f, selected ? White : Ink);
            }
            else
            {
                if (!selected)
                {
                    Draw.Rect(Inset, y + 2f, TileSize, 1f, TileEdge);
                }

                QuestionMark(Inset + TileSize / 2f, y + 2f + TileSize / 2f, 18f, selected ? Ink : Dim);
            }
        }

        // A row that can't be used right now steps back, with the reason where its value goes.
        if (feature.Blocked is { } why)
        {
            Draw.Text(feature.Label, LabelLeft, y + 7f, 15f, Ghost);
            Kit.Locked(why, ValueRight, y + 10f, 10f, Ghost, selected ? Pale : Dim, LabelLeft + Draw.TextWidth(feature.Label, 15f) + 16f);
            return;
        }

        Draw.Text(feature.Label, LabelLeft, y + 7f, 15f, selected ? White : Pale);

        // A value flashes white for a moment when it changes.
        var value = Rgba.Mix(changed ? Live : accent, White, glow);

        switch (feature)
        {
            case Toggle toggle:
                Switch(ValueRight - SwitchWidth, y + (RowHeight - SwitchHeight) / 2f, toggle.On, value);
                Draw.TextRight(toggle.ValueText, ValueRight - SwitchWidth - 9f, y + 10f, 11f, toggle.On ? value : Ghost);
                break;

            case Slider slider:
                Draw.TextRight(slider.ValueText, ValueRight, y + 7f, 15f, value);
                Kit.Meter(ValueRight - ValueRoom - MeterWidth, y + 11f, MeterWidth, 8f, 16, slider.Fill, slider.DefaultFill, Kit.Lit(slider, value), Kit.Unlit(slider, accent with { A = 36 }), Dim);
                break;

            case Choice choice:
                if (selected)
                {
                    var x = Draw.TextRight(">", ValueRight, y + 6f, 13f, Dim);
                    x = Draw.TextRightFit(choice.ValueText, x - 8f, y + 6f, 13f, value, LabelLeft + Draw.TextWidth(feature.Label, 15f) + 16f + Draw.TextWidth("<", 13f) + 8f);
                    Draw.TextRight("<", x - 8f, y + 6f, 13f, Dim);
                }
                else
                {
                    Draw.TextRightFit(choice.ValueText, ValueRight, y + 6f, 13f, value, LabelLeft + Draw.TextWidth(feature.Label, 15f) + 16f);
                }

                if (Kit.HasSwatches(choice))
                {
                    Kit.Swatches(ValueRight, y + 22f, choice.Swatches!, choice.Index);
                }
                else
                {
                    Pips(ValueRight, y + 22f, choice.Count, choice.Index, value, accent);
                }

                break;

            case Command command:
            {
                // After a press the cap shows what happened, in its own colour.
                var shown = command.Showing(now, out var outcome);
                var text = shown ? outcome.Text : command.Cap;
                var width = Draw.TextWidth(text, 10f) + 16f;
                var fill = shown ? Kit.Tone(outcome.Kind, Live, Dim) : selected ? accent : TileEdge;
                Draw.Rect(ValueRight - width, y + 7f, width, 16f, fill);
                Draw.Text(text, ValueRight - width + 8f, y + 10f, 10f, shown || selected ? Ink : Pale);
                break;
            }
        }
    }

    const float SwitchWidth = 34f;
    const float SwitchHeight = 14f;

    // Two tiles side by side. The left one is filled when off and the right one when on.
    static void Switch(float x, float y, bool on, Rgba lit)
    {
        const float cell = 16f;
        if (on)
        {
            Draw.Frame(x, y, cell, SwitchHeight, 1f, Ghost);
            Draw.Rect(x + cell + 2f, y, cell, SwitchHeight, lit);
        }
        else
        {
            Draw.Rect(x, y, cell, SwitchHeight, Ghost);
            Draw.Frame(x + cell + 2f, y, cell, SwitchHeight, 1f, Ghost);
        }
    }

    // One small square per choice, with the current one lit. A long list keeps to the shared track.
    static void Pips(float right, float y, int count, int index, Rgba lit, Rgba accent)
    {
        const int most = 10;
        const float pitch = 7f;
        if (count > most)
        {
            Kit.Dashes(right, y + 2f, count, index, lit, accent with { A = 36 });
            return;
        }

        for (var i = 0; i < count; i++)
        {
            Draw.Rect(right - 4f - (count - 1 - i) * pitch, y, 4f, 4f, i == index ? lit : accent with { A = 56 });
        }
    }

    // The help types itself out behind a block cursor each time another row is picked.
    void Help(MenuView view, Rgba accent, float top)
    {
        const float size = 13f;
        const float perSecond = 700f;
        var focus = view.Focus;
        if (!ReferenceEquals(focus, _helpFor))
        {
            _helpFor = focus;
            _helpAt = view.Now;
        }

        var help = focus?.Help;
        if (string.IsNullOrEmpty(help))
        {
            return;
        }

        Draw.Rect(Inset, top, 16f, 16f, accent);
        QuestionMark(Inset + 8f, top + 8f, 12f, Ink);

        // It is typed by width, cutting each line where the typing has got to, so no new text
        // is made as it goes.
        var typed = (float)((view.Now - _helpAt) / (double)System.Diagnostics.Stopwatch.Frequency) * perSecond;
        var left = Inset + 24f;
        var lines = Draw.Wrap(help, size, HelpWidth);
        var blink = view.Time % 0.8f < 0.4f;
        for (var i = 0; i < lines.Length && i < HelpLines; i++)
        {
            var y = top + i * HelpLineHeight;
            var width = Draw.TextWidth(lines[i], size);
            var last = i == Math.Min(lines.Length, HelpLines) - 1;
            if (typed >= width)
            {
                Draw.Text(lines[i], left, y, size, Pale);
                if (last && blink)
                {
                    Draw.Rect(left + width + 3f, y + 1f, 6f, 12f, accent);
                }

                typed -= width;
                continue;
            }

            if (typed > 0f)
            {
                Draw.TrackedHead(lines[i], left, y, size, 0f, Pale, left + typed);
                Draw.Rect(left + typed + 2f, y + 1f, 6f, 12f, accent);
            }

            break;
        }
    }

    // The bottom line. How many options are switched on, and the mod version.
    static void Status(Rgba accent, float top)
    {
        Draw.Rect(Inset, top, Width - 2f * Inset, 1f, accent with { A = 76 });

        var y = top + 11f;
        var active = Catalog.Active;
        if (active > 0)
        {
            Draw.Rect(Inset, y + 2f, 6f, 6f, Live);
        }
        else
        {
            Draw.Frame(Inset, y + 2f, 6f, 6f, 1f, Ghost);
        }

        var x = Draw.Tracked("ACTIVE", Inset + 12f, y, 10f, 2f, Ghost);
        Draw.Text(Kit.Number(active), x + 8f, y, 10f, active > 0 ? Live : Ghost);
        Draw.TextRight(Kit.Version, Width - Inset, y, 10f, Ghost);
    }

    public override float ToastHeight => 38f;

    // Each notification is a small sign that stutters on. A tile on the left turns over to show
    // how it went, and a row of small squares along the bottom goes dark with the time left.
    public override void RenderToasts(ToastView view)
    {
        const int squares = 26;
        var accent = Look.AccentOr(Accent);
        var width = ToastWidth;
        var height = ToastHeight;

        foreach (var card in view.Cards)
        {
            PlaceToast(view, card, 0f);
            var tone = Kit.Tone(card.Kind, accent, Dim);
            var strike = card.Shown >= 0.6f || Noise((int)(card.Shown * 40f) + (int)(card.Row * 7f)) > 0.4f ? 1f : 0.12f;

            Draw.Fade(card.Shown);
            Draw.GradientDown(0f, 0f, width, height, PanelTop, PanelBottom);
            Draw.Fade(card.Shown * strike);
            Neon(0f, 0f, width, height, tone, view.Time, 40 + (int)card.Row);

            Draw.Fade(card.Shown);
            var turned = Kit.EaseOut((card.Shown - 0.5f) / 0.5f);
            Flipped(9f, 9f, 20f, 20f, turned, tone);
            if (turned > 0.8f)
            {
                if (card.Kind == OutcomeKind.Done)
                {
                    Tick(19f, 19f, Ink);
                }
                else if (card.Kind != OutcomeKind.Failed)
                {
                    QuestionMark(19f, 19f, 13f, Ink);
                }
                else if (!WarningTile.At(9f, 9f, 20f, 20f, White))
                {
                    Sign(Bang, 19f, 19f, 1.8f, Ink);
                }
            }

            Draw.TrackedHead(card.Title, 38f, 7f, 8f, 2.5f, accent, width - 12f);
            ToastText(card.Text, 38f, 17f, 12f, card.Kind == OutcomeKind.Failed ? Kit.Failed : White, width - 8f);

            var lit = (int)MathF.Ceiling(card.Left * squares);
            for (var k = 0; k < squares; k++)
            {
                Draw.Rect(38f + k * 7f, height - 6f, 4f, 2f, k < lit ? tone : accent with { A = 40 });
            }
        }

        Draw.Fade(1f);
    }

    public override void RenderGuide(GuideView view)
    {
        const float inset = 14f;
        const float head = 32f;
        var accent = Look.AccentOr(Accent);
        var width = MathF.Max(GuideWidth(view), Draw.TrackedWidth(view.Title, 9f, 3f)) + 2f * inset;
        var height = head + view.Lines.Count * GuideLineHeight + 10f;

        PlaceGuide(width, height);
        Draw.Fade(view.Shown);
        Draw.GradientDown(0f, 0f, width, height, PanelTop, PanelBottom);
        Neon(0f, 0f, width, height, accent, view.Time, 70);

        Draw.Tracked(view.Title, inset, 12f, 9f, 3f, accent);
        Dotted(inset, width - inset, head - 4f, 0f, accent with { A = 150 });
        GuideLines(view, inset, head, Ghost, Pale, Dim);
        Draw.Fade(1f);
    }

    // The speedometer is a small sign of its own. Its cube turns faster the faster the car goes,
    // and the whole sign goes purple on boost.
    public override void RenderDash(DashView view)
    {
        const float width = 250f;
        const float inset = 14f;
        var accent = Look.AccentOr(Accent);
        var height = view.Stats ? 158f : 132f;
        var right = width - inset;
        var hot = view.Hot ? Live : accent;
        var empty = accent with { A = 36 };

        if (!PlaceDash(view, width, height))
        {
            return;
        }

        Draw.Fade(view.Shown);
        Draw.GradientDown(0f, 0f, width, height, PanelTop, PanelBottom);
        Neon(0f, 0f, width, height, hot, view.Time, 60);

        Draw.Tracked(view.Title, inset, 13f, 9f, 3f, accent);
        if (view.Mode.Length > 0)
        {
            Draw.Tracked(view.Mode, right - 44f - Draw.TrackedWidth(view.Mode, 9f, 3f), 13f, 9f, 3f, hot);
        }

        // Time between frames is taken from the view's own clock, which never stops.
        var passed = Math.Clamp(view.Time - _spinAt, 0f, 0.1f);
        _spinAt = view.Time;
        _spin = (_spin + passed * (0.6f + view.SpeedFill * 9f)) % (2f * MathF.PI);
        Cube(right - 18f, 34f, 13f, _spin, hot);

        DashSpeed(view, inset, 28f, White, Dim);
        DashNote(view, right - 44f, 34f, Pale, Ghost);
        Kit.Meter(inset, 72f, width - 2f * inset, 7f, 24, view.SpeedFill, 0f, hot, empty, default);

        var armour = view.LowArmour ? Kit.Failed : accent;
        DashBar(inset, right, 92f, "ARMOUR", view.Armour, view.ArmourText, Ghost, armour, empty, view.LowArmour ? Kit.Failed : Pale);
        DashBar(inset, right, 108f, "POWER", view.Power, view.PowerText, Ghost, White, empty, Pale);

        if (view.Stats)
        {
            Dotted(inset, right, 128f, 0f, accent with { A = 150 });
            DashStat(inset, 138f, "BEST", view.Best, view.Units, Ghost, Pale);
            DashStat(width / 2f + 6f, 138f, "JUMP", view.Jump, "", Ghost, Pale);
        }

        Draw.Fade(1f);
    }

    // The radial is a ring of puzzle tiles round the cube. A dotted path crawls out from the
    // middle to the one pointed at, and round it. In the middle the cube goes red: no answer.
    const float RingHub = 54f;
    const float RingReach = 150f;
    const float RingRadius = 52f;
    const float SlotHeight = 28f;
    const float SlotNameLeft = 38f;
    const float SlotPad = 10f;
    const float SlotLift = 8f;
    const float SlotFlash = 0.4f;
    const float ReadoutWidth = 440f;
    const int ReadoutLines = 2;

    const string TapHelp = "Point at a tile to see what it does.";
    const string HoldHelp = "Letting go in the middle does nothing. Point at a tile to pick it.";

    static readonly Rgba Shade = new(0, 4, 2, 100);

    public override float RadialHub => RingHub;

    public override float RadialReach => RingReach;

    public override float RadialAngle(int slot, int count)
    {
        var (x, y) = RingSpot(slot, count);
        return MathF.Atan2(x, -y) * 180f / MathF.PI;
    }

    // More tiles get a wider oval, so neighbours near the top and bottom don't touch.
    static (float X, float Y) RingSpot(int slot, int count)
    {
        var (width, height) = count > 8 ? (186f, 166f) : (146f, 124f);
        var (sin, cos) = MathF.SinCos(slot * 2f * MathF.PI / count);
        return (sin * width, -cos * height);
    }

    public override void RenderRadial(RadialView view)
    {
        var accent = Look.AccentOr(Accent);
        var count = view.Slots.Count;
        var shown = Kit.EaseOut(view.Age / 0.3f);
        var arrived = MathF.Min(view.Age, view.PageAge);
        var none = view.Hover < 0;
        var mark = none ? Kit.Failed : accent;

        Draw.Place(0f, 0f, 1f);
        Draw.Fade(shown);
        Draw.Rect(0f, 0f, Draw.ScreenWidth, Draw.ScreenHeight, Shade);

        PlaceRadial(view);
        Shapes.Under = Glass;
        Shapes.Opacity = 1f;
        Shapes.Disc(RingRadius * shown, Glass);
        if (shown >= 1f)
        {
            Shapes.Dashes(RingRadius - 2f, RingRadius, view.Time * 20f % 10f, 360f, 6f, 4f, mark);
        }

        var rest = Math.Clamp(1f - view.Age / 0.9f, 0f, 1f);
        Cube(0f, 0f, 22f, view.Time * 0.9f + 10f * rest * rest, mark);
        if (!none)
        {
            QuestionMark(0f, 0f, 24f, mark);
        }
        else if (!CancelDisc.Centred(0f, 0f, 30f, mark))
        {
            Sign(Cross, 0f, 0f, 3f, mark);
        }

        var highest = -RingRadius;
        var lowest = RingRadius;
        for (var i = 0; i < count; i++)
        {
            var (top, bottom) = RingSlot(view, i, count, accent, arrived);
            highest = MathF.Min(highest, top);
            lowest = MathF.Max(lowest, bottom);
        }

        Draw.Fade(shown);
        RingTitle(view, accent, highest - 40f);
        var end = RingReadout(view, accent, lowest + 28f);
        Kit.HintRow(view.Hints, 0f, end + 24f, 10f, Ghost, Pale, Dim);
        Draw.Fade(1f);
    }

    // What a favourite is set to, in the colour the menu gives it.
    static (string Text, Rgba Tone) RingState(RadialView view, Feature feature, Rgba accent) => feature switch
    {
        Toggle toggle => (toggle.ValueText, toggle.On ? Live : Dim),
        Choice choice => (choice.ValueText, choice.IsDefault ? Dim : Live),
        Command command when command.Showing(view.Now, out var outcome) => (outcome.Text, Kit.Tone(outcome.Kind, Live, Pale)),
        Command command => (command.Cap, accent),
        _ => (feature.ValueText, Dim),
    };

    // One favourite: its puzzle tile and its name. Returns where its top and bottom are.
    static (float Top, float Bottom) RingSlot(RadialView view, int i, int count, Rgba accent, float arrived)
    {
        var turned = Kit.EaseOut((arrived - 0.1f - i * 0.04f) / 0.2f);
        if (turned <= 0f)
        {
            return (0f, 0f);
        }

        var feature = view.Slots[i];
        var lift = view.Lifts[i];
        var over = i == view.Hover;
        var lit = RadialView.Lit(feature);
        var (state, tone) = RingState(view, feature, accent);
        var stateWidth = Draw.TrackedWidth(state, 10f, 1f);
        var width = SlotNameLeft + Draw.TextWidth(feature.Plain, 14f) + 14f + stateWidth + SlotPad;

        var (spotX, spotY) = RingSpot(i, count);
        var far = MathF.Sqrt(spotX * spotX + spotY * spotY);
        var spread = 1f + lift * SlotLift / far;
        var x = spotX * spread;
        var y = spotY * spread;
        var side = spotX > 1f ? 1 : spotX < -1f ? -1 : 0;
        var left = side > 0 ? x : side < 0 ? x - width : x - width / 2f;
        var top = y - SlotHeight / 2f;

        Draw.Fade(1f);
        if (over && turned >= 1f)
        {
            // The path crawls out from the ring to the near end of the tile.
            var (rimX, rimY) = (spotX / far * RingRadius, spotY / far * RingRadius);
            var nearX = side > 0 ? left - 4f : side < 0 ? left + width + 4f : left + width / 2f;
            var nearY = side == 0 ? (top < 0f ? top + SlotHeight + 4f : top - 4f) : top + SlotHeight / 2f;
            var length = MathF.Sqrt((nearX - rimX) * (nearX - rimX) + (nearY - rimY) * (nearY - rimY));
            for (var at = view.Time * 60f % 9f; at < length; at += 9f)
            {
                var from = at / length;
                var to = MathF.Min(1f, (at + 5f) / length);
                Draw.Line(rimX + (nearX - rimX) * from, rimY + (nearY - rimY) * from, rimX + (nearX - rimX) * to, rimY + (nearY - rimY) * to, accent);
                Draw.Line(rimX + (nearX - rimX) * from + 0.5f, rimY + (nearY - rimY) * from + 0.5f, rimX + (nearX - rimX) * to + 0.5f, rimY + (nearY - rimY) * to + 0.5f, accent);
            }
        }

        var flash = view.Flashed == i && view.FlashAge < SlotFlash;
        var tileTurned = flash ? turned * MathF.Abs(1f - 2f * view.FlashAge / SlotFlash) : turned;
        Flipped(left + TileSize + 2f, top, width - TileSize - 2f, SlotHeight, turned, Tile with { A = 235 });
        if (over)
        {
            Flipped(left + TileSize + 2f, top, width - TileSize - 2f, SlotHeight, turned, accent with { A = 40 });
        }

        Flipped(left, top, TileSize, SlotHeight, tileTurned, lit ? Live : over ? accent : Tile);
        if (turned < 0.9f)
        {
            return (top, top + SlotHeight);
        }

        if (over)
        {
            Ants(left - 4f, top - 4f, width + 8f, SlotHeight + 8f, view.Time, accent);
        }

        if (tileTurned > 0.7f)
        {
            if (lit)
            {
                Tick(left + TileSize / 2f, top + SlotHeight / 2f, Ink);
            }
            else
            {
                QuestionMark(left + TileSize / 2f, top + SlotHeight / 2f, 18f, over ? Ink : Dim);
            }
        }

        Draw.Text(feature.Plain, left + SlotNameLeft, top + 7f, 14f, over ? White : Pale);
        Draw.Tracked(state, left + width - SlotPad - stateWidth, top + 9f, 10f, 1f, Rgba.Mix(tone, White, feature.Glow(view.Now)));
        return (top, top + SlotHeight);
    }

    static void RingTitle(RadialView view, Rgba accent, float top)
    {
        const string title = "FAVOURITES";
        const float size = 12f;
        const float spacing = 4f;
        var width = Draw.TrackedWidth(title, size, spacing);
        Draw.Tracked(title, -width / 2f, top, size, spacing, Pale);
        Dotted(-width / 2f - 130f, -width / 2f - 16f, top + 6f, 0f, accent with { A = 150 });
        Dotted(width / 2f + 16f, width / 2f + 130f, top + 6f, 0f, accent with { A = 150 });

        if (view.Pages < 2)
        {
            return;
        }

        // SET 01 / 02, drawn in pieces so no new text is made each frame.
        var page = Kit.Number(view.Page + 1);
        var pages = Kit.Number(view.Pages);
        var line = Draw.TrackedWidth("SET", 9f, 2f) + 8f + Draw.TextWidth(page, 10f) + 6f + Draw.TextWidth("/", 10f) + 6f + Draw.TextWidth(pages, 10f);
        var x = -line / 2f;
        var y = top + 20f;
        x = Draw.Tracked("SET", x, y + 1f, 9f, 2f, Dim) + 8f;
        Draw.Text(page, x, y, 10f, accent);
        x += Draw.TextWidth(page, 10f) + 6f;
        Draw.Text("/", x, y, 10f, Ghost);
        x += Draw.TextWidth("/", 10f) + 6f;
        Draw.Text(pages, x, y, 10f, Dim);
    }

    // Under the tiles, a sign reads out the favourite pointed at: what kind it is, its state, its
    // hotkey and its help. Returns where it ends.
    static float RingReadout(RadialView view, Rgba accent, float top)
    {
        const float inset = 18f;
        const float helpTop = 38f;
        const float helpLine = 17f;
        var left = -ReadoutWidth / 2f;
        var right = -left;
        var helpWidth = ReadoutWidth - 2f * inset;

        // Room for the longest help on this page, so the sign doesn't jump about as the pointer
        // moves between tiles.
        var room = Draw.Wrap(view.Tap ? TapHelp : HoldHelp, 13f, helpWidth).Length;
        foreach (var slot in view.Slots)
        {
            room = Math.Max(room, Draw.Wrap(slot.Help, 13f, helpWidth).Length);
        }

        room = Math.Clamp(room, 1, ReadoutLines);
        var height = helpTop + (room - 1) * helpLine + 13f + 14f;
        var feature = view.Focus;
        Draw.GradientDown(left, top, ReadoutWidth, height, PanelTop, PanelBottom);
        Neon(left, top, ReadoutWidth, height, feature is null ? Kit.Failed : accent, view.Time, 90);

        if (feature is not null)
        {
            Draw.Rect(left + inset, top + 12f, 16f, 16f, accent);
            QuestionMark(left + inset + 8f, top + 20f, 12f, Ink);
        }
        else if (!CancelDisc.Centred(left + inset + 8f, top + 20f, 18f, Kit.Failed))
        {
            Draw.Rect(left + inset, top + 12f, 16f, 16f, Kit.Failed);
            Sign(Cross, left + inset + 8f, top + 20f, 1.5f, Ink);
        }
        var textLeft = left + inset + 26f;
        if (feature is null)
        {
            Draw.Tracked(view.Tap ? "POINT AT A TILE" : "NO ANSWER", textLeft, top + 15f, 11f, 2.5f, Pale);
            Draw.Text(view.Tap ? TapHelp : HoldHelp, left + inset, top + helpTop, 13f, Dim);
            return top + height;
        }

        var (state, tone) = RingState(view, feature, accent);
        var stateLeft = right - inset - Draw.TrackedWidth(state, 10f, 1.6f);
        Draw.Tracked(state, stateLeft, top + 15f, 10f, 1.6f, Rgba.Mix(tone, White, feature.Glow(view.Now)));

        var nameRight = stateLeft - 14f;
        var key = Hotkeys.KeyText(feature);
        if (key.Length > 0)
        {
            var capWidth = Kit.CapWidth(key, 9f);
            Kit.Cap(key, stateLeft - 12f - capWidth, top + 20f, 9f, Ghost, Pale);
            nameRight -= capWidth + 12f;
        }

        ToastText(feature.Plain, textLeft, top + 11f, 15f, White, nameRight);

        var lines = Draw.Wrap(feature.Help, 13f, helpWidth);
        for (var k = 0; k < lines.Length && k < room; k++)
        {
            Draw.Text(lines[k], left + inset, top + helpTop + k * helpLine, 13f, Dim);
        }

        return top + height;
    }

    // The parts of a question: the tag row, the title, the wrapped text and the buttons.
    const float AskWidth = 340f;
    const float AskPad = 18f;
    const float AskTag = 10f;
    const float AskTitle = 15f;
    const float AskBody = 11f;
    const float AskLine = 15f;
    const float AskButton = 24f;
    const float AskGap = 14f;

    static float AskHeight(string[] lines) =>
        AskPad + AskTag + 14f + AskTitle + 10f + lines.Length * AskLine + 14f + AskButton + AskPad;

    // A sign of its own that stutters on over the menu. It is a riddle with two answers.
    public override void RenderModal(MenuView view, ModalView modal)
    {
        var accent = Look.AccentOr(Accent);
        ShadeScreen(new Rgba(0, 4, 2, 130), modal.Opened);

        var lines = Draw.Wrap(modal.Ask.Text, AskBody, AskWidth - 2f * AskPad);
        var height = AskHeight(lines);
        PlaceModal(AskWidth, height + 34f);

        var opened = modal.Opened;
        var strike = opened >= 0.5f || Noise((int)(opened * 40f)) > 0.42f ? 1f : 0.12f;
        Draw.Fade(Kit.EaseOut(opened / 0.5f));
        Draw.GradientDown(0f, 0f, AskWidth, height, PanelTop, PanelBottom);
        Draw.Fade(Kit.EaseOut(opened / 0.5f) * strike);
        Neon(0f, 0f, AskWidth, height, accent, view.Time, 110);

        var lit = Math.Clamp((opened - 0.5f) * 2f, 0f, 1f);
        if (lit <= 0f)
        {
            return;
        }

        Draw.Fade(lit);
        var y = AskPad;
        Draw.Rect(AskPad, y - 3f, 16f, 16f, Live);
        QuestionMark(AskPad + 8f, y + 5f, 12f, Ink);
        var end = Draw.Tracked(modal.Ask.Tag, AskPad + 24f, y, AskTag, 2.5f, Live);
        Dotted(end + 8f, AskWidth - AskPad, y + AskTag / 2f, 0f, accent with { A = 150 });

        y += AskTag + 14f;
        Draw.Tracked(modal.Ask.Title, AskPad, y, AskTitle, 2f, White);
        y += AskTitle + 10f;
        foreach (var text in lines)
        {
            Draw.Text(text, AskPad, y, AskBody, Dim);
            y += AskLine;
        }

        y += 14f;
        var width = (AskWidth - 2f * AskPad - AskGap) / 2f;
        for (var i = 0; i < 2; i++)
        {
            var left = AskPad + i * (width + AskGap);
            var picked = modal.Choice == i;
            Draw.Rect(left, y, width, AskButton, picked ? accent : Tile);
            if (picked)
            {
                Ants(left - 4f, y - 4f, width + 8f, AskButton + 8f, view.Time, accent);
            }

            ButtonLabel(i == 0 ? modal.Ask.Yes : modal.Ask.No, left, y, width, AskButton, 10f, 2f, picked ? Ink : Pale);
        }

        Kit.HintRow(modal.Hints, AskWidth / 2f, height + 20f, KeyHints.Size, KeyHints.Frame, KeyHints.Key, KeyHints.Label);
        Draw.Fade(1f);
    }
}
