#nullable enable

namespace Knightfall;

// The menu as it shows on the gauntlet's hologram. It is one design for every theme, in the
// theme's colours. The screen is small and seen at an angle, so the selected option gets the
// left half in large type, and the tab's options run down a sequence line on the right.
// It is laid out flat, 400 units wide, and Gauntlet lays it onto the screen.
static class GauntletMenu
{
    public const float Width = 400f;

    const float Inset = 10f;
    const float StripMiddle = 12f;
    const float StripRule = 24f;
    const float TabsMiddle = 33f;
    const float TabsRule = 42f;
    const float BodyTop = 48f;
    const float FocusRight = 172f;
    const float FocusWidth = FocusRight - Inset;
    const float Divider = 180f;
    const float ListLeft = 182f;
    const float NodeMiddle = 192f;
    const float NodeSize = 6f;
    const float LabelLeft = 202f;
    const float LineHeight = 15f;
    const float HelpSize = 8f;
    const float HelpLineHeight = 11f;
    const float CapSize = 8f;
    const float ActiveRoom = 60f;

    static readonly Rgba White = new(255, 255, 255);
    static readonly Rgba NodeBack = new(6, 15, 26);

    // Worked out per frame from the theme's colours.
    static Rgba _accent;
    static Rgba _bright;
    static Rgba _pale;
    static Rgba _dim;
    static Rgba _ghost;
    static Rgba _live;
    static Rgba _capFrame;
    static Rgba _capKey;
    static float _fade;

    public static void Render(MenuView view, float height, float shown)
    {
        if (shown <= 0f)
        {
            return;
        }

        var theme = Theme.Current;
        _accent = Look.AccentOr(theme.Accent);
        _bright = Rgba.Mix(_accent, White, 0.7f);
        _pale = Rgba.Mix(_accent, White, 0.45f);
        _dim = _accent with { A = 170 };
        _ghost = _accent with { A = 100 };
        _live = theme.Armed;
        _capFrame = Rgba.Mix(_accent, White, 0.5f) with { A = 150 };
        _capKey = Rgba.Mix(_accent, White, 0.6f);

        // A projection is never quite steady.
        _fade = shown * (0.92f + 0.08f * MathF.Sin(view.Time * 9f));
        Draw.Fade(_fade);

        Backing(view, height);
        Pages(view);
        Tabs(view);

        var hints = new Theme.HintStyle(7f, 11f, 13f, _capFrame, _capKey, _dim);
        var hintLines = Theme.HintLines(view, Inset, Width - Inset - ActiveRoom, hints);
        var hintsTop = height - 6f - (hintLines - 1) * hints.LineHeight - hints.CapHeight;
        var bodyBottom = hintsTop - 9f;

        Draw.Rect(Divider, BodyTop, 1f, bodyBottom - BodyTop, _accent with { A = 50 });
        Focus(view, bodyBottom);
        List(view, bodyBottom);

        Draw.Rect(0f, hintsTop - 5f, Width, 1f, _accent with { A = 70 });
        Theme.DrawHints(view, Inset, Width - Inset - ActiveRoom, hintsTop, hints);
        Active(hintsTop);

        Draw.Fade(1f);
    }

    static void Backing(MenuView view, float height)
    {
        Draw.GradientDown(0f, 0f, Width, height, _accent with { A = 60 }, _accent with { A = 18 });
        for (var y = 2f; y < height - 1f; y += 3f)
        {
            Draw.Rect(1f, y, Width - 2f, 1f, _accent with { A = 14 });
        }

        Kit.Sheen(1f, 1f, Width - 2f, height - 2f, view.Time, _accent with { A = 26 });
        Draw.Frame(0f, 0f, Width, height, 1f, _accent with { A = 140 });
        Draw.Frame(-1f, -1f, Width + 2f, height + 2f, 1f, _accent with { A = 40 });
        Kit.Corners(0f, 0f, Width, height, 12f, _bright);
    }

    // The name on the left. The page sits boxed between its keys on the right, with where it
    // falls among the pages after it.
    static void Pages(MenuView view)
    {
        Draw.Tracked("KNIGHTFALL", Inset, StripMiddle - 5f, 10f, 3f, _bright);

        var x = Draw.TextRight(Kit.Number(view.Pages.Count), Width - Inset, StripMiddle - 4f, 8f, _dim);
        x = Draw.TextRight("/", x - 2f, StripMiddle - 4f, 8f, _ghost);
        x = Draw.TextRight(Kit.Number(view.PageIndex + 1), x - 2f, StripMiddle - 4f, 8f, _bright);

        x -= 10f + Kit.CapWidth(view.PageNextKey, CapSize);
        Kit.Cap(view.PageNextKey, x, StripMiddle, CapSize, _capFrame, _capKey);

        var name = view.Page.Name;
        var box = Draw.TrackedWidth(name, 10f, 2.5f) + 14f;
        x -= 6f + box;
        Draw.Rect(x, StripMiddle - 8f, box, 16f, _accent with { A = 60 });
        Draw.Frame(x, StripMiddle - 8f, box, 16f, 1f, _accent with { A = 150 });
        Draw.Tracked(name, x + 7f, StripMiddle - 5f, 10f, 2.5f, _bright);

        x -= 6f + Kit.CapWidth(view.PagePrevKey, CapSize);
        Kit.Cap(view.PagePrevKey, x, StripMiddle, CapSize, _capFrame, _capKey);

        Draw.Rect(0f, StripRule, Width, 1f, _accent with { A = 70 });
    }

    static void Tabs(MenuView view)
    {
        const float size = 8f;
        const float spacing = 2f;
        const float gap = 12f;
        const float capGap = 8f;
        const float more = Kit.MoreWidth + 8f;
        var tabs = view.Page.Tabs;
        var y = TabsMiddle - size / 2f;

        var left = Inset + Kit.Cap(view.TabPrevKey, Inset, TabsMiddle, CapSize, _capFrame, _capKey) + capGap;
        var right = Width - Inset - Kit.CapWidth(view.TabNextKey, CapSize) - capGap;
        var strip = Theme.FitTabs(view, left, right, size, spacing, 0f, gap, more);
        var before = strip.First;
        var after = tabs.Count - strip.Last;

        var x = strip.Start;
        for (var i = strip.First; i < strip.Last; i++)
        {
            var current = i == view.TabIndex;
            var end = Draw.Tracked(tabs[i].Name, x, y, size, spacing, current ? _bright : _dim);
            if (current)
            {
                Draw.Rect(x, TabsMiddle + 6f, end - x - spacing, 2f, _bright);
            }

            x = end + gap;
        }

        if (before == 0 && after == 0)
        {
            Kit.Cap(view.TabNextKey, x - gap + capGap, TabsMiddle, CapSize, _capFrame, _capKey);
        }
        else
        {
            if (before > 0 && !(after == 0 && Draw.TrackedTail(tabs[strip.First - 1].Name, strip.Start - gap, y, size, spacing, _ghost, left)))
            {
                Kit.More(left, TabsMiddle, before, left: true, _accent, _dim);
            }

            if (after > 0 && !Draw.TrackedHead(tabs[strip.Last].Name, x, y, size, spacing, _ghost, right))
            {
                Kit.More(right - Kit.MoreWidth, TabsMiddle, after, left: false, _accent, _dim);
            }

            Kit.Cap(view.TabNextKey, right + capGap, TabsMiddle, CapSize, _capFrame, _capKey);
        }

        Draw.Rect(0f, TabsRule, Width, 1f, _accent with { A = 45 });
    }

    // The selected option, large: where it sits, its name, its value with a control to match,
    // and its help.
    static void Focus(MenuView view, float bottom)
    {
        if (view.Focus is not { } feature)
        {
            Draw.Tracked("NOTHING HERE", Inset, BodyTop + 4f, 8f, 2f, _ghost);
            return;
        }

        var y = BodyTop + 2f;
        var end = Draw.Tracked(SectionOf(view) ?? view.Tab.Name, Inset, y, 7f, 2f, _dim);
        var x = end + 6f;
        x += Draw.TextWidth(Kit.Number(view.RowIndex + 1), 7f) + 1f;
        Draw.Text(Kit.Number(view.RowIndex + 1), end + 6f, y - 0.5f, 7f, _bright);
        Draw.Text("/", x, y - 0.5f, 7f, _ghost);
        Draw.Text(Kit.Number(view.Rows.Count), x + Draw.TextWidth("/", 7f) + 1f, y - 0.5f, 7f, _dim);

        y = Label(feature.Label, y + 13f);
        y = Value(view, feature, y + 5f);

        var help = feature.Help;
        if (string.IsNullOrEmpty(help))
        {
            return;
        }

        var lines = Draw.Wrap(help, HelpSize, FocusWidth);
        y += 7f;
        for (var i = 0; i < lines.Length && y + HelpSize <= bottom; i++, y += HelpLineHeight)
        {
            Draw.Text(lines[i], Inset, y, HelpSize, _pale);
        }
    }

    // The name as large as fits the half. A long one goes onto two lines. Returns where it ends.
    static float Label(string label, float top)
    {
        const float largest = 16f;
        const float smallest = 11f;

        var size = largest;
        while (size > smallest && Draw.TextWidth(label, size) > FocusWidth)
        {
            size -= 1f;
        }

        if (Draw.TextWidth(label, size) <= FocusWidth)
        {
            Draw.Text(label, Inset, top, size, White);
            return top + size;
        }

        var lines = Draw.Wrap(label, smallest, FocusWidth);
        for (var i = 0; i < lines.Length && i < 2; i++)
        {
            Draw.Text(lines[i], Inset, top + i * (smallest + 2f), smallest, White);
        }

        return top + Math.Min(lines.Length, 2) * (smallest + 2f) - 2f;
    }

    // Returns where the value and its control end.
    static float Value(MenuView view, Feature feature, float top)
    {
        var changed = !feature.Preference && !feature.IsDefault;
        var tone = Rgba.Mix(changed ? _live : _bright, White, feature.Glow(view.Now));

        switch (feature)
        {
            case Toggle toggle:
            {
                const float size = 24f;
                var color = toggle.On ? tone : _ghost;
                Draw.Text(toggle.ValueText, Inset, top, size, color);
                Kit.Switch(Inset + Draw.TextWidth(toggle.ValueText, size) + 10f, top + (size - Kit.SwitchHeight) / 2f, toggle.On, tone, _ghost);
                return top + size;
            }

            case Slider slider:
            {
                const float size = 24f;
                Draw.Text(slider.ValueText, Inset, top, size, tone);
                var meter = top + size + 6f;
                Kit.Meter(Inset, meter, FocusWidth, 6f, 24, slider.Fill, slider.DefaultFill, tone, _accent with { A = 40 }, _dim);
                Draw.Text(slider.MinText, Inset, meter + 12f, 7f, _ghost);
                Draw.TextRight(slider.MaxText, FocusRight, meter + 12f, 7f, _ghost);
                return meter + 19f;
            }

            case Choice choice:
            {
                var size = 16f;
                var room = FocusWidth - 24f;
                while (size > 9f && Draw.TextWidth(choice.ValueText, size) > room)
                {
                    size -= 1f;
                }

                Draw.Text("<", Inset, top + (size - 10f) / 2f, 10f, _dim);
                var end = Inset + 12f + Draw.TextWidth(choice.ValueText, size);
                if (end - Inset - 12f <= room)
                {
                    Draw.Text(choice.ValueText, Inset + 12f, top, size, tone);
                }
                else
                {
                    Draw.TrackedHead(choice.ValueText, Inset + 12f, top, size, 0f, tone, Inset + 12f + room);
                    end = Inset + 12f + room;
                }

                Draw.Text(">", end + 4f, top + (size - 10f) / 2f, 10f, _dim);
                Kit.Dashes(FocusRight, top + size + 6f, choice.Count, choice.Index, tone, _accent with { A = 40 });
                return top + size + 8f;
            }

            case Command command:
            {
                // After a press the button shows what happened, in its own colour.
                const float size = 12f;
                var shown = command.Showing(view.Now, out var outcome);
                var text = shown ? outcome.Text : command.Cap;
                var color = shown ? Kit.Tone(outcome.Kind, _live, _pale) : _bright;
                var width = Draw.TextWidth(text, size) + 22f;
                Draw.Rect(Inset, top, width, 22f, color with { A = 50 });
                Draw.Frame(Inset, top, width, 22f, 1f, color);
                Draw.Text(text, Inset + 11f, top + 5f, size, color);
                return top + 22f;
            }

            default:
                Draw.Text(feature.ValueText, Inset, top, 16f, tone);
                return top + 16f;
        }
    }

    // The heading the selected option sits under, if its tab has headings.
    static string? SectionOf(MenuView view)
    {
        var lines = view.Lines;
        for (var i = Math.Min(view.Tab.LineOf(view.RowIndex), lines.Count - 1); i >= 0; i--)
        {
            if (lines[i].Heading is { } heading)
            {
                return heading;
            }
        }

        return null;
    }

    // Every option on the tab, each a step on the sequence line.
    static void List(MenuView view, float bottom)
    {
        if (view.Rows.Count == 0)
        {
            return;
        }

        var lines = view.Lines;
        var visible = Math.Max(1, (int)((bottom - BodyTop) / LineHeight));
        var (first, last, glide) = Theme.Scroll(view, visible);

        // The highlight glides between rows. It is drawn first so the text sits on top.
        var highlight = BodyTop + glide * LineHeight;
        var right = Width - 6f;
        Draw.Rect(ListLeft, highlight, right - ListLeft, LineHeight, _accent with { A = 45 });
        Draw.Rect(ListLeft, highlight, right - ListLeft, 1f, _accent with { A = 120 });
        Draw.Rect(ListLeft, highlight + LineHeight - 1f, right - ListLeft, 1f, _accent with { A = 120 });
        Kit.Sheen(ListLeft, highlight, right - ListLeft, LineHeight, view.Time, new Rgba(255, 255, 255, 18));

        var lineBottom = BodyTop + (last - first - 1) * LineHeight + LineHeight / 2f;
        if (last - first > 1)
        {
            Draw.Rect(NodeMiddle - 0.5f, BodyTop + LineHeight / 2f, 1f, lineBottom - BodyTop - LineHeight / 2f, _accent with { A = 70 });
        }

        for (var i = first; i < last; i++)
        {
            // Rows come in one after another when a tab or page turns.
            var y = BodyTop + (i - first) * LineHeight;
            Draw.Fade(_fade * (view.Closing ? 1f : Kit.Cascade(view.Arrived, i - first)));

            var line = lines[i];
            if (line.Row is { } row)
            {
                Row(view, row, y, line.RowIndex == view.RowIndex);
            }
            else
            {
                Heading(line.Heading!, y);
            }
        }

        Draw.Fade(_fade);

        // Arrows at the ends that have more past them.
        var bob = 0.75f + 0.75f * MathF.Sin(view.Time * 4f);
        if (first > 0)
        {
            Arrow(Width - 14f, BodyTop - 4f - bob, down: false);
        }

        if (last < lines.Count)
        {
            Arrow(Width - 14f, bottom + bob - 2f, down: true);
        }
    }

    static void Row(MenuView view, Feature feature, float y, bool selected)
    {
        var changed = !feature.Preference && !feature.IsDefault;
        var node = y + (LineHeight - NodeSize) / 2f;
        var left = NodeMiddle - NodeSize / 2f;
        if (changed)
        {
            Draw.Rect(left, node, NodeSize, NodeSize, _live);
        }
        else if (selected)
        {
            Draw.Rect(left, node, NodeSize, NodeSize, _bright);
        }
        else
        {
            Draw.Rect(left, node, NodeSize, NodeSize, NodeBack);
            Draw.Frame(left, node, NodeSize, NodeSize, 1f, _ghost);
        }

        const float size = 8f;
        var right = Width - Inset;
        var top = y + 4f;
        var tone = Rgba.Mix(changed ? _live : _dim, White, feature.Glow(view.Now));
        float valueLeft;
        switch (feature)
        {
            case Toggle toggle:
                valueLeft = Draw.TextRight(toggle.ValueText, right, top, size, toggle.On ? tone : _ghost);
                break;

            case Command command:
            {
                var shown = command.Showing(view.Now, out var outcome);
                valueLeft = shown
                    ? Draw.TextRight(outcome.Text, right, top, size, Kit.Tone(outcome.Kind, _live, _pale))
                    : Draw.TextRight(command.Cap, right, top, size, selected ? _bright : _ghost);
                break;
            }

            case Choice choice:
                valueLeft = Draw.TextRightFit(choice.ValueText, right, top, size, tone, LabelLeft + 70f);
                break;

            default:
                valueLeft = Draw.TextRight(feature.ValueText, right, top, size, tone);
                break;
        }

        var label = feature.Label;
        var ink = selected ? White : _pale;
        var limit = valueLeft - 8f;
        if (LabelLeft + Draw.TextWidth(label, 9f) <= limit)
        {
            Draw.Text(label, LabelLeft, y + 3f, 9f, ink);
        }
        else
        {
            Draw.TrackedHead(label, LabelLeft, y + 3f, 9f, 0f, ink, limit);
        }
    }

    static void Heading(string text, float y)
    {
        var node = y + (LineHeight - NodeSize) / 2f + 1f;
        Draw.Rect(NodeMiddle - NodeSize / 2f, node, NodeSize, NodeSize, NodeBack);
        Draw.Frame(NodeMiddle - NodeSize / 2f, node, NodeSize, NodeSize, 1f, _bright);
        var end = Draw.Tracked(text, LabelLeft, y + 5f, 7f, 2.5f, _accent);
        Draw.Rect(end + 6f, y + 8.5f, Width - Inset - end - 6f, 1f, _accent with { A = 40 });
    }

    // How many options are switched on, at the right end of the bottom line.
    static void Active(float top)
    {
        var active = Catalog.Active;
        var x = Draw.TextRight(Kit.Number(active), Width - Inset, top + 1f, 9f, active > 0 ? _live : _ghost);
        Draw.Tracked("ACTIVE", x - 6f - Draw.TrackedWidth("ACTIVE", 7f, 2f), top + 2f, 7f, 2f, _ghost);
    }

    // An arrow head made of boxes, so it fades like everything else.
    static void Arrow(float middle, float y, bool down)
    {
        const int rows = 4;
        for (var i = 0; i < rows; i++)
        {
            var half = 0.5f + (down ? rows - 1 - i : i);
            Draw.Rect(middle - half, y + i, 2f * half, 1f, _accent);
        }
    }
}
