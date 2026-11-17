#nullable enable

namespace Knightfall;

// Two wings of solid blades off a blue spine: the pages on the left wing, the menu on the right.
// Every blade ends in a point, and the points get shorter down the stack like feathers. Blades are
// triangles, which the game always draws solid, so nothing here fades. The wings unfurl from the
// spine, and live current runs along whatever is picked.
sealed class NightwingTheme : Theme
{
    const float Margin = 40f;
    const float Width = 440f;
    const float Inset = 20f;
    const float Gap = 3f;

    const float HeaderHeight = 44f;
    const float StripeHeight = 5f;
    const float TabsHeight = 24f;
    const float TabLean = 6f;
    const float RowHeight = 30f;
    const float MeterWidth = 96f;
    const float ValueRoom = 54f;
    const float HelpLineHeight = 17f;
    const float StatusHeight = 24f;
    const float Punch = 12f;
    const int MaxLines = 12;
    const int HelpLines = 3;

    // The pages hang off the other side of the spine, pointing left.
    const float SpineGap = 12f;
    const float SpineLeft = -7f;
    const float PagesHeight = 26f;
    const float PagePad = 12f;
    const float PageNameLeft = 38f;
    const float PageSize = 14f;
    const float PagePunch = 8f;
    const float ColumnMinWidth = 124f;

    // How far a blade's point reaches past its end. The top blade has the longest and they
    // shorten down to the shortest by the time the stack is this tall.
    const float TipLong = 54f;
    const float TipShort = 10f;
    const float TipFall = 600f;

    // How many times a second the current takes a new shape.
    const float ZapRate = 20f;

    static readonly Rgba Dark = new(11, 14, 24);
    static readonly Rgba Darker = new(22, 28, 44);
    static readonly Rgba Blue = new(36, 150, 255);
    static readonly Rgba White = new(255, 255, 255);
    static readonly Rgba Soft = new(204, 222, 246);
    static readonly Rgba Dim = new(146, 164, 194);
    static readonly Rgba Ghost = new(88, 104, 134);
    static readonly Rgba Ink = new(6, 10, 20);
    static readonly Rgba Empty = new(40, 48, 68);
    static readonly Rgba EmptyLine = new(255, 255, 255, 36);
    static readonly Rgba EmptyOnBlue = new(0, 0, 0, 66);

    // Anything switched on is the colour of the current.
    static readonly Rgba Live = new(120, 236, 255);

    static readonly HintStyle KeyHints = new(Size: 10f, CapHeight: 17f, LineHeight: 22f, Frame: Ghost, Key: Soft, Label: Dim);

    public override string Id => "nightwing";

    public override string Name => "NIGHTWING";

    public override Rgba Accent => Blue;

    public override Rgba Armed => Live;

    public override void Render(MenuView view)
    {
        var accent = Look.AccentOr(Accent);
        var zoom = Look.Zoom;
        var open = view.Opened;
        var column = ColumnWidth(view);

        // Docked to the top left. The left wing's points and its picked page reach out past it.
        var reach = TipLong + PagePunch;
        Draw.Place(Margin + (reach + column + SpineGap) * zoom + MoveX, Margin + MoveY, zoom);

        // Heights are worked out first, so the spine can be drawn to the foot of the stack.
        var visible = Math.Clamp(view.Lines.Count, 1, MaxLines);
        var stripeTop = HeaderHeight + Gap;
        var tabsTop = stripeTop + StripeHeight + Gap;
        var rowsTop = tabsTop + TabsHeight + Gap + 3f;
        var rowsBottom = rowsTop + visible * (RowHeight + Gap) - Gap;
        var helpTop = rowsBottom + Gap + 3f;
        var helpHeight = HelpLines * HelpLineHeight + 16f;
        var hintsTop = helpTop + helpHeight + Gap;
        var hintLines = Math.Max(1, HintLines(view, Inset, Width - Inset - 10f, KeyHints));
        var hintsHeight = (hintLines - 1) * KeyHints.LineHeight + KeyHints.CapHeight + 14f;
        var statusTop = hintsTop + hintsHeight + Gap;
        var bottom = statusTop + StatusHeight;

        var columnBottom = tabsTop + view.Pages.Count * (PagesHeight + Gap);
        var tallest = bottom + (MaxLines - visible) * (RowHeight + Gap);
        Covers(-(SpineGap + column) - reach, 0f, Width + Punch + TipLong + 30f, MathF.Max(bottom, columnBottom), MathF.Max(tallest, columnBottom));

        Draw.Fade(1f);
        Spine(view, accent, bottom, rowsTop, rowsBottom, visible);

        var grow = Unfurl(view, 0f);
        Header(view, grow, accent);
        Stripe(view, Unfurl(view, stripeTop), stripeTop, accent);
        Tabs(view, tabsTop, Unfurl(view, tabsTop), accent);
        Rows(view, rowsTop, visible, accent);
        Help(view, helpTop, helpHeight, Unfurl(view, helpTop));
        Hints(view, hintsTop, hintsHeight, Unfurl(view, hintsTop));
        Status(statusTop, Unfurl(view, statusTop), accent);
        Column(view, column, tabsTop, accent);
        Draw.Fade(1f);
    }

    // How far a blade at this height has grown out of the spine, 0 to 1. The spine draws first,
    // then the feathers unfurl from the top down. Closing pulls them all in together.
    static float Unfurl(MenuView view, float y)
    {
        return view.Closing ? Kit.EaseOut(view.Opened * 1.4f) : Kit.EaseOut((view.Opened - 0.18f - y / 700f * 0.4f) / 0.36f);
    }

    static float Tip(float y)
    {
        return TipShort + (TipLong - TipShort) * MathF.Pow(Math.Clamp(1f - y / TipFall, 0f, 1f), 1.5f);
    }

    // A blade pointing right. It is flat on the left and its top edge runs on to a point. It
    // grows out from its left end.
    static void Blade(float left, float right, float y, float height, float grow, Rgba color, float tip)
    {
        grow = MathF.Min(grow, 1f);
        if (grow <= 0f || right <= left)
        {
            return;
        }

        var end = left + (right - left) * grow;
        Draw.Quad(left, y, end + tip * grow, y, end, y + height, left, y + height, color);
    }

    // The same blade pointing left, growing out from its right end.
    static void BladeLeft(float left, float right, float y, float height, float grow, Rgba color, float tip)
    {
        grow = MathF.Min(grow, 1f);
        if (grow <= 0f || right <= left)
        {
            return;
        }

        var start = right - (right - left) * grow;
        Draw.Quad(start - tip * grow, y, right, y, right, y + height, start, y + height, color);
    }

    // Text and small parts come in over the second half of a blade's growth.
    static void FadeWith(float grow)
    {
        Draw.Fade(Math.Clamp(grow * 2f - 1f, 0f, 1f));
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

    // Live current between two points: a jagged line that takes a new shape many times a second.
    // Each piece is drawn twice, half a unit apart, so it holds up on a big screen.
    static void Zap(float x0, float y0, float x1, float y1, float reach, int seed, float time, Rgba color)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 2f)
        {
            return;
        }

        var pieces = Math.Max(3, (int)(length / 13f));
        var frame = (int)(time * ZapRate);
        var (sideX, sideY) = (-dy / length, dx / length);
        var (fromX, fromY) = (x0, y0);
        for (var i = 1; i <= pieces; i++)
        {
            var along = (float)i / pieces;
            var jump = i == pieces ? 0f : (Noise(seed + i * 17 + frame * 131) - 0.5f) * 2f * reach;
            var x = x0 + dx * along + sideX * jump;
            var y = y0 + dy * along + sideY * jump;
            Draw.Line(fromX, fromY, x, y, color);
            Draw.Line(fromX + 0.5f, fromY + 0.5f, x + 0.5f, y + 0.5f, color);
            (fromX, fromY) = (x, y);
        }
    }

    // True on some frames and not others, for a spark that flickers.
    static bool Flicker(float time, int seed, float share) => Noise(seed + (int)(time * ZapRate) * 37) < share;

    // The mark: a wide swept V with a small head over it.
    static void Mark(float x, float y, float scale, Rgba color)
    {
        Draw.Quad(x - 17f * scale, y - 7f * scale, x - 10f * scale, y - 7f * scale, x, y + 2f * scale, x, y + 9f * scale, color);
        Draw.Quad(x + 17f * scale, y - 7f * scale, x + 10f * scale, y - 7f * scale, x, y + 2f * scale, x, y + 9f * scale, color);
        Draw.Quad(x, y - 10.5f * scale, x + 2.5f * scale, y - 7f * scale, x, y - 3.5f * scale, x - 2.5f * scale, y - 7f * scale, color);
    }

    // The blue line both wings hang from. It draws down first. A white pulse runs down it, or
    // on a long list a white length of it shows where you are.
    static void Spine(MenuView view, Rgba accent, float height, float rowsTop, float rowsBottom, int visible)
    {
        var drawn = view.Closing ? Kit.EaseOut(view.Opened * 1.4f) : Kit.EaseOut(view.Opened / 0.22f);
        var length = height * drawn;
        Draw.Rect(SpineLeft, 0f, 2f, length, accent);
        if (drawn < 1f)
        {
            Draw.Rect(SpineLeft - 2f, length - 4f, 6f, 6f, White);
            return;
        }

        if (view.Lines.Count > visible)
        {
            var (first, _, _) = Scroll(view, visible);
            var track = rowsBottom - rowsTop;
            Draw.Rect(SpineLeft - 1f, rowsTop + track * first / view.Lines.Count, 4f, track * visible / view.Lines.Count, White);
            return;
        }

        var at = view.Time * 260f % (height + 400f);
        if (at < height)
        {
            Draw.Rect(SpineLeft - 1f, at, 4f, MathF.Min(26f, height - at), White);
        }
    }

    static void Header(MenuView view, float grow, Rgba accent)
    {
        Blade(0f, Width, 0f, HeaderHeight, grow, Dark, Tip(0f));
        if (grow >= 1f)
        {
            Mark(Inset + 13f, 23f, 1f, accent);

            // Now and then current jumps across the mark.
            if (view.Time % 2.9f < 0.16f)
            {
                Draw.Fade(1f);
                Zap(Inset - 5f, 16f, Inset + 31f, 16f, 4f, 77, view.Time, White);
            }
        }

        FadeWith(grow);
        Draw.Tracked("KNIGHTFALL", Inset + 40f, 9f, 15f, 5f, White);
        Draw.Tracked("NIGHTWING", Inset + 41f, 29f, 9f, 6f, accent);
        Draw.TextRight(Kit.Clock, Width - Inset, 17f, 11f, Soft);
        Draw.Fade(1f);
    }

    // A thin blue blade under the header. A white pulse races along it and sparks off its point.
    static void Stripe(MenuView view, float grow, float y, Rgba accent)
    {
        const float tip = 34f;
        const float pulse = 40f;
        Blade(0f, Width, y, StripeHeight, grow, accent, tip);
        if (grow < 1f)
        {
            return;
        }

        var at = view.Time * 520f % (Width + 900f);
        if (at < Width)
        {
            var end = MathF.Min(at + pulse, Width + tip * 0.5f);
            Draw.Quad(at, y, end, y, end - 1f, y + StripeHeight, at - 1f, y + StripeHeight, White);
        }
        else if (at < Width + 90f)
        {
            Draw.Fade(1f);
            Zap(Width + tip, y, Width + tip + 22f, y - 8f + 14f * Noise((int)(view.Time * ZapRate)), 3f, 31, view.Time, White);
        }
    }

    // A tab is a box that leans forward. X is its bottom left corner.
    static void TabBox(float x, float width, float y, float grow, Rgba color)
    {
        grow = MathF.Min(grow, 1f);
        if (grow <= 0f || width <= 0f)
        {
            return;
        }

        var shown = width * grow;
        Draw.Quad(x + TabLean, y, x + TabLean + shown, y, x + shown, y + TabsHeight, x, y + TabsHeight, color);
    }

    static void Tabs(MenuView view, float y, float grow, Rgba accent)
    {
        const float size = 11f;
        const float spacing = 1.5f;
        const float pad = 14f;
        const float more = Kit.MoreWidth + 16f;
        const float edge = 6f;
        var lean = TabLean / 2f;
        var middle = y + TabsHeight / 2f;
        var tabs = view.Page.Tabs;

        var prevWidth = Kit.CapWidth(view.TabPrevKey, 9f) + 20f;
        var nextWidth = Kit.CapWidth(view.TabNextKey, 9f) + 20f;
        TabBox(0f, prevWidth - TabLean, y, grow, Dark);
        TabBox(Width - nextWidth, nextWidth - TabLean, y, grow, Dark);

        var left = prevWidth + Gap;
        var fillRight = Width - nextWidth - Gap;
        var strip = FitTabs(view, left, fillRight, size, spacing, 2f * pad, Gap, more + Gap);
        var before = strip.First;
        var after = tabs.Count - strip.Last;
        var x = strip.Start;

        // Tabs hidden off the left get a box of their own. It shows the cut off name of the tab
        // before, or a small count when that doesn't fit.
        if (before > 0)
        {
            var boxRight = x - Gap;
            TabBox(left, boxRight - left - TabLean, y, grow, Dark);
            FadeWith(grow);
            if (!Draw.TrackedTail(tabs[strip.First - 1].Name, boxRight - pad, y + 7f, size, spacing, Ghost, left + edge + lean))
            {
                Kit.More(left + 8f + lean, middle, before, left: true, accent, Dim);
            }
        }

        for (var i = strip.First; i < strip.Last; i++)
        {
            var current = i == view.TabIndex;
            var width = Draw.TrackedWidth(tabs[i].Name, size, spacing) + 2f * pad;
            TabBox(x, width - TabLean, y, grow, current ? accent : Dark);

            FadeWith(grow);
            Draw.Tracked(tabs[i].Name, x + pad, y + 7f, size, spacing, current ? White : Ghost);

            // The tab just turned to takes a jolt along its top.
            if (current && grow >= 1f && view.Arrived < 0.25f)
            {
                Draw.Fade(1f - view.Arrived / 0.25f);
                Zap(x + TabLean, y, x + width, y, 3f, i, view.Time, White);
            }

            x += width + Gap;
        }

        // Whatever room is left becomes one more box. With tabs hidden off the right, it shows
        // the cut off name of the next one, or a small count when that doesn't fit.
        if (fillRight - x > 8f + TabLean)
        {
            TabBox(x, fillRight - x - TabLean, y, grow, Dark);
            FadeWith(grow);
            if (after > 0 && !Draw.TrackedHead(tabs[strip.Last].Name, x + pad, y + 7f, size, spacing, Ghost, fillRight - edge - lean))
            {
                Kit.More(fillRight - 8f - Kit.MoreWidth - lean, middle, after, left: false, accent, Dim);
            }
        }

        FadeWith(grow);
        Kit.Cap(view.TabPrevKey, 10f + lean, middle, 9f, Ghost, Dim);
        Kit.Cap(view.TabNextKey, Width - nextWidth + 10f + lean, middle, 9f, Ghost, Dim);
        Draw.Fade(1f);
    }

    static void Rows(MenuView view, float top, int visible, Rgba accent)
    {
        var lines = view.Lines;
        if (view.Rows.Count == 0)
        {
            return;
        }

        var (first, last, _) = Scroll(view, visible);

        // The picked blade pushes out past the others each time the selection lands on it.
        var settled = 1f - MathF.Min(1f, MathF.Abs(view.Glide - view.RowIndex));

        for (var i = first; i < last; i++)
        {
            // Each line follows the one above it. Closing pulls them in together.
            var y = top + (i - first) * (RowHeight + Gap);
            var grow = view.Closing ? Unfurl(view, y) : MathF.Min(Unfurl(view, y), Kit.Cascade(view.Arrived, i - first + 2));
            var line = lines[i];
            if (line.Row is { } row)
            {
                var selected = line.RowIndex == view.RowIndex;
                Row(row, line.RowIndex, y, selected, selected ? settled * Punch : 0f, grow, accent, view.Now, view.Time);
            }
            else
            {
                Heading(line.Heading!, y, grow, accent);
            }
        }

        Draw.Fade(1f);
    }

    // A gap in the stack with the name in it. A short blue blade marks it and a thin dark one
    // runs on from the name. It sits a little low, so it reads as part of the rows under it.
    static void Heading(string text, float y, float grow, Rgba accent)
    {
        var middle = y + RowHeight / 2f + 3f;
        Blade(0f, 9f, middle - 5f, 10f, grow, accent, 6f);

        FadeWith(grow);
        var end = Draw.Tracked(text, Inset, middle - 5f, 10f, 3f, White);
        Blade(end + 10f, Width, middle - 1f, 2f, grow, Darker, 8f);
    }

    static void Row(Feature feature, int index, float y, bool selected, float punch, float grow, Rgba accent, long now, float time)
    {
        var changed = !feature.Preference && !feature.IsDefault;
        var glow = feature.Glow(now);
        var tip = Tip(y) + (selected ? 6f : 0f);
        var end = Width + punch;
        var right = Width - Inset;

        // A changed value turns the whole blade white for a moment.
        Blade(0f, end, y, RowHeight, grow, selected ? Rgba.Mix(accent, White, glow * 0.6f) : Dark, tip);

        if (selected && grow >= 1f)
        {
            // Current runs along the top of the picked blade and sparks off its point.
            Draw.Fade(1f);
            Zap(6f, y, end + tip, y, 2.2f, index * 9, time, Live);
            if (Flicker(time, index, 0.55f))
            {
                var frame = (int)(time * ZapRate);
                Zap(end + tip, y, end + tip + 10f + 16f * Noise(frame), y - 9f + 12f * Noise(frame + 5), 3f, index * 5 + 2, time, White);
            }

            if (glow > 0f)
            {
                Draw.Fade(glow);
                Zap(0f, y + RowHeight, end, y + RowHeight, 3.5f, index * 3 + 1, time, White);
                Zap(0f, y, end + tip, y, 4f, index * 3 + 7, time, White);
            }
        }

        FadeWith(grow);
        var state = Rgba.Mix(selected ? White : changed ? Live : accent, White, glow);
        Draw.Text(Kit.Number(index + 1), Inset, y + 10f, 10f, changed ? (selected ? Ink : Live) : selected ? White with { A = 180 } : Ghost);

        // A row that can't be used right now steps back, with the reason where its value goes.
        if (feature.Blocked is { } why)
        {
            var faint = selected ? White with { A = 160 } : Ghost;
            Draw.Text(feature.Label, Inset + 28f, y + 7f, 15f, faint);
            Kit.Locked(why, right, y + 10f, 10f, faint, faint, Inset + 28f + Draw.TextWidth(feature.Label, 15f) + 16f);
            return;
        }

        Draw.Text(feature.Label, Inset + 28f, y + 7f, 15f, White);
        var value = selected ? White : changed ? state : White;

        switch (feature)
        {
            case Toggle toggle:
            {
                // Two sticks. Switched on, current jumps between them.
                var x = right - 26f;
                var off = selected ? Rgba.Mix(accent, White, 0.4f) : Ghost;
                if (grow > 0.5f)
                {
                    Stick(x, y + 7f, toggle.On ? state : off);
                    Stick(x + 18f, y + 7f, toggle.On ? state : off);
                }

                if (toggle.On && grow >= 1f)
                {
                    var frame = (int)(time * 9f) + index;
                    Draw.Fade(1f);
                    Zap(x + 6f, y + 9f + 8f * Noise(frame), x + 20f, y + 11f + 9f * Noise(frame + 3), 3.2f, index * 4, time, selected ? Ink : White);
                    FadeWith(grow);
                }

                Draw.TextRight(toggle.ValueText, x - 10f, y + 10f, 11f, toggle.On ? state : off);
                break;
            }

            case Slider slider:
                Draw.TextRight(slider.ValueText, right, y + 7f, 15f, value);
                if (grow > 0.5f)
                {
                    LeanMeter(right - ValueRoom - MeterWidth, y + 11f, MeterWidth, 8f, 16, slider.Fill, Kit.Lit(slider, state), Kit.Unlit(slider, selected ? Rgba.Mix(accent, Ink, 0.45f) : Empty));
                    Draw.Rect(right - ValueRoom - MeterWidth + slider.DefaultFill * MeterWidth - 0.5f, y + 21f, 1f, 3f, selected ? White : Dim);
                }

                break;

            case Choice choice:
                if (selected)
                {
                    var x = Draw.TextRight(">", right, y + 6f, 13f, White);
                    x = Draw.TextRightFit(choice.ValueText, x - 8f, y + 6f, 13f, White, Inset + 28f + Draw.TextWidth(feature.Label, 15f) + 16f + Draw.TextWidth("<", 13f) + 8f);
                    Draw.TextRight("<", x - 8f, y + 6f, 13f, White);
                }
                else
                {
                    Draw.TextRightFit(choice.ValueText, right, y + 6f, 13f, value, Inset + 28f + Draw.TextWidth(feature.Label, 15f) + 16f);
                }

                Kit.Pick(choice, right, y + 24f, state, selected ? EmptyOnBlue : EmptyLine);
                break;

            case Command command:
            {
                // After a press the cap shows what happened, filled with its own colour.
                var shown = command.Showing(now, out var outcome);
                var text = shown ? outcome.Text : command.Cap;
                var width = Draw.TextWidth(text, 10f) + 16f;
                if (shown)
                {
                    Draw.Rect(right - width, y + 7f, width, 16f, Kit.Tone(outcome.Kind, Live, Dim));
                    Draw.Text(text, right - width + 8f, y + 10f, 10f, Ink);
                }
                else if (selected)
                {
                    Draw.Rect(right - width, y + 7f, width, 16f, White);
                    Draw.Text(text, right - width + 8f, y + 10f, 10f, accent);
                }
                else
                {
                    Draw.Frame(right - width, y + 7f, width, 16f, 1f, Ghost);
                    Draw.Text(text, right - width + 8f, y + 10f, 10f, Dim);
                }

                break;
            }
        }
    }

    // One escrima stick, leaning.
    static void Stick(float x, float y, Rgba color)
    {
        Draw.Quad(x + 4f, y, x + 7.5f, y, x + 3.5f, y + 16f, x, y + 16f, color);
    }

    // A row of leaning blocks that fill up with the value.
    static void LeanMeter(float x, float y, float width, float height, int blocks, float fill, Rgba lit, Rgba empty)
    {
        const float gap = 2f;
        const float lean = 3f;
        var block = (width - gap * (blocks - 1)) / blocks;
        var count = (int)MathF.Round(fill * blocks);
        for (var i = 0; i < blocks; i++)
        {
            var left = x + i * (block + gap);
            Draw.Quad(left + lean, y, left + block + lean, y, left + block, y + height, left, y + height, i < count ? lit : empty);
        }
    }

    static void Help(MenuView view, float y, float height, float grow)
    {
        Blade(0f, Width, y, height, grow, Dark, Tip(y));

        var help = view.Focus?.Help;
        if (string.IsNullOrEmpty(help))
        {
            return;
        }

        FadeWith(grow);
        var lines = Draw.Wrap(help, 13f, Width - 2f * Inset - 10f);
        for (var i = 0; i < lines.Length && i < HelpLines; i++)
        {
            Draw.Text(lines[i], Inset, y + 9f + i * HelpLineHeight, 13f, Soft);
        }
    }

    static void Hints(MenuView view, float y, float height, float grow)
    {
        Blade(0f, Width, y, height, grow, Dark, Tip(y));
        FadeWith(grow);
        DrawHints(view, Inset, Width - Inset - 10f, y + 7f, KeyHints);
    }

    // The bottom blade. How many options are switched on, and the mod version.
    static void Status(float y, float grow, Rgba accent)
    {
        var active = Catalog.Active;
        Blade(0f, Width, y, StatusHeight, grow, Dark, Tip(y));

        FadeWith(grow);
        if (active > 0)
        {
            Draw.Rect(Inset, y + 9f, 6f, 6f, Live);
        }
        else
        {
            Draw.Frame(Inset, y + 9f, 6f, 6f, 1f, Ghost);
        }

        var x = Draw.Tracked("ACTIVE", Inset + 12f, y + 7f, 10f, 2f, Ghost);
        Draw.Text(Kit.Number(active), x + 8f, y + 7f, 10f, active > 0 ? Live : Ghost);
        Draw.TextRight(Kit.Version, Width - Inset, y + 7f, 10f, Ghost);
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

    // The left wing: a blade with the keys that turn the pages, level with the header, then every
    // page as a blade of its own, numbered. The current one is blue and reaches out further.
    static void Column(MenuView view, float width, float top, Rgba accent)
    {
        var right = -SpineGap;
        var left = right - width;

        var grow = Unfurl(view, 0f);
        BladeLeft(left, right, 0f, HeaderHeight, grow, Dark, Tip(0f));
        FadeWith(grow);
        Draw.Tracked("PAGES", left + PagePad, 16f, 11f, 3f, Soft);
        var next = Kit.CapWidth(view.PageNextKey, 9f);
        Kit.Cap(view.PageNextKey, right - PagePad - next, HeaderHeight / 2f, 9f, Ghost, Dim);
        Kit.Cap(view.PagePrevKey, right - PagePad - next - 4f - Kit.CapWidth(view.PagePrevKey, 9f), HeaderHeight / 2f, 9f, Ghost, Dim);

        BladeLeft(left, right, HeaderHeight + Gap, StripeHeight, Unfurl(view, HeaderHeight + Gap), accent, 34f);

        var landed = Kit.EaseOut(view.Arrived / 0.2f);
        for (var i = 0; i < view.Pages.Count; i++)
        {
            var y = top + i * (PagesHeight + Gap);
            var current = i == view.PageIndex;
            grow = Unfurl(view, y);
            BladeLeft(left - (current ? PagePunch * landed : 0f), right, y, PagesHeight, grow, current ? accent : Dark, Tip(y));

            if (current && grow >= 1f && view.Arrived < 0.3f)
            {
                Draw.Fade(1f - view.Arrived / 0.3f);
                Zap(right, y, left - Tip(y) - PagePunch, y, 3f, i + 40, view.Time, White);
            }

            FadeWith(grow);
            Draw.Text(Kit.Number(i + 1), left + PagePad, y + 8f, 10f, current ? White : Ghost);
            Draw.Text(view.Pages[i].Name, left + PageNameLeft, y + 6f, PageSize, current ? White : Dim);
        }
    }

    public override float ToastWidth => 250f;

    // The card and the timer blade under it.
    public override float ToastHeight => 38f;

    // Each notification is a blade off the edge of the screen it sits by, pointing in. A strip of
    // the outcome's colour runs down its point, current crackles over it as it lands, and a thin
    // blade under it shrinks with the time left.
    public override void RenderToasts(ToastView view)
    {
        const float card = 32f;
        const float tip = 14f;
        const float tag = 10f;
        const float timer = 3f;
        var width = ToastWidth;

        foreach (var toast in view.Cards)
        {
            PlaceToast(view, toast, 0f);
            var grow = Kit.EaseOut(toast.Shown);
            var tone = Kit.Tone(toast.Kind, Live, Ghost);
            float textLeft;

            Draw.Fade(1f);
            if (view.Right)
            {
                BladeLeft(tip, width, 0f, card, grow, Dark, tip);
                BladeLeft(width - (width - tip - 3f) * toast.Left, width, card + Gap, timer, grow, tone, 3f);
                if (grow >= 1f)
                {
                    Draw.Quad(0f, 0f, tag, 0f, tip + tag, card, tip, card, tone);
                }

                textLeft = tip + tag + 12f;
            }
            else
            {
                Blade(0f, width - tip, 0f, card, grow, Dark, tip);
                Blade(0f, (width - tip - 3f) * toast.Left, card + Gap, timer, grow, tone, 3f);
                if (grow >= 1f)
                {
                    Draw.Quad(width - tag, 0f, width, 0f, width - tip, card, width - tip - tag, card, tone);
                }

                textLeft = 14f;
            }

            // Current runs over it while it comes and goes.
            if (toast.Shown > 0.45f && toast.Shown < 1f)
            {
                Zap(0f, 0f, width, 0f, 3.5f, (int)(toast.Row * 31f) + 3, view.Time, White);
                Zap(tip, card, width - tip, card, 3f, (int)(toast.Row * 31f) + 9, view.Time, Live);
            }

            FadeWith(grow);
            Draw.TrackedHead(toast.Title, textLeft, 5f, 8f, 2.5f, Dim, width - tip - tag - 4f);
            ToastText(toast.Text, textLeft, 15f, 13f, toast.Kind == OutcomeKind.Failed ? Kit.Failed : White, width - tip - tag - 4f);
        }

        Draw.Fade(1f);
    }

    // The key guide as two blades, a title in the accent colour over the keys.
    public override void RenderGuide(GuideView view)
    {
        const float inset = 18f;
        const float head = 26f;
        var accent = Look.AccentOr(Accent);
        var grow = Kit.EaseOut(view.Shown);
        var width = MathF.Max(GuideWidth(view), Draw.TrackedWidth(view.Title, 10f, 3f)) + 2f * inset;
        var body = view.Lines.Count * GuideLineHeight + 12f;

        PlaceGuide(width + 20f, head + Gap + body);
        Draw.Fade(1f);
        Draw.Rect(SpineLeft, 0f, 2f, (head + Gap + body) * grow, accent);

        Blade(0f, width, 0f, head, grow, accent, 20f);
        FadeWith(grow);
        Draw.Tracked(view.Title, inset, 8f, 10f, 3f, Ink);

        var y = head + Gap;
        Blade(0f, width, y, body, grow, Dark, 12f);
        FadeWith(grow);
        GuideLines(view, inset, y + 6f, Ghost, Soft, Dim);
        Draw.Fade(1f);
    }

    // The speedometer is blades like the menu. On boost the header goes white and current runs
    // along it and the meter.
    public override void RenderDash(DashView view)
    {
        const float width = 250f;
        const float inset = 18f;
        const float head = 26f;
        const float body = 98f;
        const float stats = 26f;
        var accent = Look.AccentOr(Accent);
        var grow = Kit.EaseOut(view.Shown);
        var height = head + Gap + body + (view.Stats ? Gap + stats : 0f);
        var hot = view.Hot ? White : accent;

        if (!PlaceDash(view, width + 24f, height))
        {
            return;
        }

        Draw.Fade(1f);
        var y = 0f;
        Blade(0f, width, y, head, grow, hot, 22f);
        if (view.Hot && grow >= 1f)
        {
            Zap(0f, y, width + 22f, y, 3f, 5, view.Time, Live);
            Zap(0f, y + head, width, y + head, 3f, 9, view.Time, White);
        }

        FadeWith(grow);
        Draw.Tracked(view.Title, inset, y + 8f, 10f, 3f, Ink);
        if (view.Mode.Length > 0)
        {
            Draw.Tracked(view.Mode, width - inset - Draw.TrackedWidth(view.Mode, 10f, 3f), y + 8f, 10f, 3f, view.Hot ? accent : Ink);
        }

        y += head + Gap;
        Draw.Fade(1f);
        Blade(0f, width, y, body, grow, Dark, 16f);
        if (grow > 0.5f)
        {
            LeanMeter(inset, y + 52f, width - 2f * inset, 6f, 24, view.SpeedFill, hot, Empty);
            if (view.Hot)
            {
                Zap(inset, y + 51f, inset + (width - 2f * inset) * view.SpeedFill, y + 51f, 2.5f, 21, view.Time, Live);
            }
        }

        FadeWith(grow);
        DashSpeed(view, inset, y + 8f, White, Dim);
        DashNote(view, width - inset, y + 14f, Soft, Ghost);
        DashBar(inset, width - inset, y + 66f, "ARMOUR", view.Armour, view.ArmourText, Ghost, view.LowArmour ? Kit.Failed : accent, EmptyLine, view.LowArmour ? Kit.Failed : Soft);
        DashBar(inset, width - inset, y + 82f, "POWER", view.Power, view.PowerText, Ghost, White, EmptyLine, Soft);

        if (view.Stats)
        {
            y += body + Gap;
            Draw.Fade(1f);
            Blade(0f, width, y, stats, grow, Darker, 10f);
            FadeWith(grow);
            DashStat(inset, y + 7f, "BEST", view.Best, view.Units, Ghost, Soft);
            DashStat(width / 2f + 6f, y + 7f, "JUMP", view.Jump, "", Ghost, Soft);
        }

        Draw.Fade(1f);
    }

    // The radial as blades bursting out of a ringed mark. Blades on the right point right and
    // blades on the left point left, so long names grow away from the middle. The one pointed at
    // turns blue and pushes out, and a bolt jumps to it from the ring.
    const float BurstHub = 62f;
    const float BurstReach = 150f;
    const float BurstRing = 58f;
    const float SlotHeight = 30f;
    const float SlotNumberLeft = 12f;
    const float SlotNameLeft = 34f;
    const float SlotPad = 14f;
    const float SlotPunch = 12f;
    const float SlotTip = 16f;
    const float SlotFlash = 0.14f;
    const float NoteWidth = 460f;
    const float NoteHead = 26f;
    const float NoteHints = 30f;
    const int NoteLines = 2;

    const string TapHelp = "Point the mouse or the right stick at a blade to see what it does.";
    const string HoldHelp = "Letting go in the middle does nothing. Point at a blade to pick it.";

    static readonly Rgba Shade = new(0, 2, 8, 90);

    public override float RadialHub => BurstHub;

    public override float RadialReach => BurstReach;

    public override float RadialAngle(int slot, int count)
    {
        var (x, y) = BurstPoint(slot, count);
        return MathF.Atan2(x, -y) * 180f / MathF.PI;
    }

    // More blades get a wider oval, so neighbours near the top and bottom don't touch.
    static (float X, float Y) BurstPoint(int slot, int count)
    {
        var (width, height) = count > 8 ? (190f, 170f) : (150f, 128f);
        var (sin, cos) = MathF.SinCos(slot * 2f * MathF.PI / count);
        return (sin * width, -cos * height);
    }

    public override void RenderRadial(RadialView view)
    {
        var accent = Look.AccentOr(Accent);
        var count = view.Slots.Count;
        var grow = Kit.EaseOut(view.Age / 0.2f);
        var arrived = MathF.Min(view.Age, view.PageAge);

        Draw.Place(0f, 0f, 1f);
        Draw.Fade(grow);
        Draw.Rect(0f, 0f, Draw.ScreenWidth, Draw.ScreenHeight, Shade);

        PlaceRadial(view);
        Draw.Fade(1f);
        Shapes.Under = Dark;
        Shapes.Opacity = 1f;
        Shapes.Disc(BurstRing * grow, Dark);
        Shapes.Ring((BurstRing - 3f) * grow, BurstRing * grow, accent);

        // White dashes turn round the ring while letting go would cancel.
        if (view.Hover < 0 && grow >= 1f)
        {
            Shapes.Dashes(BurstRing + 3f, BurstRing + 6f, view.Time * 30f % 15f, 360f, 8f, 7f, White);
        }

        if (grow >= 1f)
        {
            Mark(0f, -2f, 2f, view.Hover < 0 ? Ghost : accent);
        }

        var highest = -BurstRing;
        var lowest = BurstRing;
        for (var i = 0; i < count; i++)
        {
            var (top, bottom) = BurstBlade(view, i, count, accent, arrived);
            highest = MathF.Min(highest, top);
            lowest = MathF.Max(lowest, bottom);
        }

        BurstTitle(view, highest - 36f, grow, accent);
        BurstNote(view, lowest + 26f, grow, accent);
        Draw.Fade(1f);
    }

    // What a favourite is set to, in the colour the menu gives it.
    static (string Text, Rgba Tone) BurstState(RadialView view, Feature feature, Rgba accent) => feature switch
    {
        Toggle toggle => (toggle.ValueText, toggle.On ? Live : Dim),
        Choice choice => (choice.ValueText, choice.IsDefault ? Dim : Live),
        Command command when command.Showing(view.Now, out var outcome) => (outcome.Text, Kit.Tone(outcome.Kind, Live, Soft)),
        Command command => (command.Cap, Dim),
        _ => (feature.ValueText, Dim),
    };

    // One blade and the bolt to it. Returns where its top and bottom are.
    static (float Top, float Bottom) BurstBlade(RadialView view, int i, int count, Rgba accent, float arrived)
    {
        var grow = Kit.EaseOut((arrived - 0.08f - i * 0.03f) / 0.22f);
        if (grow <= 0f)
        {
            return (0f, 0f);
        }

        var feature = view.Slots[i];
        var lift = view.Lifts[i];
        var over = i == view.Hover;
        var (state, tone) = BurstState(view, feature, accent);
        var stateWidth = Draw.TrackedWidth(state, 10f, 1f);
        var width = SlotNameLeft + Draw.TextWidth(feature.Plain, 14f) + 14f + stateWidth + SlotPad;

        var (spotX, spotY) = BurstPoint(i, count);
        var far = MathF.Sqrt(spotX * spotX + spotY * spotY);
        var spread = (0.55f + 0.45f * grow) + lift * SlotPunch / far;
        var x = spotX * spread;
        var y = spotY * spread;
        var side = spotX > 1f ? 1 : spotX < -1f ? -1 : 0;
        var left = side > 0 ? x : side < 0 ? x - width : x - width / 2f;
        var top = y - SlotHeight / 2f;
        var tip = SlotTip + lift * 6f;

        var flash = view.Flashed == i && view.FlashAge < SlotFlash;
        var fill = flash ? White : Rgba.Mix(Dark, accent, lift);
        Draw.Fade(1f);
        if (side < 0)
        {
            BladeLeft(left, left + width, top, SlotHeight, grow, fill, tip);
        }
        else
        {
            Blade(left, left + width, top, SlotHeight, grow, fill, tip);
        }

        if (grow < 1f)
        {
            return (top, top + SlotHeight);
        }

        if (over && lift > 0.3f)
        {
            // A bolt from the ring to the near end of the blade, and current along its top.
            var (rimX, rimY) = (spotX / far * BurstRing, spotY / far * BurstRing);
            var nearX = side > 0 ? left : side < 0 ? left + width : left + width / 2f;
            var nearY = side == 0 ? (top < 0f ? top + SlotHeight : top) : top + SlotHeight / 2f;
            Draw.Fade(lift);
            Zap(rimX, rimY, nearX, nearY, 5f, i * 3, view.Time, Live);
            Zap(rimX, rimY, nearX, nearY, 7f, i * 3 + 11, view.Time, White);
            if (side < 0)
            {
                Zap(left + width, top, left - tip, top, 2.2f, i * 7, view.Time, Live);
            }
            else
            {
                Zap(left, top, left + width + tip, top, 2.2f, i * 7, view.Time, Live);
            }
        }

        Draw.Fade(1f);
        Draw.Text(Kit.Number(view.First + i + 1), left + SlotNumberLeft, top + 10f, 10f, flash ? Ink : over ? White : Ghost);
        Draw.Text(feature.Plain, left + SlotNameLeft, top + 8f, 14f, flash ? Ink : White);
        var ink = flash ? Ink : over ? White : Rgba.Mix(tone, White, feature.Glow(view.Now));
        Draw.Tracked(state, left + width - SlotPad - stateWidth, top + 10f, 10f, 1f, ink);
        return (top, top + SlotHeight);
    }

    static void BurstTitle(RadialView view, float top, float grow, Rgba accent)
    {
        const float width = 190f;
        Blade(-width / 2f, width / 2f, top, 22f, grow, accent, 14f);
        if (grow < 0.5f)
        {
            return;
        }

        FadeWith(grow);
        Draw.Tracked("FAVOURITES", -width / 2f + 12f, top + 6f, 10f, 3f, Ink);
        if (view.Pages > 1)
        {
            var x = Draw.TextRight(Kit.Number(view.Pages), width / 2f - 10f, top + 6f, 10f, Ink);
            x = Draw.TextRight("/", x - 4f, top + 6f, 10f, Ink);
            Draw.TextRight(Kit.Number(view.Page + 1), x - 4f, top + 6f, 10f, Ink);
        }

        Draw.Fade(1f);
    }

    // Under the burst, a blue blade with the name and state of the blade pointed at, a dark one
    // with its help, and one with the keys. It is sized for the longest help on the page, so it
    // doesn't change size as the pointer moves.
    static void BurstNote(RadialView view, float top, float grow, Rgba accent)
    {
        const float inset = 16f;
        const float helpLine = 17f;
        var left = -NoteWidth / 2f;
        var right = -left;
        var helpWidth = NoteWidth - 2f * inset;
        var idle = view.Tap ? TapHelp : HoldHelp;

        var room = Draw.Wrap(idle, 13f, helpWidth).Length;
        foreach (var slot in view.Slots)
        {
            room = Math.Max(room, Draw.Wrap(slot.Help, 13f, helpWidth).Length);
        }

        room = Math.Clamp(room, 1, NoteLines);
        var body = 10f + (room - 1) * helpLine + 13f + 10f;
        var bodyTop = top + NoteHead + Gap;
        var hintsTop = bodyTop + body + Gap;

        var feature = view.Focus;
        Draw.Fade(1f);
        Blade(left, right, top, NoteHead, grow, feature is null ? Darker : accent, 18f);
        Blade(left, right, bodyTop, body, grow, Dark, 14f);
        Blade(left, right, hintsTop, NoteHints, grow, Dark, 10f);
        if (grow < 0.5f)
        {
            return;
        }

        FadeWith(grow);
        if (feature is null)
        {
            Draw.Tracked(view.Tap ? "POINT AT A BLADE" : "LET GO TO CANCEL", left + inset, top + 8f, 10f, 2.5f, Dim);
        }
        else
        {
            var (state, _) = BurstState(view, feature, accent);
            var stateLeft = right - inset - Draw.TrackedWidth(state, 10f, 1.5f);
            Draw.Tracked(state, stateLeft, top + 8f, 10f, 1.5f, Ink);

            var nameRight = stateLeft - 14f;
            var key = Hotkeys.KeyText(feature);
            if (key.Length > 0)
            {
                var capWidth = Kit.CapWidth(key, 9f);
                Kit.Cap(key, stateLeft - 12f - capWidth, top + NoteHead / 2f, 9f, Ink, Ink);
                nameRight -= capWidth + 12f;
            }

            ToastText(feature.Plain, left + inset, top + 6f, 14f, Ink, nameRight);
        }

        var lines = Draw.Wrap(feature?.Help ?? idle, 13f, helpWidth);
        for (var k = 0; k < lines.Length && k < room; k++)
        {
            Draw.Text(lines[k], left + inset, bodyTop + 10f + k * helpLine, 13f, feature is null ? Dim : Soft);
        }

        Kit.HintRow(view.Hints, 0f, hintsTop + NoteHints / 2f, 10f, Ghost, Soft, Dim);
        Draw.Fade(1f);
    }

    // The parts of a question: the tag row, the title, the wrapped text and the buttons.
    const float AskWidth = 350f;
    const float AskPad = 18f;
    const float AskTag = 10f;
    const float AskTitle = 15f;
    const float AskBody = 11f;
    const float AskLine = 15f;
    const float AskButton = 24f;
    const float AskGap = 8f;
    const float AskTip = 30f;

    static float AskHeight(string[] lines) =>
        AskPad + AskTag + 12f + AskTitle + 10f + lines.Length * AskLine + 14f + AskButton + AskPad;

    // One big blade off a spine of its own. It can't fade, so it grows out, and its text comes
    // in once it has landed.
    public override void RenderModal(MenuView view, ModalView modal)
    {
        var accent = Look.AccentOr(Accent);
        ShadeScreen(new Rgba(0, 2, 8, 120), modal.Opened);

        var lines = Draw.Wrap(modal.Ask.Text, AskBody, AskWidth - 2f * AskPad);
        var height = AskHeight(lines);
        PlaceModal(AskWidth + AskTip, height + 34f);

        var grow = Kit.EaseOut(modal.Opened);
        Draw.Fade(1f);
        Draw.Rect(SpineLeft, 0f, 2f, height * Kit.EaseOut(modal.Opened / 0.3f), accent);
        Blade(0f, AskWidth, 0f, height, grow, Dark, AskTip);
        if (modal.Opened < 0.9f)
        {
            return;
        }

        if (modal.Opened < 1f)
        {
            Zap(0f, 0f, AskWidth + AskTip, 0f, 4f, 3, view.Time, White);
        }

        var y = AskPad;
        var end = Draw.Tracked(modal.Ask.Tag, AskPad, y, AskTag, 2f, Live);
        Blade(end + 8f, AskWidth - AskPad, y + AskTag / 2f - 1f, 2f, 1f, accent, 6f);

        y += AskTag + 12f;
        Draw.Tracked(modal.Ask.Title, AskPad, y, AskTitle, 1.5f, White);
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
            Draw.Quad(left + TabLean, y, left + width, y, left + width - TabLean, y + AskButton, left, y + AskButton, picked ? accent : Darker);
            if (picked)
            {
                Zap(left + TabLean, y, left + width, y, 2.2f, i + 60, view.Time, Live);
            }

            ButtonLabel(i == 0 ? modal.Ask.Yes : modal.Ask.No, left, y, width, AskButton, 10f, 2f, picked ? White : Soft);
        }

        Kit.HintRow(modal.Hints, AskWidth / 2f, height + 20f, KeyHints.Size, KeyHints.Frame, KeyHints.Key, KeyHints.Label);
    }
}
