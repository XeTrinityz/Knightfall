#nullable enable

using System.Diagnostics;

namespace Knightfall;

// What a notification shows, for the theme to draw.
sealed class ToastView
{
    // Row is where the card sits in the stack. It glides between whole rows as cards come and go.
    public readonly record struct Card(string Title, string Text, OutcomeKind Kind, float Shown, float Left, float Row);

    public readonly List<Card> Cards = new();

    // Where the stack is, in screen units. X is its left edge. Y is the top of the first row
    // when it grows down, or the bottom of it when it grows up.
    public float X;
    public float Y;

    // True when cards slide in from the right, and when the stack grows down the screen.
    public bool Right;
    public bool Down;

    // The size of one card, in units, and the zoom the stack is drawn at.
    public float Height;
    public float Zoom = 1f;

    // Seconds on a clock that keeps running. For things that loop.
    public float Time;
}

// Short notes in a corner of the screen, for things done away from the menu: hotkeys, the radial,
// EDITOR MODE keys. Each option has one card at a time, so pressing a key again updates it instead
// of stacking up. A command shows what it reported, even when that comes later, like an arrival.
static class Toasts
{
    const float Life = 2.6f;
    const float InSeconds = 0.18f;
    const float OutSeconds = 0.3f;
    const int Most = 4;

    // How quickly a card glides to its new row, per second.
    const float RowEase = 14f;

    // Where the stack sits in its corner, in units.
    const float Side = 28f;
    const float Top = 96f;
    const float Bottom = 150f;

    // The room left between the stack and the menu, or the edge, when it moves out of the way.
    const float Clear = 12f;

    // A command that hasn't reported in this long is forgotten.
    const float Patience = 60f;

    sealed class Toast
    {
        public required string Title;
        public required string Text;
        public OutcomeKind Kind;
        public Feature? From;
        public long Born;
        public float Row = -1f;
    }

    static readonly List<Toast> Live = new();
    static readonly Dictionary<Command, long> Waiting = new();
    static readonly ToastView View = new();
    static long _drawnAt;

    // Each failing key shows at most once in this long, since a broken tick fails every tick.
    const double BrokeSeconds = 30;
    static readonly Dictionary<string, long> BrokeAt = new(StringComparer.Ordinal);

    public static readonly Toggle On = new(
        "settings.general.notifications", "NOTIFICATIONS",
        "Short notes in a corner of the screen when a hotkey, the radial or EDITOR MODE does something.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Choice Corner = new(
        "settings.general.notification_corner", "NOTIFICATION CORNER",
        "Which corner of the screen notifications show in.",
        new[] { ("top_right", "TOP RIGHT"), ("top_left", "TOP LEFT"), ("bottom_right", "BOTTOM RIGHT"), ("bottom_left", "BOTTOM LEFT") })
    {
        Preference = true,
    };

    public static void Show(string title, string text, OutcomeKind kind = OutcomeKind.Done, Feature? from = null)
    {
        if (!On.On)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var known = from is null ? null : Live.Find(toast => ReferenceEquals(toast.From, from));
        if (known is not null)
        {
            // Kept where it is, so it doesn't play its arrival again.
            known.Text = text;
            known.Kind = kind;
            known.Born = now - (long)(InSeconds * Stopwatch.Frequency);
            return;
        }

        Live.Insert(0, new Toast { Title = title, Text = text, Kind = kind, From = from, Born = now });
        if (Live.Count > Most)
        {
            Live.RemoveAt(Live.Count - 1);
        }
    }

    // Something failed in the game tick or a frame. It's in the log, and this says where to look.
    // Drawing is left out, so a broken card can't keep making cards.
    public static void Broke(string key)
    {
        if (key.EndsWith("draw", StringComparison.Ordinal))
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (BrokeAt.TryGetValue(key, out var last) && (now - last) / (double)Stopwatch.Frequency < BrokeSeconds)
        {
            return;
        }

        BrokeAt[key] = now;
        var feature = Catalog.Find(key);
        var title = feature?.Plain ?? key[(key.LastIndexOf('.') + 1)..].Replace('_', ' ').ToUpperInvariant();
        Show(title, "FAILED, SEE KNIGHTFALL.LOG", OutcomeKind.Failed, feature);
    }

    // After an option was used from outside the menu. A toggle, choice or slider shows its new
    // value straight away. A command shows its result once it reports.
    public static void Used(Feature feature)
    {
        switch (feature)
        {
            case Toggle toggle:
                Show(toggle.Plain, toggle.On ? "ON" : "OFF", toggle.On ? OutcomeKind.Done : OutcomeKind.Unchanged, toggle);
                break;
            case Choice or Slider:
                Show(feature.Plain, feature.ValueText, feature.IsDefault ? OutcomeKind.Unchanged : OutcomeKind.Done, feature);
                break;
            case Command { Quiet: true }:
                break;
            case Command command:
                Waiting[command] = Stopwatch.GetTimestamp();
                break;
        }
    }

    // Runs a command now and shows what it says.
    public static void Run(Command command)
    {
        Used(command);
        command.Execute();
    }

    // Called by every command when it reports.
    public static void Reported(Command command, Outcome outcome)
    {
        if (Waiting.Remove(command, out var since)
            && (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency <= Patience)
        {
            Show(command.Plain, outcome.Text, outcome.Kind, command);
        }
    }

    public static void Render(BmSDK.Engine.Canvas canvas)
    {
        if (Live.Count == 0 || World.Paused)
        {
            // So the next card starts where it belongs instead of gliding from the old spot.
            View.Cards.Clear();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var seconds = _drawnAt == 0 ? 0f : MathF.Min((float)((now - _drawnAt) / (double)Stopwatch.Frequency), 0.1f);
        _drawnAt = now;
        var glide = 1f - MathF.Exp(-RowEase * seconds);
        var settle = View.Cards.Count == 0;
        View.Cards.Clear();
        for (var i = Live.Count - 1; i >= 0; i--)
        {
            var age = (float)((now - Live[i].Born) / (double)Stopwatch.Frequency);
            if (age >= Life + OutSeconds)
            {
                Live.RemoveAt(i);
            }
        }

        for (var i = 0; i < Live.Count; i++)
        {
            var toast = Live[i];
            toast.Row = toast.Row < 0f ? i : toast.Row + (i - toast.Row) * glide;
            var age = (float)((now - toast.Born) / (double)Stopwatch.Frequency);
            var shown = age < InSeconds ? Ease(age / InSeconds) : age > Life ? 1f - Ease((age - Life) / OutSeconds) : 1f;
            View.Cards.Add(new ToastView.Card(toast.Title, toast.Text, toast.Kind, Math.Clamp(shown, 0f, 1f), Math.Clamp(1f - age / Life, 0f, 1f), toast.Row));
        }

        if (View.Cards.Count == 0 || !Draw.Begin(canvas))
        {
            return;
        }

        try
        {
            var theme = Theme.Current;
            Stack(theme, settle ? 1f : glide);
            View.Time = (float)(now / (double)Stopwatch.Frequency % 3600.0);
            theme.RenderToasts(View);
        }
        finally
        {
            Draw.Fade(1f);
            Draw.End();
        }
    }

    // Works out where the stack goes. It sits in the chosen corner unless the menu covers that
    // spot. Then it moves just past the menu, below or above it or to its side, whichever is the
    // shorter move that still fits on screen. It glides there, and back when the menu shuts.
    static void Stack(Theme theme, float glide)
    {
        var zoom = Look.Zoom;
        var width = theme.ToastWidth * zoom;
        var tall = Most * (theme.ToastHeight + Theme.ToastGap) * zoom;
        var gap = Clear * zoom;
        var right = Corner.Index is 0 or 2;
        var down = Corner.Index is 0 or 1;
        var x = right ? Draw.ScreenWidth - Side - width : Side;
        var y = down ? Top : Draw.ScreenHeight - Bottom;

        if (Theme.MenuArea is { } menu && menu.Overlaps(Room(x, y, width, tall, down)))
        {
            var best = float.MaxValue;
            var (toX, toY) = (x, y);

            var aside = right ? menu.Left - gap - width : menu.Right + gap;
            if (aside >= Clear && aside + width <= Draw.ScreenWidth - Clear)
            {
                best = MathF.Abs(aside - x);
                (toX, toY) = (aside, y);
            }

            // Moving up or down wins a tie, so the stack stays at its own side of the screen.
            var past = down ? menu.Bottom + gap : menu.Top - gap;
            var fits = down ? past + tall <= Draw.ScreenHeight - Clear : past - tall >= Clear;
            if (fits && MathF.Abs(past - y) <= best)
            {
                (toX, toY) = (x, past);
            }

            (x, y) = (toX, toY);
        }

        View.X += (x - View.X) * glide;
        View.Y += (y - View.Y) * glide;
        View.Right = right;
        View.Down = down;
        View.Height = theme.ToastHeight;
        View.Zoom = zoom;
    }

    static Theme.Area Room(float x, float y, float width, float tall, bool down)
    {
        return down ? new Theme.Area(x, y, x + width, y + tall) : new Theme.Area(x, y - tall, x + width, y);
    }

    static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var left = 1f - t;
        return 1f - left * left * left;
    }
}
