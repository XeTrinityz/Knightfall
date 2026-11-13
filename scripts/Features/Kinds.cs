#nullable enable

using System.Globalization;

namespace Knightfall;

sealed class Toggle : Feature
{
    readonly bool _default;
    readonly bool _saved;

    // An unsaved toggle is off again every time the game starts, for things that can't be put
    // back on from a cold start, like a disguise.
    public Toggle(string id, string label, string help, bool on = false, bool saved = true)
        : base(id, label, help)
    {
        _default = on;
        _saved = saved;
        On = on;
    }

    public bool On { get; private set; }

    public override bool IsDefault => On == _default;

    public override string ValueText => On ? "ON" : "OFF";

    public override object? Stored => _saved ? On : null;

    public override void Load(object? stored) => On = _saved && stored is bool on ? on : _default;

    // Shows a state the game decided, without counting as the player changing it.
    public void Show(bool on) => On = on;

    public override void Reset() => Set(_default);

    public override void Step(int direction) => Set(direction > 0);

    public override void Press() => Set(!On);

    // Changes it the way the player would, for a row that sets others.
    public void SetTo(bool on) => Set(on);

    void Set(bool on)
    {
        if (on != On)
        {
            On = on;
            Catalog.Touched(this);
        }
    }
}

sealed class Slider : Feature
{
    readonly double _default;
    readonly string _unit;
    readonly string? _zero;
    readonly bool _saved;
    string? _text;

    // zero is shown in place of 0, for sliders where 0 means the option is off. An unsaved slider
    // shows something live, like how far the editor has moved the selected object.
    public Slider(string id, string label, string help, double min, double max, double step, double value, string unit = "", string? zero = null, bool saved = true)
        : base(id, label, help)
    {
        _saved = saved;
        Min = min;
        Max = max;
        StepSize = step;
        _unit = unit;
        _zero = zero;
        _default = Snap(value);
        Value = _default;
        MinText = Format(min);
        MaxText = Format(max);
        DefaultText = Format(_default);
    }

    public string MinText { get; }

    public string MaxText { get; }

    public string DefaultText { get; }

    public float DefaultFill => Max > Min ? (float)((_default - Min) / (Max - Min)) : 0f;

    public double Min { get; }

    public double Max { get; }

    public double StepSize { get; }

    public double Value { get; private set; }

    // How far along the track the value is, 0 to 1.
    public float Fill => Max > Min ? (float)((Value - Min) / (Max - Min)) : 0f;

    public override bool IsDefault => Value == _default;

    public override string ValueText => _text ??= Format(Value);

    string Format(double value) => value == 0 && _zero is not null ? _zero : value.ToString("0.##", CultureInfo.InvariantCulture) + _unit;

    // Whole numbers are saved without a decimal point, so the file reads "50" and not "50.0".
    public override object? Stored => !_saved ? null : Value == Math.Floor(Value) ? (object)(long)Value : Value;

    // Shows a value the game decided, without counting as the player changing it.
    public void Show(double value)
    {
        Value = Snap(value);
        _text = null;
    }

    public override void Load(object? stored)
    {
        if (!_saved)
        {
            Value = _default;
            _text = null;
            return;
        }

        Value = stored switch
        {
            long whole => Snap(whole),
            double number when double.IsFinite(number) => Snap(number),
            _ => _default,
        };
        _text = null;
    }

    public override void Reset() => Set(_default);

    public override void Step(int direction) => Set(Value + direction * StepSize);

    public void SetTo(double value) => Set(value);

    public override void Press()
    {
    }

    void Set(double value)
    {
        value = Snap(value);
        if (value != Value)
        {
            Value = value;
            _text = null;
            Catalog.Touched(this);
        }
    }

    double Snap(double value)
    {
        var steps = Math.Round((Math.Clamp(value, Min, Max) - Min) / StepSize);
        return Math.Clamp(Math.Round(Min + steps * StepSize, 6), Min, Max);
    }
}

sealed class Choice : Feature
{
    readonly bool _saved;
    (string Id, string Label)[] _options;
    int _default;

    // An unsaved choice only picks something for a command to act on, like a skin to apply.
    public Choice(string id, string label, string help, (string Id, string Label)[] options, int index = 0, bool saved = true)
        : base(id, label, help)
    {
        if (options.Length == 0)
        {
            throw new ArgumentException($"Option '{id}' has nothing to choose from.");
        }

        _saved = saved;
        _options = options;
        _default = Math.Clamp(index, 0, options.Length - 1);
        Index = _default;
    }

    public int Index { get; private set; }

    // What accept does in place of stepping on, for a list whose pick is there to be used.
    public Action? Pressed { get; init; }

    public int Count => _options.Length;

    public string CurrentId => _options[Index].Id;

    // The value it goes back to on reset.
    public int DefaultIndex => _default;

    public string LabelAt(int index) => _options[Math.Clamp(index, 0, _options.Length - 1)].Label;

    public override bool IsDefault => !_saved || Index == _default;

    public override string ValueText => _options[Index].Label;

    public override object? Stored => _saved ? _options[Index].Id : null;

    // For a list that comes from the game. Only unsaved choices change their list.
    public void SetOptions((string Id, string Label)[] options, int index)
    {
        if (_saved || options.Length == 0)
        {
            return;
        }

        _options = options;
        _default = Math.Clamp(index, 0, options.Length - 1);
        Index = _default;
    }

    public override void Load(object? stored)
    {
        var found = stored is string id ? Array.FindIndex(_options, option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase)) : -1;
        Index = found >= 0 ? found : _default;
    }

    public override void Reset() => Set(_default);

    public override void Step(int direction) => Set((Index + direction + _options.Length) % _options.Length);

    public override void Press()
    {
        if (Pressed is null)
        {
            Step(1);
            return;
        }

        Pressed();
    }

    public void SetTo(int index) => Set(Math.Clamp(index, 0, _options.Length - 1));

    void Set(int index)
    {
        if (index != Index)
        {
            Index = index;
            Catalog.Touched(this);
        }
    }
}

enum OutcomeKind
{
    Done,
    Unchanged,
    Failed,
}

// What a command reports back. The text is short, because it is shown in place of RUN.
readonly record struct Outcome(OutcomeKind Kind, string Text)
{
    public static readonly Outcome NoPlayer = Failed("NO PLAYER");

    public static Outcome Done(string text = "DONE") => new(OutcomeKind.Done, text);

    public static Outcome Unchanged(string text) => new(OutcomeKind.Unchanged, text);

    public static Outcome Failed(string text) => new(OutcomeKind.Failed, text);
}

// A row that does something once when pressed. It has no value, and shows what happened for a
// moment instead.
sealed class Command : Feature
{
    public const double ShowSeconds = 2.5;

    readonly Func<Outcome> _run;
    Outcome _last;
    long _lastAt;

    public Command(string id, string label, string help, Func<Outcome> run)
        : base(id, label, help)
    {
        _run = run;
    }

    // What the row's button says while it has no result to show.
    public string Cap { get; init; } = "RUN";

    // A quiet command shows no card when its key is pressed, for a key shared with the game, like
    // Firefly's take off on the run key.
    public bool Quiet { get; init; }

    public void Execute()
    {
        Outcome outcome;
        try
        {
            outcome = _run();
        }
        catch
        {
            // Shown as failed, and the error itself goes on to the log.
            Report(Outcome.Failed("FAILED"));
            throw;
        }

        Report(outcome);
    }

    // For a command whose work finishes later, like an autopilot arriving.
    public void Report(Outcome outcome)
    {
        _last = outcome;
        _lastAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Toasts.Reported(this, outcome);
    }

    // The last result, while it is still fresh enough to show.
    public bool Showing(long now, out Outcome outcome)
    {
        outcome = _last;
        return _lastAt != 0 && (now - _lastAt) / (double)System.Diagnostics.Stopwatch.Frequency < ShowSeconds;
    }

    public override bool IsDefault => true;

    public override string ValueText => "";

    public override object? Stored => null;

    public override void Load(object? stored)
    {
    }

    public override void Reset()
    {
    }

    public override void Step(int direction)
    {
    }

    public override void Press() => Catalog.Pressed(this);
}
