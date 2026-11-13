#nullable enable

namespace Knightfall;

// A job a row gives one of the menu's keys, with the name the key strip shows for it.
readonly record struct RowKey(string Name, Action Run);

// One option, declared once. The menu, settings.toml and ALL OFF are all built from these.
abstract class Feature
{
    protected Feature(string id, string label, string help)
    {
        var dot = id.LastIndexOf('.');
        if (dot <= 0 || dot == id.Length - 1)
        {
            throw new ArgumentException($"Option id '{id}' must look like page.tab.name.");
        }

        Id = id;
        Section = id[..dot];
        Key = id[(dot + 1)..];
        Plain = label;
        Help = help;
    }

    public string Id { get; }

    // Where the value lives in settings.toml.
    public string Section { get; }

    public string Key { get; }

    // The name as declared. Label adds the row's hotkey, for the menu.
    public string Plain { get; }

    public string Label => Hotkeys.Decorate(this);

    public string Help { get; }

    // Look and feel. ALL OFF leaves these alone.
    public bool Preference { get; init; }

    // A row that only lasts as long as the list it is in, like one save. It can't hold a hotkey
    // or go on the radial, so those two keys are free to do something of the row's own.
    public bool Passing { get; init; }

    public RowKey? BindKey { get; init; }

    public RowKey? FavouriteKey { get; init; }

    // What the key strip calls pressing the row, when SELECT doesn't say enough.
    public string? PressName { get; init; }

    // Says why the row can't be used right now, or null when it can. It is asked every frame the
    // row is on screen, so it has to be cheap. The menu draws such a row stepped back with the
    // reason where its value goes, and a hotkey or the radial shows the reason as a note.
    public Func<string?>? Block { get; init; }

    public string? Blocked => Block?.Invoke();

    // Runs every game tick while the option is away from normal. For options that have to keep
    // doing something, like healing.
    public Action? Tick { get; init; }

    // Runs once on the next game tick after the option goes back to normal. For options that
    // changed something in the game and have to put it back.
    public Action? Restore { get; init; }

    // Runs straight after the player changes the value, for options that keep it somewhere of
    // their own.
    public Action? Changed { get; init; }

    // Runs on the next game tick after the menu opens. For lists that come from the game.
    public Action? Opening { get; init; }

    // When the player last changed it, as a Stopwatch timestamp.
    public long TouchedAt { get; set; }

    // 1 right after a change, fading to 0. Themes use it to flash the new value.
    public float Glow(long now)
    {
        const double seconds = 0.4;
        if (TouchedAt == 0)
        {
            return 0f;
        }

        var age = (now - TouchedAt) / (double)System.Diagnostics.Stopwatch.Frequency;
        return age >= seconds ? 0f : (float)(1.0 - age / seconds);
    }

    public abstract bool IsDefault { get; }

    public abstract string ValueText { get; }

    // What goes into settings.toml, or null when there is nothing to save.
    public abstract object? Stored { get; }

    // Takes a value read from settings.toml. Anything unusable becomes the default.
    public abstract void Load(object? stored);

    public abstract void Reset();

    // Left and right in the menu.
    public abstract void Step(int direction);

    // Accept in the menu.
    public virtual void Press() => Step(1);
}
