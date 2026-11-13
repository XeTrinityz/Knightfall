#nullable enable

namespace Knightfall;

// A compact see-through card in the top left corner. It is only as tall as its rows, and it is
// built from rectangles alone, so every part of it can fade and let the game show through.
sealed class RailTheme : Theme
{
    const float Margin = 40f;
    const float Width = 420f;
    const float Inset = 18f;
    const float TabsMiddle = 56f;
    const float HeaderHeight = 76f;

    // The pages are listed down a card of their own, off the main card's left side.
    const float ColumnGap = 8f;
    const float ColumnInset = 14f;
    const float PageTop = 40f;
    const float PageHeight = 28f;
    const float PageNameLeft = 40f;
    const float PageSize = 14f;
    const float ColumnMinWidth = 128f;
    const float RowHeight = 30f;
    const float LabelLeft = Inset + 28f;
    const float MeterWidth = 96f;
    const float ValueRoom = 54f;
    const float HelpLineHeight = 18f;
    const float SlideIn = 16f;
    const int MaxLines = 14;
    const int HelpLines = 3;

    static readonly Rgba PanelTop = new(16, 20, 27, 236);
    static readonly Rgba PanelBottom = new(5, 7, 10, 214);
    static readonly Rgba White = new(255, 255, 255);
    static readonly Rgba Soft = new(198, 206, 214);
    static readonly Rgba Dim = new(150, 160, 172);
    static readonly Rgba Ghost = new(104, 114, 128);
    static readonly Rgba Edge = new(255, 255, 255, 30);
    static readonly Rgba Line = new(255, 255, 255, 22);
    static readonly Rgba Empty = new(255, 255, 255, 30);

    // State has its own colour, so what is switched on stands apart from where you are.
    static readonly Rgba Live = new(112, 222, 172);

    static readonly HintStyle KeyHints = new(Size: 10f, CapHeight: 17f, LineHeight: 22f, Frame: Ghost, Key: Soft, Label: Dim);

    public override string Id => "rail";

    public override string Name => "RAIL";

    public override Rgba Accent => new(242, 179, 61);

    public override Rgba Armed => Live;

    public override void Render(MenuView view)
    {
        var accent = Look.AccentOr(Accent);
        var left = Inset;
        var right = Width - Inset;
        var zoom = Look.Zoom;

        // It slides in from the left as it opens, and back out as it closes.
        var column = ColumnWidth(view);
        var slide = (1f - view.Opened) * SlideIn;
        var originX = Margin + (column + ColumnGap) * zoom - slide * zoom + MoveX;
        var originY = Margin + MoveY;
        Draw.Place(originX, originY, zoom);
        Draw.Fade(view.Opened);

        // Heights are worked out first, so the card can be drawn to fit what is in it.
        var visible = Math.Clamp(view.Lines.Count, 1, MaxLines);
        var rowsTop = HeaderHeight + 6f;
        var rowsBottom = rowsTop + visible * RowHeight;
        var helpTop = rowsBottom + 18f;
        var helpLines = HelpRoom(view, 13f, Width - 2f * Inset - 12f, HelpLines);
        var hintsTop = helpTop + helpLines * HelpLineHeight + 10f;
        var hintLines = HintLines(view, left, right, KeyHints);
        var statusTop = hintsTop + (hintLines - 1) * KeyHints.LineHeight + KeyHints.CapHeight + 12f;
        var height = statusTop + 32f;

        // Measured without the slide, so opening doesn't move the spot MENU X works from.
        var columnHeight = PageTop + view.Pages.Count * PageHeight + 10f;
        var tallest = height + (MaxLines - visible) * RowHeight + (HelpLines - helpLines) * HelpLineHeight;
        Covers(slide - (column + ColumnGap), 0f, Width + slide, MathF.Max(height, columnHeight), MathF.Max(tallest, columnHeight));

        Kit.Shadow(0f, 0f, Width, height, 0f, 0f);
        Draw.GradientDown(0f, 0f, Width, height, PanelTop, PanelBottom);
        Draw.Frame(0f, 0f, Width, height, 1f, Edge);
        Draw.GradientDown(0f, 0f, 2f, height, accent, accent with { A = 0 });
        Column(view, accent, column);

        Header(view, accent);
        Draw.Rect(0f, HeaderHeight, Width, 1f, Line);

        Rows(view, accent, rowsTop, visible, originX, originY, zoom);
        Draw.Place(originX, originY, zoom);
        Draw.Fade(view.Opened);
        Draw.Rect(left, rowsBottom + 6f, right - left, 1f, Line);

        Help(view, accent, helpTop);
        DrawHints(view, left, right, hintsTop, KeyHints);

        Draw.Rect(left, statusTop, right - left, 1f, Line);
        Status(statusTop + 11f);
    }

    static void Header(MenuView view, Rgba accent)
    {
        Kit.Signal(Inset, 13f, view.Time, accent);
        Draw.Tracked("KNIGHTFALL", Inset + 20f, 14f, 11f, 3f, Dim);
        Draw.TextRight(Kit.Clock, Width - Inset, 14f, 11f, Ghost);

        Draw.Rect(Inset, 32f, Width - 2f * Inset, 1f, Line);
        Kit.Pulse(Inset, 32f, Width - 2f * Inset, view.Time, accent);

        Tabs(view, accent);
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

    // Every page down a card of its own, numbered, with the keys that turn them at the top.
    static void Column(MenuView view, Rgba accent, float width)
    {
        var left = -(width + ColumnGap);
        var height = PageTop + view.Pages.Count * PageHeight + 10f;
        Kit.Shadow(left, 0f, width, height, 0f, 0f);
        Draw.GradientDown(left, 0f, width, height, PanelTop, PanelBottom);
        Draw.Frame(left, 0f, width, height, 1f, Edge);

        Draw.Tracked("PAGES", left + ColumnInset, 14f, 11f, 3f, Dim);
        var right = left + width - ColumnInset;
        var next = Kit.CapWidth(view.PageNextKey, 9f);
        Kit.Cap(view.PageNextKey, right - next, 19f, 9f, Ghost, Soft);
        Kit.Cap(view.PagePrevKey, right - next - 4f - Kit.CapWidth(view.PagePrevKey, 9f), 19f, 9f, Ghost, Soft);
        Draw.Rect(left + ColumnInset, 32f, width - 2f * ColumnInset, 1f, Line);

        for (var i = 0; i < view.Pages.Count; i++)
        {
            var y = PageTop + i * PageHeight;
            var current = i == view.PageIndex;
            if (current)
            {
                Draw.GradientAcross(left + 1f, y, width - 2f, PageHeight, accent with { A = 48 }, accent with { A = 6 });
                Draw.Rect(left, y, 2f, PageHeight, accent);
            }

            Draw.Text(Kit.Number(i + 1), left + ColumnInset, y + 10f, 10f, current ? accent : Ghost);
            Draw.Text(view.Pages[i].Name, left + PageNameLeft, y + 7f, PageSize, current ? White : Dim);
        }
    }

    static void Tabs(MenuView view, Rgba accent)
    {
        const float size = 12f;
        const float spacing = 1.5f;
        const float gap = 18f;
        const float capGap = 12f;
        const float more = Kit.MoreWidth + 12f;
        var tabs = view.Page.Tabs;
        var y = TabsMiddle - 6f;

        var left = Inset + Kit.Cap(view.TabPrevKey, Inset, TabsMiddle, 9f, Ghost, Dim) + capGap;
        var right = Width - Inset - Kit.CapWidth(view.TabNextKey, 9f) - capGap;
        var strip = FitTabs(view, left, right, size, spacing, 0f, gap, more);
        var before = strip.First;
        var after = tabs.Count - strip.Last;

        var x = strip.Start;
        for (var i = strip.First; i < strip.Last; i++)
        {
            var current = i == view.TabIndex;
            var end = Draw.Tracked(tabs[i].Name, x, y, size, spacing, current ? accent : Dim);
            if (current)
            {
                Draw.Rect(x, TabsMiddle + 10f, end - x, 2f, accent);
            }

            x = end + gap;
        }

        // When they all fit, the next key follows the last tab.
        if (before == 0 && after == 0)
        {
            Kit.Cap(view.TabNextKey, x - gap + capGap, TabsMiddle, 9f, Ghost, Dim);
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

        Kit.Cap(view.TabNextKey, right + capGap, TabsMiddle, 9f, Ghost, Dim);
    }

    static void Rows(MenuView view, Rgba accent, float top, int visible, float originX, float originY, float zoom)
    {
        var lines = view.Lines;
        if (view.Rows.Count == 0)
        {
            return;
        }

        var (first, last, glide) = Scroll(view, visible);

        // The highlight glides between rows. It is drawn first so the text sits on top.
        var highlight = top + glide * RowHeight;
        Draw.GradientAcross(0f, highlight, Width, RowHeight, accent with { A = 70 }, accent with { A = 6 });
        Draw.Rect(0f, highlight, 3f, RowHeight, accent);
        Kit.Sheen(0f, highlight, Width, RowHeight, view.Time, new Rgba(255, 255, 255, 16));

        if (lines.Count > visible)
        {
            var track = visible * RowHeight;
            Draw.Rect(Width - 4f, top, 2f, track, Line);
            Draw.Rect(Width - 4f, top + track * first / lines.Count, 2f, track * visible / lines.Count, accent);
        }

        // Each line comes in a moment after the one above it.
        for (var i = first; i < last; i++)
        {
            var arrive = view.Closing ? 1f : Kit.Cascade(view.Arrived, i - first);
            Draw.Place(originX - (1f - arrive) * 12f * zoom, originY, zoom);
            Draw.Fade(view.Opened * arrive);

            var y = top + (i - first) * RowHeight;
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

    // A short accent tick, the name spaced out, and a hairline to the edge. It sits a little low,
    // so it reads as part of the rows under it.
    static void Heading(string text, float y, Rgba accent)
    {
        var middle = y + RowHeight / 2f + 3f;
        Draw.Rect(Inset, middle - 1f, 8f, 2f, accent);
        var end = Draw.Tracked(text, LabelLeft, middle - 5f, 10f, 2.5f, Dim);
        Draw.Rect(end + 10f, middle, Width - Inset - end - 10f, 1f, Line);
    }

    static void Row(Feature feature, int index, float y, bool selected, Rgba accent, long now)
    {
        var right = Width - Inset;

        // The row number lights up when the option is switched away from normal.
        var changed = !feature.Preference && !feature.IsDefault;
        Draw.Text(Kit.Number(index + 1), Inset, y + 10f, 10f, changed ? Live : selected ? accent : Ghost);

        // A row that can't be used right now steps back, with the reason where its value goes.
        if (feature.Blocked is { } why)
        {
            Draw.Text(feature.Label, LabelLeft, y + 7f, 15f, selected ? Dim : Ghost);
            Kit.Locked(why, right, y + 10f, 10f, Ghost, selected ? Soft : Dim, LabelLeft + Draw.TextWidth(feature.Label, 15f) + 16f);
            return;
        }

        Draw.Text(feature.Label, LabelLeft, y + 7f, 15f, selected ? White : Soft);

        // A value flashes white for a moment when it changes.
        var glow = feature.Glow(now);
        var tone = changed ? Live : accent;
        var value = Rgba.Mix(tone, White, glow);

        switch (feature)
        {
            case Toggle toggle:
                Kit.Switch(right - Kit.SwitchWidth, y + (RowHeight - Kit.SwitchHeight) / 2f, toggle.On, value, Ghost);
                Draw.TextRight(toggle.ValueText, right - Kit.SwitchWidth - 9f, y + 10f, 11f, toggle.On ? value : Ghost);
                break;

            case Slider slider:
                Draw.TextRight(slider.ValueText, right, y + 7f, 15f, value);
                Kit.Meter(right - ValueRoom - MeterWidth, y + 11f, MeterWidth, 8f, 16, slider.Fill, slider.DefaultFill, value, Empty, Dim);
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

                Kit.Dashes(right, y + 24f, choice.Count, choice.Index, value, Empty);
                break;

            case Command command:
            {
                // After a press the cap shows what happened, in its own colour.
                var shown = command.Showing(now, out var outcome);
                var text = shown ? outcome.Text : command.Cap;
                var cap = shown ? Kit.Tone(outcome.Kind, Live, Soft) : accent;
                var width = Draw.TextWidth(text, 10f) + 14f;
                if (selected || shown)
                {
                    Draw.Rect(right - width, y + 7f, width, 16f, cap with { A = 40 });
                }

                Draw.Frame(right - width, y + 7f, width, 16f, 1f, selected || shown ? cap : Ghost);
                Draw.Text(text, right - width + 7f, y + 10f, 10f, shown ? cap : selected ? White : Dim);
                break;
            }
        }
    }

    static void Help(MenuView view, Rgba accent, float top)
    {
        var help = view.Focus?.Help;
        if (string.IsNullOrEmpty(help))
        {
            return;
        }

        Draw.Rect(Inset, top + 5f, 4f, 4f, accent);
        var lines = Draw.Wrap(help, 13f, Width - 2f * Inset - 12f);
        for (var i = 0; i < lines.Length && i < HelpLines; i++)
        {
            Draw.Text(lines[i], Inset + 12f, top + i * HelpLineHeight, 13f, Soft);
        }
    }

    // The bottom line. How many options are switched on, and the mod version.
    static void Status(float y)
    {
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

    // The speedometer is a small card like the menu, sliding in from the right edge.
    // The key guide as a small card that slides in like the menu.
    // Each notification is a small card like the menu's. It slides in from its edge, a strip of
    // the outcome's colour runs down its left side, and a line along the bottom runs down with
    // the time it has left.
    public override void RenderToasts(ToastView view)
    {
        const float inset = 12f;
        var accent = Look.AccentOr(Accent);
        var width = ToastWidth;
        var height = ToastHeight;

        foreach (var card in view.Cards)
        {
            PlaceToast(view, card, (1f - card.Shown) * 2f * SlideIn);
            Draw.Fade(card.Shown);

            var tone = Kit.Tone(card.Kind, Live, Dim);
            Kit.Shadow(0f, 0f, width, height, 0f, 0f);
            Draw.GradientDown(0f, 0f, width, height, PanelTop, PanelBottom);
            Draw.Frame(0f, 0f, width, height, 1f, Edge);
            Draw.GradientDown(0f, 0f, 2f, height, tone, tone with { A = 0 });

            Kit.Signal(inset, 7f, view.Time, accent);
            Draw.TrackedHead(card.Title, inset + 20f, 7f, 8f, 2f, Dim, width - inset);
            ToastText(card.Text, inset, 18f, 12f, card.Kind == OutcomeKind.Failed ? Kit.Failed : White, width - 8f);

            Draw.Rect(2f, height - 1f, width - 2f, 1f, Line);
            Draw.Rect(2f, height - 1f, (width - 2f) * card.Left, 1f, tone);
        }

        Draw.Fade(1f);
    }

    public override void RenderGuide(GuideView view)
    {
        const float inset = 14f;
        const float head = 34f;
        var accent = Look.AccentOr(Accent);
        var width = MathF.Max(GuideWidth(view), Draw.TrackedWidth(view.Title, 9f, 3f) + 20f) + 2f * inset;
        var height = head + view.Lines.Count * GuideLineHeight + 10f;

        PlaceGuide(width, height, (1f - view.Shown) * SlideIn);
        Draw.Fade(view.Shown);

        Kit.Shadow(0f, 0f, width, height, 0f, 0f);
        Draw.GradientDown(0f, 0f, width, height, PanelTop, PanelBottom);
        Draw.Frame(0f, 0f, width, height, 1f, Edge);
        Draw.GradientDown(0f, 0f, 2f, height, accent, accent with { A = 0 });

        Kit.Signal(inset, 10f, view.Time, accent);
        Draw.Tracked(view.Title, inset + 20f, 11f, 9f, 3f, Dim);
        Draw.Rect(inset, 28f, width - 2f * inset, 1f, Line);
        GuideLines(view, inset, head, Ghost, Soft, Dim);
        Draw.Fade(1f);
    }

    public override void RenderDash(DashView view)
    {
        const float width = 240f;
        const float inset = 14f;
        var accent = Look.AccentOr(Accent);
        var height = view.Stats ? 150f : 126f;
        var right = width - inset;
        var hot = view.Hot ? Live : accent;

        if (!PlaceDash(view, width, height, (1f - view.Shown) * SlideIn))
        {
            return;
        }

        Draw.Fade(view.Shown);

        Kit.Shadow(0f, 0f, width, height, 0f, 0f);
        Draw.GradientDown(0f, 0f, width, height, PanelTop, PanelBottom);
        Draw.Frame(0f, 0f, width, height, 1f, Edge);
        Draw.GradientDown(0f, 0f, 2f, height, accent, accent with { A = 0 });

        Kit.Signal(inset, 10f, view.Time, accent);
        Draw.Tracked(view.Title, inset + 20f, 11f, 9f, 3f, Dim);
        if (view.Mode.Length > 0)
        {
            var x = right - Draw.TrackedWidth(view.Mode, 9f, 2f);
            Draw.Rect(x - 10f, 14f, 5f, 5f, hot);
            Draw.Tracked(view.Mode, x, 11f, 9f, 2f, hot);
        }

        Draw.Rect(inset, 28f, width - 2f * inset, 1f, Line);
        Kit.Pulse(inset, 28f, width - 2f * inset, view.Time, accent);

        DashSpeed(view, inset, 34f, White, Dim);
        DashNote(view, right, 40f, Soft, Ghost);
        Kit.Meter(inset, 76f, width - 2f * inset, 5f, 24, view.SpeedFill, 0f, hot, Empty, default);

        DashBar(inset, right, 92f, "ARMOUR", view.Armour, view.ArmourText, Ghost, view.LowArmour ? Kit.Failed : accent, Empty, view.LowArmour ? Kit.Failed : Soft);
        DashBar(inset, right, 108f, "POWER", view.Power, view.PowerText, Ghost, Live, Empty, Soft);

        if (view.Stats)
        {
            Draw.Rect(inset, 125f, width - 2f * inset, 1f, Line);
            DashStat(inset, 132f, "BEST", view.Best, view.Units, Ghost, Soft);
            DashStat(width / 2f + 6f, 132f, "JUMP", view.Jump, "", Ghost, Soft);
        }

        Draw.Fade(1f);
    }

    // The radial as an orbit of Rail cards. Each favourite is a small card on an oval round the
    // middle, with the same number, name and control as its row in the menu. Cards on the right
    // start at the oval and cards on the left end at it, so long names grow away from the middle.
    // The card pointed at slides out and lights like the selected row, and the card in the middle
    // reads it out. Like the menu it is all boxes, so every part fades.
    const float OrbitHub = 70f;
    const float OrbitReach = 150f;
    const float OrbitCardHeight = 46f;
    const float OrbitNumberLeft = 12f;
    const float OrbitNameLeft = 36f;
    const float OrbitCardRight = 14f;
    const float OrbitCardMin = 150f;
    const float OrbitSlide = 12f;
    const float OrbitLift = 8f;
    const float OrbitFlash = 0.5f;
    const float GuideX = 190f;
    const float GuideY = 115f;
    const int GuideDots = 72;
    const float ReadWidth = 280f;
    const float ReadInset = 14f;
    const int ReadLines = 3;

    const string OrbitTapHelp = "Point the mouse or the right stick at a card to see what it does.";
    const string OrbitHoldHelp = "Letting go here does nothing. Point at a card to pick it.";

    static readonly Rgba OrbitShade = new(0, 0, 0, 60);
    static readonly Rgba GuideDot = new(150, 160, 172, 150);

    public override float RadialHub => OrbitHub;

    public override float RadialReach => OrbitReach;

    // Cards sit on an oval, so a card's direction from the middle is worked out from its spot.
    public override float RadialAngle(int slot, int count)
    {
        var (x, y) = OrbitPoint(slot, count);
        return MathF.Atan2(x, -y) * 180f / MathF.PI;
    }

    // More cards get a wider oval, so neighbours near the top and bottom don't touch.
    static (float X, float Y) OrbitPoint(int slot, int count)
    {
        var (width, height) = count > 8 ? (360f, 255f) : (330f, 210f);
        var (sin, cos) = MathF.SinCos(slot * 2f * MathF.PI / count);
        return (sin * width, -cos * height);
    }

    public override void RenderRadial(RadialView view)
    {
        var accent = Look.AccentOr(Accent);
        var count = view.Slots.Count;
        var shown = Kit.EaseOut(view.Age / 0.2f);

        Draw.Place(0f, 0f, 1f);
        Draw.Fade(shown);
        Draw.Rect(0f, 0f, Draw.ScreenWidth, Draw.ScreenHeight, OrbitShade);

        PlaceRadial(view);
        Draw.Fade(shown);
        OrbitGuide(view, accent, count);

        var arrived = MathF.Min(view.Age, view.PageAge);
        var lowest = 0f;
        for (var i = 0; i < count; i++)
        {
            lowest = MathF.Max(lowest, OrbitCard(view, i, count, accent, shown, arrived));
        }

        Draw.Fade(shown);
        OrbitReadout(view, accent);
        Kit.HintRow(view.Hints, 0f, lowest + 30f, 10f, Ghost, Soft, Dim);
    }

    // A ring of dots round the middle card. The dots light up towards the pointer, and a dotted
    // line runs on from the ring to the card pointed at.
    static void OrbitGuide(RadialView view, Rgba accent, int count)
    {
        var pointing = view.Pointer.Length() >= OrbitHub;
        var angle = MathF.Atan2(view.Pointer.X, -view.Pointer.Y) * 180f / MathF.PI;
        for (var k = 0; k < GuideDots; k++)
        {
            var (sin, cos) = MathF.SinCos(k * 2f * MathF.PI / GuideDots);
            var x = sin * GuideX;
            var y = -cos * GuideY;
            var near = pointing ? MathF.Max(0f, 1f - MathF.Abs(Kit.Apart(MathF.Atan2(x, -y) * 180f / MathF.PI, angle)) / 24f) : 0f;
            Draw.Rect(x - 1f, y - 1f, 2f, 2f, near > 0f ? Rgba.Blend(GuideDot, accent, near) : GuideDot);
        }

        if (view.Hover < 0)
        {
            return;
        }

        var (tx, ty) = OrbitPoint(view.Hover, count);
        var length = MathF.Sqrt(tx * tx + ty * ty);
        var (ux, uy) = (tx / length, ty / length);

        // Where the dotted line meets the ring, then on to the near edge of the card.
        var ring = 1f / MathF.Sqrt(ux * ux / (GuideX * GuideX) + uy * uy / (GuideY * GuideY));
        var end = length - 8f - (MathF.Abs(ux) <= 0.05f ? OrbitCardHeight / 2f : 0f);
        for (var d = ring + 8f; d < end; d += 6f)
        {
            Draw.Rect(ux * d - 1f, uy * d - 1f, 2f, 2f, accent);
        }
    }

    // One card. Returns where its bottom edge is.
    static float OrbitCard(RadialView view, int i, int count, Rgba accent, float shown, float arrived)
    {
        var feature = view.Slots[i];
        var (x, y) = OrbitPoint(i, count);
        var length = MathF.Sqrt(x * x + y * y);
        var (ux, uy) = (x / length, y / length);

        // Cards come in from nearer the middle one after another, and the one pointed at slides out.
        var arrive = Kit.Cascade(arrived, i);
        var push = -(1f - arrive) * OrbitSlide + view.Lifts[i] * OrbitLift;
        x += ux * push;
        y += uy * push;

        var width = MathF.Max(OrbitCardMin, OrbitNameLeft + MathF.Max(Draw.TextWidth(feature.Plain, 15f), ControlWidth(view, feature)) + OrbitCardRight);
        var left = ux > 0.05f ? x : ux < -0.05f ? x - width : x - width / 2f;
        var top = y - OrbitCardHeight / 2f;
        var over = i == view.Hover;
        var changed = RadialView.Lit(feature);

        Draw.Fade(shown * arrive);
        Kit.Shadow(left, top, width, OrbitCardHeight, 0f, 0f);
        Draw.GradientDown(left, top, width, OrbitCardHeight, PanelTop, PanelBottom);
        Draw.Frame(left, top, width, OrbitCardHeight, 1f, Edge);
        if (over)
        {
            Draw.GradientAcross(left, top, width, OrbitCardHeight, accent with { A = 70 }, accent with { A = 6 });
            Draw.Rect(left, top, 3f, OrbitCardHeight, accent);
            Kit.Sheen(left, top, width, OrbitCardHeight, view.Time, new Rgba(255, 255, 255, 16));
        }
        else
        {
            Draw.GradientDown(left, top, 2f, OrbitCardHeight, accent, accent with { A = 0 });
        }

        if (view.Flashed == i && view.FlashAge < OrbitFlash)
        {
            var fading = 1f - view.FlashAge / OrbitFlash;
            Draw.Rect(left, top, width, OrbitCardHeight, White with { A = (byte)(90 * fading * fading) });
        }

        // The number lights up when the option is switched away from normal, as in the menu.
        Draw.Text(Kit.Number(view.First + i + 1), left + OrbitNumberLeft, top + 9f, 10f, changed ? Live : over ? accent : Ghost);
        Draw.Text(feature.Plain, left + OrbitNameLeft, top + 6f, 15f, over ? White : Soft);
        OrbitControl(view, feature, left + OrbitNameLeft, top + 26f, accent, over);
        return top + OrbitCardHeight;
    }

    static float ControlWidth(RadialView view, Feature feature)
    {
        switch (feature)
        {
            case Toggle toggle:
                return Kit.SwitchWidth + 8f + Draw.TextWidth(toggle.ValueText, 11f);
            case Choice choice:
                var dashes = Math.Min(choice.Count, 10);
                return MathF.Max(Draw.TextWidth(choice.ValueText, 11f), dashes * 10f - 2f);
            case Command command:
                return Draw.TextWidth(command.Showing(view.Now, out var outcome) ? outcome.Text : command.Cap, 10f) + 14f;
            default:
                return 0f;
        }
    }

    // The same controls as the rows in the menu, under the name.
    static void OrbitControl(RadialView view, Feature feature, float x, float y, Rgba accent, bool over)
    {
        var changed = RadialView.Lit(feature);
        var value = Rgba.Mix(changed ? Live : accent, White, feature.Glow(view.Now));
        switch (feature)
        {
            case Toggle toggle:
                Kit.Switch(x, y, toggle.On, value, Ghost);
                Draw.Text(toggle.ValueText, x + Kit.SwitchWidth + 8f, y + 1.5f, 11f, toggle.On ? value : Ghost);
                break;

            case Choice choice:
            {
                Draw.Text(choice.ValueText, x, y, 11f, value);
                var dashes = Math.Min(choice.Count, 10);
                Kit.Dashes(x + dashes * 10f - 2f, y + 14f, choice.Count, choice.Index, value, Empty);
                break;
            }

            case Command command:
            {
                var shown = command.Showing(view.Now, out var outcome);
                var text = shown ? outcome.Text : command.Cap;
                var cap = shown ? Kit.Tone(outcome.Kind, Live, Soft) : accent;
                var width = Draw.TextWidth(text, 10f) + 14f;
                if (over || shown)
                {
                    Draw.Rect(x, y, width, 15f, cap with { A = 40 });
                }

                Draw.Frame(x, y, width, 15f, 1f, over || shown ? cap : Ghost);
                Draw.Text(text, x + 7f, y + 2.5f, 10f, shown ? cap : over ? White : Dim);
                break;
            }
        }
    }

    static (string Text, Rgba Tone) OrbitState(RadialView view, Feature feature, Rgba accent) => feature switch
    {
        Toggle toggle => (toggle.ValueText, toggle.On ? Live : Ghost),
        Choice choice => (choice.ValueText, choice.IsDefault ? accent : Live),
        Command command when command.Showing(view.Now, out var outcome) => (outcome.Text, Kit.Tone(outcome.Kind, Live, Soft)),
        Command command => (command.Cap, accent),
        _ => (feature.ValueText, Ghost),
    };

    // The card in the middle reads out the favourite pointed at, with the page numbers on top.
    // It is sized for the longest help on the page, so it doesn't change size as the pointer moves.
    static void OrbitReadout(RadialView view, Rgba accent)
    {
        const float nameTop = 42f;
        const float kindTop = 64f;
        const float helpTop = 84f;
        const float helpLine = 16f;
        const float bottomPad = 12f;
        var helpWidth = ReadWidth - 2f * ReadInset;
        var idle = view.Tap ? OrbitTapHelp : OrbitHoldHelp;

        var room = Draw.Wrap(idle, 13f, helpWidth).Length;
        foreach (var slot in view.Slots)
        {
            room = Math.Max(room, Draw.Wrap(slot.Help, 13f, helpWidth).Length);
        }

        room = Math.Clamp(room, 1, ReadLines);
        var height = helpTop + (room - 1) * helpLine + 13f + bottomPad;
        var left = -ReadWidth / 2f;
        var top = -height / 2f;
        var right = left + ReadWidth - ReadInset;
        var textLeft = left + ReadInset;

        Kit.Shadow(left, top, ReadWidth, height, 0f, 0f);
        Draw.GradientDown(left, top, ReadWidth, height, PanelTop, PanelBottom);
        Draw.Frame(left, top, ReadWidth, height, 1f, Edge);
        Draw.GradientDown(left, top, 2f, height, accent, accent with { A = 0 });

        Kit.Signal(textLeft, top + 13f, view.Time, accent);
        Draw.Tracked("FAVOURITES", textLeft + 20f, top + 14f, 11f, 3f, Dim);

        // The pages are numbered like the menu's tabs, with the one shown underlined.
        if (view.Pages > 1)
        {
            var x = right;
            for (var p = view.Pages - 1; p >= 0; p--)
            {
                var number = Kit.Number(p + 1);
                var numberWidth = Draw.TextWidth(number, 11f);
                x -= numberWidth;
                var current = p == view.Page;
                Draw.Text(number, x, top + 14f, 11f, current ? accent : Ghost);
                if (current)
                {
                    Draw.Rect(x, top + 28f, numberWidth, 2f, accent);
                }

                x -= 10f;
            }
        }

        Draw.Rect(textLeft, top + 32f, right - textLeft, 1f, Line);
        Kit.Pulse(textLeft, top + 32f, right - textLeft, view.Time, accent);

        var feature = view.Focus;
        string[] lines;
        if (feature is null)
        {
            Draw.Text(view.Tap ? "POINT AT A CARD" : "LET GO TO CANCEL", textLeft, top + nameTop, 15f, Soft);
            Draw.Tracked("NOTHING PICKED", textLeft, top + kindTop, 9f, 2f, Ghost);
            lines = Draw.Wrap(idle, 13f, helpWidth);
        }
        else
        {
            ToastText(feature.Plain, textLeft, top + nameTop, 15f, White, right);
            var kindEnd = Draw.Tracked(RadialView.Kind(feature), textLeft, top + kindTop, 9f, 2f, Ghost);
            var (state, tone) = OrbitState(view, feature, accent);
            Draw.TextRight(state, right, top + kindTop - 1.5f, 11f, Rgba.Mix(tone, White, feature.Glow(view.Now)));

            var key = Hotkeys.KeyText(feature);
            if (key.Length > 0)
            {
                Kit.Cap(key, kindEnd + 10f, top + kindTop + 4.5f, 9f, Ghost, Soft);
            }

            lines = Draw.Wrap(feature.Help, 13f, helpWidth);
        }

        for (var k = 0; k < lines.Length && k < room; k++)
        {
            Draw.Text(lines[k], textLeft, top + helpTop + k * helpLine, 13f, feature is null ? Dim : Soft);
        }
    }
}
