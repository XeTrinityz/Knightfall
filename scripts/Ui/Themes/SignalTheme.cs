#nullable enable

using System.Diagnostics;

namespace Knightfall;

// The Bat-Signal's rooftop lamp, as the plate its crew works it from. Gunmetal with rivets and
// grooves, a fresnel lens with the bat on it, pilot lamps, lever switches and push buttons. It
// opens as two shutter blades part, then the lens strikes with a flicker and warms up.
sealed class SignalTheme : Theme
{
    const float Margin = 40f;
    const float Width = 600f;
    const float Column = 136f;
    const float BodyLeft = Column + 18f;
    const float BodyRight = Width - 18f;
    const float Head = 96f;
    const float TabsMiddle = Head + 20f;
    const float RowsTop = Head + 44f;
    const float RowHeight = 30f;
    const float PagesTop = Head + 26f;
    const float PageStep = 27f;
    const float HelpLine = 18f;
    const int HelpLines = 3;
    const int MaxLines = 9;
    const float LensX = 58f;
    const float LensY = 50f;
    const float LensRadius = 30f;

    // The lamp's own clock, since the menu's open is too quick for shutters and strike.
    const float ShutterSeconds = 0.35f;
    const float StrikeAt = 0.3f;
    const float StrikeSeconds = 0.15f;
    const float WarmSeconds = 0.8f;

    static readonly Rgba Plate = new(30, 32, 36, 245);
    static readonly Rgba PlateLow = new(20, 21, 24, 245);
    static readonly Rgba Groove = new(8, 8, 10);
    static readonly Rgba Hi = new(255, 255, 255);
    static readonly Rgba RivetTone = new(64, 67, 72);
    static readonly Rgba Well = new(12, 12, 13);
    static readonly Rgba Lever = new(150, 148, 142);
    static readonly Rgba Knob = new(168, 164, 156);
    static readonly Rgba Button = new(40, 42, 46);
    static readonly Rgba ButtonHot = new(58, 60, 64);
    static readonly Rgba TabPlate = new(50, 52, 57);
    static readonly Rgba Blade = new(40, 42, 46);
    static readonly Rgba Housing = new(14, 14, 16);
    static readonly Rgba Rim = new(52, 54, 58);
    static readonly Rgba LensDark = new(26, 26, 24);
    static readonly Rgba BatInk = new(18, 14, 8);
    static readonly Rgba PilotOff = new(44, 40, 34);
    static readonly Rgba Spark = new(255, 248, 225);
    static readonly Rgba Orange = new(214, 96, 36);
    static readonly Rgba Bright = new(252, 246, 230);
    static readonly Rgba HelpInk = new(200, 192, 176);

    static readonly Rgba Soft = new(222, 214, 196);
    static readonly Rgba Dim = new(140, 134, 120);
    static readonly Rgba Ghost = new(92, 88, 80);

    static readonly Rgba Gold = new(255, 206, 104);
    static readonly Rgba Moon = new(140, 196, 255);

    static readonly HintStyle KeyHints = new(Size: 10f, CapHeight: 17f, LineHeight: 22f, Frame: Ghost, Key: Soft, Label: Dim);

    public override string Id => "signal";

    public override string Name => "SIGNAL";

    public override Rgba Accent => Gold;

    public override Rgba Armed => LiveWith(Look.AccentOr(Gold));

    // With an accent close to moonlight, things that are on use the lamp's gold instead.
    static Rgba LiveWith(Rgba accent)
    {
        var apart = Math.Abs(accent.R - Moon.R) + Math.Abs(accent.G - Moon.G) + Math.Abs(accent.B - Moon.B);
        return apart < 120 ? Gold : Moon;
    }

    static long _lastDrawn;
    static long _openedAt;
    static bool _wasClosing;
    static float _shutter;

    static float Seconds(long from, long to) => (float)((to - from) / (double)Stopwatch.Frequency);

    // How far the lamp has come on, 0 to 1, from the moment the menu began to open. Closing runs
    // everything back with the menu's own close.
    readonly record struct Lamp(float Shutter, float Strike, Rgba Light);

    static Lamp LampFor(MenuView view, Rgba accent)
    {
        // A menu that is coming in after not being drawn for a moment is opening afresh. One that
        // opens again on its way out carries on from where its shutters are, so they don't slam.
        var fresh = _lastDrawn == 0 || (Seconds(_lastDrawn, view.Now) > 0.25f && view.Opened < 1f);
        if (fresh || (_wasClosing && !view.Closing))
        {
            var from = fresh ? 0f : Math.Clamp(_shutter, 0f, 1f);
            var back = 1f - MathF.Cbrt(1f - from);
            _openedAt = view.Now - (long)(back * ShutterSeconds * Stopwatch.Frequency);
        }

        _lastDrawn = view.Now;
        _wasClosing = view.Closing;
        var since = Seconds(_openedAt, view.Now);
        var warm = Math.Clamp((since - StrikeAt) / WarmSeconds, 0f, 1f);
        var light = Rgba.Mix(Orange, accent, warm);
        var shutter = Kit.EaseOut(since / ShutterSeconds);
        var strike = Math.Clamp((since - StrikeAt) / StrikeSeconds, 0f, 1f);
        if (view.Closing)
        {
            shutter = MathF.Min(shutter, view.Opened);
            strike = MathF.Min(strike, view.Opened);
        }
        else if (strike < 1f && MathF.Sin(view.Time * 63f) < 0f)
        {
            strike *= 0.45f;
        }

        _shutter = shutter;
        return new Lamp(shutter, strike, light);
    }

    public override void Render(MenuView view)
    {
        var accent = Look.AccentOr(Accent);
        var zoom = Look.Zoom;
        var lamp = LampFor(view, accent);
        var light = lamp.Light;

        // Heights first, so the plate fits what is in it. Text is measured at the zoom it is drawn at.
        Draw.Place(Margin + MoveX, 0f, zoom);
        var visible = Math.Clamp(view.Lines.Count, 1, MaxLines);
        var helpWidth = BodyRight - BodyLeft - 20f;
        var helpLines = HelpRoom(view, 13f, helpWidth, HelpLines);
        var helpTop = RowsTop + visible * RowHeight + 14f;
        var hintsTop = helpTop + helpLines * HelpLine + 18f;
        var hintLines = HintLines(view, BodyLeft - 8f, BodyRight, KeyHints);
        var content = hintsTop + (hintLines - 1) * KeyHints.LineHeight + KeyHints.CapHeight + 18f;
        var column = PagesTop + view.Pages.Count * PageStep + 16f;
        var height = MathF.Max(content, column);
        var tallest = MathF.Max(content + (MaxLines - visible) * RowHeight + (HelpLines - helpLines) * HelpLine, column);

        // Down the left edge, centred by its tallest so the top stays put between tabs.
        Draw.Place(Margin + MoveX, (Draw.ScreenHeight - tallest * zoom) / 2f + MoveY, zoom);
        Covers(0f, 0f, Width, height, tallest);
        Draw.Fade(view.Opened);

        Kit.Shadow(0f, 0f, Width, height, 0f, 0f);
        Draw.GradientDown(0f, 0f, Width, height, Plate, PlateLow);
        Draw.Rect(0f, 0f, Width, 1f, Hi with { A = 36 });
        Draw.Rect(0f, 1f, 1f, height - 1f, Hi with { A = 16 });
        Draw.Rect(0f, height - 1f, Width, 1f, Groove);
        Rivet(8f, 8f);
        Rivet(Width - 12f, 8f);
        Rivet(8f, height - 12f);
        Rivet(Width - 12f, height - 12f);
        Rivet(Column - 2f, 8f);

        Header(view, lamp);
        Pages(view, light, height);
        Tabs(view, light);
        GrooveLine(BodyLeft, RowsTop - 8f, BodyRight - BodyLeft);
        Rows(view, light, visible);
        Help(view, helpTop, helpLines, helpWidth);
        DrawHints(view, BodyLeft - 8f, BodyRight, hintsTop, KeyHints);

        Shutters(Width, height, lamp.Shutter);
        Draw.Fade(1f);
    }

    static void Rivet(float x, float y)
    {
        Draw.Rect(x, y, 4f, 4f, RivetTone);
        Draw.Rect(x, y, 4f, 1f, Hi with { A = 46 });
        Draw.Rect(x + 3f, y + 1f, 1f, 3f, Groove with { A = 200 });
    }

    // A cut in the plate: a dark line with a faint lit edge under it.
    static void GrooveLine(float x, float y, float width)
    {
        Draw.Rect(x, y, width, 1f, Groove with { A = 230 });
        Draw.Rect(x, y + 1f, width, 1f, Hi with { A = 13 });
    }

    static void Raised(float x, float y, float width, float height, Rgba color)
    {
        Draw.Rect(x, y, width, height, color);
        Draw.Rect(x, y, width, 1f, Hi with { A = 40 });
        Draw.Rect(x, y + height - 1f, width, 1f, Groove with { A = 230 });
    }

    // A small lamp. Lit, it glows with a hot spot in it.
    static void Pilot(float x, float y, bool on, Rgba light)
    {
        if (on)
        {
            Draw.Rect(x - 4f, y - 4f, 14f, 14f, light with { A = 30 });
            Draw.Rect(x - 2f, y - 2f, 10f, 10f, light with { A = 64 });
            Draw.Rect(x, y, 6f, 6f, light);
            Draw.Rect(x + 1f, y + 1f, 2f, 2f, Spark);
        }
        else
        {
            Draw.Rect(x, y, 6f, 6f, PilotOff);
            Draw.Rect(x, y, 6f, 1f, Hi with { A = 20 });
        }
    }

    // The fresnel lens with the bat across it. It is made of triangles, which can't fade, so it
    // is left out until the plate is nearly solid.
    static void Lens(float x, float y, float radius, float strike, Rgba light, float shown)
    {
        if (shown < 0.9f)
        {
            return;
        }

        Disc(x, y, radius + 7f, Housing);
        Disc(x, y, radius + 5f, Rim);
        var lit = Rgba.Mix(LensDark, light, strike);
        Disc(x, y, radius, lit);
        if (strike > 0f)
        {
            Disc(x, y, radius * 0.72f, Rgba.Mix(lit, Spark, 0.12f));
            Disc(x, y, radius * 0.42f, Rgba.Mix(lit, Spark, 0.24f));
        }

        var block = radius * 1.6f / Kit.BatColumns;
        Kit.Bat(x - Kit.BatColumns * block / 2f, y - 5.5f * block, block, strike > 0f ? BatInk : Housing);
    }

    static void Header(MenuView view, Lamp lamp)
    {
        Lens(LensX, LensY, LensRadius, lamp.Strike, lamp.Light, view.Opened);

        Draw.Tracked("KNIGHTFALL", 108f, 26f, 14f, 4f, Soft);
        Draw.Tracked("SIGNAL", 108f, 50f, 10f, 4f, lamp.Light);

        // A lit counter for how many options are switched on, with the clock and version beside it.
        var right = BodyRight;
        Raised(right - 74f, 22f, 74f, 44f, new Rgba(24, 25, 28));
        Draw.Rect(right - 66f, 30f, 58f, 22f, new Rgba(10, 9, 7));
        var active = Catalog.Active;
        var count = lamp.Light with { A = (byte)(active > 0 ? 255 : 70) };
        Draw.TextRight(Kit.Number(active), right - 12f, 31f, 18f, count);
        Draw.Tracked("ACTIVE", right - 66f, 55f, 7f, 2f, Ghost);
        Draw.TextRight(Kit.Clock, right - 86f, 30f, 11f, Dim);
        Draw.TextRight(Kit.Version, right - 86f, 48f, 9f, Ghost);

        GrooveLine(10f, Head - 6f, Width - 20f);
    }

    // The pages down a selector column, each with a pilot lamp. The one shown is lit.
    static void Pages(MenuView view, Rgba light, float height)
    {
        Draw.Tracked("PAGE", 18f, Head + 6f, 8f, 3f, Ghost);
        var next = Kit.CapWidth(view.PageNextKey, 9f);
        Kit.Cap(view.PageNextKey, Column - 10f - next, Head + 11f, 9f, Ghost, Dim);
        Kit.Cap(view.PagePrevKey, Column - 14f - next - Kit.CapWidth(view.PagePrevKey, 9f), Head + 11f, 9f, Ghost, Dim);

        for (var i = 0; i < view.Pages.Count; i++)
        {
            var y = PagesTop + i * PageStep;
            var current = i == view.PageIndex;
            if (current)
            {
                Draw.GradientAcross(6f, y - 2f, Column - 12f, 24f, light with { A = 40 }, light with { A = 0 });
            }

            Pilot(18f, y + 7f, current, light);
            Draw.Tracked(view.Pages[i].Name, 34f, y + 5f, 10f, 2f, current ? Bright : Dim);
            if (i < view.Pages.Count - 1)
            {
                GrooveLine(14f, y + 23f, Column - 28f);
            }
        }

        Draw.Rect(Column, Head, 1f, height - Head - 10f, Groove);
        Draw.Rect(Column + 1f, Head, 1f, height - Head - 10f, Hi with { A = 13 });
    }

    // The tabs on small raised plates. Tabs that don't fit are cut off at the ends with how
    // many more lie that way.
    static void Tabs(MenuView view, Rgba light)
    {
        const float size = 10f;
        const float spacing = 2f;
        const float pad = 16f;
        const float gap = 6f;
        const float capGap = 10f;
        const float more = Kit.MoreWidth + 10f;
        var tabs = view.Page.Tabs;
        var middle = TabsMiddle;

        var left = BodyLeft + Kit.Cap(view.TabPrevKey, BodyLeft, middle, 9f, Ghost, Dim) + capGap;
        var right = BodyRight - Kit.CapWidth(view.TabNextKey, 9f) - capGap;
        Kit.Cap(view.TabNextKey, right + capGap, middle, 9f, Ghost, Dim);
        var strip = FitTabs(view, left, right, size, spacing, pad, gap, more);

        var x = strip.Start;
        for (var i = strip.First; i < strip.Last; i++)
        {
            var name = tabs[i].Name;
            var width = Draw.TrackedWidth(name, size, spacing) + pad;
            var current = i == view.TabIndex;
            if (current)
            {
                Raised(x, middle - 11f, width, 22f, TabPlate);
                Draw.Rect(x, middle + 9f, width, 2f, light);
            }

            Draw.Tracked(name, x + pad / 2f, middle - size / 2f - 1f, size, spacing, current ? Bright : Dim);
            x += width + gap;
        }

        if (strip.First > 0)
        {
            Kit.More(left, middle, strip.First, left: true, light, Dim);
        }

        if (strip.Last < tabs.Count)
        {
            Kit.More(right - Kit.MoreWidth, middle, tabs.Count - strip.Last, left: false, light, Dim);
        }
    }

    static void Rows(MenuView view, Rgba light, int visible)
    {
        if (view.Rows.Count == 0)
        {
            return;
        }

        var (first, last, glide) = Scroll(view, visible);

        // The selected row is lit from behind, and now and then a spark runs along its foot.
        var top = RowsTop + glide * RowHeight;
        var width = BodyRight - BodyLeft + 8f;
        Draw.GradientAcross(BodyLeft - 8f, top, width, RowHeight, light with { A = 62 }, light with { A = 8 });
        Draw.Rect(BodyLeft - 8f, top + 3f, 3f, RowHeight - 6f, light);
        Kit.Pulse(BodyLeft - 8f, top + RowHeight - 1f, width, view.Time, Spark);

        if (view.Lines.Count > visible)
        {
            var track = visible * RowHeight;
            Draw.Rect(BodyRight + 7f, RowsTop, 2f, track, Groove);
            Draw.Rect(BodyRight + 7f, RowsTop + track * ScrollGlide(view, visible) / view.Lines.Count, 2f, track * visible / view.Lines.Count, light);
        }

        // A new tab's rows drop in one after another with a small bounce.
        for (var i = first; i < last; i++)
        {
            var arrive = view.Closing ? 1f : Settle((view.Arrived - (i - first) * 0.03f) / 0.24f);
            Draw.Fade(view.Opened * Math.Clamp(arrive * 1.4f, 0f, 1f));
            var y = RowsTop + (i - first) * RowHeight - (1f - arrive) * 12f;
            var line = view.Lines[i];
            if (line.Row is { } row)
            {
                Row(row, y, line.RowIndex == view.RowIndex, light, view.Now);
            }
            else
            {
                var end = Draw.Tracked(line.Heading!, BodyLeft, y + 11f, 9f, 3f, Dim);
                GrooveLine(end + 10f, y + 16f, BodyRight - end - 10f);
            }
        }

        Draw.Fade(view.Opened);
    }

    // Comes to rest past 1 and back, for a part that lands with a knock.
    static float Settle(float t)
    {
        t = Math.Clamp(t, 0f, 1f) - 1f;
        const float overshoot = 1.9f;
        return 1f + (overshoot + 1f) * t * t * t + overshoot * t * t;
    }

    static void Row(Feature feature, float y, bool selected, Rgba light, long now)
    {
        const float left = BodyLeft + 16f;
        const float right = BodyRight;
        var labelEnd = left + Draw.TextWidth(feature.Label, 15f);

        // A row that can't be used right now is greyed, with hazard stripes behind the reason.
        if (feature.Blocked is { } why)
        {
            Draw.Text(feature.Label, left, y + 7f, 15f, Ghost);
            var leftmost = labelEnd + 16f;
            var start = right - MathF.Min(Draw.TextWidth(why, 10f), right - leftmost - 14f) - 14f;
            Draw.Hatch(start - 6f, y + 6f, right - start + 6f, 18f, 7f, light with { A = 30 });
            Kit.Locked(why, right, y + 10f, 10f, Dim, Dim, leftmost);
            return;
        }

        var changed = !feature.Preference && !feature.IsDefault;
        if (changed)
        {
            Pilot(left - 16f, y + 12f, true, light);
        }

        Draw.Text(feature.Label, left, y + 7f, 15f, selected ? Bright : Soft);
        var glow = feature.Glow(now);
        var value = Rgba.Mix(changed ? light : Soft, Hi, glow);

        switch (feature)
        {
            case Toggle toggle:
            {
                var x = right - 26f;
                Draw.Rect(x, y + 6f, 22f, 18f, Well);
                Draw.Rect(x, y + 23f, 22f, 1f, Hi with { A = 13 });
                Raised(x + 8f, toggle.On ? y + 3f : y + 13f, 6f, 14f, Lever);
                Pilot(x - 14f, y + 12f, toggle.On, light);
                Draw.TextRight(toggle.ValueText, x - 20f, y + 10f, 10f, toggle.On ? value : Ghost);
                break;
            }

            case Slider slider:
            {
                const float width = 120f;
                var x = right - 64f - width;
                Draw.Rect(x, y + 15f, width, 2f, Groove);
                for (var k = 0; k <= 10; k++)
                {
                    var major = k % 5 == 0;
                    Draw.Rect(x + width * k / 10f, y + (major ? 18f : 19f), 1f, major ? 5f : 3f, Ghost);
                }

                Draw.Rect(x, y + 15f, width * slider.Fill, 2f, light with { A = 230 });
                Raised(x + width * slider.Fill - 3f, y + 8f, 7f, 15f, Knob);
                Draw.Rect(x + width * slider.DefaultFill, y + 24f, 1f, 3f, light);
                Draw.TextRight(slider.ValueText, right, y + 7f, 15f, value);
                break;
            }

            case Choice choice:
                ChoiceWindow(choice, y, selected, labelEnd + 20f, value, light);
                break;

            case Command command:
            {
                // A push button. After a press it shows what happened, in its own colour.
                var shown = command.Showing(now, out var outcome);
                var text = shown ? outcome.Text : command.Cap;
                var tone = shown ? Kit.Tone(outcome.Kind, light, Soft) : light;
                var textWidth = Draw.TrackedWidth(text, 9f, 2f);
                var width = MathF.Max(48f, textWidth + 18f);
                var x = right - width;
                Raised(x, y + 6f, width, 18f, shown ? Rgba.Mix(ButtonHot, tone, 0.25f) : selected ? ButtonHot : Button);
                if (shown)
                {
                    Draw.Frame(x, y + 6f, width, 18f, 1f, tone);
                }

                Draw.Tracked(text, x + (width - textWidth) / 2f, y + 10f, 9f, 2f, shown ? tone : selected ? Bright : Dim);
                break;
            }
        }

        if (glow > 0f)
        {
            Draw.GradientAcross(right - 160f, y + RowHeight - 1f, 160f, 1f, Hi with { A = 0 }, Hi with { A = (byte)(150 * glow) });
        }
    }

    // A choice shows its value in a recessed window, with arrows when selected and a notch for
    // each option under it. Long lists become a track with a marker.
    static void ChoiceWindow(Choice choice, float y, bool selected, float leftmost, Rgba value, Rgba light)
    {
        const float right = BodyRight;
        var text = choice.ValueText;
        var width = Math.Clamp(Draw.TextWidth(text, 13f) + 36f, 120f, MathF.Max(120f, right - leftmost));
        var x = right - width;
        Draw.Rect(x, y + 4f, width, 20f, Well);
        Draw.Rect(x, y + 23f, width, 1f, Hi with { A = 13 });
        if (Draw.TextWidth(text, 13f) <= width - 30f)
        {
            Draw.Text(text, x + (width - Draw.TextWidth(text, 13f)) / 2f, y + 7f, 13f, value);
        }
        else
        {
            Draw.TextRightFit(text, right - 15f, y + 7f, 13f, value, x + 15f);
        }

        if (selected)
        {
            Arrow(x + 5f, y + 14f, left: true, light);
            Arrow(right - 9f, y + 14f, left: false, light);
        }

        const int most = 14;
        var count = choice.Count;
        if (count > most)
        {
            const float track = most * 8f - 3f;
            var marker = MathF.Max(4f, track / count);
            var start = x + (width - track) / 2f;
            Draw.Rect(start, y + 26f, track, 1f, Ghost);
            Draw.Rect(start + (track - marker) * choice.Index / (count - 1), y + 25f, marker, 3f, light);
            return;
        }

        var notches = x + width / 2f - count * 4f;
        for (var k = 0; k < count; k++)
        {
            Draw.Rect(notches + k * 8f, y + 26f, 5f, 2f, k == choice.Index ? light : Ghost);
        }
    }

    // A small solid arrow made of boxes, so it fades with the row.
    static void Arrow(float x, float middle, bool left, Rgba color)
    {
        for (var i = 0; i < 4; i++)
        {
            var half = 0.5f + (left ? i : 3 - i);
            Draw.Rect(x + i, middle - half, 1f, 2f * half, color);
        }
    }

    // The help on a recessed strip under the rows.
    static void Help(MenuView view, float top, int lines, float width)
    {
        Draw.Rect(BodyLeft - 8f, top - 6f, BodyRight - BodyLeft + 8f, lines * HelpLine + 10f, Groove with { A = 140 });
        Draw.Rect(BodyLeft - 8f, top + lines * HelpLine + 4f, BodyRight - BodyLeft + 8f, 1f, Hi with { A = 13 });
        var help = view.Focus?.Help;
        if (string.IsNullOrEmpty(help))
        {
            return;
        }

        var wrapped = Draw.Wrap(help, 13f, width);
        for (var i = 0; i < wrapped.Length && i < lines; i++)
        {
            Draw.Text(wrapped[i], BodyLeft + 4f, top + i * HelpLine, 13f, HelpInk);
        }
    }

    // Two blades over a plate that part from the middle as it opens.
    static void Shutters(float width, float height, float open)
    {
        var cover = (1f - open) * height / 2f;
        if (cover < 0.5f)
        {
            return;
        }

        ShutterBlade(width, 0f, cover);
        ShutterBlade(width, height - cover, cover);
        Draw.Rect(0f, cover - 2f, width, 2f, Hi with { A = 50 });
        Draw.Rect(0f, height - cover, width, 2f, Groove);
    }

    static void ShutterBlade(float width, float y, float height)
    {
        Draw.Rect(0f, y, width, height, Blade);
        for (var s = y + 6f; s < y + height - 2f; s += 9f)
        {
            Draw.Rect(0f, s, width, 1f, Groove with { A = 180 });
            Draw.Rect(0f, s + 1f, width, 1f, Hi with { A = 15 });
        }
    }

    // A small riveted plate, for the readouts away from the menu.
    static void SmallPlate(float x, float y, float width, float height)
    {
        Kit.Shadow(x, y, width, height, 0f, 0f);
        Draw.GradientDown(x, y, width, height, Plate, PlateLow);
        Draw.Rect(x, y, width, 1f, Hi with { A = 36 });
        Draw.Rect(x, y + height - 1f, width, 1f, Groove);
        Rivet(x + 4f, y + 4f);
        Rivet(x + width - 8f, y + 4f);
    }

    // A round lamp, as a fan of triangles sent two at a time. They are always solid, whatever the fade.
    static void Disc(float x, float y, float radius, Rgba color)
    {
        if (radius < 0.5f)
        {
            return;
        }

        var pieces = radius > 30f ? 40 : 24;
        var step = 2f * MathF.PI / pieces;
        var (px, py) = (x, y - radius);
        for (var i = 2; i <= pieces; i += 2)
        {
            var (ms, mc) = MathF.SinCos((i - 1) * step);
            var (ns, nc) = MathF.SinCos(i * step);
            var (nx, ny) = (x + ns * radius, y - nc * radius);
            Draw.Quad(x, y, px, py, x + ms * radius, y - mc * radius, nx, ny, color);
            (px, py) = (nx, ny);
        }
    }

    // Each notification is a small riveted plate with a pilot lamp in the outcome's colour. A row
    // of lamps goes out one by one as its time runs down.
    public override void RenderToasts(ToastView view)
    {
        const int lamps = 10;
        var light = Look.AccentOr(Accent);
        var width = ToastWidth;
        var height = ToastHeight;

        foreach (var card in view.Cards)
        {
            PlaceToast(view, card, (1f - card.Shown) * 30f);
            Draw.Fade(card.Shown);

            var tone = Kit.Tone(card.Kind, light, Dim);
            SmallPlate(0f, 0f, width, height);
            Pilot(14f, height / 2f - 3f, true, tone);
            Draw.TrackedHead(card.Title, 30f, 6f, 8f, 2f, Dim, width - 14f - lamps * 7f);
            ToastText(card.Text, 30f, 17f, 12f, card.Kind == OutcomeKind.Failed ? Kit.Failed : Soft, width - 8f);
            for (var k = 0; k < lamps; k++)
            {
                var lit = (k + 0.5f) / lamps < card.Left;
                Draw.Rect(width - 12f - (lamps - k) * 7f + 7f, 7f, 4f, 4f, lit ? tone : PilotOff);
            }
        }

        Draw.Fade(1f);
    }

    public override void RenderGuide(GuideView view)
    {
        const float inset = 16f;
        const float head = 36f;
        var light = Look.AccentOr(Accent);
        var width = MathF.Max(GuideWidth(view), Draw.TrackedWidth(view.Title, 9f, 3f) + 24f) + 2f * inset;
        var height = head + view.Lines.Count * GuideLineHeight + 14f;

        PlaceGuide(width, height, (1f - view.Shown) * 24f);
        Draw.Fade(view.Shown);
        SmallPlate(0f, 0f, width, height);
        Pilot(inset, 14f, true, light);
        Draw.Tracked(view.Title, inset + 16f, 13f, 9f, 3f, Soft);
        GrooveLine(inset - 6f, 30f, width - 2f * inset + 12f);
        GuideLines(view, inset, head, Ghost, Soft, Dim);
        Draw.Fade(1f);
    }

    // The driving readout on a plate of its own. The speed sits over a notched rail, with the
    // armour and power under it.
    public override void RenderDash(DashView view)
    {
        const float width = 250f;
        const float inset = 16f;
        var light = Look.AccentOr(Accent);
        var hot = view.Hot ? LiveWith(light) : light;
        var height = view.Stats ? 156f : 132f;
        var right = width - inset;

        if (!PlaceDash(view, width, height, (1f - view.Shown) * 24f))
        {
            return;
        }

        Draw.Fade(view.Shown);
        SmallPlate(0f, 0f, width, height);
        Pilot(inset, 14f, true, light);
        Draw.Tracked(view.Title, inset + 16f, 13f, 9f, 3f, Soft);
        if (view.Mode.Length > 0)
        {
            var x = right - Draw.TrackedWidth(view.Mode, 9f, 2f);
            Pilot(x - 14f, 14f, true, hot);
            Draw.Tracked(view.Mode, x, 13f, 9f, 2f, hot);
        }

        GrooveLine(inset - 6f, 30f, width - 2f * inset + 12f);
        DashSpeed(view, inset, 36f, Bright, Dim);
        DashNote(view, right, 42f, Soft, Ghost);

        var rail = width - 2f * inset;
        Draw.Rect(inset, 82f, rail, 2f, Groove);
        for (var k = 0; k <= 20; k++)
        {
            var major = k % 5 == 0;
            Draw.Rect(inset + rail * k / 20f, major ? 85f : 86f, 1f, major ? 4f : 2f, Ghost);
        }

        Draw.Rect(inset, 82f, rail * Math.Clamp(view.SpeedFill, 0f, 1f), 2f, hot);

        DashBar(inset, right, 98f, "ARMOUR", view.Armour, view.ArmourText, Ghost, view.LowArmour ? Kit.Failed : light, Groove, view.LowArmour ? Kit.Failed : Soft);
        DashBar(inset, right, 114f, "POWER", view.Power, view.PowerText, Ghost, LiveWith(light), Groove, Soft);

        if (view.Stats)
        {
            GrooveLine(inset - 6f, 131f, width - 2f * inset + 12f);
            DashStat(inset, 138f, "BEST", view.Best, view.Units, Ghost, Soft);
            DashStat(width / 2f + 6f, 138f, "JUMP", view.Jump, "", Ghost, Soft);
        }

        Draw.Fade(1f);
    }

    // The radial: the lamp in the middle, the favourites round it on riveted plates, and a plate
    // under the ring that reads out the one pointed at. The lamp strikes as the plates come in.
    const float RadialLens = 42f;
    const float RingSmall = 230f;
    const float RingLarge = 255f;
    const float SlotHeight = 44f;
    const float SlotMin = 150f;
    const float SlotFlash = 0.5f;
    const float ReadWidth = 420f;
    const int ReadLines = 3;
    const int PageLamps = 6;

    const string TapHelp = "Point the mouse or the right stick at a favourite to see what it does.";
    const string HoldHelp = "Letting go here does nothing. Point at a favourite to pick it.";

    public override float RadialHub => 70f;

    public override float RadialReach => 170f;

    static float Ring(int count) => count > 8 ? RingLarge : RingSmall;

    public override void RenderRadial(RadialView view)
    {
        var accent = Look.AccentOr(Accent);
        var count = view.Slots.Count;
        var shown = Kit.EaseOut(view.Age / 0.22f);
        var strike = Math.Clamp((view.Age - 0.2f) / 0.15f, 0f, 1f);
        if (strike < 1f && MathF.Sin(view.Time * 63f) < 0f)
        {
            strike *= 0.45f;
        }

        var light = Rgba.Mix(Orange, accent, Math.Clamp((view.Age - 0.2f) / WarmSeconds, 0f, 1f));

        Draw.Place(0f, 0f, 1f);
        Draw.Fade(shown);
        Draw.Rect(0f, 0f, Draw.ScreenWidth, Draw.ScreenHeight, new Rgba(0, 0, 0, 115));

        PlaceRadial(view);
        Draw.Fade(shown);
        HubRing();
        Lens(0f, 0f, RadialLens, strike, light, shown);

        var arrived = MathF.Min(view.Age, view.PageAge);
        for (var i = 0; i < count; i++)
        {
            Slot(view, i, count, light, shown, arrived);
        }

        Draw.Fade(shown);
        var top = Ring(count) + 64f;
        var bottom = Readout(view, light, top);
        Draw.Fade(shown);
        Kit.HintRow(view.Hints, 0f, bottom + 22f, 10f, Ghost, Soft, Dim);
        Draw.Fade(1f);
    }

    // A dashed ring where the hub ends. Letting go inside it cancels.
    void HubRing()
    {
        const int dashes = 48;
        var color = Dim with { A = 130 };
        for (var i = 0; i < dashes; i += 2)
        {
            var (ax, ay) = Kit.Around(i * 360f / dashes, RadialHub);
            var (bx, by) = Kit.Around((i + 1) * 360f / dashes, RadialHub);
            Draw.Line(ax, ay, bx, by, color);
        }
    }

    static (string Text, Rgba Tone) State(RadialView view, Feature feature, Rgba light) => feature switch
    {
        _ when feature.Blocked is { } why => (why, Ghost),
        Toggle toggle => (toggle.ValueText, toggle.On ? light : Ghost),
        Choice choice => (choice.ValueText, choice.IsDefault ? Dim : light),
        Command command when command.Showing(view.Now, out var outcome) => (outcome.Text, Kit.Tone(outcome.Kind, light, Soft)),
        Command command => (command.Cap, Dim),
        _ => (feature.ValueText, Ghost),
    };

    // One favourite on a riveted plate: a pilot lamp lit when it is on, its number, name and
    // state. Plates on the right start at the ring and plates on the left end at it, so long names
    // grow away from the middle.
    void Slot(RadialView view, int i, int count, Rgba light, float shown, float arrived)
    {
        const float stateMost = 190f;
        var feature = view.Slots[i];
        var angle = RadialAngle(i, count);
        var arrive = Settle((arrived - 0.1f - i * 0.03f) / 0.25f);
        var over = i == view.Hover;
        var (x, y) = Kit.Around(angle, Ring(count) - (1f - arrive) * 30f + view.Lifts[i] * 10f);
        var side = MathF.Sin(angle * MathF.PI / 180f);

        var (state, tone) = State(view, feature, light);
        var locked = feature.Blocked is not null;
        var stateLeft = locked ? 13f : 0f;
        var stateWidth = MathF.Min(stateMost, Draw.TrackedWidth(state, 8f, 2f));
        var width = MathF.Max(SlotMin, 34f + MathF.Max(Draw.TextWidth(feature.Plain, 14f), stateLeft + stateWidth) + 16f);
        var left = side > 0.2f ? x : side < -0.2f ? x - width : x - width / 2f;
        var top = y - SlotHeight / 2f;

        Draw.Fade(shown * Math.Clamp(arrive * 1.5f, 0f, 1f));
        Raised(left, top, width, SlotHeight, over ? TabPlate : new Rgba(32, 34, 38, 245));
        Rivet(left + 4f, top + 4f);
        Rivet(left + width - 8f, top + 4f);
        if (over)
        {
            Draw.GradientAcross(left, top, width, SlotHeight, light with { A = 56 }, light with { A = 5 });
            Draw.Rect(left, top + 4f, 3f, SlotHeight - 8f, light);
        }

        if (view.Flashed == i && view.FlashAge < SlotFlash)
        {
            var fading = 1f - view.FlashAge / SlotFlash;
            Draw.Frame(left, top, width, SlotHeight, 1f, light with { A = (byte)(255 * fading) });
        }

        Pilot(left + 14f, top + 10f, RadialView.Lit(feature), light);
        Draw.Text(Kit.Number(view.First + i + 1), left + 12f, top + 25f, 9f, over ? light : Ghost);
        Draw.Text(feature.Plain, left + 34f, top + 7f, 14f, locked ? over ? Soft : Dim : over ? Bright : Soft);
        if (locked)
        {
            Kit.Padlock(left + 34f, top + 26f, 9f, over ? Soft : Dim);
            tone = over ? Soft : Dim;
        }

        var ink = Rgba.Mix(tone, Hi, feature.Glow(view.Now));
        Draw.TrackedHead(state, left + 34f + stateLeft, top + 27f, 8f, 2f, ink, left + width - 10f);
    }

    static void Centre(string text, float y, float size, Rgba color)
    {
        Draw.Text(text, -Draw.TextWidth(text, size) / 2f, y, size, color);
    }

    // What the favourite pointed at is, on a plate under the ring. It is sized for the longest help
    // on the page, so it doesn't change size as the pointer moves. Returns its bottom.
    static float Readout(RadialView view, Rgba light, float top)
    {
        const float pad = 14f;
        const float helpLine = 16f;
        var helpWidth = ReadWidth - 2f * pad - 20f;
        var idle = view.Tap ? TapHelp : HoldHelp;

        var room = Draw.Wrap(idle, 12f, helpWidth).Length;
        foreach (var slot in view.Slots)
        {
            room = Math.Max(room, Draw.Wrap(slot.Help, 12f, helpWidth).Length);
        }

        room = Math.Clamp(room, 1, ReadLines);
        var nameTop = top + pad;
        var kindTop = nameTop + 22f;
        var helpTop = kindTop + 20f;
        var height = helpTop - top + room * helpLine + pad - 4f;
        if (view.Pages > 1)
        {
            height = MathF.Max(height, 22f + Math.Min(view.Pages, PageLamps) * 12f + 12f);
        }

        var left = -ReadWidth / 2f;

        Kit.Shadow(left, top, ReadWidth, height, 0f, 0f);
        Draw.GradientDown(left, top, ReadWidth, height, Plate, PlateLow);
        Draw.Rect(left, top, ReadWidth, 1f, Hi with { A = 36 });
        Draw.Rect(left, top + height - 1f, ReadWidth, 1f, Groove);
        Rivet(left + 6f, top + 6f);
        Rivet(-left - 10f, top + 6f);
        Rivet(left + 6f, top + height - 10f);
        Rivet(-left - 10f, top + height - 10f);

        // A pilot lamp per page down the left, the one shown lit. Past that many it is numbered.
        if (view.Pages > PageLamps)
        {
            Draw.Text(Kit.Number(view.Page + 1), left + 12f, top + 20f, 10f, light);
            Draw.Text(Kit.Number(view.Pages), left + 12f, top + 34f, 10f, Ghost);
        }
        else if (view.Pages > 1)
        {
            for (var p = 0; p < view.Pages; p++)
            {
                Pilot(left + 14f, top + 22f + p * 12f, p == view.Page, light);
            }
        }

        var feature = view.Focus;
        string[] lines;
        if (feature is null)
        {
            Centre(view.Tap ? "POINT AT A FAVOURITE" : "LET GO TO CANCEL", nameTop, 15f, Dim);
            var nothing = Draw.TrackedWidth("NOTHING PICKED", 8f, 2f);
            Draw.Tracked("NOTHING PICKED", -nothing / 2f, kindTop + 1f, 8f, 2f, Ghost);
            lines = Draw.Wrap(idle, 12f, helpWidth);
        }
        else
        {
            Centre(feature.Plain, nameTop, 15f, Bright);
            var kind = RadialView.Kind(feature);
            var (state, tone) = State(view, feature, light);
            var key = Hotkeys.KeyText(feature);
            var kindWidth = Draw.TrackedWidth(kind, 8f, 2f);
            var stateWidth = Draw.TextWidth(state, 11f);
            var keyWidth = key.Length > 0 ? Kit.CapWidth(key, 9f) + 10f : 0f;
            var x = -(kindWidth + 10f + stateWidth + keyWidth) / 2f;
            x = Draw.Tracked(kind, x, kindTop + 1f, 8f, 2f, Ghost) + 10f;
            Draw.Text(state, x, kindTop - 1f, 11f, Rgba.Mix(tone, Hi, feature.Glow(view.Now)));
            if (key.Length > 0)
            {
                Kit.Cap(key, x + stateWidth + 10f, kindTop + 4f, 9f, Ghost, Soft);
            }

            lines = Draw.Wrap(feature.Help, 12f, helpWidth);
        }

        for (var k = 0; k < lines.Length && k < room; k++)
        {
            Centre(lines[k], helpTop + k * helpLine, 12f, HelpInk);
        }

        return top + height;
    }

    // The parts of a question: the lens, the tag, the title, the wrapped text and the buttons.
    const float AskWidth = 420f;
    const float AskTextLeft = 100f;
    const float AskButton = 28f;
    const float AskLine = 16f;

    static ModalView? _asked;
    static long _askedAt;

    // A plate of its own over the middle of the screen. Its shutters part and its lens strikes.
    // The answer picked is a lit push button.
    public override void RenderModal(MenuView view, ModalView modal)
    {
        var accent = Look.AccentOr(Accent);
        if (!ReferenceEquals(modal, _asked))
        {
            _asked = modal;
            _askedAt = view.Now;
        }

        var since = Seconds(_askedAt, view.Now);
        var leaving = modal.Leaving;
        var shutter = leaving ? modal.Opened : Kit.EaseOut(since / 0.3f);
        var strike = leaving ? modal.Opened : Math.Clamp((since - 0.25f) / 0.12f, 0f, 1f);
        if (!leaving && strike < 1f && MathF.Sin(view.Time * 63f) < 0f)
        {
            strike *= 0.45f;
        }

        var light = Rgba.Mix(Orange, accent, Math.Clamp((since - 0.25f) / WarmSeconds, 0f, 1f));

        ShadeScreen(new Rgba(0, 0, 0, 130), modal.Opened);
        var lines = Draw.Wrap(modal.Ask.Text, 12f, AskWidth - AskTextLeft - 24f);
        var height = 78f + lines.Length * AskLine + 18f + AskButton + 30f;
        PlaceModal(AskWidth, height + 34f);
        Draw.Fade(modal.Opened);

        Kit.Shadow(0f, 0f, AskWidth, height, 0f, 0f);
        Draw.GradientDown(0f, 0f, AskWidth, height, Plate, PlateLow);
        Draw.Rect(0f, 0f, AskWidth, 1f, Hi with { A = 36 });
        Draw.Rect(0f, height - 1f, AskWidth, 1f, Groove);
        Rivet(8f, 8f);
        Rivet(AskWidth - 12f, 8f);
        Rivet(8f, height - 12f);
        Rivet(AskWidth - 12f, height - 12f);

        Lens(54f, 56f, 26f, strike, light, modal.Opened);
        Draw.Tracked(modal.Ask.Tag, AskTextLeft, 26f, 9f, 3f, light);
        Draw.Tracked(modal.Ask.Title, AskTextLeft, 44f, 14f, 2f, Bright);
        for (var k = 0; k < lines.Length; k++)
        {
            Draw.Text(lines[k], AskTextLeft, 72f + k * AskLine, 12f, Dim);
        }

        var top = height - AskButton - 30f;
        var width = (AskWidth - 52f - 12f) / 2f;
        for (var i = 0; i < 2; i++)
        {
            var x = 26f + i * (width + 12f);
            var picked = modal.Choice == i;
            Raised(x, top, width, AskButton, picked ? ButtonHot : Button);
            if (picked)
            {
                Draw.GradientAcross(x, top, width, AskButton, light with { A = 50 }, light with { A = 10 });
                Draw.Frame(x, top, width, AskButton, 1f, light);
            }

            Pilot(x + 12f, top + 11f, picked, light);
            var label = i == 0 ? modal.Ask.Yes : modal.Ask.No;
            Draw.TrackedHead(label, x + 28f, top + 10f, 9f, 2f, picked ? Bright : Dim, x + width - 8f);
        }

        Shutters(AskWidth, height, shutter);
        Kit.HintRow(modal.Hints, AskWidth / 2f, height + 20f, 10f, Ghost, Soft, Dim);
        Draw.Fade(1f);
    }
}
