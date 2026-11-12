#nullable enable

using System.Diagnostics;

namespace Knightfall;

// Keys that work an option straight from the game, with the menu shut. Any toggle, command or
// choice can have one, bound from the menu. Each sits next to its option in settings.toml as
// <name>_key and <name>_pad. A command runs, a toggle flips and a choice steps to its next value.
// The game still sees the keys, so pick ones it doesn't use.
static class Hotkeys
{
    // Binding gives up if nothing is pressed for this long.
    const double ListenSeconds = 6;

    sealed class Binding
    {
        public required Feature Feature;
        public Keys Key;
        public Pad.Button Buttons;
        public bool Held;

        public bool Empty => Key == Keys.None && Buttons == Pad.Button.None;
    }

    static readonly Dictionary<Feature, Binding> Bindings = new();

    // Each row's name as the menu shows it, made once until its hotkey or favourite changes.
    static readonly Dictionary<Feature, string> Labels = new();

    // Every key a binding can be, worked out once. Mouse buttons are left out, the menu needs them.
    static readonly Keys[] Candidates = Enumerable.Range(1, 254).Select(code => (Keys)code)
        .Where(key => key is not (Keys.LButton or Keys.RButton or Keys.MButton or Keys.XButton1 or Keys.XButton2 or Keys.Cancel))
        .Distinct().ToArray();

    static Feature? _listening;
    static string? _listeningLabel;
    static long _listenAt;
    static readonly HashSet<Keys> KeysAtStart = new();
    static Pad.Button _padAtStart;
    static Pad.Button _chord;

    public static Feature? Listening => _listening;

    public static bool Bindable(Feature? feature) => feature is (Toggle or Command or Choice) and { Passing: false };

    public static void Clear()
    {
        Bindings.Clear();
        Labels.Clear();
        _listening = null;
    }

    public static void Relabel() => Labels.Clear();

    public static bool Has(Feature feature) => Bindings.TryGetValue(feature, out var binding) && !binding.Empty;

    // Whether an option's key is down right now, for one that is held as well as pressed.
    public static bool IsHeld(Feature feature) =>
        !Menu.Showing && _listening is null && Bindings.TryGetValue(feature, out var binding) && binding.Held;

    // A key an option comes with. Empty means no key. A key the player sets or clears in the menu
    // wins over it from then on.
    public static void Add(Feature feature, string key = "", string pad = "")
    {
        var binding = new Binding { Feature = feature };

        var keyName = feature.Key + "_key";
        var keyText = Settings.Text(feature.Section, keyName, key).Trim();
        if (keyText.Length > 0 && !Controls.TryKey(keyText, out binding.Key))
        {
            Log.Warn("hotkey." + feature.Id, $"settings.toml: '{keyText}' is not a key name for {feature.Section}.{keyName}. Using '{key}'.");
            Settings.Reset(feature.Section, keyName, key);
            Controls.TryKey(key, out binding.Key);
        }

        var padName = feature.Key + "_pad";
        var padText = Settings.Text(feature.Section, padName, pad);
        if (!Pad.TryParse(padText, out binding.Buttons))
        {
            Log.Warn("hotkey." + feature.Id, $"settings.toml: '{padText}' has a button name I don't know for {feature.Section}.{padName}. Using '{pad}'.");
            Settings.Reset(feature.Section, padName, pad);
            Pad.TryParse(pad, out binding.Buttons);
        }

        Bindings[feature] = binding;
    }

    // The keys the player bound in the menu, for every other option. Only read, so options with
    // no key leave nothing in settings.toml.
    public static void LoadSaved()
    {
        foreach (var feature in Catalog.All)
        {
            if (!Bindable(feature) || Bindings.ContainsKey(feature))
            {
                continue;
            }

            var binding = new Binding { Feature = feature };
            if (Settings.Kept(feature.Section, feature.Key + "_key") is string keyText && keyText.Trim().Length > 0
                && !Controls.TryKey(keyText.Trim(), out binding.Key))
            {
                Log.Warn("hotkey." + feature.Id, $"settings.toml: '{keyText}' is not a key name for {feature.Section}.{feature.Key}_key, so it was left off.");
            }

            if (Settings.Kept(feature.Section, feature.Key + "_pad") is string padText && !Pad.TryParse(padText, out binding.Buttons))
            {
                Log.Warn("hotkey." + feature.Id, $"settings.toml: '{padText}' has a button name I don't know for {feature.Section}.{feature.Key}_pad, so it was left off.");
            }

            if (!binding.Empty)
            {
                Bindings[feature] = binding;
            }
        }
    }

    // The row's name with its keys after it and a mark when it's on the radial, or a prompt
    // while it waits for a key.
    public static string Decorate(Feature feature)
    {
        if (ReferenceEquals(feature, _listening))
        {
            return _listeningLabel ??= feature.Plain + "  (PRESS A KEY)";
        }

        if (Labels.TryGetValue(feature, out var known))
        {
            return known;
        }

        var label = feature.Plain;
        if (Bindings.TryGetValue(feature, out var binding) && !binding.Empty)
        {
            label += $"  ({Describe(binding)})";
        }

        if (Favourites.Has(feature))
        {
            label += "  [FAV]";
        }

        Labels[feature] = label;
        return label;
    }

    static string Describe(Binding binding)
    {
        var key = binding.Key == Keys.None ? "" : KeyName(binding.Key);
        var pad = binding.Buttons == Pad.Button.None ? "" : Pad.Describe(binding.Buttons);
        return key.Length > 0 && pad.Length > 0 ? $"{key} / {pad}" : key + pad;
    }

    // A row's key, or its pad buttons when the pad was used last, for prompts on screen.
    public static string KeyText(Feature feature)
    {
        if (!Bindings.TryGetValue(feature, out var binding) || binding.Empty)
        {
            return "";
        }

        var key = binding.Key == Keys.None ? "" : KeyName(binding.Key);
        var pad = binding.Buttons == Pad.Button.None ? "" : Pad.Describe(binding.Buttons);
        return Controls.LastDevice == Device.Pad ? (pad.Length > 0 ? pad : key) : (key.Length > 0 ? key : pad);
    }

    public static string KeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((int)key - (int)Keys.D0).ToString(),
        Keys.LButton => "LMB",
        Keys.RButton => "RMB",
        Keys.MButton => "MMB",
        Keys.XButton1 => "MB4",
        Keys.XButton2 => "MB5",
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => "SHIFT",
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => "CTRL",
        Keys.Menu or Keys.LMenu or Keys.RMenu => "ALT",
        Keys.Prior => "PGUP",
        Keys.Next => "PGDN",
        Keys.Back => "BKSP",
        Keys.Return => "ENTER",
        _ => key.ToString().ToUpperInvariant(),
    };

    // Waits for the next key or buttons to bind to this row.
    public static void Listen(Feature feature)
    {
        _listening = feature;
        _listeningLabel = null;
        _listenAt = Stopwatch.GetTimestamp();
        _chord = Pad.Button.None;
        _padAtStart = Pad.Held;
        KeysAtStart.Clear();
        foreach (var key in Candidates)
        {
            if (Keyboard.IsDown(key))
            {
                KeysAtStart.Add(key);
            }
        }
    }

    static void StopListening()
    {
        _listening = null;
        _listeningLabel = null;
    }

    // Every frame while waiting. The menu's back key cancels and its bind key clears the row. On
    // a pad, the buttons held together are bound as one when they're let go.
    public static void Listen()
    {
        if (_listening is not { } feature)
        {
            return;
        }

        if ((Stopwatch.GetTimestamp() - _listenAt) / (double)Stopwatch.Frequency > ListenSeconds)
        {
            StopListening();
            return;
        }

        foreach (var key in Candidates)
        {
            var down = Keyboard.IsDown(key);
            if (!down)
            {
                KeysAtStart.Remove(key);
                continue;
            }

            if (KeysAtStart.Contains(key))
            {
                continue;
            }

            KeysAtStart.Add(key);
            if (key == Controls.KeyFor(Act.Back))
            {
                StopListening();
            }
            else if (key == Controls.KeyFor(Act.Bind))
            {
                Unbind(feature);
            }
            else if (key != Controls.KeyFor(Act.Open))
            {
                SetKey(feature, key);
            }

            return;
        }

        // Buttons already down when binding started count only once they've been let go.
        var held = Pad.Held;
        _padAtStart &= held;
        held &= ~_padAtStart;
        if (held != Pad.Button.None)
        {
            _chord |= held;
            return;
        }

        if (_chord == Pad.Button.None)
        {
            return;
        }

        var chord = _chord;
        _chord = Pad.Button.None;
        if (chord == Controls.ButtonFor(Act.Back))
        {
            StopListening();
        }
        else if (chord == Controls.ButtonFor(Act.Bind))
        {
            Unbind(feature);
        }
        else if (chord != Controls.ButtonFor(Act.Open))
        {
            SetPad(feature, chord);
        }
    }

    // A key moves to its new row, so one key never works two options.
    static void SetKey(Feature feature, Keys key)
    {
        foreach (var other in Bindings.Values.Where(other => other.Key == key && !ReferenceEquals(other.Feature, feature)).ToArray())
        {
            other.Key = Keys.None;
            Save(other);
            Moved(other.Feature, KeyName(key), feature);
        }

        var binding = Find(feature);
        binding.Key = key;
        Save(binding);
        StopListening();
    }

    static void SetPad(Feature feature, Pad.Button chord)
    {
        foreach (var other in Bindings.Values.Where(other => other.Buttons == chord && !ReferenceEquals(other.Feature, feature)).ToArray())
        {
            other.Buttons = Pad.Button.None;
            Save(other);
            Moved(other.Feature, Pad.Describe(chord), feature);
        }

        var binding = Find(feature);
        binding.Buttons = chord;
        Save(binding);
        StopListening();
    }

    // Tells the player which option just lost its key, since that row may be on another page.
    static void Moved(Feature from, string key, Feature to)
    {
        Toasts.Show(from.Plain, $"{key} NOW ON {to.Plain}", OutcomeKind.Unchanged, from);
    }

    static void Unbind(Feature feature)
    {
        if (Bindings.TryGetValue(feature, out var binding))
        {
            binding.Key = Keys.None;
            binding.Buttons = Pad.Button.None;
            Save(binding);
        }

        StopListening();
    }

    static Binding Find(Feature feature)
    {
        if (!Bindings.TryGetValue(feature, out var binding))
        {
            binding = new Binding { Feature = feature };
            Bindings[feature] = binding;
        }

        return binding;
    }

    static void Save(Binding binding)
    {
        var feature = binding.Feature;
        Settings.Set(feature.Section, feature.Key + "_key", binding.Key == Keys.None ? "" : binding.Key.ToString());
        Settings.Set(feature.Section, feature.Key + "_pad", Pad.Stored(binding.Buttons));
        Labels.Remove(feature);
    }

    public static Outcome ClearAll()
    {
        var count = 0;
        foreach (var binding in Bindings.Values)
        {
            if (!binding.Empty)
            {
                binding.Key = Keys.None;
                binding.Buttons = Pad.Button.None;
                Save(binding);
                count++;
            }
        }

        return count > 0 ? Outcome.Done($"{count} CLEARED") : Outcome.Unchanged("NONE BOUND");
    }

    // Runs with the rest of the input. A key held while the menu shuts doesn't fire on its way up.
    public static void Poll()
    {
        foreach (var binding in Bindings.Values)
        {
            var down = (binding.Key != Keys.None && Keyboard.IsDown(binding.Key)) || Pad.IsDown(binding.Buttons);
            if (down && !binding.Held && !Menu.Showing && _listening is null)
            {
                Fire(binding.Feature);
            }

            binding.Held = down;
        }
    }

    // What a hotkey or a radial slot does to its row.
    public static void Fire(Feature feature)
    {
        switch (feature)
        {
            case Command command:
                Toasts.Used(command);
                Catalog.Pressed(command);
                break;
            case Toggle toggle:
                toggle.Press();
                Toasts.Used(toggle);
                break;
            case Choice choice:
                choice.Step(1);
                Toasts.Used(choice);
                break;
        }
    }
}
