#nullable enable

namespace Knightfall;

// Gotham's deco side. One black panel with gold hairlines under a stepped crown, and a half
// sunburst standing over the bat. Everything opens outwards from the middle, like lift doors.
// Rays, gems and rings are triangles, which the game always draws solid, so they grow in.
sealed class WayneTheme : Theme
{
    const float Margin = 40f;
    const float Width = 440f;
    const float Middle = Width / 2f;
    const float Inset = 20f;
    const float TabsMiddle = 92f;
    const float HeaderRule = 114f;
    const float HeaderHeight = 116f;

    // The pages are listed down a narrower panel of their own, off the main one's left side.
    const float ColumnGap = 10f;
    const float ColumnInset = 14f;
    const float ColumnRule = 54f;
    const float PageTop = 62f;
    const float PageHeight = 26f;
    const float PageNameLeft = 40f;
    const float PageSize = 14f;
    const float ColumnMinWidth = 132f;
    const float RowHeight = 30f;

    const float RuleGap = 8f;
    const float NodeSize = 11f;
    const float LabelLeft = Inset + 26f;
    const float MeterWidth = 96f;
    const float ValueRoom = 54f;
    const float HelpLineHeight = 18f;
    const float HelpWidth = Width - 2f * Inset - 12f;
    const float BatBlock = 1.25f;
    const int MaxLines = 14;
    const int HelpLines = 3;

    // The medal sits on the top edge with the rays above it, so the menu starts this far down.
    const float MedalY = -2f;
    const float MedalRadius = 27f;
    const float CrownRise = 64f;
    const float BaseDrop = 9f;

    const float RayInner = 31f;
    const float RayLong = 26f;
    const float RayShort = 14f;
    const float RayRipple = 4f;
    const float RayReach = 34f;
    const float RaySpread = 30f;
    const float RayHalf = 1.7f;

    // How much of the opening is spent on the rays and the line down the middle.
    const float LineShare = 0.3f;

    // How far inside the opening doors something has to be before it is fully lit.
    const float RevealDepth = 12f;

    static readonly Rgba PanelTop = new(15, 13, 10, 242);
    static readonly Rgba PanelBottom = new(7, 6, 4, 236);
    static readonly Rgba Black = new(10, 9, 7);
    static readonly Rgba Gold = new(214, 174, 98);
    static readonly Rgba Bright = new(255, 236, 180);
    static readonly Rgba Ivory = new(246, 238, 216);
    static readonly Rgba Pale = new(216, 204, 176);
    static readonly Rgba Dim = new(152, 140, 114);
    static readonly Rgba Ghost = new(100, 90, 72);
    static readonly Rgba Unlit = new(58, 48, 30);

    // Jade beside the gold, for anything switched on.
    static readonly Rgba Live = new(86, 218, 172);

    static readonly HintStyle KeyHints = new(Size: 10f, CapHeight: 17f, LineHeight: 22f, Frame: Ghost, Key: Pale, Label: Dim);

    // The part of the panel the doors have opened so far. Set before each panel is drawn.
    float _doorLeft;
    float _doorRight;
    bool _oneLeaf;
    bool _open;
    float _lit;

    public override string Id => "wayne";

    public override string Name => "WAYNE";

    public override Rgba Accent => Gold;

    public override Rgba Armed => Live;

    public override void Render(MenuView view)
    {
        var accent = Look.AccentOr(Accent);
        var left = Inset;
        var right = Width - Inset;
        var zoom = Look.Zoom;

        var column = ColumnWidth(view);
        Draw.Place(Margin + (column + ColumnGap) * zoom + MoveX, Margin + CrownRise * zoom + MoveY, zoom);

        var visible = Math.Clamp(view.Lines.Count, 1, MaxLines);
        var rowsTop = HeaderHeight + RuleGap;
        var rowsBottom = rowsTop + visible * RowHeight;
        var helpTop = rowsBottom + 20f;
        var helpLines = HelpRoom(view, 13f, HelpWidth, HelpLines);
        var hintsTop = helpTop + helpLines * HelpLineHeight + 10f;
        var hintLines = HintLines(view, left, right, KeyHints);
        var statusTop = hintsTop + (hintLines - 1) * KeyHints.LineHeight + KeyHints.CapHeight + 12f;
        var height = statusTop + 32f;

        var columnHeight = PageTop + view.Pages.Count * PageHeight + 10f;
        var tallest = height + (MaxLines - visible) * RowHeight + (HelpLines - helpLines) * HelpLineHeight;
        Covers(-(column + ColumnGap) - 1f, -CrownRise, Width + 1f, MathF.Max(height, columnHeight) + BaseDrop, MathF.Max(tallest, columnHeight) + BaseDrop);

        var rise = Kit.EaseOut(view.Opened / LineShare);
        var swing = Math.Clamp((view.Opened - LineShare) / (1f - LineShare), 0f, 1f);
        var doors = Kit.EaseOut(swing);

        Draw.Fade(1f);
        Burst(Middle, MedalY, RayInner, -84f, 84f, 17, rise, view.Time, accent);

        if (swing <= 0f)
        {
            // First a gold line drops from the medal to where the panel will end.
            var drop = (height - MedalY) * rise;
            Draw.Rect(Middle - 0.5f, MedalY, 1f, drop, accent);
            Draw.Rect(Middle - 1.5f, MedalY + drop - 3f, 3f, 3f, Bright);
            Medal(Middle, MedalY, MedalRadius, rise, view.Time, accent, 40f);
            return;
        }

        Column(view, accent, column, Kit.EaseOut((swing - 0.35f) / 0.5f));

        var half = Middle * doors;
        _doorLeft = Middle - half;
        _doorRight = Middle + half;
        _oneLeaf = false;
        _open = doors >= 1f;

        Draw.Fade(1f);
        Tier(100f, -7f, 240f, 7f, accent);
        Tier(150f, -13f, 140f, 6f, accent);
        Tier(184f, -18f, 72f, 5f, accent);
        Foot(60f, height, 320f, 5f, accent);
        Foot(140f, height + 5f, 160f, 4f, accent);

        Draw.GradientDown(_doorLeft, 0f, _doorRight - _doorLeft, height, PanelTop, PanelBottom);
        Edges(0f, 0f, Width, height, accent);
        if (doors < 1f)
        {
            Draw.GradientAcross(_doorLeft, 1f, MathF.Min(24f, half), height - 2f, accent with { A = 70 }, accent with { A = 0 });
            Draw.GradientAcross(_doorRight - MathF.Min(24f, half), 1f, MathF.Min(24f, half), height - 2f, accent with { A = 0 }, accent with { A = 70 });
            Draw.Rect(_doorLeft, 0f, 2f, height, Bright);
            Draw.Rect(_doorRight - 2f, 0f, 2f, height, Bright);
        }

        Header(view, accent);
        Rows(view, accent, rowsTop, visible);

        if (Show(left))
        {
            Draw.Rect(left, rowsBottom + RuleGap, right - left, 1f, accent with { A = 76 });
        }

        Help(view, accent, helpTop);

        if (Show(left))
        {
            DrawHints(view, left, right, hintsTop, KeyHints);
        }

        Status(accent, statusTop);

        // Once it is open a run of light keeps going round the frame.
        Draw.Fade(1f);
        if (doors >= 1f)
        {
            FrameRun(0f, 0f, Width, height, view.Time * 130f, 60f, Bright);
        }

        Medal(Middle, MedalY, MedalRadius, 1f, view.Time, accent, 40f);
        Draw.Fade(1f);
    }

    // Lights whatever is drawn next by how far the doors have passed it. Give it the edge of the
    // thing that is furthest from where the doors part. Once they are all the way open everything
    // is lit. Returns false when they haven't reached it.
    bool Show(float x, float extra = 1f)
    {
        var inside = _oneLeaf ? x - _doorLeft : MathF.Min(x - _doorLeft, _doorRight - x);
        _lit = (_open ? 1f : Math.Clamp(inside / RevealDepth, 0f, 1f)) * extra;
        Draw.Fade(_lit);
        return _lit > 0f;
    }

    // A box cut to the part of the panel that is open.
    void DoorRect(float x, float y, float width, float height, Rgba color)
    {
        var from = MathF.Max(x, _doorLeft);
        var to = MathF.Min(x + width, _doorRight);
        if (to > from)
        {
            Draw.Rect(from, y, to - from, height, color);
        }
    }

    // The gold frame and a fainter one inside it.
    void Edges(float x, float y, float width, float height, Rgba accent)
    {
        var line = accent with { A = 230 };
        DoorRect(x, y, width, 1f, line);
        DoorRect(x, y + height - 1f, width, 1f, line);
        DoorRect(x, y + 1f, 1f, height - 2f, line);
        DoorRect(x + width - 1f, y + 1f, 1f, height - 2f, line);

        var inner = accent with { A = 70 };
        DoorRect(x + 4f, y + 4f, width - 8f, 1f, inner);
        DoorRect(x + 4f, y + height - 5f, width - 8f, 1f, inner);
        DoorRect(x + 4f, y + 5f, 1f, height - 10f, inner);
        DoorRect(x + width - 5f, y + 5f, 1f, height - 10f, inner);
    }

    // One step of the crown: a black block with gold along its top and sides.
    void Tier(float x, float y, float width, float height, Rgba accent)
    {
        DoorRect(x, y, width, height, Black);
        DoorRect(x, y, width, 1f, accent);
        DoorRect(x, y, 1f, height, accent);
        DoorRect(x + width - 1f, y, 1f, height, accent);
    }

    // One step of the base, the crown upside down.
    void Foot(float x, float y, float width, float height, Rgba accent)
    {
        DoorRect(x, y, width, height, Black);
        DoorRect(x, y + height - 1f, width, 1f, accent);
        DoorRect(x, y, 1f, height, accent);
        DoorRect(x + width - 1f, y, 1f, height, accent);
    }

    // Three bars that step down in height, like the fins on a deco tower. Dir is the way they step.
    static void Fins(float x, float y, float height, float dir, Rgba color)
    {
        Draw.Rect(x, y, 2f, height, color);
        Draw.Rect(x + dir * 4f, y + height * 0.17f, 1f, height * 0.66f, color);
        Draw.Rect(x + dir * 7f, y + height * 0.33f, 1f, height * 0.34f, color);
    }

    // A square stood on its corner. It is one quad, so it can't fade and is sized by grow instead.
    static void Gem(float x, float y, float size, Rgba color, float grow = 1f)
    {
        var half = size / 2f * Math.Clamp(grow, 0f, 1f);
        if (half >= 0.5f)
        {
            Draw.Quad(x, y - half, x + half, y, x, y + half, x - half, y, color);
        }
    }

    static void HollowGem(float x, float y, float size, Rgba color, float grow = 1f)
    {
        Gem(x, y, size, color, grow);
        Gem(x, y, size - 3.5f, Black, grow);
    }

    // A fan of rays round a point, each one thin triangle. They ripple outwards all the time. The
    // rays near reach grow long and bright by amount.
    static void Burst(float x, float y, float inner, float from, float to, int count, float grow, float time, Rgba accent, float reach = 0f, float amount = 0f)
    {
        if (grow <= 0f)
        {
            return;
        }

        var faint = Rgba.Mix(accent, Black, 0.4f);
        for (var i = 0; i < count; i++)
        {
            var angle = from + (to - from) * i / (count - 1f);
            var tall = i % 2 == 0;
            var near = amount > 0f ? Math.Clamp(1f - MathF.Abs(Kit.Apart(angle, reach)) / RaySpread, 0f, 1f) * amount : 0f;
            var ripple = RayRipple * MathF.Sin(time * 2.2f - MathF.Abs(Kit.Apart(angle, 0f)) * 0.05f);
            var length = ((tall ? RayLong : RayShort) + ripple + near * RayReach) * grow;
            var (ax, ay) = Kit.Around(angle - RayHalf, inner);
            var (bx, by) = Kit.Around(angle + RayHalf, inner);
            var (tx, ty) = Kit.Around(angle, inner + length);
            Draw.Triangle(x + ax, y + ay, x + bx, y + by, x + tx, y + ty, Rgba.Mix(tall ? accent : faint, Bright, near));
        }
    }

    // The black disc with the bat on it. Its rings draw round as it comes in. The bat is the
    // game's own spinning one when that is loaded, and one made of blocks when it isn't.
    static void Medal(float x, float y, float radius, float drawn, float time, Rgba accent, float batSize)
    {
        var originX = Draw.ScreenX(0f);
        var originY = Draw.ScreenY(0f);
        var zoom = Draw.Zoom;

        // Round shapes are drawn about the place's own middle, so the place moves to the medal.
        Draw.Place(originX + x * zoom, originY + y * zoom, zoom);
        Shapes.Under = Black;
        Shapes.Opacity = 1f;
        Shapes.Disc(radius, Black);
        Shapes.Arc(radius - 1.4f, radius, 0f, 360f * drawn, accent);
        Shapes.Arc(radius - 5.6f, radius - 4.8f, 0f, 360f * drawn, accent, 0.5f);
        Draw.Place(originX, originY, zoom);

        if (drawn < 1f)
        {
            return;
        }

        Draw.Fade(1f);
        if (!Kit.SpinningBat(x - batSize / 2f, y - batSize / 2f + batSize * 0.065f, batSize, time, 9f, accent))
        {
            var block = radius * 0.047f;
            Kit.Bat(x - Kit.BatColumns * block / 2f, y - Kit.BatRowCount * block / 2f + 1f, block, accent);
        }
    }

    // A short run of light on a frame, at a distance round it clockwise from the top left.
    static void FrameRun(float x, float y, float width, float height, float at, float length, Rgba color)
    {
        var around = 2f * (width + height);
        at %= around;
        var left = length;
        for (var side = 0; side < 5 && left > 0.01f; side++)
        {
            float piece;
            if (at < width)
            {
                piece = MathF.Min(left, width - at);
                Draw.Rect(x + at, y, piece, 1f, color);
            }
            else if (at < width + height)
            {
                var down = at - width;
                piece = MathF.Min(left, height - down);
                Draw.Rect(x + width - 1f, y + down, 1f, piece, color);
            }
            else if (at < 2f * width + height)
            {
                var back = at - width - height;
                piece = MathF.Min(left, width - back);
                Draw.Rect(x + width - back - piece, y + height - 1f, piece, 1f, color);
            }
            else
            {
                var up = at - 2f * width - height;
                piece = MathF.Min(left, height - up);
                Draw.Rect(x, y + height - up - piece, 1f, piece, color);
            }

            left -= piece;
            at = (at + piece) % around;
        }
    }

    void Header(MenuView view, Rgba accent)
    {
        if (Show(Inset))
        {
            Draw.Text(Kit.Clock, Inset, 15f, 10f, Ghost);
            StatusChip(view, Width - Inset - StatusChipWidth(), 11f);
        }

        // KNIGHTFALL in the middle, with three lines stepping in on each side of it.
        const float nameSize = 15f;
        const float nameSpacing = 7f;
        const float nameTop = 36f;
        var nameWidth = Draw.TrackedWidth("KNIGHTFALL", nameSize, nameSpacing);
        var nameLeft = Middle - nameWidth / 2f;
        if (Show(nameLeft))
        {
            Draw.Tracked("KNIGHTFALL", nameLeft, nameTop, nameSize, nameSpacing, Ivory);
        }

        for (var i = 0; i < 3; i++)
        {
            var length = 60f - i * 16f;
            var y = nameTop + 4f + i * 5f;
            if (Show(nameLeft - 14f - length))
            {
                var line = accent with { A = (byte)(255 - i * 50) };
                Draw.Rect(nameLeft - 14f - length, y, length, 1f, line);
                Draw.Rect(nameLeft + nameWidth + 14f, y, length, 1f, line);
            }
        }

        const string owner = "WAYNE ENTERPRISES";
        var ownerLeft = Middle - Draw.TrackedWidth(owner, 9f, 5f) / 2f;
        if (Show(ownerLeft))
        {
            Draw.Tracked(owner, ownerLeft, 59f, 9f, 5f, accent);
        }

        Tabs(view, accent);

        // The rule under the tabs breaks round a gem in the middle.
        Draw.Fade(1f);
        DoorRect(Inset, HeaderRule, Middle - Inset - 10f, 1f, accent with { A = 180 });
        DoorRect(Middle + 10f, HeaderRule, Middle - Inset - 10f, 1f, accent with { A = 180 });
        Gem(Middle, HeaderRule + 0.5f, 7f, accent);
    }

    const float ChipSize = 9f;
    const float ChipSpacing = 2f;

    static float StatusChipWidth()
    {
        var armed = Catalog.Active > 0;
        return Draw.TrackedWidth(armed ? "ARMED" : "STANDBY", ChipSize, ChipSpacing) + (armed ? 28f : 16f);
    }

    // STANDBY while nothing is switched on, ARMED once something is.
    void StatusChip(MenuView view, float left, float y)
    {
        const float height = 16f;
        var armed = Catalog.Active > 0;
        var width = StatusChipWidth();

        if (armed)
        {
            Draw.Rect(left, y, width, height, Live with { A = 36 });
            Draw.Frame(left, y, width, height, 1f, Live);
            if (view.Time % 1f < 0.6f)
            {
                Gem(left + 10f, y + 8f, 7f, Live, _lit);
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

    // Every page down the column, numbered, with the keys that turn them at the top. It opens
    // leftwards off the main panel once the doors are partly open.
    void Column(MenuView view, Rgba accent, float width, float open)
    {
        if (open <= 0f)
        {
            return;
        }

        var right = -ColumnGap;
        var left = right - width;
        var height = PageTop + view.Pages.Count * PageHeight + 10f;
        _doorLeft = right - width * open;
        _doorRight = right;
        _oneLeaf = true;
        _open = open >= 1f;

        Draw.Fade(1f);
        Tier(left + 40f, -5f, width - 80f, 5f, accent);
        Draw.GradientDown(_doorLeft, 0f, _doorRight - _doorLeft, height, PanelTop, PanelBottom);
        Edges(left, 0f, width, height, accent);
        if (open < 1f)
        {
            Draw.Rect(_doorLeft, 0f, 2f, height, Bright);
        }

        if (Show(left + ColumnInset))
        {
            Draw.Tracked("PAGES", left + ColumnInset, 19f, 10f, 4f, accent);
            var edge = left + width - ColumnInset;
            var next = Kit.CapWidth(view.PageNextKey, 10f);
            Kit.Cap(view.PageNextKey, edge - next, 23f, 10f, Ghost, Pale);
            Kit.Cap(view.PagePrevKey, edge - next - 4f - Kit.CapWidth(view.PagePrevKey, 10f), 23f, 10f, Ghost, Pale);
            Draw.Rect(left + ColumnInset, ColumnRule, width - 2f * ColumnInset, 1f, accent with { A = 120 });
        }

        if (!Show(left + 4f))
        {
            return;
        }

        for (var i = 0; i < view.Pages.Count; i++)
        {
            var y = PageTop + i * PageHeight;
            var current = i == view.PageIndex;
            if (current)
            {
                Draw.Rect(left + 1f, y, width - 2f, PageHeight, accent with { A = 34 });
                Fins(left + 4f, y + 2f, PageHeight - 4f, 1f, accent);
                Kit.Sheen(left + 1f, y, width - 2f, PageHeight, view.Time, new Rgba(255, 255, 255, 18));
            }

            Draw.Text(Kit.Number(i + 1), left + ColumnInset + 4f, y + 9f, 10f, current ? accent : Ghost);
            Draw.Text(view.Pages[i].Name, left + PageNameLeft, y + 6f, PageSize, current ? Ivory : Dim);
        }
    }

    // The tabs sit in the middle when they all fit. The current one stands on a stepped underline.
    void Tabs(MenuView view, Rgba accent)
    {
        const float size = 11f;
        const float spacing = 2f;
        const float gap = 18f;
        const float capGap = 12f;
        const float more = Kit.MoreWidth + 12f;
        var tabs = view.Page.Tabs;
        var y = TabsMiddle - 6f;

        var prev = Kit.CapWidth(view.TabPrevKey, 9f);
        var next = Kit.CapWidth(view.TabNextKey, 9f);
        var left = Inset + prev + capGap;
        var right = Width - Inset - next - capGap;
        var strip = FitTabs(view, left, right, size, spacing, 0f, gap, more);
        var before = strip.First;
        var after = tabs.Count - strip.Last;
        var whole = before == 0 && after == 0;

        var x = strip.Start;
        if (whole)
        {
            var total = -gap;
            foreach (var tab in tabs)
            {
                total += Draw.TrackedWidth(tab.Name, size, spacing) + gap;
            }

            x = Middle - total / 2f;
        }

        var prevLeft = whole ? x - capGap - prev : Inset;
        if (Show(prevLeft))
        {
            Kit.Cap(view.TabPrevKey, prevLeft, TabsMiddle, 9f, Ghost, Pale);
        }

        for (var i = strip.First; i < strip.Last; i++)
        {
            var current = i == view.TabIndex;
            var width = Draw.TrackedWidth(tabs[i].Name, size, spacing);
            if (Show(x + width / 2f < Middle ? x : x + width))
            {
                Draw.Tracked(tabs[i].Name, x, y, size, spacing, current ? accent : Dim);
                if (current)
                {
                    Draw.Rect(x, TabsMiddle + 9f, width, 2f, accent);
                    Draw.Rect(x + 6f, TabsMiddle + 13f, width - 12f, 1f, accent with { A = 150 });
                    Gem(x + width / 2f, TabsMiddle + 17.5f, 5f, accent, _lit);
                }
            }

            x += width + gap;
        }

        if (whole)
        {
            if (Show(x - gap + capGap + next))
            {
                Kit.Cap(view.TabNextKey, x - gap + capGap, TabsMiddle, 9f, Ghost, Pale);
            }

            return;
        }

        // Otherwise the strip runs the full width. The room left over shows the cut off name of
        // the next tab that way, or a small count when not even that fits.
        if (!Show(Inset))
        {
            return;
        }

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

    void Rows(MenuView view, Rgba accent, float top, int visible)
    {
        var lines = view.Lines;
        if (view.Rows.Count == 0)
        {
            return;
        }

        var (first, last, glide) = Scroll(view, visible);

        // The highlight glides between rows, a gold band between two sets of fins.
        var highlight = top + glide * RowHeight;
        if (Show(3f))
        {
            Draw.Rect(1f, highlight, Width - 2f, RowHeight, accent with { A = 34 });
            Draw.Rect(13f, highlight, Width - 26f, 1f, accent with { A = 216 });
            Draw.Rect(13f, highlight + RowHeight - 1f, Width - 26f, 1f, accent with { A = 216 });
            Fins(3f, highlight, RowHeight, 1f, accent);
            Fins(Width - 5f, highlight, RowHeight, -1f, accent);
            Kit.Sheen(1f, highlight, Width - 2f, RowHeight, view.Time, new Rgba(255, 255, 255, 18));
        }

        if (lines.Count > visible)
        {
            More(view, accent, top, visible);
        }

        for (var i = first; i < last; i++)
        {
            // Rows also come in one after another when a tab or page turns.
            var y = top + (i - first) * RowHeight;
            var arrive = view.Closing ? 1f : Kit.Cascade(view.Arrived, i - first);
            if (!Show(Inset, arrive))
            {
                continue;
            }

            var line = lines[i];
            if (line.Row is { } row)
            {
                Row(row, y, line.RowIndex == view.RowIndex, accent, view.Now);
            }
            else
            {
                Heading(line.Heading!, y, accent);
            }
        }
    }

    // There is no scroll bar. When the list runs on past the rows on show, a gem with a short
    // rule each side bobs at that end. Each one comes in as the list leaves that end.
    void More(MenuView view, Rgba accent, float top, int visible)
    {
        var bottom = top + visible * RowHeight;
        var smooth = ScrollGlide(view, visible);
        var above = MathF.Min(1f, smooth);
        var below = MathF.Min(1f, view.Lines.Count - visible - smooth);
        var bob = 1.5f * MathF.Sin(view.Time * 4f);

        if (above > 0f && Show(Middle - 40f, above))
        {
            MoreMark(top - RuleGap + 2f - bob, accent);
        }

        if (below > 0f && Show(Middle - 40f, below))
        {
            MoreMark(bottom + 5f + bob, accent);
        }
    }

    void MoreMark(float y, Rgba accent)
    {
        Draw.GradientAcross(Middle - 40f, y, 32f, 1f, accent with { A = 0 }, accent);
        Draw.GradientAcross(Middle + 8f, y, 32f, 1f, accent, accent with { A = 0 });
        Gem(Middle, y + 0.5f, 7f, Bright, _lit);
    }

    // A heading sits a little low, so it reads as part of the rows under it. A hollow gem, its
    // name spaced out, and two rules that step in before a small gem at the far end.
    void Heading(string text, float y, Rgba accent)
    {
        var middle = y + RowHeight / 2f + 3f;
        var right = Width - Inset;
        HollowGem(Inset + 5f, middle, NodeSize, accent, _lit);

        var end = Draw.Tracked(text, LabelLeft, middle - 5f, 10f, 3f, accent);
        Draw.Rect(end + 10f, middle, right - end - 18f, 1f, accent with { A = 86 });
        Draw.Rect(end + 10f, middle + 3f, right - end - 46f, 1f, accent with { A = 46 });
        Gem(right - 3f, middle + 0.5f, 5f, accent, _lit);
    }

    void Row(Feature feature, float y, bool selected, Rgba accent, long now)
    {
        var right = Width - Inset;
        var changed = !feature.Preference && !feature.IsDefault;

        // Each option has a gem. It fills with jade once the option is changed.
        var nodeX = Inset + 5f;
        var nodeY = y + RowHeight / 2f;
        if (changed)
        {
            Gem(nodeX, nodeY, NodeSize, Live, _lit);
        }
        else if (selected)
        {
            Gem(nodeX, nodeY, NodeSize, accent, _lit);
        }
        else
        {
            HollowGem(nodeX, nodeY, NodeSize, Ghost, _lit);
        }

        // A row that can't be used right now steps back, with the reason where its value goes.
        if (feature.Blocked is { } why)
        {
            Draw.Text(feature.Label, LabelLeft, y + 7f, 15f, selected ? Dim : Ghost);
            Kit.Locked(why, right, y + 10f, 10f, Ghost, selected ? Pale : Dim, LabelLeft + Draw.TextWidth(feature.Label, 15f) + 16f);
            return;
        }

        Draw.Text(feature.Label, LabelLeft, y + 7f, 15f, selected ? Ivory : Pale);

        // A value flashes white for a moment when it changes.
        var value = Rgba.Mix(changed ? Live : accent, new Rgba(255, 255, 255), feature.Glow(now));

        switch (feature)
        {
            case ModeToggle moded:
                Kit.Capsule(right, y + (RowHeight - 16f) / 2f, 16f, 10f, moded, selected, value, Black, selected ? accent : Ghost, selected ? accent : Dim, Dim);
                break;

            case Toggle toggle:
                Switch(right - SwitchWidth, y + (RowHeight - SwitchHeight) / 2f, toggle.On, value);
                Draw.TextRight(toggle.ValueText, right - SwitchWidth - 9f, y + 10f, 11f, toggle.On ? value : Ghost);
                break;

            case Slider slider:
                Draw.TextRight(slider.ValueText, right, y + 7f, 15f, value);
                Kit.Meter(right - ValueRoom - MeterWidth, y + 11f, MeterWidth, 8f, 16, slider.Fill, slider.DefaultFill, value, accent with { A = 36 }, Dim);
                break;

            case Choice choice:
                if (selected)
                {
                    var x = Draw.TextRight(">", right, y + 6f, 13f, Dim);
                    x = Draw.TextRightFit(choice.ValueText, x - 8f, y + 6f, 13f, value, LabelLeft + Draw.TextWidth(feature.Label, 15f) + 16f + Draw.TextWidth("<", 13f) + 8f);
                    Draw.TextRight("<", x - 8f, y + 6f, 13f, Dim);
                }
                else
                {
                    Draw.TextRightFit(choice.ValueText, right, y + 6f, 13f, value, LabelLeft + Draw.TextWidth(feature.Label, 15f) + 16f);
                }

                Pips(right, y + 25f, choice.Count, choice.Index, value, accent);
                break;

            case Command command:
            {
                // After a press the cap shows what happened, in its own colour.
                var shown = command.Showing(now, out var outcome);
                var text = shown ? outcome.Text : command.Cap;
                var tone = shown ? Kit.Tone(outcome.Kind, Live, Pale) : accent;
                var width = Draw.TextWidth(text, 10f) + 16f;
                var lit = selected || shown;
                if (lit)
                {
                    Draw.Rect(right - width, y + 7f, width, 16f, tone with { A = 50 });
                }

                var edge = lit ? tone : Ghost;
                Draw.Frame(right - width, y + 7f, width, 16f, 1f, edge);
                Draw.Rect(right - width - 3f, y + 10f, 1f, 10f, edge);
                Draw.Rect(right + 2f, y + 10f, 1f, 10f, edge);
                Draw.Text(text, right - width + 8f, y + 10f, 10f, shown ? tone : selected ? Ivory : Dim);
                break;
            }
        }
    }

    const float SwitchWidth = 30f;
    const float SwitchHeight = 14f;

    // A slide switch with a gem for its knob. The gem sits right when on.
    void Switch(float x, float y, bool on, Rgba lit)
    {
        if (on)
        {
            Draw.Rect(x + 1f, y + 1f, SwitchWidth - 2f, SwitchHeight - 2f, lit with { A = 52 });
            Draw.Frame(x, y, SwitchWidth, SwitchHeight, 1f, lit);
            Gem(x + SwitchWidth - 8f, y + SwitchHeight / 2f, 11f, lit, _lit);
        }
        else
        {
            Draw.Frame(x, y, SwitchWidth, SwitchHeight, 1f, Ghost);
            Gem(x + 8f, y + SwitchHeight / 2f, 9f, Ghost, _lit);
        }
    }

    // One small gem per choice, with the current one lit. A long list keeps to the shared track.
    void Pips(float right, float y, int count, int index, Rgba lit, Rgba accent)
    {
        const int most = 10;
        const float pitch = 9f;
        if (count > most)
        {
            Kit.Dashes(right, y - 1f, count, index, lit, accent with { A = 36 });
            return;
        }

        var empty = Rgba.Mix(Black, accent, 0.3f);
        for (var i = 0; i < count; i++)
        {
            Gem(right - 3f - (count - 1 - i) * pitch, y, 6f, i == index ? lit : empty, _lit);
        }
    }

    void Help(MenuView view, Rgba accent, float top)
    {
        var help = view.Focus?.Help;
        if (string.IsNullOrEmpty(help) || !Show(Inset))
        {
            return;
        }

        Gem(Inset + 3f, top + 7f, 6f, accent, _lit);
        var lines = Draw.Wrap(help, 13f, HelpWidth);
        for (var i = 0; i < lines.Length && i < HelpLines; i++)
        {
            Draw.Text(lines[i], Inset + 12f, top + i * HelpLineHeight, 13f, Pale);
        }
    }

    // The bottom line. How many options are switched on, and the mod version.
    void Status(Rgba accent, float top)
    {
        if (!Show(Inset))
        {
            return;
        }

        var rule = accent with { A = 76 };
        Draw.Rect(Inset, top, Middle - Inset - 10f, 1f, rule);
        Draw.Rect(Middle + 10f, top, Middle - Inset - 10f, 1f, rule);
        Gem(Middle, top + 0.5f, 5f, Rgba.Mix(Black, accent, 0.7f), _lit);

        var y = top + 11f;
        var active = Catalog.Active;
        if (active > 0)
        {
            Gem(Inset + 3f, y + 5f, 7f, Live, _lit);
        }
        else
        {
            HollowGem(Inset + 3f, y + 5f, 7f, Ghost, _lit);
        }

        var x = Draw.Tracked("ACTIVE", Inset + 12f, y, 10f, 2f, Ghost);
        Draw.Text(Kit.Number(active), x + 8f, y, 10f, active > 0 ? Live : Ghost);
        Draw.TextRight(Kit.Version, Width - Inset, y, 10f, Ghost);
    }

    // A small black and gold panel with the same two frames, for the things that stand alone.
    static void Plate(float x, float y, float width, float height, Rgba accent)
    {
        Draw.GradientDown(x, y, width, height, PanelTop, PanelBottom);
        Draw.Frame(x, y, width, height, 1f, accent with { A = 230 });
        Draw.Frame(x + 4f, y + 4f, width - 8f, height - 8f, 1f, accent with { A = 70 });
    }

    // The crown's lowest step alone, on top of a small panel.
    static void Cap(float middle, float y, float width, Rgba accent)
    {
        Draw.Rect(middle - width / 2f, y - 5f, width, 5f, Black);
        Draw.Rect(middle - width / 2f, y - 5f, width, 1f, accent);
        Draw.Rect(middle - width / 2f, y - 5f, 1f, 5f, accent);
        Draw.Rect(middle + width / 2f - 1f, y - 5f, 1f, 5f, accent);
    }

    public override float ToastHeight => 36f;

    // Each notification opens from its middle like the menu's doors. A gem on the left is in the
    // outcome's colour, and a line along the bottom shrinks to the middle with the time left.
    public override void RenderToasts(ToastView view)
    {
        const float openShare = 0.7f;
        var accent = Look.AccentOr(Accent);
        var width = ToastWidth;
        var height = ToastHeight;
        var middle = width / 2f;

        foreach (var card in view.Cards)
        {
            PlaceToast(view, card, 0f);
            var open = Kit.EaseOut(card.Shown / openShare);
            var lit = Math.Clamp((card.Shown - openShare) / (1f - openShare), 0f, 1f);
            var half = middle * open;
            var tone = Kit.Tone(card.Kind, Live, Dim);

            Draw.Fade(1f);
            Draw.GradientDown(middle - half, 0f, 2f * half, height, PanelTop, PanelBottom);
            Draw.Rect(middle - half, 0f, 2f * half, 1f, accent);
            Draw.Rect(middle - half, height - 1f, 2f * half, 1f, accent);
            if (open < 1f)
            {
                Draw.Rect(middle - half, 0f, 2f, height, Bright);
                Draw.Rect(middle + half - 2f, 0f, 2f, height, Bright);
                continue;
            }

            Draw.Rect(0f, 1f, 1f, height - 2f, accent);
            Draw.Rect(width - 1f, 1f, 1f, height - 2f, accent);
            Draw.Frame(3f, 3f, width - 6f, height - 6f, 1f, accent with { A = 64 });

            // Two short bars step out past each end.
            Draw.Rect(-4f, 6f, 1f, height - 12f, accent);
            Draw.Rect(-7f, 12f, 1f, height - 24f, accent);
            Draw.Rect(width + 3f, 6f, 1f, height - 12f, accent);
            Draw.Rect(width + 6f, 12f, 1f, height - 24f, accent);

            if (card.Kind == OutcomeKind.Done)
            {
                Gem(18f, height / 2f, 13f, tone, lit);
            }
            else
            {
                HollowGem(18f, height / 2f, 13f, tone, lit);
            }

            Draw.Fade(lit);
            Draw.TrackedHead(card.Title, 32f, 7f, 8f, 2.5f, accent, width - 12f);
            ToastText(card.Text, 32f, 17f, 12f, card.Kind == OutcomeKind.Failed ? Kit.Failed : Ivory, width - 8f);

            Draw.Fade(1f);
            var timer = (width - 24f) * card.Left;
            Draw.Rect(middle - timer / 2f, height - 4f, timer, 1f, tone);
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
        Cap(width / 2f, 0f, MathF.Min(60f, width - 40f), accent);
        Plate(0f, 0f, width, height, accent);

        Draw.Tracked(view.Title, inset, 12f, 9f, 3f, accent);
        Draw.Rect(inset, head - 4f, width - 2f * inset, 1f, accent with { A = 90 });
        GuideLines(view, inset, head, Ghost, Pale, Dim);
        Draw.Fade(1f);
    }

    const float FanInner = 46f;
    const int FanRays = 25;

    // The speedometer is a fan of rays over the speed, like the dial of a deco radio. The rays
    // light from the left as the car gets faster.
    public override void RenderDash(DashView view)
    {
        const float width = 250f;
        const float inset = 14f;
        const float fanY = 86f;
        var accent = Look.AccentOr(Accent);
        var height = view.Stats ? 164f : 138f;
        var right = width - inset;
        var middle = width / 2f;

        if (!PlaceDash(view, width, height))
        {
            return;
        }

        Draw.Fade(view.Shown);
        Cap(middle, 0f, 80f, accent);
        Plate(0f, 0f, width, height, accent);

        Draw.Tracked(view.Title, inset, 12f, 9f, 3f, accent);
        if (view.Mode.Length > 0)
        {
            var hot = view.Hot ? Bright : accent;
            Draw.Tracked(view.Mode, right - Draw.TrackedWidth(view.Mode, 9f, 3f), 12f, 9f, 3f, hot);
        }

        // Rays are solid, so they wait until the panel under them is there.
        if (view.Shown > 0.5f)
        {
            var lit = (int)MathF.Round(view.SpeedFill * FanRays);
            for (var i = 0; i < FanRays; i++)
            {
                var angle = -78f + 156f * i / (FanRays - 1f);
                var on = i < lit;
                var length = (i % 2 == 0 ? 22f : 15f) + (on && view.Hot ? 3f * MathF.Sin(view.Time * 22f + i) : 0f);
                var (ax, ay) = Kit.Around(angle - 2f, FanInner);
                var (bx, by) = Kit.Around(angle + 2f, FanInner);
                var (tx, ty) = Kit.Around(angle, FanInner + length);
                Draw.Triangle(middle + ax, fanY + ay, middle + bx, fanY + by, middle + tx, fanY + ty, on ? (view.Hot ? Bright : accent) : Unlit);
            }
        }

        Draw.Text(view.Speed, middle - Draw.TextWidth(view.Speed, 32f) / 2f, fanY - 34f, 32f, Ivory);
        Draw.Tracked(view.Units, middle - Draw.TrackedWidth(view.Units, 9f, 3f) / 2f, fanY - 1f, 9f, 3f, Dim);
        DashNote(view, right, 30f, Pale, Ghost);

        var rule = fanY + 14f;
        Draw.Rect(inset, rule, middle - inset - 8f, 1f, accent with { A = 76 });
        Draw.Rect(middle + 8f, rule, middle - inset - 8f, 1f, accent with { A = 76 });
        Gem(middle, rule + 0.5f, 5f, accent, view.Shown);

        var armour = view.LowArmour ? Kit.Failed : accent;
        DashBar(inset, right, fanY + 22f, "ARMOUR", view.Armour, view.ArmourText, Ghost, armour, accent with { A = 40 }, view.LowArmour ? Kit.Failed : Pale);
        DashBar(inset, right, fanY + 36f, "POWER", view.Power, view.PowerText, Ghost, Live, accent with { A = 40 }, Pale);

        if (view.Stats)
        {
            Draw.Rect(inset, 138f, width - 2f * inset, 1f, accent with { A = 76 });
            DashStat(inset, 146f, "BEST", view.Best, view.Units, Ghost, Pale);
            DashStat(middle + 6f, 146f, "JUMP", view.Jump, "", Ghost, Pale);
        }

        Draw.Fade(1f);
    }

    // The radial is a full sunburst round the bat. Each favourite is a small plaque on an oval
    // round it. The rays nearest the one pointed at reach out and turn bright, and that plaque
    // fills with gold.
    const float SunHub = 50f;
    const float SunReach = 150f;
    const float SunMedal = 40f;
    const float SunRayInner = 44f;
    const int SunRays = 32;
    const float PlaqueHeight = 30f;
    const float PlaqueNameLeft = 26f;
    const float PlaquePad = 12f;
    const float PlaqueLift = 10f;
    const float PlaqueFlash = 0.5f;
    const float ReadoutWidth = 440f;
    const int ReadoutLines = 2;

    const string TapHelp = "Point at a plaque to see what it does.";
    const string HoldHelp = "Letting go in the middle does nothing. Point at a plaque to pick it.";

    static readonly Rgba Shade = new(0, 0, 0, 96);

    public override float RadialHub => SunHub;

    public override float RadialReach => SunReach;

    public override float RadialAngle(int slot, int count)
    {
        var (x, y) = SunSpot(slot, count);
        return MathF.Atan2(x, -y) * 180f / MathF.PI;
    }

    // More plaques get a wider oval, so neighbours near the top and bottom don't touch.
    static (float X, float Y) SunSpot(int slot, int count)
    {
        var (width, height) = count > 8 ? (200f, 168f) : (160f, 130f);
        var (sin, cos) = MathF.SinCos(slot * 2f * MathF.PI / count);
        return (sin * width, -cos * height);
    }

    public override void RenderRadial(RadialView view)
    {
        var accent = Look.AccentOr(Accent);
        var count = view.Slots.Count;
        var grow = Kit.EaseOut(view.Age / 0.3f);
        var arrived = MathF.Min(view.Age, view.PageAge);

        Draw.Place(0f, 0f, 1f);
        Draw.Fade(grow);
        Draw.Rect(0f, 0f, Draw.ScreenWidth, Draw.ScreenHeight, Shade);

        PlaceRadial(view);
        Draw.Fade(1f);
        var reach = view.Hover >= 0 ? RadialAngle(view.Hover, count) : 0f;
        var amount = view.Hover >= 0 ? view.Lifts[view.Hover] : 0f;
        Burst(0f, 0f, SunRayInner, 0f, 360f - 360f / SunRays, SunRays, grow, view.Time, accent, reach, amount);
        Medal(0f, 0f, SunMedal, grow, view.Time, accent, 60f);

        // A dotted ring round the medal while letting go would cancel.
        if (view.Hover < 0 && grow >= 1f)
        {
            Shapes.Under = Black;
            Shapes.Opacity = 1f;
            Shapes.Dashes(SunMedal + 7f, SunMedal + 9f, view.Time * 20f % 12f, 360f, 6f, 6f, Bright);
        }

        var highest = -SunMedal;
        var lowest = SunMedal;
        for (var i = 0; i < count; i++)
        {
            var (top, bottom) = Plaque(view, i, count, accent, arrived);
            highest = MathF.Min(highest, top);
            lowest = MathF.Max(lowest, bottom);
        }

        if (grow < 0.5f)
        {
            return;
        }

        Draw.Fade(Math.Clamp(grow * 2f - 1f, 0f, 1f));
        SunTitle(view, accent, highest - 44f);
        var end = SunReadout(view, accent, lowest + 30f);
        Kit.HintRow(view.Hints, 0f, end + 24f, 10f, Ghost, Pale, Dim);
        Draw.Fade(1f);
    }

    // What a favourite is set to, in the colour the menu gives it.
    static (string Text, Rgba Tone) SunState(RadialView view, Feature feature, Rgba accent) => feature switch
    {
        Toggle toggle => (toggle.ValueText, toggle.On ? Live : Dim),
        Choice choice => (choice.ValueText, choice.IsDefault ? Dim : Live),
        Command command when command.Showing(view.Now, out var outcome) => (outcome.Text, Kit.Tone(outcome.Kind, Live, Pale)),
        Command command => (command.Cap, accent),
        _ => (feature.ValueText, Dim),
    };

    // One favourite. It opens from its own middle a moment after the one before. Returns where
    // its top and bottom are.
    static (float Top, float Bottom) Plaque(RadialView view, int i, int count, Rgba accent, float arrived)
    {
        var grow = Kit.EaseOut((arrived - 0.1f - i * 0.03f) / 0.24f);
        if (grow <= 0f)
        {
            return (0f, 0f);
        }

        var feature = view.Slots[i];
        var lift = view.Lifts[i];
        var lit = RadialView.Lit(feature);
        var (state, tone) = SunState(view, feature, accent);
        var stateWidth = Draw.TrackedWidth(state, 10f, 1f);
        var width = PlaqueNameLeft + Draw.TextWidth(feature.Plain, 14f) + 12f + stateWidth + PlaquePad;

        var (spotX, spotY) = SunSpot(i, count);
        var far = MathF.Sqrt(spotX * spotX + spotY * spotY);
        var spread = (0.6f + 0.4f * grow) + lift * PlaqueLift / far;
        var x = spotX * spread;
        var y = spotY * spread;
        var left = spotX > 1f ? x : spotX < -1f ? x - width : x - width / 2f;
        var top = y - PlaqueHeight / 2f;

        var middle = left + width / 2f;
        var half = width / 2f * grow;
        var fill = Rgba.Mix(Black, accent, lift);
        Draw.Fade(1f);
        Draw.Rect(middle - half, top, 2f * half, PlaqueHeight, fill with { A = 244 });
        Draw.Rect(middle - half, top, 2f * half, 1f, accent);
        Draw.Rect(middle - half, top + PlaqueHeight - 1f, 2f * half, 1f, accent);
        if (grow < 1f)
        {
            Draw.Rect(middle - half, top, 2f, PlaqueHeight, Bright);
            Draw.Rect(middle + half - 2f, top, 2f, PlaqueHeight, Bright);
            return (top, top + PlaqueHeight);
        }

        Draw.Rect(left, top + 1f, 1f, PlaqueHeight - 2f, accent);
        Draw.Rect(left + width - 1f, top + 1f, 1f, PlaqueHeight - 2f, accent);
        Draw.Frame(left + 3f, top + 3f, width - 6f, PlaqueHeight - 6f, 1f, accent with { A = (byte)(64 * (1f - lift)) });
        Draw.Rect(left - 4f, top + 5f, 1f, PlaqueHeight - 10f, accent);
        Draw.Rect(left - 7f, top + 10f, 1f, PlaqueHeight - 20f, accent);
        Draw.Rect(left + width + 3f, top + 5f, 1f, PlaqueHeight - 10f, accent);
        Draw.Rect(left + width + 6f, top + 10f, 1f, PlaqueHeight - 20f, accent);

        if (view.Flashed == i && view.FlashAge < PlaqueFlash)
        {
            var left2 = 1f - view.FlashAge / PlaqueFlash;
            Draw.Rect(left, top, width, PlaqueHeight, new Rgba(255, 255, 255, (byte)(140 * left2 * left2)));
        }

        // On gold the gem and the words turn dark.
        var gemY = top + PlaqueHeight / 2f;
        if (lit)
        {
            Gem(left + 14f, gemY, 10f, Rgba.Mix(Live, Black, lift));
        }
        else
        {
            Gem(left + 14f, gemY, 10f, Rgba.Mix(Ghost, Black, lift));
            Gem(left + 14f, gemY, 6.5f, fill);
        }

        Draw.Text(feature.Plain, left + PlaqueNameLeft, top + 8f, 14f, Rgba.Mix(Ivory, Black, lift));
        var ink = Rgba.Mix(Rgba.Mix(tone, new Rgba(255, 255, 255), feature.Glow(view.Now)), Black, lift);
        Draw.Tracked(state, left + width - PlaquePad - stateWidth, top + 10f, 10f, 1f, ink);
        return (top, top + PlaqueHeight);
    }

    static void SunTitle(RadialView view, Rgba accent, float top)
    {
        const string title = "FAVOURITES";
        const float size = 12f;
        const float spacing = 5f;
        var width = Draw.TrackedWidth(title, size, spacing);
        Draw.Tracked(title, -width / 2f, top, size, spacing, Ivory);
        for (var i = 0; i < 3; i++)
        {
            var length = 60f - i * 16f;
            var line = accent with { A = (byte)(255 - i * 50) };
            Draw.Rect(-width / 2f - 14f - length, top + 3f + i * 4f, length, 1f, line);
            Draw.Rect(width / 2f + 14f, top + 3f + i * 4f, length, 1f, line);
        }

        if (view.Pages < 2)
        {
            return;
        }

        // SET 01 / 02, drawn in pieces so no new text is made each frame.
        var page = Kit.Number(view.Page + 1);
        var pages = Kit.Number(view.Pages);
        var line2 = Draw.TrackedWidth("SET", 9f, 2f) + 8f + Draw.TextWidth(page, 10f) + 6f + Draw.TextWidth("/", 10f) + 6f + Draw.TextWidth(pages, 10f);
        var x = -line2 / 2f;
        var y = top + 20f;
        x = Draw.Tracked("SET", x, y + 1f, 9f, 2f, Dim) + 8f;
        Draw.Text(page, x, y, 10f, accent);
        x += Draw.TextWidth(page, 10f) + 6f;
        Draw.Text("/", x, y, 10f, Ghost);
        x += Draw.TextWidth("/", 10f) + 6f;
        Draw.Text(pages, x, y, 10f, Dim);
    }

    // Under the plaques, a panel reads out the favourite pointed at: what kind it is, its state,
    // its hotkey and its help. Returns where the panel ends.
    static float SunReadout(RadialView view, Rgba accent, float top)
    {
        const float inset = 18f;
        const float helpTop = 38f;
        const float helpLine = 17f;
        var left = -ReadoutWidth / 2f;
        var right = -left;
        var helpWidth = ReadoutWidth - 2f * inset;

        // Room for the longest help on this page, so the panel doesn't jump about as the pointer
        // moves between plaques.
        var room = Draw.Wrap(view.Tap ? TapHelp : HoldHelp, 13f, helpWidth).Length;
        foreach (var slot in view.Slots)
        {
            room = Math.Max(room, Draw.Wrap(slot.Help, 13f, helpWidth).Length);
        }

        room = Math.Clamp(room, 1, ReadoutLines);
        var height = helpTop + (room - 1) * helpLine + 13f + 14f;
        Cap(0f, top, 120f, accent);
        Plate(left, top, ReadoutWidth, height, accent);

        var feature = view.Focus;
        if (feature is null)
        {
            Draw.Tracked(view.Tap ? "POINT AT A FAVOURITE" : "LET GO TO CANCEL", left + inset, top + 14f, 11f, 2.5f, Pale);
            Draw.Text(view.Tap ? TapHelp : HoldHelp, left + inset, top + helpTop, 13f, Dim);
            return top + height;
        }

        var kind = RadialView.Kind(feature);
        var kindWidth = Draw.TrackedWidth(kind, 8f, 1.5f) + 12f;
        Draw.Frame(left + inset, top + 13f, kindWidth, 15f, 1f, accent with { A = 170 });
        Draw.Tracked(kind, left + inset + 6f, top + 16.5f, 8f, 1.5f, accent);

        var (state, tone) = SunState(view, feature, accent);
        var stateLeft = right - inset - Draw.TrackedWidth(state, 10f, 1.6f);
        Draw.Tracked(state, stateLeft, top + 15f, 10f, 1.6f, Rgba.Mix(tone, new Rgba(255, 255, 255), feature.Glow(view.Now)));

        var nameRight = stateLeft - 14f;
        var key = Hotkeys.KeyText(feature);
        if (key.Length > 0)
        {
            var capWidth = Kit.CapWidth(key, 9f);
            Kit.Cap(key, stateLeft - 12f - capWidth, top + 20f, 9f, Ghost, Pale);
            nameRight -= capWidth + 12f;
        }

        ToastText(feature.Plain, left + inset + kindWidth + 10f, top + 11f, 15f, Ivory, nameRight);

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
        AskPad + AskTag + 12f + AskTitle + 10f + lines.Length * AskLine + 14f + AskButton + AskPad;

    // A second set of doors. A gold line drops down the middle, then the panel opens out from it.
    public override void RenderModal(MenuView view, ModalView modal)
    {
        var accent = Look.AccentOr(Accent);
        ShadeScreen(new Rgba(0, 0, 0, 120), modal.Opened);

        var lines = Draw.Wrap(modal.Ask.Text, AskBody, AskWidth - 2f * AskPad);
        var height = AskHeight(lines);
        PlaceModal(AskWidth, height + 34f);

        var drop = Kit.EaseOut(modal.Opened / LineShare);
        var open = Kit.EaseOut((modal.Opened - LineShare) / (1f - LineShare));
        var middle = AskWidth / 2f;
        var half = middle * open;

        Draw.Fade(1f);
        if (open <= 0f)
        {
            Draw.Rect(middle - 0.5f, 0f, 1f, height * drop, accent);
            return;
        }

        Draw.GradientDown(middle - half, 0f, 2f * half, height, PanelTop, PanelBottom);
        Draw.Rect(middle - half, 0f, 2f * half, 1f, accent);
        Draw.Rect(middle - half, height - 1f, 2f * half, 1f, accent);
        if (open < 1f)
        {
            Draw.Rect(middle - half, 0f, 2f, height, Bright);
            Draw.Rect(middle + half - 2f, 0f, 2f, height, Bright);
            return;
        }

        Cap(middle, 0f, 120f, accent);
        Draw.Rect(0f, 1f, 1f, height - 2f, accent);
        Draw.Rect(AskWidth - 1f, 1f, 1f, height - 2f, accent);
        Draw.Frame(4f, 4f, AskWidth - 8f, height - 8f, 1f, accent with { A = 70 });

        var y = AskPad;
        Gem(AskPad + 5f, y + AskTag / 2f, 10f, Live);
        var end = Draw.Tracked(modal.Ask.Tag, AskPad + 18f, y, AskTag, 2.5f, Live);
        Draw.Rect(end + 8f, y + AskTag / 2f, AskWidth - AskPad - end - 8f, 1f, accent with { A = 90 });

        y += AskTag + 12f;
        Draw.Tracked(modal.Ask.Title, AskPad, y, AskTitle, 2f, Ivory);
        y += AskTitle + 10f;
        foreach (var text in lines)
        {
            Draw.Text(text, AskPad, y, AskBody, Dim);
            y += AskLine;
        }

        y += 14f;
        var width = (AskWidth - 2f * AskPad - AskGap) / 2f;
        AskButtonAt(modal.Ask.Yes, AskPad, y, width, modal.Choice == 0, accent);
        AskButtonAt(modal.Ask.No, AskPad + width + AskGap, y, width, modal.Choice == 1, accent);

        Kit.HintRow(modal.Hints, middle, height + 20f, KeyHints.Size, KeyHints.Frame, KeyHints.Key, KeyHints.Label);
        Draw.Fade(1f);
    }

    // The picked button stands between fins, like the selected row.
    static void AskButtonAt(string label, float x, float y, float width, bool picked, Rgba accent)
    {
        if (picked)
        {
            Draw.Rect(x, y, width, AskButton, accent with { A = 40 });
            Draw.Frame(x, y, width, AskButton, 1f, accent);
            Fins(x - 2f, y, AskButton, -1f, accent);
            Fins(x + width, y, AskButton, 1f, accent);
        }
        else
        {
            Draw.Frame(x, y, width, AskButton, 1f, Ghost);
        }

        ButtonLabel(label, x, y, width, AskButton, 10f, 2f, picked ? Ivory : Dim);
    }
}
