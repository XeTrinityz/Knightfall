#nullable enable

namespace Knightfall;

// A solid panel in the middle of the screen. Pages down the left, the list in the middle, and a
// readout on the right for the selected option. The game behind is dimmed.
sealed class ConsoleTheme : Theme
{
    const float Width = 920f;
    const float Height = 500f;
    const float Cut = 22f;
    const float HeaderHeight = 50f;
    const float FooterHeight = 38f;
    const float BodyTop = HeaderHeight + 2f;
    const float BodyBottom = Height - FooterHeight;
    const float ListLeft = 160f;
    const float DetailLeft = 610f;
    const float RowHeight = 30f;
    const float RowsTop = BodyTop + 54f;
    const float DetailInset = 24f;

    static readonly Rgba Shade = new(4, 6, 9, 130);
    static readonly Rgba Panel = new(12, 17, 24);
    static readonly Rgba Bar = new(18, 26, 36);
    static readonly Rgba PagesTop = new(12, 17, 24);
    static readonly Rgba PagesBottom = new(7, 10, 15);
    static readonly Rgba DetailTop = new(18, 26, 36);
    static readonly Rgba DetailBottom = new(11, 16, 23);
    static readonly Rgba Empty = new(36, 48, 61);
    static readonly Rgba Edge = new(52, 64, 78);
    static readonly Rgba White = new(255, 255, 255);
    static readonly Rgba Soft = new(198, 206, 214);
    static readonly Rgba Dim = new(150, 160, 172);
    static readonly Rgba Ghost = new(104, 114, 128);
    static readonly Rgba Ink = new(7, 16, 25);

    // State has its own colour, so what is switched on stands apart from where you are.
    static readonly Rgba Live = new(255, 170, 84);

    static readonly HintStyle KeyHints = new(Size: 10f, CapHeight: 17f, LineHeight: 22f, Frame: Ghost, Key: Soft, Label: Dim);

    public override string Id => "console";

    public override string Name => "CONSOLE";

    public override Rgba Accent => new(127, 214, 255);

    public override Rgba Armed => Live;

    public override void Render(MenuView view)
    {
        var accent = Look.AccentOr(Accent);
        var barLeft = Rgba.Mix(Bar, accent, 0.14f);

        // The selected row is a tint of the accent. It is mixed, not see-through, so it matches
        // on every part of the panel.
        var tint = Rgba.Mix(Panel, accent, 0.18f);

        Draw.Fade(view.Opened);
        Draw.Rect(0f, 0f, Draw.ScreenWidth, Draw.ScreenHeight, Shade);

        // Laid out from 0,0, then centred. It shrinks if the screen is too small for it, and it
        // grows a touch as it opens and shrinks a touch as it closes.
        var fit = MathF.Min((Draw.ScreenWidth - 80f) / Width, (Draw.ScreenHeight - 80f) / Height);
        var zoom = MathF.Min(Look.Zoom, fit) * (0.96f + 0.04f * view.Opened);
        var originX = (Draw.ScreenWidth - Width * zoom) / 2f + MoveX;
        var originY = (Draw.ScreenHeight - Height * zoom) / 2f + MoveY;
        Draw.Place(originX, originY, zoom);
        Covers(0f, 0f, Width, Height, Height);

        Kit.Shadow(0f, 0f, Width, Height, Cut, Cut);
        Frame(view, accent, barLeft);
        Header(view, accent);
        Pages(view, accent, tint);
        Tabs(view, accent);
        Rows(view, accent, tint, originX, originY, zoom);
        Draw.Place(originX, originY, zoom);
        Draw.Fade(view.Opened);
        Detail(view, accent);
        Footer(view);
    }

    // The panel with its top left and bottom right corners cut off. Each cut is a square left
    // out of the bar and filled half back in.
    static void Frame(MenuView view, Rgba accent, Rgba barLeft)
    {
        Draw.Rect(0f, Cut, Cut, HeaderHeight - Cut, barLeft);
        Draw.GradientAcross(Cut, 0f, Width - Cut, HeaderHeight, barLeft, Bar);
        TopLeftCorner(barLeft, view.Opened);
        Draw.Rect(0f, HeaderHeight, Width, 2f, accent);
        Kit.Pulse(0f, HeaderHeight, Width, view.Time, new Rgba(255, 255, 255, 200));

        Draw.GradientDown(0f, BodyTop, ListLeft, BodyBottom - BodyTop, PagesTop, PagesBottom);
        Draw.Rect(ListLeft, BodyTop, DetailLeft - ListLeft, BodyBottom - BodyTop, Panel);
        Draw.GradientDown(DetailLeft, BodyTop, Width - DetailLeft, BodyBottom - BodyTop, DetailTop, DetailBottom);
        Draw.Rect(ListLeft, BodyTop, 1f, BodyBottom - BodyTop, Edge with { A = 120 });
        Draw.Rect(DetailLeft, BodyTop, 1f, BodyBottom - BodyTop, Edge with { A = 120 });

        Draw.Rect(0f, BodyBottom, Width - Cut, FooterHeight, Bar);
        Draw.Rect(Width - Cut, BodyBottom, Cut, FooterHeight - Cut, Bar);
        Draw.Rect(0f, BodyBottom, Width, 1f, Edge);
        BottomRightCorner(Bar, view.Opened);
    }

    const int CornerStrips = 11;

    // The top left cut. The panel side of the 0,0 to Cut,Cut square is filled. Triangles can't
    // fade, so while the menu fades in or out the fill is built from thin strips instead.
    static void TopLeftCorner(Rgba color, float opened)
    {
        if (opened >= 1f)
        {
            Draw.Triangle(Cut, 0f, 0f, Cut, Cut, Cut, color);
            return;
        }

        var step = Cut / CornerStrips;
        for (var i = 0; i < CornerStrips; i++)
        {
            var x = i * step;
            var top = Cut - (x + step / 2f);
            Draw.Rect(x, top, step, Cut - top, color);
        }
    }

    // The bottom right cut, filled on the panel side in the same way.
    static void BottomRightCorner(Rgba color, float opened, float right = Width, float bottom = Height)
    {
        var x0 = right - Cut;
        var y0 = bottom - Cut;

        if (opened >= 1f)
        {
            Draw.Triangle(x0, y0, right, y0, x0, bottom, color);
            return;
        }

        var step = Cut / CornerStrips;
        for (var i = 0; i < CornerStrips; i++)
        {
            var x = i * step;
            var fill = Cut - (x + step / 2f);
            Draw.Rect(x0 + x, y0, step, fill, color);
        }
    }

    static void Header(MenuView view, Rgba accent)
    {
        Kit.Signal(30f, 19f, view.Time, accent);
        Draw.Tracked("KNIGHTFALL", 52f, 19f, 15f, 4f, accent);

        // Where you are, top right. Page, then tab, then the time.
        var x = Draw.TextRight(Kit.Clock, Width - 24f, 21f, 11f, Ghost);
        Draw.Rect(x - 14f, 22f, 1f, 9f, Edge);

        var tab = view.Tab.Name;
        var page = view.Page.Name;
        x = x - 28f - Draw.TrackedWidth(tab, 11f, 2f);
        Draw.Tracked(tab, x, 21f, 11f, 2f, White);
        x = Draw.TextRight("/", x - 10f, 21f, 11f, Ghost);
        Draw.Tracked(page, x - 10f - Draw.TrackedWidth(page, 11f, 2f), 21f, 11f, 2f, Dim);
    }

    static void Pages(MenuView view, Rgba accent, Rgba tint)
    {
        const float height = 32f;

        // A caption with the keys that turn the pages.
        Draw.Tracked("PAGES", 18f, BodyTop + 14f, 9f, 2f, Ghost);
        var capRight = ListLeft - 14f;
        var next = Kit.CapWidth(view.PageNextKey, 9f);
        Kit.Cap(view.PageNextKey, capRight - next, BodyTop + 19f, 9f, Ghost, Dim);
        Kit.Cap(view.PagePrevKey, capRight - next - 4f - Kit.CapWidth(view.PagePrevKey, 9f), BodyTop + 19f, 9f, Ghost, Dim);

        var y = BodyTop + 36f;
        for (var i = 0; i < view.Pages.Count; i++)
        {
            var current = i == view.PageIndex;
            if (current)
            {
                Draw.GradientAcross(0f, y, ListLeft, height, tint, PagesTop);
                Draw.Rect(0f, y, 3f, height, accent);
            }

            Draw.Text(Kit.Number(i + 1), 18f, y + 11f, 10f, current ? accent : Ghost);
            Draw.Text(view.Pages[i].Name, 44f, y + 8f, 15f, current ? White : Dim);
            y += height + 2f;
        }
    }

    static void Tabs(MenuView view, Rgba accent)
    {
        const float size = 11f;
        const float spacing = 1.5f;
        const float height = 24f;
        const float pad = 12f;
        const float gap = 6f;
        const float capGap = 8f;
        const float more = Kit.MoreWidth + 8f;
        var y = BodyTop + 16f;
        var middle = y + height / 2f;
        var tabs = view.Page.Tabs;

        // The strip stops short of the readout on the right.
        var left = ListLeft + 18f;
        left += Kit.Cap(view.TabPrevKey, left, middle, 9f, Ghost, Dim) + capGap;
        var right = DetailLeft - 18f - Kit.CapWidth(view.TabNextKey, 9f) - capGap;
        var strip = FitTabs(view, left, right, size, spacing, 2f * pad, gap, more);
        var before = strip.First;
        var after = tabs.Count - strip.Last;

        var x = strip.Start;
        for (var i = strip.First; i < strip.Last; i++)
        {
            var width = Draw.TrackedWidth(tabs[i].Name, size, spacing) + 2f * pad;
            if (i == view.TabIndex)
            {
                Draw.Rect(x, y, width, height, accent);
                Draw.Tracked(tabs[i].Name, x + pad, y + 7f, size, spacing, Ink);
            }
            else
            {
                Draw.Frame(x, y, width, height, 1f, Edge);
                Draw.Tracked(tabs[i].Name, x + pad, y + 7f, size, spacing, Dim);
            }

            x += width + gap;
        }

        // When they all fit, the next key follows the last tab.
        if (before == 0 && after == 0)
        {
            Kit.Cap(view.TabNextKey, x - gap + capGap, middle, 9f, Ghost, Dim);
            return;
        }

        // Otherwise the strip runs the full width. The room left over shows the cut off box of
        // the next tab that way, or a small count when not even that fits.
        if (before > 0 && !(after == 0 && CutTab(tabs[strip.First - 1].Name, left, strip.Start - gap, y, openLeft: true)))
        {
            Kit.More(left, middle, before, left: true, accent, Dim);
        }

        if (after > 0 && !CutTab(tabs[strip.Last].Name, x, right, y, openLeft: false))
        {
            Kit.More(right - Kit.MoreWidth, middle, after, left: false, accent, Dim);
        }

        Kit.Cap(view.TabNextKey, right + capGap, middle, 9f, Ghost, Dim);
    }

    // A tab that runs off one end of the strip. Its box is left open on that side and fades out
    // towards it, with as much of the name as fits. Returns false when no name fits.
    static bool CutTab(string name, float from, float to, float y, bool openLeft)
    {
        const float size = 11f;
        const float spacing = 1.5f;
        const float height = 24f;
        const float pad = 12f;

        var gone = Edge with { A = 0 };
        var width = to - from;
        if (openLeft)
        {
            if (!Draw.TrackedTail(name, to - pad, y + 7f, size, spacing, Dim, from + 2f))
            {
                return false;
            }

            Draw.GradientAcross(from, y, width, 1f, gone, Edge);
            Draw.GradientAcross(from, y + height - 1f, width, 1f, gone, Edge);
            Draw.Rect(to - 1f, y, 1f, height, Edge);
            return true;
        }

        if (!Draw.TrackedHead(name, from + pad, y + 7f, size, spacing, Dim, to - 2f))
        {
            return false;
        }

        Draw.GradientAcross(from, y, width, 1f, Edge, gone);
        Draw.GradientAcross(from, y + height - 1f, width, 1f, Edge, gone);
        Draw.Rect(from, y, 1f, height, Edge);
        return true;
    }

    static void Rows(MenuView view, Rgba accent, Rgba tint, float originX, float originY, float zoom)
    {
        var lines = view.Lines;
        if (view.Rows.Count == 0)
        {
            return;
        }

        var visible = Math.Max(1, (int)((BodyBottom - 10f - RowsTop) / RowHeight));
        var (first, last, glide) = Scroll(view, visible);

        // The highlight glides between rows. It is drawn first so the text sits on top.
        var highlight = RowsTop + glide * RowHeight;
        Draw.GradientAcross(ListLeft + 1f, highlight, DetailLeft - ListLeft - 1f, RowHeight, tint, Panel);
        Draw.Rect(ListLeft + 1f, highlight, 3f, RowHeight, accent);
        Kit.Sheen(ListLeft + 1f, highlight, DetailLeft - ListLeft - 1f, RowHeight, view.Time, new Rgba(255, 255, 255, 14));

        if (lines.Count > visible)
        {
            var track = visible * RowHeight;
            Draw.Rect(DetailLeft - 4f, RowsTop, 2f, track, Empty);
            Draw.Rect(DetailLeft - 4f, RowsTop + track * first / lines.Count, 2f, track * visible / lines.Count, accent);
        }

        // Each line comes in a moment after the one above it.
        for (var i = first; i < last; i++)
        {
            var arrive = view.Closing ? 1f : Kit.Cascade(view.Arrived, i - first);
            Draw.Place(originX + (1f - arrive) * 12f * zoom, originY, zoom);
            Draw.Fade(view.Opened * arrive);

            var y = RowsTop + (i - first) * RowHeight;
            var line = lines[i];
            if (line.Row is { } row)
            {
                Row(row, line.RowIndex, y, line.RowIndex == view.RowIndex, accent, view.Now);
            }
            else
            {
                Heading(line.Heading!, y, accent);
            }
        }
    }

    // The name in the accent colour, spaced out, with a rule across the list. It sits a little
    // low, so it reads as part of the rows under it.
    static void Heading(string text, float y, Rgba accent)
    {
        var middle = y + RowHeight / 2f + 3f;
        var end = Draw.Tracked(text, ListLeft + 18f, middle - 5f, 10f, 2.5f, accent);
        Draw.Rect(end + 10f, middle, DetailLeft - 20f - end - 10f, 1f, Edge);
    }

    // The list keeps each row short. The readout on the right has the detail.
    static void Row(Feature feature, int index, float y, bool selected, Rgba accent, long now)
    {
        var right = DetailLeft - 20f;

        // The row number lights up when the option is switched away from normal.
        var changed = !feature.Preference && !feature.IsDefault;
        Draw.Text(Kit.Number(index + 1), ListLeft + 18f, y + 10f, 10f, changed ? Live : selected ? accent : Ghost);

        // A row that can't be used right now steps back, with the reason where its value goes.
        if (feature.Blocked is { } why)
        {
            Draw.Text(feature.Label, ListLeft + 44f, y + 7f, 15f, selected ? Dim : Ghost);
            Kit.Locked(why, right, y + 10f, 10f, Ghost, selected ? Soft : Dim, ListLeft + 44f + Draw.TextWidth(feature.Label, 15f) + 16f);
            return;
        }

        Draw.Text(feature.Label, ListLeft + 44f, y + 7f, 15f, selected ? White : Soft);

        // A value flashes white for a moment when it changes.
        var value = Rgba.Mix(changed ? Live : accent, White, feature.Glow(now));

        switch (feature)
        {
            case Toggle toggle:
                Kit.Switch(right - Kit.SwitchWidth, y + (RowHeight - Kit.SwitchHeight) / 2f, toggle.On, value, Ghost);
                break;

            case Slider or Choice:
                if (selected)
                {
                    var x = Draw.TextRight(">", right, y + 7f, 15f, Dim);
                    x = Draw.TextRightFit(feature.ValueText, x - 8f, y + 7f, 15f, value, ListLeft + 44f + Draw.TextWidth(feature.Label, 15f) + 16f + Draw.TextWidth("<", 15f) + 8f);
                    Draw.TextRight("<", x - 8f, y + 7f, 15f, Dim);
                }
                else
                {
                    Draw.TextRightFit(feature.ValueText, right, y + 7f, 15f, value, ListLeft + 44f + Draw.TextWidth(feature.Label, 15f) + 16f);
                }

                break;

            case Command command:
            {
                // After a press the cap shows what happened, filled with its own colour.
                var shown = command.Showing(now, out var outcome);
                var text = shown ? outcome.Text : command.Cap;
                var tone = shown ? Kit.Tone(outcome.Kind, Live, Soft) : accent;
                var width = Draw.TextWidth(text, 10f) + 14f;
                if (selected || shown)
                {
                    Draw.Rect(right - width, y + 7f, width, 16f, tone);
                }

                Draw.Frame(right - width, y + 7f, width, 16f, 1f, selected || shown ? tone : Ghost);
                Draw.Text(text, right - width + 7f, y + 10f, 10f, selected || shown ? Ink : Dim);
                break;
            }
        }
    }

    // The readout. The selected option's value in a bracketed box, whether it is still at its
    // default, a meter for sliders, and the help text.
    static void Detail(MenuView view, Rgba accent)
    {
        var feature = view.Focus;
        if (feature is null)
        {
            return;
        }

        var left = DetailLeft + DetailInset;
        var width = Width - DetailLeft - 2f * DetailInset;
        var y = BodyTop + 20f;
        var changed = !feature.Preference && !feature.IsDefault;
        var value = Rgba.Mix(changed ? Live : accent, White, feature.Glow(view.Now));

        Draw.Tracked(feature.Label, left, y, 11f, 2f, Dim);
        y += 24f;

        if (feature.ValueText.Length > 0)
        {
            const float box = 62f;
            Draw.GradientAcross(left, y, width, box, (changed ? Live : accent) with { A = 26 }, accent with { A = 0 });
            Kit.Corners(left, y, width, box, 8f, changed ? Live : accent);
            // A row that can't be used right now keeps its value on show, stepped back.
            var locked = feature.Blocked is not null;
            Draw.Text(feature.ValueText, left + 14f, y + 16f, 30f, locked ? Ghost : value);
            Tag(locked ? "LOCKED" : feature.IsDefault ? "DEFAULT" : "CHANGED", left + width - 10f, y + 23f, locked || feature.IsDefault ? Ghost : Live);
            y += box + 14f;
        }

        switch (feature)
        {
            case Slider slider:
                Kit.Meter(left, y, width, 10f, 30, slider.Fill, slider.DefaultFill, value, Empty, Soft);
                Draw.Text(slider.MinText, left, y + 20f, 10f, Ghost);
                Draw.TextRight(slider.MaxText, left + width, y + 20f, 10f, Ghost);
                y += 42f;
                break;

            case Choice choice:
                var current = Kit.Number(choice.Index + 1);
                Draw.Text(current, left, y, 11f, accent);
                var x = left + Draw.TextWidth(current, 11f) + 5f;
                Draw.Text("/", x, y, 11f, Ghost);
                Draw.Text(Kit.Number(choice.Count), x + Draw.TextWidth("/", 11f) + 5f, y, 11f, Ghost);
                Kit.Dashes(left + width, y + 5f, choice.Count, choice.Index, value, Empty);
                y += 28f;
                break;
        }

        Draw.Rect(left, y, width, 1f, Edge);
        y += 14f;

        var lines = Draw.Wrap(feature.Help, 13f, width);
        for (var i = 0; i < lines.Length && y + 18f < BodyBottom; i++)
        {
            Draw.Text(lines[i], left, y, 13f, Soft);
            y += 18f;
        }
    }

    static void Footer(MenuView view)
    {
        var middle = BodyBottom + FooterHeight / 2f;
        DrawHints(view, 24f, DetailLeft, middle - KeyHints.CapHeight / 2f, KeyHints);

        var active = Catalog.Active;
        var x = DetailLeft + DetailInset;
        if (active > 0)
        {
            Draw.Rect(x, middle - 3f, 6f, 6f, Live);
        }
        else
        {
            Draw.Frame(x, middle - 3f, 6f, 6f, 1f, Ghost);
        }

        x = Draw.Tracked("ACTIVE", x + 12f, middle - 5f, 10f, 2f, Ghost);
        Draw.Text(Kit.Number(active), x + 8f, middle - 5f, 10f, active > 0 ? Live : Ghost);
        Draw.TextRight(Kit.Version, Width - Cut - 16f, middle - 5f, 10f, Ghost);
    }

    // A small outlined word, placed against a right edge.
    static void Tag(string text, float right, float y, Rgba color)
    {
        var width = Draw.TrackedWidth(text, 9f, 1.5f) + 12f;
        Draw.Frame(right - width, y, width, 16f, 1f, color);
        Draw.Tracked(text, right - width + 6f, y + 4f, 9f, 1.5f, color);
    }

    // The speedometer is a small solid panel with the menu's header bar.
    // The key guide as a small window with a title bar.
    public override float ToastWidth => 240f;

    public override float ToastHeight => 40f;

    // Each notification is a small panel like the menu's. The title sits in a bar, and the line
    // under the bar is the outcome's colour, running down with the time left.
    public override void RenderToasts(ToastView view)
    {
        const float inset = 10f;
        const float bar = 16f;
        var accent = Look.AccentOr(Accent);
        var width = ToastWidth;
        var height = ToastHeight;

        foreach (var card in view.Cards)
        {
            PlaceToast(view, card, (1f - card.Shown) * 24f);
            Draw.Fade(card.Shown);

            var tone = Kit.Tone(card.Kind, Live, Dim);
            Kit.Shadow(0f, 0f, width, height, 0f, 0f);
            Draw.GradientAcross(0f, 0f, width, bar, Rgba.Mix(Bar, accent, 0.14f), Bar);
            Draw.GradientDown(0f, bar + 2f, width, height - bar - 2f, DetailTop, DetailBottom);
            Draw.Rect(0f, bar, width, 2f, Empty);
            Draw.Rect(0f, bar, width * card.Left, 2f, tone);
            Draw.Frame(0f, 0f, width, height, 1f, Edge);

            Draw.Rect(inset, 6f, 4f, 4f, accent);
            Draw.TrackedHead(card.Title, inset + 10f, 4f, 8f, 2f, Soft, width - inset);
            ToastText(card.Text, inset, bar + 7f, 12f, card.Kind == OutcomeKind.Failed ? Kit.Failed : White, width - 8f);
        }

        Draw.Fade(1f);
    }

    public override void RenderGuide(GuideView view)
    {
        const float inset = 14f;
        const float header = 28f;
        var accent = Look.AccentOr(Accent);
        var width = MathF.Max(GuideWidth(view), Draw.TrackedWidth(view.Title, 10f, 3f)) + 2f * inset;
        var height = header + 8f + view.Lines.Count * GuideLineHeight + 8f;

        PlaceGuide(width, height);
        Draw.Fade(view.Shown);

        Kit.Shadow(0f, 0f, width, height, 0f, 0f);
        Draw.GradientAcross(0f, 0f, width, header, Rgba.Mix(Bar, accent, 0.14f), Bar);
        Draw.Rect(0f, header, width, 2f, accent);
        Draw.GradientDown(0f, header + 2f, width, height - header - 2f, DetailTop, DetailBottom);
        Draw.Frame(0f, 0f, width, height, 1f, Edge);

        Draw.Tracked(view.Title, inset, 10f, 10f, 3f, Soft);
        GuideLines(view, inset, header + 8f, Ghost, Soft, Dim);
        Draw.Fade(1f);
    }

    public override void RenderDash(DashView view)
    {
        const float width = 250f;
        const float header = 28f;
        const float inset = 14f;
        var accent = Look.AccentOr(Accent);
        var height = view.Stats ? 162f : 138f;
        var right = width - inset;
        var hot = view.Hot ? Live : accent;

        if (!PlaceDash(view, width, height))
        {
            return;
        }

        Draw.Fade(view.Shown);

        Kit.Shadow(0f, 0f, width, height, 0f, 0f);
        Draw.GradientAcross(0f, 0f, width, header, Rgba.Mix(Bar, accent, 0.14f), Bar);
        Draw.Rect(0f, header, width, 2f, accent);
        Kit.Pulse(0f, header, width, view.Time, new Rgba(255, 255, 255, 200));
        Draw.GradientDown(0f, header + 2f, width, height - header - 2f, DetailTop, DetailBottom);
        Draw.Frame(0f, 0f, width, height, 1f, Edge);

        Draw.Tracked(view.Title, inset, 10f, 10f, 3f, Soft);
        if (view.Mode.Length > 0)
        {
            var chip = Draw.TrackedWidth(view.Mode, 9f, 2f) + 14f;
            Draw.Rect(right - chip, 6f, chip, 16f, hot);
            Draw.Tracked(view.Mode, right - chip + 7f, 10f, 9f, 2f, Ink);
        }

        DashSpeed(view, inset, 40f, White, Dim);
        DashNote(view, right, 46f, Soft, Ghost);
        Kit.Meter(inset, 84f, width - 2f * inset, 6f, 24, view.SpeedFill, 0f, hot, Empty, default);

        DashBar(inset, right, 102f, "ARMOUR", view.Armour, view.ArmourText, Ghost, view.LowArmour ? Kit.Failed : accent, Empty, view.LowArmour ? Kit.Failed : Soft);
        DashBar(inset, right, 118f, "POWER", view.Power, view.PowerText, Ghost, Live, Empty, Soft);

        if (view.Stats)
        {
            Draw.Rect(1f, 137f, width - 2f, 1f, Edge);
            DashStat(inset, 144f, "BEST", view.Best, view.Units, Ghost, Soft);
            DashStat(width / 2f + 6f, 144f, "JUMP", view.Jump, "", Ghost, Soft);
        }

        Draw.Fade(1f);
    }

    // The radial as an instrument dial on a Console panel, which dims the game like the menu. On
    // the left is a dial of solid segments with a needle in the hub that swings to where you point.
    // On the right is the readout: the value set large, every value of a choice listed with the
    // current one marked, the help and the hotkey.
    const float DialWidth = 860f;
    const float DialHeight = 500f;
    const float DialHeader = 46f;
    const float DialFooter = 38f;
    const float DialSplit = 470f;
    const float DialX = 235f;
    const float DialY = 257f;
    const float WedgeIn = 88f;
    const float WedgeOut = 170f;
    const float WedgeGap = 3f;
    const float DialTickIn = 176f;
    const float DialTickOut = 182f;
    const float DialRim = 187f;
    const float DialHub = 74f;
    const float NeedleTip = 90f;
    const float NeedleSize = 14f;
    const float NeedleEase = 14f;
    const float DialFlash = 0.5f;
    const float ValueBox = 62f;
    const int ValueRows = 6;
    const float ValueRowHeight = 24f;

    const string DialTapHelp = "Point the mouse or the right stick at a segment of the dial to see it here.";
    const string DialHoldHelp = "Letting go in the middle of the dial does nothing. Point at a segment to pick it.";

    static float _needle;
    static long _needleAt;

    public override float RadialHub => 70f;

    public override float RadialReach => 150f;

    public override void RenderRadial(RadialView view)
    {
        var accent = Look.AccentOr(Accent);
        var shown = Kit.EaseOut(view.Age / 0.18f);
        var barLeft = Rgba.Mix(Bar, accent, 0.14f);

        Draw.Place(0f, 0f, 1f);
        Draw.Fade(shown);
        Draw.Rect(0f, 0f, Draw.ScreenWidth, Draw.ScreenHeight, Shade);

        // Centred, shrunk to fit a small screen, and growing a touch as it opens like the menu.
        var fit = MathF.Min((Draw.ScreenWidth - 80f) / DialWidth, (Draw.ScreenHeight - 80f) / DialHeight);
        var zoom = MathF.Min(Look.Zoom * view.Size, fit) * (0.96f + 0.04f * shown);
        var originX = (Draw.ScreenWidth - DialWidth * zoom) / 2f;
        var originY = (Draw.ScreenHeight - DialHeight * zoom) / 2f;
        Draw.Place(originX, originY, zoom);
        DialPanel(view, accent, barLeft, shown);
        DialTop(view, accent);

        Draw.Place(originX + DialX * zoom, originY + DialY * zoom, zoom);
        Dial(view, accent, shown);

        Draw.Place(originX, originY, zoom);
        Draw.Fade(shown);
        DialReadout(view, accent);
        DialBottom(view);
    }

    static void DialPanel(RadialView view, Rgba accent, Rgba barLeft, float shown)
    {
        const float bodyTop = DialHeader + 2f;
        const float bodyBottom = DialHeight - DialFooter;
        Kit.Shadow(0f, 0f, DialWidth, DialHeight, Cut, Cut);

        Draw.Rect(0f, Cut, Cut, DialHeader - Cut, barLeft);
        Draw.GradientAcross(Cut, 0f, DialWidth - Cut, DialHeader, barLeft, Bar);
        TopLeftCorner(barLeft, shown);
        Draw.Rect(0f, DialHeader, DialWidth, 2f, accent);
        Kit.Pulse(0f, DialHeader, DialWidth, view.Time, new Rgba(255, 255, 255, 200));

        Draw.Rect(0f, bodyTop, DialSplit, bodyBottom - bodyTop, Panel);
        Draw.GradientDown(DialSplit, bodyTop, DialWidth - DialSplit, bodyBottom - bodyTop, DetailTop, DetailBottom);
        Draw.Rect(DialSplit, bodyTop, 1f, bodyBottom - bodyTop, Edge with { A = 120 });

        Draw.Rect(0f, bodyBottom, DialWidth - Cut, DialFooter, Bar);
        Draw.Rect(DialWidth - Cut, bodyBottom, Cut, DialFooter - Cut, Bar);
        Draw.Rect(0f, bodyBottom, DialWidth, 1f, Edge);
        BottomRightCorner(Bar, shown, DialWidth, DialHeight);
    }

    // FAVOURITES on the left, the page and the time on the right.
    static void DialTop(RadialView view, Rgba accent)
    {
        Kit.Signal(30f, 19f, view.Time, accent);
        Draw.Tracked("FAVOURITES", 52f, 19f, 15f, 4f, accent);

        var x = Draw.TextRight(Kit.Clock, DialWidth - 24f, 21f, 11f, Ghost);
        if (view.Pages < 2)
        {
            return;
        }

        Draw.Rect(x - 14f, 22f, 1f, 9f, Edge);
        x = Draw.TextRight(Kit.Number(view.Pages), x - 28f, 21f, 11f, Dim);
        x = Draw.TextRight("/", x - 6f, 21f, 11f, Ghost);
        x = Draw.TextRight(Kit.Number(view.Page + 1), x - 6f, 21f, 11f, White);
        Draw.Tracked("PAGE", x - 10f - Draw.TrackedWidth("PAGE", 11f, 2f), 21f, 11f, 2f, Dim);
    }

    // The dial, laid out round 0,0. Its segments are triangles, which the game can't fade, so they
    // are mixed toward the panel instead while they come in.
    static void Dial(RadialView view, Rgba accent, float shown)
    {
        var count = view.Slots.Count;
        var step = 360f / count;
        var arrived = MathF.Min(view.Age, view.PageAge);
        var empty = Rgba.Mix(Panel, Edge, 0.4f);
        var tint = Rgba.Mix(Panel, accent, 0.2f);
        var labelSize = count > 8 ? 9f : 11f;
        var gap = count > 1 ? WedgeGap / ((WedgeIn + WedgeOut) / 2f) * 180f / MathF.PI : 0f;

        Shapes.Under = Panel;
        Shapes.Opacity = shown;
        try
        {
            Shapes.Ring(DialRim - 1.2f, DialRim, Edge);
            Shapes.Ticks(DialTickIn, DialTickOut, 60, 0f, 1f, Edge);
            if (count > 1)
            {
                for (var i = 0; i < count; i++)
                {
                    Shapes.Spoke(i * step + step / 2f, WedgeOut + 2f, DialRim, 1.6f, Rgba.Mix(Edge, accent, 0.35f));
                }
            }

            for (var i = 0; i < count; i++)
            {
                var appear = Kit.EaseOut((arrived - 0.08f - i * 0.03f) / 0.18f);
                if (appear <= 0f)
                {
                    continue;
                }

                var feature = view.Slots[i];
                var mid = i * step;
                var from = mid - step / 2f + gap / 2f;
                var to = mid + step / 2f - gap / 2f;
                var over = i == view.Hover;
                var lift = view.Lifts[i];
                var fill = Rgba.Mix(empty, tint, lift);
                var (state, tone) = DialState(view, feature, accent);

                Shapes.Opacity = shown * appear;
                Shapes.Under = Panel;
                Shapes.Arc(WedgeIn, WedgeOut, from, to, fill);
                Shapes.Under = fill;
                Shapes.Arc(WedgeOut - 3f - 2f * lift, WedgeOut, from, to, over ? accent : Edge);
                if (tone == Live)
                {
                    Shapes.Arc(WedgeIn, WedgeIn + 4f, from, to, Live);
                }

                if (view.Flashed == i && view.FlashAge < DialFlash)
                {
                    var fading = 1f - view.FlashAge / DialFlash;
                    Shapes.Arc(WedgeIn, WedgeOut, from, to, White, 0.5f * fading * fading);
                }

                // The name across the middle of its segment, with its state under it.
                Draw.Fade(shown * appear);
                var (mx, my) = Kit.Around(mid, (WedgeIn + WedgeOut) / 2f);
                var room = MathF.Min((to - from) * MathF.PI / 180f * (WedgeIn + WedgeOut) / 2f * 0.82f, 104f);
                var lines = Draw.Wrap(feature.Plain, labelSize, room);
                var shownLines = Math.Min(lines.Length, 3);
                var lineHeight = labelSize + 2f;
                var y = my - (shownLines * lineHeight + 12f) / 2f;
                for (var k = 0; k < shownLines; k++)
                {
                    Draw.Text(lines[k], mx - Draw.TextWidth(lines[k], labelSize) / 2f, y, labelSize, over ? White : Soft);
                    y += lineHeight;
                }

                var stateWidth = Draw.TrackedWidth(state, 8f, 1.4f);
                Draw.Tracked(state, mx - stateWidth / 2f, y + 2f, 8f, 1.4f, Rgba.Mix(tone, White, feature.Glow(view.Now)));
            }

            Shapes.Opacity = shown;
            Shapes.Under = Panel;
            Shapes.Disc(DialHub, Bar);
            Shapes.Under = Bar;
            Shapes.Ring(DialHub - 1.2f, DialHub, Edge);
            Shapes.Under = Panel;
            Needle(view, accent);
        }
        finally
        {
            Shapes.Opacity = 1f;
        }

        // The hub shows the number of the segment pointed at.
        Draw.Fade(shown);
        if (view.Focus is { } focus)
        {
            var number = Kit.Number(view.First + view.Hover + 1);
            Draw.Text(number, -Draw.TextWidth(number, 30f) / 2f, -24f, 30f, White);
            var kind = RadialView.Kind(focus);
            Draw.Tracked(kind, -Draw.TrackedWidth(kind, 8f, 1.8f) / 2f, 14f, 8f, 1.8f, accent);
        }
        else
        {
            var word = view.Tap ? "POINT" : "CANCEL";
            Draw.Tracked(word, -Draw.TrackedWidth(word, 10f, 2.2f) / 2f, -5f, 10f, 2.2f, Dim);
        }
    }

    // A needle on the hub's edge swings round to the pointer, and rests where it was left.
    static void Needle(RadialView view, Rgba accent)
    {
        var seconds = _needleAt == 0 ? 1f : MathF.Min((float)((view.Now - _needleAt) / (double)System.Diagnostics.Stopwatch.Frequency), 0.1f);
        _needleAt = view.Now;
        var pointing = view.Pointer.Length() >= 70f;
        if (pointing)
        {
            var target = MathF.Atan2(view.Pointer.X, -view.Pointer.Y) * 180f / MathF.PI;
            _needle += Kit.Apart(target, _needle) * (1f - MathF.Exp(-NeedleEase * seconds));
        }

        Shapes.Notch(_needle, NeedleTip, NeedleSize, pointing ? accent : Edge);
    }

    static (string Text, Rgba Tone) DialState(RadialView view, Feature feature, Rgba accent) => feature switch
    {
        Toggle toggle => (toggle.ValueText, toggle.On ? Live : Ghost),
        Choice choice => (choice.ValueText, choice.IsDefault ? Dim : Live),
        Command command when command.Showing(view.Now, out var outcome) => (outcome.Text, Kit.Tone(outcome.Kind, Live, Soft)),
        Command command => (command.Cap, accent),
        _ => (feature.ValueText, Ghost),
    };

    // The readout on the right, built like the menu's: the name, the value set large in a box,
    // then every value of a choice, the help and the hotkey.
    static void DialReadout(RadialView view, Rgba accent)
    {
        const float bodyBottom = DialHeight - DialFooter;
        var left = DialSplit + DetailInset;
        var width = DialWidth - DialSplit - 2f * DetailInset;
        var y = DialHeader + 22f;

        var feature = view.Focus;
        if (feature is null)
        {
            Draw.Tracked("NOTHING PICKED", left, y, 11f, 2f, Dim);
            y += 30f;
            foreach (var line in Draw.Wrap(view.Tap ? DialTapHelp : DialHoldHelp, 13f, width))
            {
                Draw.Text(line, left, y, 13f, Soft);
                y += 18f;
            }

            return;
        }

        var (state, tone) = DialState(view, feature, accent);
        var lit = tone == Live;
        var mark = lit ? Live : accent;
        var value = Rgba.Mix(tone == Ghost ? Dim : tone, White, feature.Glow(view.Now));

        Draw.TrackedHead(feature.Plain, left, y, 11f, 2f, Dim, left + width);
        y += 24f;

        Draw.GradientAcross(left, y, width, ValueBox, mark with { A = 26 }, accent with { A = 0 });
        Kit.Corners(left, y, width, ValueBox, 8f, mark);
        var tag = feature is Command ? "COMMAND" : feature.IsDefault ? "DEFAULT" : "CHANGED";
        var tagWidth = Draw.TrackedWidth(tag, 9f, 1.5f) + 12f;
        ToastText(state, left + 14f, y + 16f, 30f, value, left + width - tagWidth - 20f);
        Tag(tag, left + width - 10f, y + 23f, lit ? Live : Ghost);
        y += ValueBox + 14f;

        // Every value of a choice, in a window round the current one when there are many.
        if (feature is Choice choice)
        {
            var rows = Math.Min(choice.Count, ValueRows);
            var first = Math.Clamp(choice.Index - rows / 2, 0, choice.Count - rows);
            for (var k = 0; k < rows; k++)
            {
                var index = first + k;
                var current = index == choice.Index;
                if (current)
                {
                    Draw.GradientAcross(left, y, width, ValueRowHeight - 2f, Rgba.Mix(Panel, accent, 0.18f), DetailTop);
                    Draw.Rect(left, y, 3f, ValueRowHeight - 2f, accent);
                }

                Draw.Text(Kit.Number(index + 1), left + 12f, y + 6f, 10f, current ? accent : Ghost);
                var label = choice.LabelAt(index);
                Draw.TrackedHead(label, left + 38f, y + 5f, 11f, 1f, current ? (lit ? Live : White) : Dim, left + width - 70f);
                if (index == choice.DefaultIndex)
                {
                    Draw.TextRight("DEFAULT", left + width - 8f, y + 7f, 8f, Ghost);
                }

                y += ValueRowHeight;
            }

            y += 8f;
        }

        Draw.Rect(left, y, width, 1f, Edge);
        y += 14f;

        // The hotkey sits at the bottom, and the help fills the room above it.
        const float keyRoom = 34f;
        var lines = Draw.Wrap(feature.Help, 13f, width);
        for (var i = 0; i < lines.Length && y + 18f < bodyBottom - keyRoom; i++)
        {
            Draw.Text(lines[i], left, y, 13f, Soft);
            y += 18f;
        }

        var key = Hotkeys.KeyText(feature);
        var keyY = bodyBottom - 22f;
        var keyEnd = Draw.Tracked("HOTKEY", left, keyY - 4.5f, 9f, 2f, Ghost);
        if (key.Length > 0)
        {
            Kit.Cap(key, keyEnd + 10f, keyY, 9f, Ghost, Soft);
        }
        else
        {
            Draw.Text("NONE", keyEnd + 10f, keyY - 5f, 10f, Ghost);
        }
    }

    // The keys under the dial, and how many options are on under the readout.
    static void DialBottom(RadialView view)
    {
        var middle = DialHeight - DialFooter / 2f;
        Kit.HintRow(view.Hints, DialX, middle, 9f, Ghost, Soft, Dim);

        var active = Catalog.Active;
        var x = DialSplit + DetailInset;
        if (active > 0)
        {
            Draw.Rect(x, middle - 3f, 6f, 6f, Live);
        }
        else
        {
            Draw.Frame(x, middle - 3f, 6f, 6f, 1f, Ghost);
        }

        x = Draw.Tracked("ACTIVE", x + 12f, middle - 5f, 10f, 2f, Ghost);
        Draw.Text(Kit.Number(active), x + 8f, middle - 5f, 10f, active > 0 ? Live : Ghost);
        Draw.TextRight(Kit.Version, DialWidth - Cut - 16f, middle - 5f, 10f, Ghost);
    }
}
