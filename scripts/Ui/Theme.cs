#nullable enable

namespace Knightfall;

// A theme draws the whole menu its own way. It decides where everything sits, not just the
// colours. Controls, rows and text are the same in every theme.
abstract class Theme
{
    // The first one is the default.
    public static readonly Theme[] All = { new ProtocolTheme(), new RailTheme(), new ConsoleTheme(), new MilitiaTheme() };

    public static Theme Current => All[Math.Clamp(Look.Theme.Index, 0, All.Length - 1)];

    public abstract string Id { get; }

    public abstract string Name { get; }

    // Used when ACCENT is left on THEME.
    public abstract Rgba Accent { get; }

    // The colour the theme gives to something switched on or live.
    public abstract Rgba Armed { get; }

    public abstract void Render(MenuView view);

    // The driving readout. Each theme draws it its own way, where the player put it.
    public abstract void RenderDash(DashView view);

    // The key guide for modes with controls of their own, at the middle of the left edge.
    public abstract void RenderGuide(GuideView view);

    // Notifications, stacked where ToastView says, newest nearest the corner. Each theme draws
    // them in its own style. PlaceToast puts each card where it belongs.
    public abstract void RenderToasts(ToastView view);

    // The favourites radial, in the middle of the screen. Each theme draws it its own way.
    public abstract void RenderRadial(RadialView view);

    // Inside this distance from the middle the pointer is on no slot, so letting go cancels.
    public abstract float RadialHub { get; }

    // How far out the pointer can go.
    public abstract float RadialReach { get; }

    // Which way a slot lies from the middle, in degrees clockwise from the top. The pointer picks
    // the slot whose direction is nearest its own.
    public virtual float RadialAngle(int slot, int count) => slot * 360f / count;

    // Centres the radial on the screen at its size.
    protected static void PlaceRadial(RadialView view)
    {
        Draw.Place(Draw.ScreenWidth / 2f, Draw.ScreenHeight / 2f, Look.Zoom * view.Size);
    }

    // How big one notification card is, in units at full size. The stack is laid out from it.
    public virtual float ToastWidth => 236f;

    public virtual float ToastHeight => 34f;

    public const float ToastGap = 6f;

    // Puts a card in its row of the stack. Slide moves it out towards the edge it came from.
    protected static void PlaceToast(ToastView view, ToastView.Card card, float slide)
    {
        var zoom = view.Zoom;
        var step = (view.Height + ToastGap) * zoom;
        var x = view.X + (view.Right ? slide : -slide) * zoom;
        var y = view.Down ? view.Y + card.Row * step : view.Y - (card.Row + 1f) * step + ToastGap * zoom;
        Draw.Place(x, y, zoom);
    }

    // The note on a card. A long one, like a song's name, fades out at the right end.
    protected static void ToastText(string text, float left, float top, float size, Rgba ink, float right)
    {
        if (Draw.TextWidth(text, size) <= right - left)
        {
            Draw.Text(text, left, top, size, ink);
        }
        else
        {
            Draw.TrackedHead(text, left, top, size, 0f, ink, right);
        }
    }

    // An area of the screen, in screen units.
    public readonly record struct Area(float Left, float Top, float Right, float Bottom)
    {
        public bool Overlaps(Area other) => Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;
    }

    // What the menu covered this frame, or null when it isn't drawn. Notifications keep clear of it.
    public static Area? MenuArea { get; private set; }

    // How close MENU X and MENU Y can take the menu to the edge of the screen.
    const float MoveEdge = 40f;

    // How quickly the menu glides to a new spot, per second.
    const float MoveEase = 12f;

    // Where the theme put the menu the last time it was drawn, before it was moved.
    static Area _home;
    static bool _homeKnown;
    static bool _aimed;
    static long _movedAt;

    // How far the menu is moved from the theme's own spot, in screen units. Themes add it to
    // where they place the menu.
    protected static float MoveX { get; private set; }

    protected static float MoveY { get; private set; }

    // Before the menu is drawn each frame. MENU X and MENU Y are a share of the room between the
    // theme's spot and the edge. That room comes from the last frame, and the menu glides there,
    // so a taller tab or another theme doesn't make it jump.
    public static void NoMenu() => MenuArea = null;

    public static void BeginMenu()
    {
        MenuArea = null;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var seconds = _movedAt == 0 ? 1f : (float)((now - _movedAt) / (double)System.Diagnostics.Stopwatch.Frequency);
        _movedAt = now;

        var x = 0f;
        var y = 0f;
        if (_homeKnown)
        {
            x = Share((float)Look.MoveX.Value / 100f, _home.Left - MoveEdge, Draw.ScreenWidth - MoveEdge - _home.Right);
            y = Share((float)Look.MoveY.Value / 100f, _home.Top - MoveEdge, Draw.ScreenHeight - MoveEdge - _home.Bottom);
        }

        // A menu that was shut, or hadn't been measured yet, goes straight where it belongs.
        var glide = seconds > 0.25f || !_aimed ? 1f : 1f - MathF.Exp(-MoveEase * seconds);
        _aimed = _homeKnown;
        MoveX += (x - MoveX) * glide;
        MoveY += (y - MoveY) * glide;
    }

    static float Share(float share, float before, float after)
    {
        return share < 0f ? share * MathF.Max(0f, before) : share * MathF.Max(0f, after);
    }

    // Each theme says what part of the screen its menu covers, in units at the current place.
    // Tallest is where the bottom would be on the longest tab the theme can show. MENU Y works
    // from that, so the menu stays put while the tabs change height.
    protected static void Covers(float left, float top, float right, float bottom, float tallest)
    {
        var area = new Area(Draw.ScreenX(left), Draw.ScreenY(top), Draw.ScreenX(right), Draw.ScreenY(bottom));
        MenuArea = area;
        _home = new Area(area.Left - MoveX, area.Top - MoveY, area.Right - MoveX, Draw.ScreenY(MathF.Max(bottom, tallest)) - MoveY);
        _homeKnown = true;
    }

    protected const float GuideCapSize = 10f;
    protected const float GuideLineHeight = 22f;
    const float GuideKeyGap = 4f;
    const float GuideLabelGap = 12f;
    const float GuideLabelSize = 9f;
    const float GuideLabelSpacing = 2f;

    // How wide the lines are, keys and labels together.
    protected static float GuideWidth(GuideView view)
    {
        var keys = GuideKeysWidth(view);
        var labels = 0f;
        foreach (var line in view.Lines)
        {
            labels = MathF.Max(labels, Draw.TrackedWidth(line.Label, GuideLabelSize, GuideLabelSpacing));
        }

        return keys + GuideLabelGap + labels;
    }

    static float GuideKeysWidth(GuideView view)
    {
        var widest = 0f;
        foreach (var line in view.Lines)
        {
            var width = -GuideKeyGap;
            foreach (var key in line.Keys)
            {
                width += Kit.CapWidth(key, GuideCapSize) + GuideKeyGap;
            }

            widest = MathF.Max(widest, width);
        }

        return widest;
    }

    protected static void PlaceGuide(float width, float height, float slide = 0f)
    {
        var zoom = Look.Zoom;
        Draw.Place(DashMargin - slide * zoom, (Draw.ScreenHeight - height * zoom) / 2f, zoom);
    }

    // Each line's keys in a column, and what they do after them. Slant moves each line left by
    // that much for every unit down, for themes that lean.
    protected static void GuideLines(GuideView view, float left, float top, Rgba frame, Rgba key, Rgba label, float slant = 0f)
    {
        var labelLeft = GuideKeysWidth(view) + GuideLabelGap;
        for (var i = 0; i < view.Lines.Count; i++)
        {
            var line = view.Lines[i];
            var middle = top + (i + 0.5f) * GuideLineHeight;
            var x = left - slant * middle;
            var at = x;
            foreach (var name in line.Keys)
            {
                at += Kit.Cap(name, at, middle, GuideCapSize, frame, key) + GuideKeyGap;
            }

            Draw.Tracked(line.Label, x + labelLeft, middle - GuideLabelSize / 2f, GuideLabelSize, GuideLabelSpacing, label);
        }
    }

    const float DashMargin = 40f;

    // Room under it for the game's own HUD along the bottom of the screen.
    const float DashLift = 110f;

    // Puts a readout of this size where the player chose, at the menu's size. Slide moves it out
    // towards its edge by that many units, for themes that slide it in. On the car it is laid
    // onto a panel in the world instead, and false means that panel can't be seen.
    protected static bool PlaceDash(DashView view, float width, float height, float slide = 0f)
    {
        var zoom = Look.Zoom;
        var spot = view.Spot;
        if (spot == DashSpot.OnCar)
        {
            if (Hologram.Available)
            {
                return Hologram.Place(view.Panel, width, height);
            }

            spot = DashSpot.MiddleRight;
        }

        var left = spot is DashSpot.MiddleLeft or DashSpot.BottomLeft;
        var x = left ? DashMargin - slide * zoom : Draw.ScreenWidth - DashMargin - (width - slide) * zoom;
        var y = spot is DashSpot.MiddleLeft or DashSpot.MiddleRight
            ? (Draw.ScreenHeight - height * zoom) / 2f
            : Draw.ScreenHeight - DashMargin - DashLift - height * zoom;
        Draw.Place(x, y, zoom);
        return true;
    }

    protected const float DashSpeedSize = 34f;

    // The speed in large figures with its units after them, sitting on the same line. Returns
    // where the units end.
    protected static float DashSpeed(DashView view, float left, float top, Rgba figures, Rgba units)
    {
        Draw.Text(view.Speed, left, top, DashSpeedSize, figures);
        var x = left + Draw.TextWidth(view.Speed, DashSpeedSize) + 6f;
        return Draw.Tracked(view.Units, x, top + DashSpeedSize - 14f, 9f, 2f, units);
    }

    // A small label, a thin bar filled to the value, and the value at the right end.
    protected static void DashBar(float left, float right, float y, string label, float fill, string value, Rgba labelColor, Rgba lit, Rgba empty, Rgba valueColor)
    {
        const float size = 9f;
        const float labelRoom = 58f;
        const float valueRoom = 38f;
        const float thick = 3f;

        Draw.Tracked(label, left, y, size, 2f, labelColor);
        var barLeft = left + labelRoom;
        var barWidth = right - valueRoom - barLeft;
        var middle = y + size / 2f - thick / 2f;
        Draw.Rect(barLeft, middle, barWidth, thick, empty);
        Draw.Rect(barLeft, middle, barWidth * Math.Clamp(fill, 0f, 1f), thick, lit);
        Draw.TextRight(value, right, y - 1f, 11f, valueColor);
    }

    // A small label with a value after it, and the units after that if there are any.
    protected static void DashStat(float x, float y, string label, string value, string units, Rgba labelColor, Rgba valueColor)
    {
        var at = Draw.Tracked(label, x, y + 1f, 9f, 2f, labelColor) + 8f;
        Draw.Text(value, at, y, 11f, valueColor);
        if (units.Length > 0)
        {
            Draw.Tracked(units, at + Draw.TextWidth(value, 11f) + 4f, y + 1f, 8f, 2f, labelColor);
        }
    }

    // The distance the autopilot has left, at the right edge beside the speed.
    protected static void DashNote(DashView view, float right, float top, Rgba value, Rgba label)
    {
        if (view.Note.Length == 0)
        {
            return;
        }

        Draw.TextRight(view.Note, right, top, 13f, value);
        Draw.TextRight("TO GO", right, top + 17f, 9f, label);
    }

    // Which lines of the tab are on show, and where the highlight is among them, in lines from
    // the first one shown. The list scrolls to keep the selected row near the middle.
    internal readonly record struct Window(int First, int Last, float Highlight);

    internal static Window Scroll(MenuView view, int visible)
    {
        var count = view.Lines.Count;
        var selected = view.Tab.LineOf(view.RowIndex);
        var first = Math.Clamp(selected - visible / 2, 0, Math.Max(0, count - visible));
        var last = Math.Min(count, first + visible);
        var highlight = Math.Clamp(view.GlideLine - first, 0f, Math.Max(0, last - first - 1));
        return new Window(first, last, highlight);
    }

    // Where the top of the list would be if it scrolled smoothly, in lines. The rows move a whole
    // line at a time, but a scroll bar can follow this and glide.
    protected static float ScrollGlide(MenuView view, int visible)
    {
        return Math.Clamp(view.GlideLine - visible / 2, 0f, Math.Max(0, view.Lines.Count - visible));
    }

    // The first tab on show the last time the tabs didn't all fit. It is kept so the strip only
    // moves when the current tab would go off one end.
    static int _tabFirst;

    // Which tabs are drawn and where the first of them starts. Last is one past the final one.
    internal readonly record struct TabStrip(int First, int Last, float Start);

    // Fits the tabs between two edges when there may be more than fit. A tab is its name plus
    // pad wide, with gap between tabs. An end with tabs hidden past it keeps mark of the room
    // free, so the theme can show there are more that way. When only the left end hides tabs,
    // the ones on show sit against the right edge and the spare room is on the left.
    internal static TabStrip FitTabs(MenuView view, float left, float right, float size, float spacing, float pad, float gap, float mark)
    {
        var tabs = view.Page.Tabs;
        var count = tabs.Count;
        if (count == 0)
        {
            return new TabStrip(0, 0, left);
        }

        // The last tab on show needs no gap after it.
        var room = right - left + gap;
        var extra = pad + gap;

        // Where the strip ends if it starts at a given tab.
        int End(int first)
        {
            var left = room - (first > 0 ? mark : 0f);
            var i = first;
            while (i < count)
            {
                var width = Draw.TrackedWidth(tabs[i].Name, size, spacing) + extra;
                if (width + (i + 1 < count ? mark : 0f) > left)
                {
                    break;
                }

                left -= width;
                i++;
            }

            return i;
        }

        if (End(0) == count)
        {
            _tabFirst = 0;
            return new TabStrip(0, count, left);
        }

        var current = Math.Clamp(view.TabIndex, 0, count - 1);
        var start = Math.Clamp(_tabFirst, 0, current);
        var end = End(start);
        while (end <= current && start < current)
        {
            start++;
            end = End(start);
        }

        // Room left over at the far end brings earlier tabs back.
        while (start > 0 && end == count && End(start - 1) == count)
        {
            start--;
        }

        _tabFirst = start;
        end = Math.Max(end, start + 1);
        if (start == 0 || end < count)
        {
            return new TabStrip(start, end, left + (start > 0 ? mark : 0f));
        }

        var width = -gap;
        for (var i = start; i < end; i++)
        {
            width += Draw.TrackedWidth(tabs[i].Name, size, spacing) + extra;
        }

        return new TabStrip(start, end, MathF.Max(left + mark, right - width));
    }

    // How many lines of help to leave room for. It is the longest help on the current tab, so
    // the space fits the tab and still doesn't jump while moving between its rows.
    protected static int HelpRoom(MenuView view, float size, float width, int most)
    {
        var lines = 1;
        foreach (var row in view.Rows)
        {
            if (!string.IsNullOrEmpty(row.Help))
            {
                lines = Math.Max(lines, Draw.Wrap(row.Help, size, width).Length);
            }
        }

        return Math.Min(lines, most);
    }

    internal readonly record struct HintStyle(float Size, float CapHeight, float LineHeight, Rgba Frame, Rgba Key, Rgba Label);

    // How many lines the key hints take between two edges. Themes use it to leave room.
    internal static int HintLines(MenuView view, float left, float right, HintStyle style)
    {
        return FlowHints(view, left, right, 0f, style, draw: false);
    }

    // Key names in small boxes, each followed by what it does. They wrap onto the next line.
    internal static void DrawHints(MenuView view, float left, float right, float top, HintStyle style)
    {
        FlowHints(view, left, right, top, style, draw: true);
    }

    static int FlowHints(MenuView view, float left, float right, float top, HintStyle style, bool draw)
    {
        // Spacing follows the text size, so the same code serves small and large hints.
        var capInset = style.Size * 0.5f;
        var capGap = style.Size * 0.35f;
        var labelGap = style.Size * 0.45f;
        var groupGap = style.Size * 1.4f;

        var x = left;
        var y = top;
        var lines = view.Hints.Count > 0 ? 1 : 0;
        var textTop = (style.CapHeight - style.Size) / 2f;

        foreach (var hint in view.Hints)
        {
            var labelWidth = Draw.TextWidth(hint.Label, style.Size);
            var width = labelWidth + labelGap;
            foreach (var key in hint.Keys)
            {
                width += Draw.TextWidth(key, style.Size) + 2f * capInset + capGap;
            }

            if (x + width > right && x > left)
            {
                x = left;
                y += style.LineHeight;
                lines++;
            }

            foreach (var key in hint.Keys)
            {
                var cap = Draw.TextWidth(key, style.Size) + 2f * capInset;
                if (draw)
                {
                    Draw.Frame(x, y, cap, style.CapHeight, 1f, style.Frame);
                    Draw.Text(key, x + capInset, y + textTop, style.Size, style.Key);
                }

                x += cap + capGap;
            }

            if (draw)
            {
                Draw.Text(hint.Label, x + labelGap, y + textTop, style.Size, style.Label);
            }

            x += labelWidth + labelGap + groupGap;
        }

        return lines;
    }
}
