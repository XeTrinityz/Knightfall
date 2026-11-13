#nullable enable

namespace Knightfall;

// The default look. The Knightfall Protocol as a hologram from the Batcomputer: a line of light
// that unfolds into a panel, with the options as steps on a sequence line. It is all boxes, so
// the whole thing is see-through and can flicker and fade like a projection.
sealed class ProtocolTheme : Theme
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

    // The space between the rows and the rule above and below them.
    const float RuleGap = 8f;
    const float NodeSize = 9f;
    const float LabelLeft = Inset + 26f;
    const float MeterWidth = 96f;
    const float ValueRoom = 54f;
    const float HelpLineHeight = 18f;
    const float HelpWidth = Width - 2f * Inset - 12f;
    const float BatBlock = 1.5f;
    const int MaxLines = 14;
    const int HelpLines = 3;

    // How much of the opening is spent drawing the first line of light, before it unfolds.
    const float LineShare = 0.3f;

    // How far below the unfolding edge something has to be before it is fully lit.
    const float RevealDepth = 26f;

    static readonly Rgba PanelTop = new(8, 20, 34, 232);
    static readonly Rgba PanelBottom = new(3, 9, 18, 218);
    static readonly Rgba NodeBack = new(6, 15, 26);
    static readonly Rgba White = new(255, 255, 255);
    static readonly Rgba Pale = new(206, 228, 240);
    static readonly Rgba Dim = new(130, 160, 180);
    static readonly Rgba Ghost = new(84, 110, 130);

    // The colour the game writes its key prompts in, like "(Tab)".
    static readonly Rgba Prompt = new(184, 192, 248);

    // What is switched on glows amber, the way an armed system would.
    static readonly Rgba Live = new(250, 184, 78);

    static readonly HintStyle KeyHints = new(Size: 10f, CapHeight: 17f, LineHeight: 22f, Frame: Prompt with { A = 150 }, Key: Prompt, Label: Dim);

    // Set at the start of each frame and used by Show.
    float _unfolded;
    float _flicker;

    public override string Id => "protocol";

    public override string Name => "PROTOCOL";

    // The cyan blue of the game's own HUD lines.
    public override Rgba Accent => new(127, 219, 227);

    public override Rgba Armed => Live;

    public override void Render(MenuView view)
    {
        var accent = Look.AccentOr(Accent);
        var left = Inset;
        var right = Width - Inset;

        var column = ColumnWidth(view);
        Draw.Place(Margin + (column + ColumnGap) * Look.Zoom + MoveX, Margin + MoveY, Look.Zoom);

        // Heights are worked out first, so the panel can be drawn to fit what is in it.
        var visible = Math.Clamp(view.Lines.Count, 1, MaxLines);
        var rowsTop = HeaderHeight + RuleGap;
        var rowsBottom = rowsTop + visible * RowHeight;
        var helpTop = rowsBottom + 20f;
        var helpLines = HelpRoom(view, 13f, HelpWidth, HelpLines);
        var hintsTop = helpTop + helpLines * HelpLineHeight + 10f;
        var hintLines = HintLines(view, left, right, KeyHints);
        var statusTop = hintsTop + (hintLines - 1) * KeyHints.LineHeight + KeyHints.CapHeight + 12f;
        var height = statusTop + 32f;

        // The glow reaches 3 units past each edge.
        var columnHeight = PageTop + view.Pages.Count * PageHeight + 10f;
        var tallest = height + (MaxLines - visible) * RowHeight + (HelpLines - helpLines) * HelpLineHeight;
        Covers(-(column + ColumnGap) - 3f, -3f, Width + 3f, MathF.Max(height, columnHeight) + 3f, MathF.Max(tallest, columnHeight) + 3f);

        // A projection flickers a little until it has settled.
        _flicker = view.Opened < 1f ? 0.82f + 0.18f * MathF.Sin(view.Time * 75f) : 1f;

        var line = Math.Clamp(view.Opened / LineShare, 0f, 1f);
        var unfold = Math.Clamp((view.Opened - LineShare) / (1f - LineShare), 0f, 1f);
        _unfolded = height * unfold;

        if (unfold <= 0f)
        {
            // First a line of light grows out from the middle.
            var across = Width * line;
            Draw.Fade(_flicker);
            Draw.Rect((Width - across) / 2f, 0f, across, 2f, accent);
            Draw.Rect((Width - across) / 2f, -2f, across, 6f, accent with { A = 40 });
            return;
        }

        Draw.Fade(_flicker);
        Backing(view.Time, accent, 0f, 0f, Width, _unfolded, unfold < 1f);
        Column(view, accent, column);

        Header(view, accent);
        Rows(view, accent, rowsTop, visible);

        if (Show(rowsBottom + RuleGap))
        {
            Draw.Rect(left, rowsBottom + RuleGap, right - left, 1f, accent with { A = 60 });
        }

        Help(view, accent, helpTop);

        if (Show(hintsTop))
        {
            DrawHints(view, left, right, hintsTop, KeyHints);
        }

        Status(accent, statusTop);
        Draw.Fade(1f);
    }

    // Lights whatever is drawn next according to how far the unfolding edge has passed it.
    // Returns false when the edge hasn't reached it yet.
    bool Show(float y, float extra = 1f)
    {
        var lit = Math.Clamp((_unfolded - y) / RevealDepth, 0f, 1f) * extra;
        Draw.Fade(_flicker * lit);
        return lit > 0f;
    }

    // A panel, its glowing edge, the scanlines, and the bright edge it unfolds along.
    static void Backing(float time, Rgba accent, float left, float top, float width, float height, bool unfolding)
    {
        var bottom = top + height;
        Draw.GradientDown(left, top, width, height, PanelTop, PanelBottom);

        for (var y = top + 2f; y < bottom - 1f; y += 4f)
        {
            Draw.Rect(left + 1f, y, width - 2f, 1f, accent with { A = 8 });
        }

        // A soft band of light drifts down the panel every few seconds.
        const float every = 5.5f;
        const float travel = 2.4f;
        const float band = 30f;
        var phase = time % every;
        if (phase < travel)
        {
            var lower = top + (height + band) * (phase / travel);
            var upper = MathF.Max(top + 1f, lower - band);
            lower = MathF.Min(lower, bottom - 1f);
            if (lower > upper)
            {
                Draw.GradientDown(left + 1f, upper, width - 2f, lower - upper, accent with { A = 0 }, accent with { A = 24 });
            }
        }

        // The edge is one sharp line with fainter ones outside it, which reads as a glow.
        Draw.Frame(left, top, width, height, 1f, accent with { A = 170 });
        Draw.Frame(left - 1f, top - 1f, width + 2f, height + 2f, 1f, accent with { A = 56 });
        Draw.Frame(left - 2f, top - 2f, width + 4f, height + 4f, 1f, accent with { A = 24 });
        Draw.Frame(left - 3f, top - 3f, width + 6f, height + 6f, 1f, accent with { A = 9 });

        // Four small points, where a projector would anchor the image.
        const float node = 5f;
        Draw.Rect(left - 2f, top - 2f, node, node, accent);
        Draw.Rect(left + width - 3f, top - 2f, node, node, accent);
        Draw.Rect(left - 2f, bottom - 3f, node, node, accent);
        Draw.Rect(left + width - 3f, bottom - 3f, node, node, accent);

        if (unfolding)
        {
            Draw.GradientDown(left + 1f, MathF.Max(top + 1f, bottom - 28f), width - 2f, MathF.Min(27f, height - 1f), accent with { A = 0 }, accent with { A = 60 });
            Draw.Rect(left, bottom - 1f, width, 2f, White with { A = 220 });
        }
    }

    void Header(MenuView view, Rgba accent)
    {
        if (!Show(14f))
        {
            return;
        }

        Title(view, accent);

        var chipWidth = StatusChipWidth();
        StatusChip(view, Width - Inset - chipWidth, 12f);
        Draw.TextRight(Kit.Clock, Width - Inset, 36f, 10f, Ghost);

        Draw.Rect(Inset, 54f, Width - 2f * Inset, 1f, accent with { A = 60 });
        Kit.Pulse(Inset, 54f, Width - 2f * Inset, view.Time, White);

        if (Show(TabsMiddle - 10f))
        {
            Tabs(view, accent);
        }

        if (Show(HeaderHeight))
        {
            Draw.Rect(1f, HeaderHeight, Width - 2f, 1f, accent with { A = 90 });
        }
    }

    // The bat, with KNIGHTFALL and PROTOCOL stacked beside it. The bat is centred on the two
    // lines of text together. It is the game's own spinning bat when that is loaded, and a bat
    // made of blocks when it isn't.
    static void Title(MenuView view, Rgba accent)
    {
        const float nameTop = 12f;
        const float nameSize = 15f;
        const float subTop = 32f;
        const float subSize = 9f;
        const float iconSize = 46f;
        const float iconFps = 9f;

        // The bat sits high in its frame, so the frame is dropped a little to centre the bat itself.
        const float iconDrop = 3f;

        var middle = (nameTop + subTop + subSize) / 2f;
        var iconLeft = Inset - 4f;
        if (!Kit.SpinningBat(iconLeft, middle - iconSize / 2f + iconDrop, iconSize, view.Time, iconFps, accent))
        {
            Kit.Bat(Inset, middle - Kit.BatRowCount * BatBlock / 2f, BatBlock, accent);
        }

        var left = iconLeft + iconSize + 10f;
        Draw.Tracked("KNIGHTFALL", left, nameTop, nameSize, 5f, White);
        Draw.Tracked("PROTOCOL", left + 1f, subTop, subSize, 6f, accent);
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
        const float size = ChipSize;
        const float spacing = ChipSpacing;
        const float height = 16f;
        var armed = Catalog.Active > 0;
        var text = armed ? "ARMED" : "STANDBY";
        var width = StatusChipWidth();

        if (armed)
        {
            Draw.Rect(left, y, width, height, Live with { A = 36 });
            Draw.Frame(left, y, width, height, 1f, Live);

            // A light that blinks while the protocol is armed.
            if (view.Time % 1f < 0.6f)
            {
                Draw.Rect(left + 7f, y + 5f, 6f, 6f, Live);
            }

            Draw.Tracked(text, left + 20f, y + 4f, size, spacing, Live);
        }
        else
        {
            Draw.Frame(left, y, width, height, 1f, Ghost);
            Draw.Tracked(text, left + 8f, y + 4f, size, spacing, Dim);
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

    // Every page down the column, numbered, with the keys that turn them at the top. It unfolds
    // with the main panel.
    void Column(MenuView view, Rgba accent, float width)
    {
        var left = -(width + ColumnGap);
        var height = PageTop + view.Pages.Count * PageHeight + 10f;
        var shown = MathF.Min(_unfolded, height);
        Draw.Fade(_flicker);
        Backing(view.Time, accent, left, 0f, width, shown, shown < height);

        if (Show(14f))
        {
            Draw.Tracked("PAGES", left + ColumnInset, 18f, 10f, 4f, accent);
            var right = left + width - ColumnInset;
            var frame = Prompt with { A = 150 };
            var next = Kit.CapWidth(view.PageNextKey, 10f);
            Kit.Cap(view.PageNextKey, right - next, 23f, 10f, frame, Prompt);
            Kit.Cap(view.PagePrevKey, right - next - 4f - Kit.CapWidth(view.PagePrevKey, 10f), 23f, 10f, frame, Prompt);
        }

        if (Show(ColumnRule))
        {
            Draw.Rect(left + ColumnInset, ColumnRule, width - 2f * ColumnInset, 1f, accent with { A = 60 });
        }

        for (var i = 0; i < view.Pages.Count; i++)
        {
            var y = PageTop + i * PageHeight;
            if (!Show(y))
            {
                break;
            }

            var current = i == view.PageIndex;
            if (current)
            {
                Draw.Rect(left + 1f, y, width - 2f, PageHeight, accent with { A = 34 });
                Draw.Rect(left + 1f, y, 2f, PageHeight, accent);
                Kit.Sheen(left + 1f, y, width - 2f, PageHeight, view.Time, new Rgba(255, 255, 255, 18));
            }

            Draw.Text(Kit.Number(i + 1), left + ColumnInset, y + 9f, 10f, current ? accent : Ghost);
            Draw.Text(view.Pages[i].Name, left + PageNameLeft, y + 6f, PageSize, current ? White : Dim);
        }
    }

    static void Tabs(MenuView view, Rgba accent)
    {
        const float size = 11f;
        const float spacing = 2f;
        const float gap = 18f;
        const float capGap = 12f;
        const float more = Kit.MoreWidth + 12f;
        var tabs = view.Page.Tabs;
        var frame = Prompt with { A = 120 };
        var y = TabsMiddle - 6f;

        var left = Inset + Kit.Cap(view.TabPrevKey, Inset, TabsMiddle, 9f, frame, Prompt) + capGap;
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
                Draw.Rect(x, TabsMiddle + 9f, end - x, 2f, accent);
            }

            x = end + gap;
        }

        // When they all fit, the next key follows the last tab.
        if (before == 0 && after == 0)
        {
            Kit.Cap(view.TabNextKey, x - gap + capGap, TabsMiddle, 9f, frame, Prompt);
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

        Kit.Cap(view.TabNextKey, right + capGap, TabsMiddle, 9f, frame, Prompt);
    }

    void Rows(MenuView view, Rgba accent, float top, int visible)
    {
        var lines = view.Lines;
        if (view.Rows.Count == 0)
        {
            return;
        }

        var (first, last, glide) = Scroll(view, visible);

        // The highlight glides between rows. It is drawn first so the text sits on top.
        var highlight = top + glide * RowHeight;
        if (Show(highlight))
        {
            Draw.Rect(1f, highlight, Width - 2f, RowHeight, accent with { A = 34 });
            Draw.Rect(1f, highlight, Width - 2f, 1f, accent with { A = 130 });
            Draw.Rect(1f, highlight + RowHeight - 1f, Width - 2f, 1f, accent with { A = 130 });
            Kit.Sheen(1f, highlight, Width - 2f, RowHeight, view.Time, new Rgba(255, 255, 255, 18));
        }

        // The sequence line that every step hangs off.
        var lineTop = top + RowHeight / 2f;
        var lineBottom = MathF.Min(top + (last - first - 1) * RowHeight + RowHeight / 2f, _unfolded);
        if (lineBottom > lineTop && Show(lineTop))
        {
            Draw.Rect(Inset + NodeSize / 2f - 0.5f, lineTop, 1f, lineBottom - lineTop, accent with { A = 80 });
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
            if (!Show(y + 8f, arrive))
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

    // There is no scroll bar. When the list runs on past the rows on show, the rule at that end
    // lights up in the middle and an arrow bobs beside it. Each one fades in as the list leaves
    // that end.
    void More(MenuView view, Rgba accent, float top, int visible)
    {
        var bottom = top + visible * RowHeight;
        var smooth = ScrollGlide(view, visible);
        var above = MathF.Min(1f, smooth);
        var below = MathF.Min(1f, view.Lines.Count - visible - smooth);
        var bob = 0.75f + 0.75f * MathF.Sin(view.Time * 4f);

        if (above > 0f && Show(top - RuleGap, above))
        {
            MoreMark(top - RuleGap, top - RuleGap + 3.5f - bob, down: false, accent);
        }

        if (below > 0f && Show(bottom + RuleGap, below))
        {
            MoreMark(bottom + RuleGap, bottom + 1.5f + bob, down: true, accent);
        }
    }

    static void MoreMark(float rule, float arrow, bool down, Rgba accent)
    {
        const float reach = 34f;
        const float middle = Width / 2f;

        Draw.GradientAcross(middle - reach, rule, reach, 1f, accent with { A = 0 }, accent);
        Draw.GradientAcross(middle, rule, reach, 1f, accent, accent with { A = 0 });
        Arrow(middle, arrow, down, accent);
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

    // A heading is a phase of the sequence. It sits on the line as a hollow marker, with its name
    // spaced out beside it and a faint rule running on to the edge. It sits a little low, so it
    // reads as part of the rows under it.
    static void Heading(string text, float y, Rgba accent)
    {
        var middle = y + RowHeight / 2f + 3f;
        var node = middle - NodeSize / 2f;
        Draw.Rect(Inset, node, NodeSize, NodeSize, NodeBack);
        Draw.Frame(Inset, node, NodeSize, NodeSize, 1f, accent);

        var end = Draw.Tracked(text, LabelLeft, middle - 5f, 10f, 3f, accent);
        Draw.Rect(end + 10f, middle, Width - Inset - end - 10f, 1f, accent with { A = 46 });
    }

    static void Row(Feature feature, float y, bool selected, Rgba accent, long now)
    {
        var right = Width - Inset;
        var changed = !feature.Preference && !feature.IsDefault;

        // Each option is a step on the sequence line. A step that is switched on fills in amber.
        var nodeTop = y + (RowHeight - NodeSize) / 2f;
        if (selected)
        {
            Draw.Rect(Inset - 2f, nodeTop - 2f, NodeSize + 4f, NodeSize + 4f, (changed ? Live : accent) with { A = 60 });
        }

        if (changed)
        {
            Draw.Rect(Inset, nodeTop, NodeSize, NodeSize, Live);
        }
        else if (selected)
        {
            Draw.Rect(Inset, nodeTop, NodeSize, NodeSize, accent);
        }
        else
        {
            Draw.Rect(Inset, nodeTop, NodeSize, NodeSize, NodeBack);
            Draw.Frame(Inset, nodeTop, NodeSize, NodeSize, 1f, Ghost);
        }

        // A row that can't be used right now steps back, with the reason where its value goes.
        if (feature.Blocked is { } why)
        {
            Draw.Text(feature.Label, LabelLeft, y + 7f, 15f, selected ? Dim : Ghost);
            Kit.Locked(why, right, y + 10f, 10f, Ghost, selected ? Pale : Dim, LabelLeft + Draw.TextWidth(feature.Label, 15f) + 16f);
            return;
        }

        Draw.Text(feature.Label, LabelLeft, y + 7f, 15f, selected ? White : Pale);

        // A value flashes white for a moment when it changes.
        var value = Rgba.Mix(changed ? Live : accent, White, feature.Glow(now));

        switch (feature)
        {
            case Toggle toggle:
                Kit.Switch(right - Kit.SwitchWidth, y + (RowHeight - Kit.SwitchHeight) / 2f, toggle.On, value, Ghost);
                Draw.TextRight(toggle.ValueText, right - Kit.SwitchWidth - 9f, y + 10f, 11f, toggle.On ? value : Ghost);
                break;

            case Slider slider:
                Draw.TextRight(slider.ValueText, right, y + 7f, 15f, value);
                Kit.Meter(right - ValueRoom - MeterWidth, y + 11f, MeterWidth, 8f, 16, slider.Fill, slider.DefaultFill, value, accent with { A = 34 }, Dim);
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

                Kit.Dashes(right, y + 24f, choice.Count, choice.Index, value, accent with { A = 34 });
                break;

            case Command command:
            {
                // After a press the cap shows what happened, in its own colour.
                var shown = command.Showing(now, out var outcome);
                var text = shown ? outcome.Text : command.Cap;
                var tone = shown ? Kit.Tone(outcome.Kind, Live, Pale) : accent;
                var width = Draw.TextWidth(text, 10f) + 14f;
                if (selected || shown)
                {
                    Draw.Rect(right - width, y + 7f, width, 16f, tone with { A = 50 });
                }

                Draw.Frame(right - width, y + 7f, width, 16f, 1f, selected || shown ? tone : Ghost);
                Draw.Text(text, right - width + 7f, y + 10f, 10f, shown ? tone : selected ? White : Dim);
                break;
            }
        }
    }

    void Help(MenuView view, Rgba accent, float top)
    {
        var help = view.Focus?.Help;
        if (string.IsNullOrEmpty(help) || !Show(top))
        {
            return;
        }

        Draw.Rect(Inset, top + 5f, 4f, 4f, accent);
        var lines = Draw.Wrap(help, 13f, HelpWidth);
        for (var i = 0; i < lines.Length && i < HelpLines; i++)
        {
            Draw.Text(lines[i], Inset + 12f, top + i * HelpLineHeight, 13f, Pale);
        }
    }

    // The bottom line. How many options are switched on, and the mod version.
    void Status(Rgba accent, float top)
    {
        if (!Show(top))
        {
            return;
        }

        Draw.Rect(Inset, top, Width - 2f * Inset, 1f, accent with { A = 60 });

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

    // The speedometer is a small projection of its own, with the same glowing edge and scanlines.
    // It flickers while it fades in.
    // The key guide as a small panel of the same light.
    public override float ToastHeight => 36f;

    // Each notification is a small projection. A line of light draws across, opens into a panel,
    // and folds back into the line as it goes. A node on the left is lit in the outcome's colour,
    // and the bottom edge burns down with the time left.
    public override void RenderToasts(ToastView view)
    {
        const float inset = 12f;
        const float node = 4f;
        const float lineShare = 0.35f;
        var accent = Look.AccentOr(Accent);
        var width = ToastWidth;
        var height = ToastHeight;

        foreach (var card in view.Cards)
        {
            PlaceToast(view, card, 0f);
            var flicker = card.Shown < 1f ? 0.82f + 0.18f * MathF.Sin(view.Time * 75f) : 1f;
            var line = Math.Clamp(card.Shown / lineShare, 0f, 1f);
            var unfold = Math.Clamp((card.Shown - lineShare) / (1f - lineShare), 0f, 1f);
            var tone = Kit.Tone(card.Kind, accent, Dim);

            if (unfold <= 0f)
            {
                var across = width * line;
                Draw.Fade(flicker);
                Draw.Rect((width - across) / 2f, height / 2f - 1f, across, 2f, accent);
                Draw.Rect((width - across) / 2f, height / 2f - 3f, across, 6f, accent with { A = 40 });
                continue;
            }

            // It opens from the middle line outwards.
            var open = height * unfold;
            var top = (height - open) / 2f;
            Draw.Fade(flicker);
            Draw.GradientDown(0f, top, width, open, PanelTop, PanelBottom);
            for (var y = top + 2f; y < top + open - 1f; y += 4f)
            {
                Draw.Rect(1f, y, width - 2f, 1f, accent with { A = 8 });
            }

            Draw.Frame(0f, top, width, open, 1f, accent with { A = 170 });
            Draw.Frame(-1f, top - 1f, width + 2f, open + 2f, 1f, accent with { A = 56 });
            Draw.Frame(-2f, top - 2f, width + 4f, open + 4f, 1f, accent with { A = 24 });
            Draw.Rect(-2f, top - 2f, node, node, accent);
            Draw.Rect(width - 2f, top - 2f, node, node, accent);

            // The text comes up once the panel has room for it.
            Draw.Fade(flicker * unfold * unfold);
            var middle = height / 2f;
            Draw.Rect(inset - 1f, middle - 5f, 9f, 9f, NodeBack);
            Draw.Frame(inset - 1f, middle - 5f, 9f, 9f, 1f, tone);
            Draw.Rect(inset + 1f, middle - 3f, 5f, 5f, tone);
            Draw.Rect(inset + 3f, middle - 13f, 1f, 7f, tone with { A = 90 });
            Draw.Rect(inset + 3f, middle + 5f, 1f, 7f, tone with { A = 90 });

            var textLeft = inset + 20f;
            Draw.TrackedHead(card.Title, textLeft, 7f, 8f, 2.5f, accent, width - inset);
            ToastText(card.Text, textLeft, 17f, 12f, card.Kind == OutcomeKind.Failed ? Kit.Failed : White, width - 8f);

            Draw.Fade(flicker);
            Draw.Rect(1f, top + open - 2f, (width - 2f) * card.Left, 1f, tone);
        }

        Draw.Fade(1f);
    }

    public override void RenderGuide(GuideView view)
    {
        const float inset = 14f;
        const float head = 32f;
        const float node = 5f;
        var accent = Look.AccentOr(Accent);
        var width = MathF.Max(GuideWidth(view), Draw.TrackedWidth(view.Title, 9f, 3f)) + 2f * inset;
        var height = head + view.Lines.Count * GuideLineHeight + 10f;

        PlaceGuide(width, height);
        Draw.Fade(view.Shown * (view.Shown < 1f ? 0.82f + 0.18f * MathF.Sin(view.Time * 75f) : 1f));

        Draw.GradientDown(0f, 0f, width, height, PanelTop, PanelBottom);
        for (var y = 2f; y < height - 1f; y += 4f)
        {
            Draw.Rect(1f, y, width - 2f, 1f, accent with { A = 8 });
        }

        Draw.Frame(0f, 0f, width, height, 1f, accent with { A = 170 });
        Draw.Frame(-1f, -1f, width + 2f, height + 2f, 1f, accent with { A = 56 });
        Draw.Frame(-2f, -2f, width + 4f, height + 4f, 1f, accent with { A = 24 });
        Draw.Rect(-2f, -2f, node, node, accent);
        Draw.Rect(width - 3f, -2f, node, node, accent);
        Draw.Rect(-2f, height - 3f, node, node, accent);
        Draw.Rect(width - 3f, height - 3f, node, node, accent);

        Draw.Tracked(view.Title, inset, 12f, 9f, 3f, accent);
        Draw.Rect(inset, head - 4f, width - 2f * inset, 1f, accent with { A = 60 });
        GuideLines(view, inset, head, Prompt with { A = 150 }, Prompt, Dim);
        Draw.Fade(1f);
    }

    public override void RenderDash(DashView view)
    {
        const float width = 250f;
        const float inset = 14f;
        const float node = 5f;
        var accent = Look.AccentOr(Accent);
        var height = view.Stats ? 158f : 132f;
        var right = width - inset;
        var hot = view.Hot ? Live : accent;
        var empty = accent with { A = 34 };

        if (!PlaceDash(view, width, height))
        {
            return;
        }

        Draw.Fade(view.Shown * (view.Shown < 1f ? 0.82f + 0.18f * MathF.Sin(view.Time * 75f) : 1f));

        Draw.GradientDown(0f, 0f, width, height, PanelTop, PanelBottom);
        for (var y = 2f; y < height - 1f; y += 4f)
        {
            Draw.Rect(1f, y, width - 2f, 1f, accent with { A = 8 });
        }

        Draw.Frame(0f, 0f, width, height, 1f, accent with { A = 170 });
        Draw.Frame(-1f, -1f, width + 2f, height + 2f, 1f, accent with { A = 56 });
        Draw.Frame(-2f, -2f, width + 4f, height + 4f, 1f, accent with { A = 24 });
        Draw.Rect(-2f, -2f, node, node, accent);
        Draw.Rect(width - 3f, -2f, node, node, accent);
        Draw.Rect(-2f, height - 3f, node, node, accent);
        Draw.Rect(width - 3f, height - 3f, node, node, accent);

        Draw.Tracked(view.Title, inset, 12f, 9f, 3f, accent);
        if (view.Mode.Length > 0)
        {
            var chip = Draw.TrackedWidth(view.Mode, 9f, 2f) + 16f;
            Draw.Rect(right - chip, 8f, chip, 16f, hot with { A = 36 });
            Draw.Frame(right - chip, 8f, chip, 16f, 1f, hot);
            Draw.Tracked(view.Mode, right - chip + 8f, 12f, 9f, 2f, hot);
        }

        Draw.Rect(inset, 30f, width - 2f * inset, 1f, accent with { A = 60 });
        Kit.Pulse(inset, 30f, width - 2f * inset, view.Time, White);

        DashSpeed(view, inset, 38f, White, Dim);
        DashNote(view, right, 44f, Pale, Ghost);
        Kit.Meter(inset, 80f, width - 2f * inset, 6f, 24, view.SpeedFill, 0f, hot, empty, default);

        var armour = view.LowArmour ? Kit.Failed : accent;
        DashBar(inset, right, 98f, "ARMOUR", view.Armour, view.ArmourText, Ghost, armour, empty, view.LowArmour ? Kit.Failed : Pale);
        DashBar(inset, right, 114f, "POWER", view.Power, view.PowerText, Ghost, Live, empty, Pale);

        if (view.Stats)
        {
            Draw.Rect(inset, 132f, width - 2f * inset, 1f, accent with { A = 60 });
            DashStat(inset, 140f, "BEST", view.Best, view.Units, Ghost, Pale);
            DashStat(width / 2f + 6f, 140f, "JUMP", view.Jump, "", Ghost, Pale);
        }

        Draw.Fade(1f);
    }

    // The radial as a scan on the Batcomputer. A ring of light draws itself round from the top and
    // lights each favourite as it passes. Each favourite is a node on the ring with a leader out to
    // a card down the left or right, so names stay flat and whole however many there are. It is all
    // lines and boxes, so it fades and flickers like the menu.
    const float ScanHub = 58f;
    const float ScanDashes = 50f;
    const float ScanRing = 150f;
    const float ScanLeader = 160f;
    const float ScanTickIn = 170f;
    const float ScanTickOut = 175f;
    const float ScanElbow = 186f;
    const float ScanColumn = 232f;
    const float ScanTitleTop = -300f;
    const float ScanReadoutTop = 236f;
    const float CardHeight = 38f;
    const float CardGap = 6f;
    const float CardNumberLeft = 10f;
    const float CardNameLeft = 34f;
    const float CardRight = 12f;
    const float ReadoutWidth = 620f;
    const int ReadoutLines = 3;

    const float ScanSeconds = 0.32f;
    const float CardSeconds = 0.18f;
    const float SettleSeconds = 0.12f;
    const float FlashSeconds = 0.5f;
    const float SweepSpeed = 70f;
    const float SweepTrail = 2.2f;
    const int SweepLines = 9;

    // Circles are drawn as short straight lines, this many degrees each.
    const float ArcPiece = 6f;

    const string TapHelp = "Point at a node or its card to see what it does.";
    const string HoldHelp = "Letting go here does nothing. Point at a node or its card to pick it.";

    static readonly Rgba Shade = new(2, 8, 16, 110);
    static readonly Rgba CardLitTop = new(14, 36, 54, 236);
    static readonly Rgba CardLitBottom = new(6, 18, 30, 222);
    static readonly float[] SectorRings = { 86f, 108f, 130f };

    // Where each card goes this frame: its side, the height it would like, and where it ends up.
    static readonly int[] CardSide = new int[RadialView.MostSlots];
    static readonly float[] CardWant = new float[RadialView.MostSlots];
    static readonly float[] CardY = new float[RadialView.MostSlots];
    static readonly int[] CardOrder = new int[RadialView.MostSlots];
    static float _cardsTop;
    static float _cardsBottom;

    public override float RadialHub => ScanHub;

    public override float RadialReach => ScanRing;

    public override void RenderRadial(RadialView view)
    {
        var accent = Look.AccentOr(Accent);
        var count = view.Slots.Count;
        var step = 360f / count;
        var shown = Kit.EaseOut(view.Age / 0.25f);
        var flicker = view.Age < ScanSeconds + CardSeconds ? 0.82f + 0.18f * MathF.Sin(view.Time * 75f) : 1f;
        var head = 360f * Math.Clamp(view.Age / ScanSeconds, 0f, 1f);

        Draw.Place(0f, 0f, 1f);
        Draw.Fade(shown);
        Draw.Rect(0f, 0f, Draw.ScreenWidth, Draw.ScreenHeight, Shade);

        PlaceRadial(view);
        var fade = shown * flicker;
        Draw.Fade(fade);
        ScanRings(view, accent, count, step, head);
        ScanSector(view, accent, step);
        ScanPointer(view);
        ScanBat(view, accent);

        PlaceCards(count, step);
        var slotsAge = MathF.Min(view.Age, view.PageAge);
        for (var i = 0; i < count; i++)
        {
            ScanSlot(view, i, step, accent, slotsAge, fade);
        }

        Draw.Fade(fade);
        ScanTitle(view, accent);
        var bottom = ScanReadout(view, accent);
        Kit.HintRow(view.Hints, 0f, bottom + 24f, 10f, Prompt with { A = 150 }, Prompt, Dim);
    }

    static void Arc(float radius, float from, float to, Rgba color)
    {
        if (to <= from)
        {
            return;
        }

        var pieces = Math.Max(1, (int)MathF.Ceiling((to - from) / ArcPiece));
        var (x0, y0) = Kit.Around(from, radius);
        for (var k = 1; k <= pieces; k++)
        {
            var (x1, y1) = Kit.Around(from + (to - from) * k / pieces, radius);
            Draw.Line(x0, y0, x1, y1, color);
            x0 = x1;
            y0 = y1;
        }
    }

    // A sharp line with fainter ones either side, which reads as a glow.
    static void GlowArc(float radius, float from, float to, Rgba color, byte strength)
    {
        Arc(radius, from, to, color with { A = strength });
        Arc(radius - 1f, from, to, color with { A = (byte)(strength / 3) });
        Arc(radius + 1f, from, to, color with { A = (byte)(strength / 3) });
    }

    static void Spoke(float degrees, float inner, float outer, Rgba color)
    {
        var (ax, ay) = Kit.Around(degrees, inner);
        var (bx, by) = Kit.Around(degrees, outer);
        Draw.Line(ax, ay, bx, by, color);
    }

    // The hub, the node ring and the turning ticks, each drawn as far round as the light has got.
    static void ScanRings(RadialView view, Rgba accent, int count, float step, float head)
    {
        GlowArc(ScanHub, 0f, head, accent, 200);
        for (var a = 0f; a < head; a += 6f)
        {
            Arc(ScanDashes, a, MathF.Min(a + 3f, head), accent with { A = 70 });
        }

        GlowArc(ScanRing, 0f, head, accent, 150);

        // The bright head of the light while it draws.
        if (head < 360f)
        {
            for (var k = 0; k < 6; k++)
            {
                var bright = White with { A = (byte)(230 * (1f - k / 6f)) };
                GlowArc(ScanRing, head - 7f * (k + 1), head - 7f * k, bright, bright.A);
            }
        }

        var turn = view.Time * 4f % 360f;
        for (var k = 0; k < 72; k++)
        {
            var angle = (turn + k * 5f) % 360f;
            if (angle <= head)
            {
                Spoke(angle, ScanTickIn, ScanTickOut, accent with { A = 70 });
            }
        }

        if (count > 1)
        {
            for (var i = 0; i < count; i++)
            {
                var edge = i * step + step / 2f;
                if (edge <= head)
                {
                    Spoke(edge, ScanTickIn - 7f, ScanTickOut + 6f, accent with { A = 140 });
                }
            }
        }

        // Once the scan is drawn a sweep turns round it, fading behind itself.
        if (head >= 360f)
        {
            var sweep = view.Time * SweepSpeed % 360f;
            for (var k = 0; k < SweepLines; k++)
            {
                Spoke(sweep - k * SweepTrail, ScanHub + 4f, ScanTickIn - 4f, accent with { A = (byte)(140 * (1f - (float)k / SweepLines)) });
            }
        }
    }

    // The slice of the scan the pointer is in lights up.
    static void ScanSector(RadialView view, Rgba accent, float step)
    {
        if (view.Hover < 0)
        {
            return;
        }

        var mid = view.Hover * step;
        var from = mid - step / 2f;
        var to = mid + step / 2f;
        if (view.Slots.Count > 1)
        {
            Spoke(from, ScanHub + 4f, ScanTickIn - 4f, accent with { A = 170 });
            Spoke(to, ScanHub + 4f, ScanTickIn - 4f, accent with { A = 170 });
        }

        foreach (var radius in SectorRings)
        {
            Arc(radius, from + 1f, to - 1f, accent with { A = 60 });
        }

        for (var d = -1.5f; d <= 1.5f; d += 0.5f)
        {
            Arc(ScanRing + d, from + 1.5f, to - 1.5f, accent);
        }
    }

    // A dashed line from the hub out to the pointer, which is a small lit square.
    static void ScanPointer(RadialView view)
    {
        var length = view.Pointer.Length();
        if (length <= ScanHub + 4f)
        {
            return;
        }

        var direction = view.Pointer / length;
        for (var d = ScanHub + 4f; d < length - 5f; d += 7f)
        {
            var end = MathF.Min(d + 3.5f, length - 5f);
            Draw.Line(direction.X * d, direction.Y * d, direction.X * end, direction.Y * end, White with { A = 150 });
        }

        var at = view.Pointer;
        Draw.Rect(at.X - 2.5f, at.Y - 2.5f, 5f, 5f, White);
        Draw.Frame(at.X - 5.5f, at.Y - 5.5f, 11f, 11f, 1f, White with { A = 110 });
    }

    // The game's spinning bat in the hub. While the pointer rests there the hub's ring pulses.
    static void ScanBat(RadialView view, Rgba accent)
    {
        const float size = 64f;
        const float drop = 4f;
        const float fps = 9f;
        if (!Kit.SpinningBat(-size / 2f, -size / 2f + drop, size, view.Time, fps, accent))
        {
            Kit.Bat(-Kit.BatColumns * BatBlock / 2f, -Kit.BatRowCount * BatBlock / 2f, BatBlock, accent);
        }

        if (view.Hover < 0 && view.Age > ScanSeconds)
        {
            Arc(ScanHub - 3f, 0f, 360f, White with { A = (byte)(70 + 50 * MathF.Sin(view.Time * 6f)) });
        }
    }

    // Each card sits level with its node, down the side its node is on. Cards that would overlap
    // are spread apart, and then the group is moved back so it stays centred on where they wanted.
    static void PlaceCards(int count, float step)
    {
        for (var i = 0; i < count; i++)
        {
            var (sin, cos) = MathF.SinCos(i * step * MathF.PI / 180f);
            CardSide[i] = sin > 0.02f ? 1 : sin < -0.02f ? -1 : cos > 0f ? 1 : -1;
            CardWant[i] = -cos * ScanElbow;
            CardY[i] = CardWant[i];
        }

        Spread(count, 1);
        Spread(count, -1);

        _cardsTop = float.MaxValue;
        _cardsBottom = float.MinValue;
        for (var i = 0; i < count; i++)
        {
            _cardsTop = MathF.Min(_cardsTop, CardY[i] - CardHeight / 2f);
            _cardsBottom = MathF.Max(_cardsBottom, CardY[i] + CardHeight / 2f);
        }
    }

    static void Spread(int count, int side)
    {
        var n = 0;
        for (var i = 0; i < count; i++)
        {
            if (CardSide[i] != side)
            {
                continue;
            }

            var at = n++;
            while (at > 0 && CardWant[CardOrder[at - 1]] > CardWant[i])
            {
                CardOrder[at] = CardOrder[at - 1];
                at--;
            }

            CardOrder[at] = i;
        }

        if (n == 0)
        {
            return;
        }

        PushApart(n);
        var shift = 0f;
        for (var k = 0; k < n; k++)
        {
            shift += CardY[CardOrder[k]] - CardWant[CardOrder[k]];
        }

        shift /= n;
        for (var k = 0; k < n; k++)
        {
            CardY[CardOrder[k]] -= shift;
        }

        PushApart(n);
    }

    static void PushApart(int n)
    {
        const float apart = CardHeight + CardGap;
        for (var k = 1; k < n; k++)
        {
            var lowest = CardY[CardOrder[k - 1]] + apart;
            if (CardY[CardOrder[k]] < lowest)
            {
                CardY[CardOrder[k]] = lowest;
            }
        }
    }

    // A node on the ring and its card. The light reaches each node in turn, and the card opens
    // out from the leader just after.
    static void ScanSlot(RadialView view, int i, float step, Rgba accent, float slotsAge, float fade)
    {
        var feature = view.Slots[i];
        var mid = i * step;
        var since = slotsAge - mid / 360f * ScanSeconds;
        if (since < 0f)
        {
            return;
        }

        var settle = since < SettleSeconds ? 0.82f + 0.18f * MathF.Sin(view.Time * 75f + i) : 1f;
        Draw.Fade(fade * settle);
        var over = i == view.Hover;
        var lift = view.Lifts[i];
        var lit = RadialView.Lit(feature);
        var side = CardSide[i];
        var y = CardY[i];

        var lead = over ? accent : accent with { A = 110 };
        var (ax, ay) = Kit.Around(mid, ScanLeader);
        var (bx, by) = Kit.Around(mid, ScanElbow);
        Draw.Line(ax, ay, bx, by, lead);
        Draw.Line(bx, by, side * (ScanColumn - 12f), y, lead);
        Draw.Line(side * (ScanColumn - 12f), y, side * ScanColumn, y, lead);

        // The node is a step like the menu's, amber when on.
        var (nx, ny) = Kit.Around(mid, ScanRing);
        var size = NodeSize + 3f * lift;
        var half = size / 2f;
        if (lift > 0.02f)
        {
            Draw.Rect(nx - half - 3f, ny - half - 3f, size + 6f, size + 6f, (lit ? Live : accent) with { A = (byte)(70 * lift) });
        }

        if (lit)
        {
            Draw.Rect(nx - half, ny - half, size, size, Live);
        }
        else
        {
            Draw.Rect(nx - half, ny - half, size, size, NodeBack);
            Draw.Frame(nx - half, ny - half, size, size, 1f, over ? accent : accent with { A = 170 });
        }

        if (feature is Command)
        {
            Draw.Rect(nx - 1.5f, ny - 1.5f, 3f, 3f, accent);
        }

        // Brackets close in on the node pointed at.
        if (lift > 0.02f)
        {
            var reach = 9f + 12f * (1f - lift);
            Kit.Corners(nx - reach, ny - reach, 2f * reach, 2f * reach, 4f, White with { A = (byte)(255 * lift) });
        }

        ScanCard(view, feature, i, side, y, accent, since, over, lit);
    }

    static void ScanCard(RadialView view, Feature feature, int i, int side, float y, Rgba accent, float since, bool over, bool lit)
    {
        var open = Kit.EaseOut((since - 0.04f) / CardSeconds);
        if (open <= 0f)
        {
            return;
        }

        var (state, tone) = ScanState(view, feature, accent);
        var width = MathF.Max(Draw.TextWidth(feature.Plain, 15f), Draw.TrackedWidth(state, 9f, 1.6f)) + CardNameLeft + CardRight;
        var left = side > 0 ? ScanColumn : -ScanColumn - width;
        var top = y - CardHeight / 2f;
        var shownWidth = width * open;
        var from = side > 0 ? left : left + width - shownWidth;

        Draw.GradientDown(from, top, shownWidth, CardHeight, over ? CardLitTop : PanelTop, over ? CardLitBottom : PanelBottom);
        for (var line = top + 2f; line < top + CardHeight - 1f; line += 4f)
        {
            Draw.Rect(from, line, shownWidth, 1f, accent with { A = 8 });
        }

        Draw.Frame(from, top, shownWidth, CardHeight, 1f, accent with { A = (byte)(over ? 170 : 70) });
        if (over)
        {
            Draw.Frame(from - 1f, top - 1f, shownWidth + 2f, CardHeight + 2f, 1f, accent with { A = 40 });
            Draw.Rect(from, top + 1f + view.Time * 36f % (CardHeight - 2f), shownWidth, 1f, accent with { A = 90 });
        }

        // The edge the leader meets.
        Draw.Rect(side > 0 ? left : left + width - 2f, top, 2f, CardHeight, over ? White : lit ? Live : accent);

        if (view.Flashed == i && view.FlashAge < FlashSeconds)
        {
            var left2 = 1f - view.FlashAge / FlashSeconds;
            Draw.Rect(from, top, shownWidth, CardHeight, White with { A = (byte)(110 * left2 * left2) });
        }

        if (open < 1f)
        {
            return;
        }

        var textLeft = left + CardNameLeft;
        Draw.Text(Kit.Number(view.First + i + 1), left + CardNumberLeft, top + 6f, 10f, over ? accent : Ghost);
        Draw.Text(feature.Plain, textLeft, top + 4f, 15f, over ? White : Pale);
        Draw.Tracked(state, textLeft, top + 24f, 9f, 1.6f, Rgba.Mix(tone, White, feature.Glow(view.Now)));
    }

    // What a favourite is set to, in the colour the menu gives it.
    static (string Text, Rgba Tone) ScanState(RadialView view, Feature feature, Rgba accent) => feature switch
    {
        Toggle toggle => (toggle.ValueText, toggle.On ? Live : Dim),
        Choice choice => (choice.ValueText, choice.IsDefault ? Dim : Live),
        Command command when command.Showing(view.Now, out var outcome) => (outcome.Text, Kit.Tone(outcome.Kind, Live, Pale)),
        Command command => (command.Cap, accent),
        _ => (feature.ValueText, Dim),
    };

    static void ScanTitle(RadialView view, Rgba accent)
    {
        const string title = "FAVOURITES";
        const float size = 12f;
        const float spacing = 4f;
        const float rule = 110f;
        var top = MathF.Min(ScanTitleTop, _cardsTop - 56f);
        var width = Draw.TrackedWidth(title, size, spacing);
        Draw.Tracked(title, -width / 2f, top, size, spacing, Pale);
        Draw.Rect(-width / 2f - 20f - rule, top + 6f, rule, 1f, accent with { A = 110 });
        Draw.Rect(width / 2f + 20f, top + 6f, rule, 1f, accent with { A = 110 });
        Kit.Pulse(width / 2f + 20f, top + 6f, rule, view.Time, White);

        if (view.Pages < 2)
        {
            return;
        }

        // SET 01 / 02, drawn in pieces so no new text is made each frame.
        var page = Kit.Number(view.Page + 1);
        var pages = Kit.Number(view.Pages);
        var line = Draw.TrackedWidth("SET", 9f, 2f) + 8f + Draw.TextWidth(page, 10f) + 6f + Draw.TextWidth("/", 10f) + 6f + Draw.TextWidth(pages, 10f);
        var x = -line / 2f;
        var y = top + 22f;
        x = Draw.Tracked("SET", x, y + 1f, 9f, 2f, Dim) + 8f;
        Draw.Text(page, x, y, 10f, accent);
        x += Draw.TextWidth(page, 10f) + 6f;
        Draw.Text("/", x, y, 10f, Ghost);
        x += Draw.TextWidth("/", 10f) + 6f;
        Draw.Text(pages, x, y, 10f, Dim);
    }

    // Under the scan, a panel of the same glass reads out the favourite pointed at: what kind it
    // is, its state, its hotkey and its help. Returns where the panel ends.
    static float ScanReadout(RadialView view, Rgba accent)
    {
        const float inset = 16f;
        const float helpTop = 36f;
        const float helpLine = 17f;
        const float bottomPad = 14f;
        var top = MathF.Max(ScanReadoutTop, _cardsBottom + 28f);
        var left = -ReadoutWidth / 2f;
        var right = -left;
        var helpWidth = ReadoutWidth - 2f * inset;

        // Room for the longest help on this page, so the panel fits it and doesn't jump about
        // as the pointer moves between slots.
        var room = Draw.Wrap(view.Tap ? TapHelp : HoldHelp, 13f, helpWidth).Length;
        foreach (var slot in view.Slots)
        {
            room = Math.Max(room, Draw.Wrap(slot.Help, 13f, helpWidth).Length);
        }

        room = Math.Clamp(room, 1, ReadoutLines);
        var height = helpTop + (room - 1) * helpLine + 13f + bottomPad;
        Backing(view.Time, accent, left, top, ReadoutWidth, height, false);

        var feature = view.Focus;
        if (feature is null)
        {
            Draw.Rect(left + inset, top + 16f, 4f, 4f, accent);
            Draw.Tracked(view.Tap ? "POINT AT A FAVOURITE" : "LET GO TO CANCEL", left + inset + 12f, top + 13f, 11f, 2.5f, Pale);
            Draw.Text(view.Tap ? TapHelp : HoldHelp, left + inset, top + helpTop, 13f, Dim);
            return top + height;
        }

        var kind = RadialView.Kind(feature);
        var kindWidth = Draw.TrackedWidth(kind, 8f, 1.5f) + 12f;
        Draw.Frame(left + inset, top + 12f, kindWidth, 15f, 1f, accent with { A = 170 });
        Draw.Tracked(kind, left + inset + 6f, top + 15.5f, 8f, 1.5f, accent);

        var (state, tone) = ScanState(view, feature, accent);
        var stateLeft = right - inset - Draw.TrackedWidth(state, 10f, 1.6f);
        Draw.Tracked(state, stateLeft, top + 14f, 10f, 1.6f, Rgba.Mix(tone, White, feature.Glow(view.Now)));

        var nameRight = stateLeft - 14f;
        var key = Hotkeys.KeyText(feature);
        if (key.Length > 0)
        {
            var capWidth = Kit.CapWidth(key, 9f);
            Kit.Cap(key, stateLeft - 12f - capWidth, top + 19f, 9f, Prompt with { A = 150 }, Prompt);
            nameRight -= capWidth + 12f;
        }

        ToastText(feature.Plain, left + inset + kindWidth + 10f, top + 10f, 15f, White, nameRight);

        var lines = Draw.Wrap(feature.Help, 13f, helpWidth);
        for (var k = 0; k < lines.Length && k < room; k++)
        {
            Draw.Text(lines[k], left + inset, top + helpTop + k * helpLine, 13f, Dim);
        }

        return top + height;
    }
}
