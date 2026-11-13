#nullable enable

namespace Knightfall;

// A stack of leaning solid slabs in the top right corner. Slanted shapes are triangles, and the
// game can't fade those, so nothing here fades. Slabs shoot out from the right edge when the
// menu opens and pull back in when it closes, and the game shows through the gaps between them.
sealed class MilitiaTheme : Theme
{
    const float Margin = 40f;
    const float Width = 440f;
    const float Inset = 20f;
    const float Gap = 3f;

    // How far a slab's edge moves left for each unit it goes down. The whole stack shares it,
    // so the edges of every slab line up into one long slanted line.
    const float Slant = 0.16f;

    const float HeaderHeight = 44f;
    const float StripeHeight = 6f;
    const float PagesHeight = 26f;

    // The pages hang in a column of slabs off the stack's left side.
    const float ColumnGap = 8f;
    const float PagePad = 12f;
    const float PageNameLeft = 38f;
    const float PageSize = 14f;
    const float ColumnMinWidth = 124f;
    const float TabsHeight = 24f;
    const float RowHeight = 30f;
    const float ChipWidth = 60f;
    const float MeterWidth = 96f;
    const float ValueRoom = 54f;
    const float HelpLineHeight = 17f;
    const float StatusHeight = 24f;
    const float Punch = 12f;
    const int MaxLines = 12;
    const int HelpLines = 3;
    const int Stripes = 22;

    static readonly Rgba Dark = new(11, 11, 13);
    static readonly Rgba Darker = new(22, 22, 26);
    static readonly Rgba White = new(255, 255, 255);
    static readonly Rgba Soft = new(208, 212, 218);
    static readonly Rgba Dim = new(166, 170, 178);
    static readonly Rgba Ghost = new(112, 116, 124);
    static readonly Rgba Ink = new(11, 11, 13);
    static readonly Rgba Empty = new(255, 255, 255, 36);
    static readonly Rgba EmptyOnWhite = new(0, 0, 0, 44);
    static readonly Rgba Warning = new(255, 184, 0);

    static readonly HintStyle KeyHints = new(Size: 10f, CapHeight: 17f, LineHeight: 22f, Frame: Ghost, Key: Soft, Label: Dim);

    public override string Id => "militia";

    public override string Name => "MILITIA";

    public override Rgba Accent => new(240, 65, 58);

    public override Rgba Armed => Warning;

    public override void Render(MenuView view)
    {
        var accent = Look.AccentOr(Accent);
        var zoom = Look.Zoom;
        var open = view.Opened;

        // Docked to the top right corner. The stack leans left as it goes down.
        Draw.Place(Draw.ScreenWidth - Margin - Width * zoom + MoveX, Margin + MoveY, zoom);

        var y = 0f;
        Header(view, y, open, accent);
        y += HeaderHeight + Gap;

        Hazard(view, y, open, accent);
        y += StripeHeight + Gap;

        var columnTop = y;
        Column(view, y, open);

        Tabs(view, y, open, accent);
        y += TabsHeight + Gap + 3f;

        y = Rows(view, y, open, accent) + 3f;

        y = Help(view, y, open) + Gap;
        y = Hints(view, y, open) + Gap;
        Status(y, open, accent);

        // The lean takes the bottom of each part further left, and the scroll bar sits just
        // past the right edge. The left edge is measured at the tallest the stack gets, so it
        // doesn't shift with the tab either.
        var bottom = y + StatusHeight;
        var tallest = bottom + (MaxLines - Math.Clamp(view.Lines.Count, 1, MaxLines)) * (RowHeight + Gap);
        var columnBottom = columnTop + view.Pages.Count * (PagesHeight + Gap);
        var columnLeft = -(Punch + ColumnGap) - ColumnWidth(view);
        var leftmost = MathF.Min(columnLeft - Slant * columnBottom, -Punch - Slant * tallest);
        Covers(leftmost, 0f, Width + 7f, MathF.Max(bottom, columnBottom), MathF.Max(tallest, columnBottom));

        Draw.Fade(1f);
    }

    // A slab between two x positions, measured where the top of the stack is. It leans with the
    // stack and grows out from its right end.
    static void Slab(float left, float right, float y, float height, float grow, Rgba color)
    {
        if (grow <= 0f || right <= left)
        {
            return;
        }

        var from = right - (right - left) * MathF.Min(grow, 1f);
        var top = Slant * y;
        var bottom = Slant * (y + height);
        Draw.Quad(from - top, y, right - top, y, right - bottom, y + height, from - bottom, y + height, color);
    }

    // How far left things sit at the middle of a slab, to follow the lean.
    static float Lean(float y, float height) => Slant * (y + height / 2f);

    // Text and small parts can fade, so they come in over the second half of a slab's growth.
    static void FadeWith(float grow)
    {
        Draw.Fade(Math.Clamp(grow * 2f - 1f, 0f, 1f));
    }

    static void Header(MenuView view, float y, float grow, Rgba accent)
    {
        var lean = Lean(y, HeaderHeight);
        Slab(0f, Width, y, HeaderHeight, grow, accent);

        FadeWith(grow);
        Kit.Signal(Inset - lean, y + 16f, view.Time, Ink);
        Draw.Tracked("KNIGHTFALL", Inset + 22f - lean, y + 15f, 15f, 4f, Ink);
        Draw.TextRight(Kit.Clock, Width - Inset - lean, y + 17f, 11f, Ink);
    }

    // A strip of small hazard bars under the header. A lit group runs along it.
    static void Hazard(MenuView view, float y, float grow, Rgba accent)
    {
        const float pitch = 20f;
        const float bar = 11f;
        var top = Slant * y;
        var bottom = Slant * (y + StripeHeight);
        var head = (int)(view.Time * 16f) % (Stripes + 10);
        var shown = (int)(Stripes * Math.Clamp(grow, 0f, 1f));

        for (var i = Stripes - shown; i < Stripes; i++)
        {
            // The bars lean harder than the slabs do, like hazard tape.
            var x = i * pitch;
            var lit = i <= head && i > head - 4;
            Draw.Quad(
                x + StripeHeight - top, y,
                x + StripeHeight + bar - top, y,
                x + bar - bottom, y + StripeHeight,
                x - bottom, y + StripeHeight,
                lit ? accent : Darker);
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

        return MathF.Max(ColumnMinWidth, PageNameLeft + widest + PagePad);
    }

    // A dark slab with the keys that turn the pages, level with the header, then every page as
    // a slab of its own from where the tabs start, numbered, the current one white.
    static void Column(MenuView view, float top, float grow)
    {
        // The chosen row pokes out to the left, so the column keeps clear of that too.
        var right = -(Punch + ColumnGap);
        var left = right - ColumnWidth(view);

        var lean = Lean(0f, HeaderHeight);
        Slab(left, right, 0f, HeaderHeight, grow, Dark);
        FadeWith(grow);
        Draw.Tracked("PAGES", left + PagePad - lean, 16f, 11f, 3f, Soft);
        var next = Kit.CapWidth(view.PageNextKey, 9f);
        var capRight = right - PagePad - lean;
        Kit.Cap(view.PageNextKey, capRight - next, HeaderHeight / 2f, 9f, Ghost, Dim);
        Kit.Cap(view.PagePrevKey, capRight - next - 4f - Kit.CapWidth(view.PagePrevKey, 9f), HeaderHeight / 2f, 9f, Ghost, Dim);

        for (var i = 0; i < view.Pages.Count; i++)
        {
            var y = top + i * (PagesHeight + Gap);
            var current = i == view.PageIndex;
            Slab(left, right, y, PagesHeight, grow, current ? White : Dark);
            FadeWith(grow);
            lean = Lean(y, PagesHeight);
            Draw.Text(Kit.Number(i + 1), left + PagePad - lean, y + 8f, 10f, current ? Ink : Ghost);
            Draw.Text(view.Pages[i].Name, left + PageNameLeft - lean, y + 6f, PageSize, current ? Ink : Dim);
        }
    }

    static void Tabs(MenuView view, float y, float grow, Rgba accent)
    {
        const float size = 11f;
        const float spacing = 1.5f;
        const float pad = 14f;
        var lean = Lean(y, TabsHeight);
        var middle = y + TabsHeight / 2f;
        var tabs = view.Page.Tabs;

        var prevWidth = Kit.CapWidth(view.TabPrevKey, 9f) + 20f;
        var nextWidth = Kit.CapWidth(view.TabNextKey, 9f) + 20f;
        Slab(0f, prevWidth, y, TabsHeight, grow, Dark);
        Slab(Width - nextWidth, Width, y, TabsHeight, grow, Dark);

        const float more = Kit.MoreWidth + 16f;
        const float edge = 6f;
        var left = prevWidth + Gap;
        var fillRight = Width - nextWidth - Gap;
        var strip = FitTabs(view, left, fillRight, size, spacing, 2f * pad, Gap, more + Gap);
        var before = strip.First;
        var after = tabs.Count - strip.Last;
        var x = strip.Start;

        // Tabs hidden off the left get a slab of their own. It takes any room left over and
        // shows the cut off name of the tab before, or a small count when that doesn't fit.
        if (before > 0)
        {
            var slabRight = x - Gap;
            Slab(left, slabRight, y, TabsHeight, grow, Dark);
            FadeWith(grow);
            if (!Draw.TrackedTail(tabs[strip.First - 1].Name, slabRight - pad - lean, y + 7f, size, spacing, Ghost, left + edge - lean))
            {
                Kit.More(left + 8f - lean, middle, before, left: true, accent, Dim);
            }
        }

        for (var i = strip.First; i < strip.Last; i++)
        {
            var current = i == view.TabIndex;
            var width = Draw.TrackedWidth(tabs[i].Name, size, spacing) + 2f * pad;
            Slab(x, x + width, y, TabsHeight, grow, Dark);
            if (current)
            {
                // The current tab sits on a thin bar of the accent colour.
                Slab(x, x + width, y + TabsHeight, 3f, grow, accent);
            }

            FadeWith(grow);
            Draw.Tracked(tabs[i].Name, x + pad - lean, y + 7f, size, spacing, current ? White : Ghost);
            x += width + Gap;
        }

        // Whatever room is left becomes one more slab. With tabs hidden off the right, it shows
        // the cut off name of the next one, or a small count when that doesn't fit.
        if (fillRight - x > 8f)
        {
            Slab(x, fillRight, y, TabsHeight, grow, Dark);
            FadeWith(grow);
            if (after > 0 && !Draw.TrackedHead(tabs[strip.Last].Name, x + pad - lean, y + 7f, size, spacing, Ghost, fillRight - edge - lean))
            {
                Kit.More(fillRight - 8f - Kit.MoreWidth - lean, middle, after, left: false, accent, Dim);
            }
        }

        FadeWith(grow);
        Kit.Cap(view.TabPrevKey, 10f - lean, middle, 9f, Ghost, Dim);
        Kit.Cap(view.TabNextKey, Width - nextWidth + 10f - lean, middle, 9f, Ghost, Dim);
    }

    // Returns where the rows end.
    static float Rows(MenuView view, float top, float open, Rgba accent)
    {
        var lines = view.Lines;
        var visible = Math.Clamp(lines.Count, 1, MaxLines);
        var bottom = top + visible * (RowHeight + Gap) - Gap;
        if (view.Rows.Count == 0)
        {
            return bottom;
        }

        var (first, last, _) = Scroll(view, visible);

        // The selected slab punches out to the left each time the selection lands on it.
        var settled = 1f - MathF.Min(1f, MathF.Abs(view.Glide - view.RowIndex));

        for (var i = first; i < last; i++)
        {
            // Each line shoots out a moment after the one above it. Closing pulls them in together.
            var grow = view.Closing ? open : MathF.Min(open, Kit.Cascade(view.Arrived, i - first + 2));
            var y = top + (i - first) * (RowHeight + Gap);
            var line = lines[i];
            if (line.Row is { } row)
            {
                var selected = line.RowIndex == view.RowIndex;
                Row(row, line.RowIndex, y, selected, selected ? settled * Punch : 0f, grow, accent, view.Now);
            }
            else
            {
                Heading(line.Heading!, y, grow, accent);
            }
        }

        if (lines.Count > visible)
        {
            // A thin leaning bar down the right side shows where you are in a long list.
            var track = bottom - top;
            var thumbTop = top + track * first / lines.Count;
            var thumbHeight = track * visible / lines.Count;
            Slab(Width + 4f, Width + 7f, top, track, open, Darker);
            Slab(Width + 4f, Width + 7f, thumbTop, thumbHeight, open, accent);
        }

        return bottom;
    }

    // A gap in the stack with the name in it. A small block of the accent sits on the leaning
    // edge and a thin dark slab runs on from the name to the right side. It sits a little low,
    // so it reads as part of the rows under it.
    static void Heading(string text, float y, float grow, Rgba accent)
    {
        var middle = y + RowHeight / 2f + 3f;
        var lean = Lean(middle, 0f);
        Slab(0f, 8f, middle - 5f, 10f, grow, accent);

        FadeWith(grow);
        var end = Draw.Tracked(text, Inset - lean, middle - 5f, 10f, 3f, White);
        Slab(end + 10f + lean, Width, middle - 1f, 2f, grow, Darker);
    }

    static void Row(Feature feature, int index, float y, bool selected, float punch, float grow, Rgba accent, long now)
    {
        var lean = Lean(y, RowHeight);
        var changed = !feature.Preference && !feature.IsDefault;
        var back = selected ? White : Dark;
        var ink = selected ? Ink : White;

        // A state flashes white for a moment when it changes.
        var state = Rgba.Mix(accent, White, feature.Glow(now));

        // A row that can't be used right now has no slab of its own to press. Its name steps
        // back and the reason sits where its value goes.
        var why = feature.Blocked;

        // Toggles and commands end in their own short slab, split off by a slanted gap.
        var chip = why is null && feature is Toggle or Command;
        var mainRight = chip ? Width - ChipWidth - Gap : Width;
        Slab(-punch, mainRight, y, RowHeight, grow, back);

        if (chip)
        {
            var lit = feature is Toggle { On: true } || (feature is Command && selected);
            var text = feature is Toggle toggle ? toggle.ValueText : feature is Command button ? button.Cap : "RUN";
            var fill = lit ? state : Dark;

            // After a press the chip shows what happened, filled with its own colour. The accent
            // is already the selected chip and red reads as failure too, so neither is used here.
            if (feature is Command command && command.Showing(now, out var outcome))
            {
                lit = true;
                text = outcome.Text;
                fill = outcome.Kind switch
                {
                    OutcomeKind.Done => White,
                    OutcomeKind.Failed => Warning,
                    _ => Dim,
                };
            }

            Slab(Width - ChipWidth, Width, y, RowHeight, grow, fill);

            // A longer result is drawn smaller so it stays inside the chip.
            var size = Draw.TextWidth(text, 13f) > ChipWidth - 12f ? 10f : 13f;
            FadeWith(grow);
            var textLeft = Width - ChipWidth / 2f - lean - Draw.TextWidth(text, size) / 2f;
            Draw.Text(text, textLeft, y + (RowHeight - size) / 2f, size, lit ? Ink : Ghost);
        }

        FadeWith(grow);
        Draw.Text(Kit.Number(index + 1), Inset - lean, y + 10f, 10f, changed ? accent : selected ? Ink : Ghost);
        var right = Width - Inset - lean;
        if (why is not null)
        {
            var faint = selected ? Ink with { A = 150 } : Ghost;
            Draw.Text(feature.Label, Inset + 28f - lean, y + 7f, 15f, faint);
            Kit.Locked(why, right, y + 10f, 10f, faint, faint, Inset + 28f - lean + Draw.TextWidth(feature.Label, 15f) + 16f);
            return;
        }

        Draw.Text(feature.Label, Inset + 28f - lean, y + 7f, 15f, ink);
        var value = selected ? Ink : changed ? state : White;

        switch (feature)
        {
            case Slider slider:
                Draw.TextRight(slider.ValueText, right, y + 7f, 15f, value);
                Kit.Meter(right - ValueRoom - MeterWidth, y + 11f, MeterWidth, 8f, 16, slider.Fill, slider.DefaultFill, state, selected ? EmptyOnWhite : Empty, selected ? Ink : Dim);
                break;

            case Choice choice:
                if (selected)
                {
                    var x = Draw.TextRight(">", right, y + 6f, 13f, Ink);
                    x = Draw.TextRightFit(choice.ValueText, x - 8f, y + 6f, 13f, Ink, Inset + 28f - lean + Draw.TextWidth(feature.Label, 15f) + 16f + Draw.TextWidth("<", 13f) + 8f);
                    Draw.TextRight("<", x - 8f, y + 6f, 13f, Ink);
                }
                else
                {
                    Draw.TextRightFit(choice.ValueText, right, y + 6f, 13f, value, Inset + 28f - lean + Draw.TextWidth(feature.Label, 15f) + 16f);
                }

                Kit.Dashes(right, y + 24f, choice.Count, choice.Index, state, selected ? EmptyOnWhite : Empty);
                break;
        }
    }

    // Returns where the help slab ends.
    static float Help(MenuView view, float y, float grow)
    {
        var height = HelpLines * HelpLineHeight + 16f;
        Slab(0f, Width, y, height, grow, Dark);

        var help = view.Focus?.Help;
        if (!string.IsNullOrEmpty(help))
        {
            FadeWith(grow);
            var lines = Draw.Wrap(help, 13f, Width - 2f * Inset - 10f);
            for (var i = 0; i < lines.Length && i < HelpLines; i++)
            {
                // Each line steps left a little, so the block follows the lean of its slab.
                var lineTop = y + 9f + i * HelpLineHeight;
                Draw.Text(lines[i], Inset - Slant * (lineTop + 6f), lineTop, 13f, Soft);
            }
        }

        return y + height;
    }

    // Returns where the hints slab ends.
    static float Hints(MenuView view, float y, float grow)
    {
        var left = Inset;
        var right = Width - Inset - 10f;
        var lines = Math.Max(1, HintLines(view, left, right, KeyHints));
        var height = (lines - 1) * KeyHints.LineHeight + KeyHints.CapHeight + 14f;
        var lean = Lean(y, height);

        Slab(0f, Width, y, height, grow, Dark);
        FadeWith(grow);
        DrawHints(view, left - lean, right - lean, y + 7f, KeyHints);
        return y + height;
    }

    // The bottom slab. How many options are switched on, and the mod version.
    static void Status(float y, float grow, Rgba accent)
    {
        var lean = Lean(y, StatusHeight);
        var active = Catalog.Active;
        Slab(0f, Width, y, StatusHeight, grow, Dark);

        FadeWith(grow);
        var x = Inset - lean;
        if (active > 0)
        {
            Draw.Rect(x, y + 9f, 6f, 6f, accent);
        }
        else
        {
            Draw.Frame(x, y + 9f, 6f, 6f, 1f, Ghost);
        }

        x = Draw.Tracked("ACTIVE", x + 12f, y + 7f, 10f, 2f, Ghost);
        Draw.Text(Kit.Number(active), x + 8f, y + 7f, 10f, active > 0 ? accent : Ghost);
        Draw.TextRight(Kit.Version, Width - Inset - lean, y + 7f, 10f, Ghost);
    }

    public override float ToastWidth => 250f;

    // The card and the timer bar under it.
    public override float ToastHeight => 40f;

    // Each notification is leaning slabs like the menu. They shoot out from the right end and
    // pull back in as it goes. A short slab of the outcome's colour leads it, and a thin bar
    // under it shrinks with the time left.
    public override void RenderToasts(ToastView view)
    {
        const float card = 32f;
        const float tag = 10f;
        const float inset = 22f;
        const float timer = 3f;
        var width = ToastWidth;

        foreach (var toast in view.Cards)
        {
            PlaceToast(view, toast, 0f);
            var grow = toast.Shown;

            // Red is the accent and reads as failure too, so the outcome colours follow the
            // command chips: white done, yellow failed.
            var tone = toast.Kind switch
            {
                OutcomeKind.Done => White,
                OutcomeKind.Failed => Warning,
                _ => Ghost,
            };

            Slab(0f, tag, 0f, card, grow, tone);
            Slab(tag + Gap, width, 0f, card, grow, Dark);
            Slab(width - (width - tag - Gap) * toast.Left, width, card + Gap, timer, grow, tone);

            FadeWith(grow);
            var lean = Lean(0f, card);
            Draw.TrackedHead(toast.Title, inset - lean, 5f, 8f, 2.5f, Dim, width - 12f - lean);
            ToastText(toast.Text, inset - lean, 15f, 13f, toast.Kind == OutcomeKind.Failed ? Warning : White, width - 8f - lean);
        }

        Draw.Fade(1f);
    }

    // The speedometer is leaning slabs like the menu, which shoot out from the right edge. Each
    // part inside a slab moves left with the lean at its own height.
    // The key guide as two slabs, a title in the accent colour over the keys.
    public override void RenderGuide(GuideView view)
    {
        const float inset = 18f;
        const float head = 26f;
        var accent = Look.AccentOr(Accent);
        var grow = view.Shown;
        var width = MathF.Max(GuideWidth(view), Draw.TrackedWidth(view.Title, 10f, 3f)) + 2f * inset;
        var body = view.Lines.Count * GuideLineHeight + 12f;

        // The lean takes the bottom further left the taller the guide is, so it's moved right by
        // that much to keep its foot on screen.
        var height = head + Gap + body;
        PlaceGuide(width, height, -Slant * height);

        Slab(0f, width, 0f, head, grow, accent);
        FadeWith(grow);
        Draw.Tracked(view.Title, inset - Lean(0f, head), 8f, 10f, 3f, Ink);

        var y = head + Gap;
        Slab(0f, width, y, body, grow, Dark);
        FadeWith(grow);
        GuideLines(view, inset, y + 6f, Ghost, Soft, Dim, Slant);
        Draw.Fade(1f);
    }

    public override void RenderDash(DashView view)
    {
        const float width = 250f;
        const float inset = 18f;
        const float head = 26f;
        const float body = 98f;
        const float stats = 26f;
        var accent = Look.AccentOr(Accent);
        var grow = view.Shown;
        var height = head + Gap + body + (view.Stats ? Gap + stats : 0f);
        var hot = view.Hot ? Warning : accent;

        if (!PlaceDash(view, width, height))
        {
            return;
        }

        var y = 0f;
        Slab(0f, width, y, head, grow, accent);
        FadeWith(grow);
        var lean = Lean(y, head);
        Draw.Tracked(view.Title, inset - lean, y + 8f, 10f, 3f, Ink);
        if (view.Mode.Length > 0)
        {
            var x = width - inset - lean - Draw.TrackedWidth(view.Mode, 10f, 3f);
            if (view.Hot)
            {
                Draw.Rect(x - 6f, y + 4f, width - inset - lean - x + 12f, head - 8f, Ink);
            }

            Draw.Tracked(view.Mode, x, y + 8f, 10f, 3f, view.Hot ? Warning : Ink);
        }

        y += head + Gap;
        Slab(0f, width, y, body, grow, Dark);
        FadeWith(grow);
        DashSpeed(view, inset - Slant * (y + 26f), y + 8f, White, Dim);
        DashNote(view, width - inset - Slant * (y + 22f), y + 14f, Soft, Ghost);

        var row = y + 52f;
        Kit.Meter(inset - Slant * row, row, width - 2f * inset, 6f, 24, view.SpeedFill, 0f, hot, Empty, default);
        row = y + 66f;
        DashBar(inset - Slant * row, width - inset - Slant * row, row, "ARMOUR", view.Armour, view.ArmourText, Ghost, view.LowArmour ? Warning : accent, Empty, view.LowArmour ? Warning : Soft);
        row = y + 82f;
        DashBar(inset - Slant * row, width - inset - Slant * row, row, "POWER", view.Power, view.PowerText, Ghost, White, Empty, Soft);

        if (view.Stats)
        {
            y += body + Gap;
            Slab(0f, width, y, stats, grow, Darker);
            FadeWith(grow);
            lean = Lean(y, stats);
            DashStat(inset - lean, y + 7f, "BEST", view.Best, view.Units, Ghost, Soft);
            DashStat(width / 2f + 6f - lean, y + 7f, "JUMP", view.Jump, "", Ghost, Soft);
        }

        Draw.Fade(1f);
    }

    // The radial as a burst of Militia slabs round a red header slab. Each favourite is a leaning
    // slab with its state in a split-off chip, as in the menu. Slabs on the right start at the
    // oval and slabs on the left end at it, so long names grow away from the middle. The one
    // pointed at turns white and punches out, with a red line back to the middle. Like the menu
    // nothing fades: slabs grow out from the middle and their text comes in as they do.
    const float BurstHub = 66f;
    const float BurstReach = 150f;
    const float BladeChipMin = 56f;
    const float BladeNumberLeft = 12f;
    const float BladeNameLeft = 36f;
    const float BladePad = 14f;
    const float BladePunch = 12f;
    const float BurstLineWidth = 3f;
    const float BurstFlash = 0.14f;
    const float CoreWidth = 220f;
    const float CoreHead = 26f;
    const float CoreBody = 30f;
    const float CoreStripe = 7f;
    const float NoteWidth = 600f;
    const float NoteHints = 30f;
    const int NoteLines = 2;

    const string BurstTapHelp = "Point the mouse or the right stick at a slab to see what it does.";
    const string BurstHoldHelp = "Letting go in the middle does nothing. Point at a slab to pick it.";

    public override float RadialHub => BurstHub;

    public override float RadialReach => BurstReach;

    public override float RadialAngle(int slot, int count)
    {
        var (x, y) = BurstPoint(slot, count);
        return MathF.Atan2(x, -y) * 180f / MathF.PI;
    }

    // More slabs get a wider oval and thinner slabs, so neighbours near the top and bottom don't touch.
    static float BladeHeight(int count) => count > 8 ? 34f : 38f;

    static (float X, float Y) BurstPoint(int slot, int count)
    {
        var (width, height) = count > 8 ? (330f, 250f) : (300f, 200f);
        var (sin, cos) = MathF.SinCos(slot * 2f * MathF.PI / count);
        return (sin * width, -cos * height);
    }

    public override void RenderRadial(RadialView view)
    {
        var accent = Look.AccentOr(Accent);
        var count = view.Slots.Count;
        var grow = Kit.EaseOut(view.Age / 0.2f);
        var arrived = MathF.Min(view.Age, view.PageAge);

        PlaceRadial(view);
        Draw.Fade(1f);
        BurstLine(view, count, accent, arrived);

        var lowest = (CoreHead + CoreBody + CoreStripe + 2f * Gap) / 2f;
        for (var i = 0; i < count; i++)
        {
            lowest = MathF.Max(lowest, BurstBlade(view, i, count, accent, arrived));
        }

        BurstCore(view, grow, accent);
        BurstNote(view, lowest + 26f, grow);
        Draw.Fade(1f);
    }

    // A slab leaning like the menu's, given by its top edge. The bottom edge sits further left.
    static void Blade(float left, float right, float top, float height, Rgba color)
    {
        if (right <= left)
        {
            return;
        }

        var lean = Slant * height;
        Draw.Quad(left, top, right, top, right - lean, top + height, left - lean, top + height, color);
    }

    // How far a slab has grown out, 0 to 1. Each starts a moment after the one before.
    static float BladeGrow(float arrived, int index) => Kit.EaseOut((arrived - index * 0.03f) / 0.22f);

    // Where a slab's near end sits, pushed out while it is pointed at.
    static (float X, float Y) BladeSpot(RadialView view, int i, int count, float grow)
    {
        var (x, y) = BurstPoint(i, count);
        var length = MathF.Sqrt(x * x + y * y);
        var reach = length * (0.55f + 0.45f * grow) + view.Lifts[i] * BladePunch;
        return (x / length * reach, y / length * reach);
    }

    // The red line from the middle out to the slab pointed at, drawn under everything.
    static void BurstLine(RadialView view, int count, Rgba accent, float arrived)
    {
        if (view.Hover < 0)
        {
            return;
        }

        var (x, y) = BladeSpot(view, view.Hover, count, BladeGrow(arrived, view.Hover));
        var length = MathF.Sqrt(x * x + y * y);
        var (sx, sy) = (-y / length * BurstLineWidth / 2f, x / length * BurstLineWidth / 2f);
        Draw.Quad(sx, sy, x + sx, y + sy, x - sx, y - sy, -sx, -sy, accent);
    }

    // One slab and its chip. Returns where its bottom edge is.
    static float BurstBlade(RadialView view, int i, int count, Rgba accent, float arrived)
    {
        var grow = BladeGrow(arrived, i);
        if (grow <= 0f)
        {
            return float.MinValue;
        }

        var feature = view.Slots[i];
        var height = BladeHeight(count);
        var over = i == view.Hover;
        var changed = RadialView.Lit(feature);
        var (x, y) = BladeSpot(view, i, count, grow);
        var (chipText, chipFill, chipInk) = BladeChip(view, feature, over, accent);

        // A long value, like a choice's, gets smaller text and a wider chip.
        var chipSize = Draw.TextWidth(chipText, 13f) > BladeChipMin - 12f ? 11f : 13f;
        var chip = MathF.Max(BladeChipMin, Draw.TextWidth(chipText, chipSize) + 18f) * grow;
        var body = (BladeNameLeft + Draw.TextWidth(feature.Plain, 15f) + BladePad) * grow;
        var total = body + Gap + chip;

        var (endX, _) = BurstPoint(i, count);
        var left = endX > 1f ? x : endX < -1f ? x - total : x - total / 2f;
        var top = y - height / 2f;

        var flash = view.Flashed == i && view.FlashAge < BurstFlash;
        Blade(left, left + body, top, height, flash ? accent : over ? White : Dark);
        Blade(left + body + Gap, left + total, top, height, chipFill);

        if (grow < 0.5f)
        {
            return top + height;
        }

        FadeWith(grow);
        var lean = Slant * height / 2f;
        Draw.Text(Kit.Number(view.First + i + 1), left + BladeNumberLeft - lean, top + (height - 10f) / 2f, 10f, changed ? (over ? Ink : accent) : over ? Ink : Ghost);
        Draw.Text(feature.Plain, left + BladeNameLeft - lean, top + (height - 15f) / 2f, 15f, over && !flash ? Ink : White);
        var chipMiddle = left + body + Gap + chip / 2f - lean;
        Draw.Text(chipText, chipMiddle - Draw.TextWidth(chipText, chipSize) / 2f, top + (height - chipSize) / 2f, chipSize, chipInk);
        Draw.Fade(1f);
        return top + height;
    }

    // The chip at the end of a slab, coloured like the menu's chips.
    static (string Text, Rgba Fill, Rgba Ink) BladeChip(RadialView view, Feature feature, bool over, Rgba accent)
    {
        var state = Rgba.Mix(accent, White, feature.Glow(view.Now));
        switch (feature)
        {
            case Toggle toggle:
                return toggle.On ? (toggle.ValueText, state, Ink) : (toggle.ValueText, Darker, Ghost);
            case Choice choice:
                return choice.IsDefault ? (choice.ValueText, Darker, Soft) : (choice.ValueText, state, Ink);
            case Command command when command.Showing(view.Now, out var outcome):
                // Red is the accent and reads as failure too, so results use white and yellow.
                return (outcome.Text, outcome.Kind switch
                {
                    OutcomeKind.Done => White,
                    OutcomeKind.Failed => Warning,
                    _ => Dim,
                }, Ink);
            case Command command:
                return over ? (command.Cap, state, Ink) : (command.Cap, Darker, Ghost);
            default:
                return (feature.ValueText, Darker, Ghost);
        }
    }

    // The middle: a red header slab, a dark slab saying what letting go will do, and the hazard
    // strip. The three are stacked on one lean, centred on the middle of the screen.
    static void BurstCore(RadialView view, float grow, Rgba accent)
    {
        const float stack = CoreHead + CoreBody + CoreStripe + 2f * Gap;
        var width = CoreWidth * (0.3f + 0.7f * grow);
        var top = -stack / 2f;
        var left = -width / 2f + Slant * stack / 2f;
        var bodyTop = top + CoreHead + Gap;
        var bodyLeft = left - Slant * (bodyTop - top);
        var stripeTop = bodyTop + CoreBody + Gap;
        var stripeLeft = left - Slant * (stripeTop - top);

        Blade(left, left + width, top, CoreHead, accent);
        Blade(bodyLeft, bodyLeft + width, bodyTop, CoreBody, Dark);

        // Hazard bars along the strip, leaning harder than the slabs. A lit group runs along it.
        const int bars = 14;
        var pitch = width / bars;
        var head = (int)(view.Time * 16f) % (bars + 8);
        for (var i = 0; i < bars; i++)
        {
            var x = stripeLeft + i * pitch;
            var lit = i <= head && i > head - 4;
            Draw.Quad(x + CoreStripe, stripeTop, x + CoreStripe + pitch * 0.55f, stripeTop, x + pitch * 0.55f, stripeTop + CoreStripe, x, stripeTop + CoreStripe, lit ? accent : Darker);
        }

        if (grow < 0.5f)
        {
            return;
        }

        FadeWith(grow);
        var headLean = Slant * CoreHead / 2f;
        Draw.Tracked("FAVOURITES", left + 12f - headLean, top + 7f, 11f, 3f, Ink);
        if (view.Pages > 1)
        {
            var x = Draw.TextRight(Kit.Number(view.Pages), left + width - 12f - headLean, top + 7f, 11f, Ink);
            x = Draw.TextRight("/", x - 4f, top + 7f, 11f, Ink);
            Draw.TextRight(Kit.Number(view.Page + 1), x - 4f, top + 7f, 11f, Ink);
        }

        var feature = view.Focus;
        var action = feature switch
        {
            null => view.Tap ? "POINT AT A SLAB" : "LET GO TO CANCEL",
            Toggle toggle => toggle.On ? "SWITCHES IT OFF" : "SWITCHES IT ON",
            Choice => "SCROLL TO CHANGE",
            _ => "RUNS IT",
        };

        Draw.Tracked(action, bodyLeft + 12f - Slant * CoreBody / 2f, bodyTop + 10f, 9f, 2f, feature is null ? Dim : White);
        Draw.Fade(1f);
    }

    // Under the burst, a slab with the name, hotkey and help of the slab pointed at, and a slab of
    // keys under that. It is sized for the longest help on the page, so it doesn't change size as
    // the pointer moves.
    static void BurstNote(RadialView view, float top, float grow)
    {
        const float nameTop = 10f;
        const float helpTop = 34f;
        const float helpLine = 17f;
        var helpWidth = NoteWidth - 2f * Inset - 20f;
        var idle = view.Tap ? BurstTapHelp : BurstHoldHelp;

        var room = Draw.Wrap(idle, 13f, helpWidth).Length;
        foreach (var slot in view.Slots)
        {
            room = Math.Max(room, Draw.Wrap(slot.Help, 13f, helpWidth).Length);
        }

        room = Math.Clamp(room, 1, NoteLines);
        var height = helpTop + (room - 1) * helpLine + 13f + 12f;
        var left = -NoteWidth / 2f + Slant * height / 2f;
        var hintsTop = top + height + Gap;
        var hintsLeft = left - Slant * (height + Gap);

        // Like the menu's slabs, these grow out from their right ends.
        var cut = NoteWidth * (1f - grow);
        Blade(left + cut, left + NoteWidth, top, height, Dark);
        Blade(hintsLeft + cut, hintsLeft + NoteWidth, hintsTop, NoteHints, Dark);

        if (grow < 0.5f)
        {
            return;
        }

        FadeWith(grow);
        float LeanAt(float y) => Slant * (y - top);
        var feature = view.Focus;
        var right = left + NoteWidth - Inset;
        if (feature is null)
        {
            Draw.Tracked("NOTHING PICKED", left + Inset - LeanAt(top + nameTop + 7f), top + nameTop + 3f, 10f, 2f, Ghost);
        }
        else
        {
            var nameRight = right - LeanAt(top + nameTop + 7f);
            var key = Hotkeys.KeyText(feature);
            if (key.Length > 0)
            {
                var capWidth = Kit.CapWidth(key, 9f);
                Kit.Cap(key, nameRight - capWidth, top + nameTop + 7f, 9f, Ghost, Soft);
                nameRight -= capWidth + 12f;
            }

            ToastText(feature.Plain, left + Inset - LeanAt(top + nameTop + 7f), top + nameTop, 15f, White, nameRight);
        }

        var lines = Draw.Wrap(feature?.Help ?? idle, 13f, helpWidth);
        for (var k = 0; k < lines.Length && k < room; k++)
        {
            var lineTop = top + helpTop + k * helpLine;
            Draw.Text(lines[k], left + Inset - LeanAt(lineTop + 6f), lineTop, 13f, feature is null ? Dim : Soft);
        }

        Kit.HintRow(view.Hints, hintsLeft + NoteWidth / 2f - Slant * NoteHints / 2f, hintsTop + NoteHints / 2f, 10f, Ghost, Soft, Dim);
        Draw.Fade(1f);
    }
}
