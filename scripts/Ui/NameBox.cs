#nullable enable

using System.Diagnostics;
using System.Text;

namespace Knightfall;

// A box over the screen for typing a name. A keyboard types straight in. A pad moves over a
// letter board and presses letters, so naming works without a keyboard.
static class NameBox
{
    sealed class Cover : Overlay
    {
        public override void Keys() => NameBox.Input();

        public override bool TakesText => true;

        public override void Render(MenuView view) => NameBox.Render(view);

        public override void Shown() => Take(true);

        public override void Gone() => Take(false);
    }

    readonly record struct Cell(int Row, int Column, int Span, string Label, char? Letter);

    public const int Longest = 24;

    const float Width = 560f;
    const float CellHeight = 34f;
    const float CellGap = 5f;
    const float Inset = 22f;
    const float InSeconds = 0.18f;
    const double ArmSeconds = 0.2;
    const double RepeatAfter = 0.4;
    const double RepeatEvery = 0.035;

    const string Space = "SPACE";
    const string Delete = "DEL";
    const string Save = "SAVE";

    static readonly Cover Instance = new();
    static readonly Rgba Shade = new(0, 0, 0, 170);
    static readonly Rgba Panel = new(6, 16, 28, 236);
    static readonly Rgba PanelEdge = new(70, 100, 125, 150);
    static readonly Rgba CellBack = new(8, 20, 34, 240);
    static readonly Rgba CellEdge = new(70, 100, 125, 110);
    static readonly Rgba Pale = new(206, 228, 240);
    static readonly Rgba Dim = new(130, 160, 180);
    static readonly Rgba Ghost = new(84, 110, 130);
    static readonly Rgba Key = new(184, 192, 248);
    static readonly Rgba Amber = new(250, 184, 78);

    static readonly Cell[] Cells = Build();

    static readonly Keys[] Typed = BuildTyped();

    // Every key on the keyboard. All of them are kept from the game while the box is up, since
    // the game binds keys like Ctrl and the letters to its own moves.
    static readonly Keys[] Everything = Enumerable.Range(8, 247).Select(code => (Keys)code)
        .Where(key => key is not (Keys.LWin or Keys.RWin or Keys.Apps)).Distinct().ToArray();

    static readonly HashSet<Keys> Taken = new();

    static readonly StringBuilder Text = new();
    static string _title = "";
    static string _tag = "";
    static Func<string, Outcome>? _done;
    static Func<string, bool>? _taken;
    static string _replaces = "";
    static string? _takenFor;
    static bool _takenNow;
    static bool _startTaken;
    static int _at;
    static long _openedAt;
    static bool _startHeld;
    static bool _armed;
    static Outcome? _refused;
    static bool _all;

    // The letter board only shows for a pad. Typing on a keyboard folds it away.
    static bool _board;
    static object? _repeating;
    static long _repeatAt;

    // Opens the box with a name to start from. done gets the name on save, and closes the box
    // unless it fails. taken says when a name is already used, to warn before replacing it.
    public static void Ask(string title, string tag, string start, Func<string, Outcome> done, Func<string, bool>? taken = null, string replaces = "SAVING REPLACES THAT NAME")
    {
        _title = title;
        _tag = tag;
        _done = done;
        _taken = taken;
        _replaces = replaces;
        _takenFor = null;
        _refused = null;
        Text.Clear().Append(start.Length > Longest ? start[..Longest] : start);
        _at = Array.FindIndex(Cells, cell => cell.Label == Save);
        _openedAt = Stopwatch.GetTimestamp();
        _startHeld = true;
        _armed = false;
        _all = false;
        _repeating = null;
        _board = Controls.LastDevice == Device.Pad;
        Menu.Lift(Instance);
    }

    static Cell[] Build()
    {
        var cells = new List<Cell>();
        void Letters(int row, string letters)
        {
            for (var i = 0; i < letters.Length; i++)
            {
                cells.Add(new Cell(row, i, 1, letters[i].ToString(), letters[i]));
            }
        }

        Letters(0, "1234567890");
        Letters(1, "QWERTYUIOP");
        Letters(2, "ASDFGHJKL-");
        Letters(3, "ZXCVBNM");
        cells.Add(new Cell(3, 7, 3, Delete, null));
        cells.Add(new Cell(4, 0, 5, Space, ' '));
        cells.Add(new Cell(4, 5, 5, Save, null));
        return cells.ToArray();
    }

    static Keys[] BuildTyped()
    {
        var keys = new List<Keys>();
        for (var key = Keys.A; key <= Keys.Z; key++)
        {
            keys.Add(key);
        }

        for (var key = Keys.D0; key <= Keys.D9; key++)
        {
            keys.Add(key);
        }

        keys.AddRange(new[] { Keys.NumPad0, Keys.NumPad1, Keys.NumPad2, Keys.NumPad3, Keys.NumPad4, Keys.NumPad5, Keys.NumPad6, Keys.NumPad7, Keys.NumPad8, Keys.NumPad9 });
        keys.AddRange(new[] { Keys.Space, Keys.Back, Keys.OemMinus, Keys.Subtract });
        return keys.ToArray();
    }

    // A key changes hands only while it's up. Taking a held key would leave it stuck down in the
    // game, so the rest are taken as they're let go.
    static void Take(bool take)
    {
        if (!take)
        {
            foreach (var key in Taken)
            {
                Swallowed.Give(key);
            }

            Taken.Clear();
            if (_startTaken)
            {
                Swallowed.Give(Pad.Button.Start);
                _startTaken = false;
            }

            return;
        }

        // Start saves the name, so the game mustn't also take it for its pause menu.
        if (!_startTaken && Pad.Connected && !Pad.IsDown(Pad.Button.Start))
        {
            Swallowed.Take(Pad.Button.Start);
            _startTaken = true;
        }

        foreach (var key in Everything)
        {
            if (!Taken.Contains(key) && !Keyboard.IsDown(key))
            {
                Swallowed.Take(key);
                Taken.Add(key);
            }
        }
    }

    static char? CharOf(Keys key) => key switch
    {
        >= Keys.A and <= Keys.Z => (char)('A' + (key - Keys.A)),
        >= Keys.D0 and <= Keys.D9 => (char)('0' + (key - Keys.D0)),
        >= Keys.NumPad0 and <= Keys.NumPad9 => (char)('0' + (key - Keys.NumPad0)),
        Keys.Space => ' ',
        Keys.OemMinus or Keys.Subtract => '-',
        _ => null,
    };

    static void Type(char letter)
    {
        if (_all)
        {
            Text.Clear();
            _all = false;
        }

        // No space at the start, and no two in a row, so the file name stays tidy.
        if (Text.Length >= Longest || letter == ' ' && (Text.Length == 0 || Text[^1] == ' '))
        {
            return;
        }

        Text.Append(letter);
        _refused = null;
    }

    static void Erase()
    {
        if (_all)
        {
            Text.Clear();
            _all = false;
        }
        else if (Text.Length > 0)
        {
            Text.Length--;
        }

        _refused = null;
    }

    static void Input()
    {
        Take(true);

        // The press that opened the box still reads as down for a moment, and would save or type
        // at once. Nothing counts until every key that confirms has been let go.
        if (!_armed)
        {
            var settled = Stopwatch.GetElapsedTime(_openedAt).TotalSeconds >= ArmSeconds;
            _armed = settled && !Keyboard.IsDown(Keys.Enter) && !Pad.IsDown(Pad.Button.Start) && !Pad.IsDown(Controls.ButtonFor(Act.Accept));
            return;
        }

        var ctrl = Keyboard.IsDown(Keys.ControlKey) || Keyboard.IsDown(Keys.LControlKey) || Keyboard.IsDown(Keys.RControlKey);
        if (ctrl)
        {
            Shortcuts();
        }
        else
        {
            foreach (var key in Typed)
            {
                if (Fires(key, Keyboard.WasPressed(key), Keyboard.IsDown(key)))
                {
                    _board = false;
                    if (key == Keys.Back)
                    {
                        Erase();
                    }
                    else if (CharOf(key) is { } letter)
                    {
                        Type(letter);
                    }
                }
            }
        }

        // Enter on a keyboard always saves. A keyboard user is typing, not on the board.
        if (Keyboard.WasPressed(Keys.Enter))
        {
            Finish();
            return;
        }

        var start = Pad.IsDown(Pad.Button.Start);
        if (start && !_startHeld)
        {
            _startHeld = true;
            Finish();
            return;
        }

        _startHeld = start;
        var pad = Controls.LastDevice == Device.Pad;
        if (pad && (Controls.Pressed(Act.Up) || Controls.Pressed(Act.Down) || Controls.Pressed(Act.Left) || Controls.Pressed(Act.Right)
            || Controls.Pressed(Act.Accept) || Controls.Pressed(Act.Favourite) || Controls.Pressed(Act.Bind)))
        {
            // The first press only brings the board back, so it doesn't type blind.
            if (!_board)
            {
                _board = true;
                return;
            }
        }

        if (!_board)
        {
            return;
        }

        if (Controls.Pressed(Act.Left) || Controls.Pressed(Act.Right))
        {
            var step = Controls.Pressed(Act.Left) ? -1 : 1;
            var row = Cells[_at].Row;
            var inRow = Array.FindAll(Cells, cell => cell.Row == row);
            var index = Array.IndexOf(inRow, Cells[_at]);
            _at = Array.IndexOf(Cells, inRow[(index + step + inRow.Length) % inRow.Length]);
        }

        if (Controls.Pressed(Act.Up) || Controls.Pressed(Act.Down))
        {
            var rows = Cells.Max(cell => cell.Row) + 1;
            var row = (Cells[_at].Row + (Controls.Pressed(Act.Up) ? -1 : 1) + rows) % rows;
            var middle = Cells[_at].Column + Cells[_at].Span / 2f;
            _at = Array.FindIndex(Cells, cell => cell.Row == row && middle >= cell.Column && middle < cell.Column + cell.Span);
            if (_at < 0)
            {
                _at = Array.FindLastIndex(Cells, cell => cell.Row == row);
            }
        }

        if (pad && Controls.Pressed(Act.Accept))
        {
            Press(Cells[_at]);
        }

        // On a keyboard these acts are letters, which were typed above.
        var erase = Controls.ButtonFor(Act.Favourite);
        if (pad && Fires(erase, Controls.Pressed(Act.Favourite), Pad.IsDown(erase)))
        {
            Erase();
        }

        if (pad && Controls.Pressed(Act.Bind))
        {
            Type(' ');
        }
    }

    // A key or button fires when pressed, and again and again while it's held, like typing in
    // Windows. Only the last one pressed repeats.
    static bool Fires(object which, bool pressed, bool down)
    {
        var now = Stopwatch.GetTimestamp();
        if (pressed)
        {
            _repeating = which;
            _repeatAt = now + (long)(RepeatAfter * Stopwatch.Frequency);
            return true;
        }

        if (!down)
        {
            if (Equals(_repeating, which))
            {
                _repeating = null;
            }

            return false;
        }

        if (Equals(_repeating, which) && now >= _repeatAt)
        {
            _repeatAt = now + (long)(RepeatEvery * Stopwatch.Frequency);
            return true;
        }

        return false;
    }

    // Ctrl with A picks all, C copies, X cuts, V pastes and Backspace deletes the last word.
    static void Shortcuts()
    {
        if (Keyboard.WasPressed(Keys.A))
        {
            _all = Text.Length > 0;
        }
        else if (Keyboard.WasPressed(Keys.C))
        {
            Clipboard.Put(Text.ToString());
        }
        else if (Keyboard.WasPressed(Keys.X))
        {
            Clipboard.Put(Text.ToString());
            Text.Clear();
            _all = false;
        }
        else if (Keyboard.WasPressed(Keys.V))
        {
            foreach (var letter in Clipboard.Get().ToUpperInvariant())
            {
                if (char.IsAsciiLetterOrDigit(letter) || letter is ' ' or '-')
                {
                    Type(letter);
                }
                else if (letter is '_' or '\t')
                {
                    Type(' ');
                }
            }
        }
        else if (Fires("word", Keyboard.WasPressed(Keys.Back), Keyboard.IsDown(Keys.Back)))
        {
            if (_all)
            {
                Erase();
                return;
            }

            var end = Text.ToString().TrimEnd();
            var cut = end.LastIndexOf(' ');
            Text.Length = cut < 0 ? 0 : cut + 1;
            _refused = null;
        }
    }

    static void Press(Cell cell)
    {
        switch (cell.Label)
        {
            case Delete:
                Erase();
                break;
            case Save:
                Finish();
                break;
            default:
                if (cell.Letter is { } letter)
                {
                    Type(letter);
                }

                break;
        }
    }

    static void Finish()
    {
        var name = Text.ToString().Trim();
        if (name.Length == 0 || _done is null)
        {
            _refused = Outcome.Failed("TYPE A NAME");
            return;
        }

        var outcome = _done(name);
        if (outcome.Kind == OutcomeKind.Failed)
        {
            _refused = outcome;
            return;
        }

        _done = null;
        Menu.Uncover();
    }

    static void Render(MenuView view)
    {
        var accent = Viewer.Accent;
        var shown = view.Opened * Kit.EaseOut((float)((Stopwatch.GetTimestamp() - _openedAt) / (double)Stopwatch.Frequency) / InSeconds);
        Draw.Place(0f, 0f, 1f);
        Draw.Fade(shown);
        Draw.Rect(0f, 0f, Draw.ScreenWidth, Draw.ScreenHeight, Shade);

        var rows = Cells.Max(cell => cell.Row) + 1;
        var cellWidth = (Width - 2 * Inset - 9 * CellGap) / 10f;
        var gridHeight = _board ? rows * CellHeight + (rows - 1) * CellGap : 0f;
        var height = Inset + 18f + 16f + 52f + 14f + gridHeight + (_board ? 16f : 0f) + 18f + Inset;
        var x = (Draw.ScreenWidth - Width) / 2f;
        var y = (Draw.ScreenHeight - height) / 2f + (1f - shown) * 14f;

        Kit.Shadow(x, y, Width, height, 0f, 0f);
        Draw.Rect(x, y, Width, height, Panel);
        Draw.Frame(x, y, Width, height, 1f, PanelEdge);
        Kit.Corners(x - 1f, y - 1f, Width + 2f, height + 2f, 16f, accent);

        var line = y + Inset;
        Draw.Tracked(_tag, x + Inset, line, 10f, 3f, Amber);
        Draw.TextRight($"{Longest - Text.Length} LEFT", x + Width - Inset, line, 11f, Ghost);
        line += 18f;
        Draw.Tracked(_title, x + Inset, line, 18f, 2f, Pale);
        line += 16f + 10f;

        // The name, with a blinking caret and a line that lights while typing.
        var fieldHeight = 40f;
        Draw.Rect(x + Inset, line, Width - 2 * Inset, fieldHeight, new Rgba(0, 0, 0, 110));
        Draw.Frame(x + Inset, line, Width - 2 * Inset, fieldHeight, 1f, CellEdge);
        var name = Text.ToString();
        var textX = x + Inset + 12f;
        if (_all && name.Length > 0)
        {
            Draw.Rect(textX - 3f, line + 7f, Draw.TextWidth(name, 20f) + 6f, 26f, Rgba.Mix(Panel, accent, 0.45f));
        }

        Draw.Text(name, textX, line + 9f, 20f, Pale);
        if ((long)(view.Time * 2f) % 2 == 0)
        {
            Draw.Rect(textX + Draw.TextWidth(name, 20f) + 3f, line + 10f, 10f, 20f, accent);
        }

        Draw.Rect(x + Inset, line + fieldHeight - 2f, Width - 2 * Inset, 2f, accent);
        line += fieldHeight + 6f;

        // Whether the name is used is only asked again when the name changes.
        var trimmed = name.Trim();
        if (trimmed != _takenFor)
        {
            _takenFor = trimmed;
            _takenNow = _taken is not null && trimmed.Length > 0 && _taken(trimmed);
        }

        var note = _refused is { } refused ? refused.Text : _takenNow ? _replaces : "";
        if (note.Length > 0)
        {
            Draw.Tracked(note, x + Inset, line, 9f, 2f, _refused is not null ? Kit.Failed : Amber);
        }

        line += 14f + 6f;
        for (var i = 0; _board && i < Cells.Length; i++)
        {
            var cell = Cells[i];
            var cx = x + Inset + cell.Column * (cellWidth + CellGap);
            var cy = line + cell.Row * (CellHeight + CellGap);
            var cw = cell.Span * cellWidth + (cell.Span - 1) * CellGap;
            var picked = i == _at;
            var save = cell.Label == Save;
            Draw.Rect(cx, cy, cw, CellHeight, picked ? Rgba.Mix(CellBack, accent, 0.28f) : CellBack);
            Draw.Frame(cx, cy, cw, CellHeight, 1f, picked ? accent : save ? Rgba.Mix(CellEdge, accent, 0.6f) : CellEdge);
            var size = cell.Letter is not null && cell.Label.Length == 1 ? 15f : 11f;
            var label = cell.Label;
            var width = cell.Label.Length == 1 ? Draw.TextWidth(label, size) : Draw.TrackedWidth(label, size, 2f);
            var labelX = cx + (cw - width) / 2f;
            var labelY = cy + (CellHeight - size) / 2f - 1f;
            var colour = picked ? Pale : save ? accent : Dim;
            if (cell.Label.Length == 1)
            {
                Draw.Text(label, labelX, labelY, size, colour);
            }
            else
            {
                Draw.Tracked(label, labelX, labelY, size, 2f, colour);
            }
        }

        line += gridHeight + (_board ? 16f : 0f);
        var keyboard = !_board;
        var hints = keyboard
            ? new[] { ("TYPE", "A-Z 0-9"), ("ENTER", "SAVE"), ("BACKSPACE", "DELETE"), ("CTRL A C X V", "EDIT"), (Controls.KeyLabel(Act.Back), "BACK") }
            : new[] { (Controls.PadLabel(Act.Accept), "TYPE"), (Controls.PadLabel(Act.Favourite), "DELETE"), (Controls.PadLabel(Act.Bind), "SPACE"), (Pad.Describe(Pad.Button.Start).ToUpperInvariant(), "SAVE"), (Controls.PadLabel(Act.Back), "BACK") };
        var hintX = x + Inset;
        foreach (var (key, what) in hints)
        {
            hintX += Kit.Cap(key, hintX, line + 6f, 10f, CellEdge, Key) + 6f;
            hintX = Draw.Tracked(what, hintX, line + 1f, 9f, 2f, Dim) + 16f;
        }

        Draw.Fade(1f);
    }
}
