#nullable enable

using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Knightfall;

// Where a LookBook keeps its looks and what it calls things.
sealed record LookSpec(string Prefix, string Store, string Root, string Noun, string NoKey, Painter Painter);

// The LOOKS rows for one Painter. A look is every part's colour for one suit or skin, kept as a
// file in a folder per key, so it can be worn again, put on by itself, or shared.
sealed class LookBook
{
    readonly LookSpec _spec;
    readonly Painter _p;

    static readonly (string Id, string Label)[] None = { ("", "NONE") };

    public readonly Choice Saved;
    public readonly Command SaveNew;
    public readonly Command WearSaved;
    public readonly Toggle PutOn;
    public readonly Command Delete;

    public LookBook(LookSpec spec)
    {
        _spec = spec;
        _p = spec.Painter;
        var id = spec.Prefix;
        var noun = spec.Noun;

        Saved = new Choice(
            $"{id}.saved_look", "SAVED LOOK",
            $"Looks saved for the {noun} you have on. Each is a file in data/looks under the {noun}'s name, so it can be shared.",
            None, saved: false)
        {
            Opening = Refresh,
            Changed = ShowPutOn,
            Block = NoneSaved,
            Preference = true,
        };

        SaveNew = new Command(
            $"{id}.save_look", "SAVE LOOK",
            $"Saves this {noun}'s colours as a new look.",
            SaveLook)
        {
            Block = () => _p.Key.Length == 0 ? _spec.NoKey : _p.Painted ? null : "NO COLOURS SET",
        };

        WearSaved = new Command(
            $"{id}.wear_look", "WEAR SAVED LOOK",
            $"Puts the picked look on this {noun}. UNDO takes it back off.",
            WearLook)
        {
            Block = NoneSaved,
        };

        PutOn = new Toggle(
            $"{id}.wear_on_put_on", "WEAR WHEN PUT ON",
            $"Puts the picked look on every time this {noun} is put on. One look per {noun}.",
            saved: false)
        {
            Changed = SetPutOn,
            Block = NoneSaved,
            Preference = true,
        };

        Delete = new Command(
            $"{id}.delete_look", "DELETE SAVED LOOK",
            "Deletes the picked look's file.",
            DeleteLook)
        {
            Block = NoneSaved,
        };
    }

    // Keeps every key's put-on look in settings.toml.
    public void KeepStore() => Settings.KeepAll(_spec.Store);

    string? NoneSaved() => _p.Key.Length == 0 ? _spec.NoKey : Saved.CurrentId.Length == 0 ? "NONE SAVED" : null;

    string Folder(string suit) => Path.Combine(_spec.Root, suit);

    string FileOf(string suit, string name) => Path.Combine(Folder(suit), name + ".toml");

    string Shown(string name) => name.Replace('_', ' ').ToUpperInvariant();

    // Lists this key's looks again, keeping the pick when it's still there.
    public void Refresh()
    {
        var suit = _p.Key;
        var names = new List<string>();
        try
        {
            if (suit.Length > 0 && Directory.Exists(Folder(suit)))
            {
                names.AddRange(Directory.GetFiles(Folder(suit), "*.toml").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception error)
        {
            Log.Warn($"{_spec.Prefix}.looks.list", $"Saved looks couldn't be listed. {error.Message}");
        }

        var keep = Saved.CurrentId;
        var list = names.Count == 0 ? None : names.Select(name => (name, Shown(name))).ToArray();
        var index = Array.FindIndex(list, option => option.Item1 == keep);
        Saved.SetOptions(list, Math.Max(0, index));
        ShowPutOn();
    }

    void ShowPutOn() => PutOn.Show(Saved.CurrentId.Length > 0 && PutOnName(_p.Key) == Saved.CurrentId);

    string PutOnName(string suit) => suit.Length > 0 && Settings.Kept(_spec.Store, suit) is string name ? name : "";

    void SetPutOn()
    {
        var suit = _p.Key;
        if (suit.Length == 0 || Saved.CurrentId.Length == 0)
        {
            return;
        }

        if (PutOn.On)
        {
            Settings.Set(_spec.Store, suit, Saved.CurrentId);
        }
        else if (PutOnName(suit) == Saved.CurrentId)
        {
            Settings.Set(_spec.Store, suit, "");
        }
    }

    // The look to put on as this key is worn, or null for none.
    public Dictionary<string, SuitLook>? PutOnLooks(string suit)
    {
        var name = PutOnName(suit);
        return name.Length > 0 && File.Exists(FileOf(suit, name)) ? Read(FileOf(suit, name)) : null;
    }

    // Asks for a name, starting from the next free LOOK number.
    Outcome SaveLook()
    {
        var suit = _p.Key;
        if (_p.Snapshot().Count == 0)
        {
            return Outcome.Unchanged("NO COLOURS SET");
        }

        var number = 1;
        while (File.Exists(FileOf(suit, "look_" + number.ToString(CultureInfo.InvariantCulture))))
        {
            number++;
        }

        NameBox.Ask("NAME THIS LOOK", "SAVE LOOK  //  " + Shown(suit), "LOOK " + number.ToString(CultureInfo.InvariantCulture),
            name => Store(suit, name), name => File.Exists(FileOf(suit, FileName(name))), "SAVING REPLACES THE LOOK WITH THIS NAME");
        return Outcome.Asking;
    }

    // Letters and numbers as typed, spaces and dashes as underscores.
    string FileName(string name)
    {
        var text = new string(name.Trim().ToLowerInvariant().Select(letter => char.IsAsciiLetterOrDigit(letter) ? letter : '_').ToArray());
        while (text.Contains("__"))
        {
            text = text.Replace("__", "_");
        }

        return text.Trim('_');
    }

    Outcome Store(string suit, string typed)
    {
        var name = FileName(typed);
        if (name.Length == 0)
        {
            return Outcome.Failed("TYPE A NAME");
        }

        if (suit != _p.Key)
        {
            return Outcome.Failed(_spec.Noun.ToUpperInvariant() + " CHANGED");
        }

        var looks = _p.Snapshot();
        if (looks.Count == 0)
        {
            return Outcome.Failed("NO COLOURS SET");
        }

        try
        {
            File.WriteAllText(ModFiles.Ready(FileOf(suit, name)), Write(suit, looks));
        }
        catch (Exception error)
        {
            Log.Warn($"{_spec.Prefix}.looks.save", $"The look couldn't be saved. {error.Message}");
            return Outcome.Failed("CAN'T SAVE");
        }

        Log.Info($"Saved {_spec.Noun} look {ModFiles.Shown(FileOf(suit, name))}.");
        Saved.SetOptions(new[] { (name, Shown(name)) }, 0);
        Refresh();
        var done = Outcome.Done("SAVED AS " + Shown(name));
        SaveNew.Report(done);
        return done;
    }

    Outcome WearLook()
    {
        var file = FileOf(_p.Key, Saved.CurrentId);
        if (!File.Exists(file))
        {
            Refresh();
            return Outcome.Failed("FILE GONE");
        }

        if (Read(file) is not { } looks)
        {
            return Outcome.Failed("CAN'T READ");
        }

        _p.Wear(looks);
        return Outcome.Done("WORN");
    }

    Outcome DeleteLook()
    {
        var suit = _p.Key;
        var name = Saved.CurrentId;
        var ask = new Confirmation("CAN'T UNDO", "DELETE LOOK", $"{Shown(name)} is deleted from {ModFiles.Shown(Folder(suit))}.", "DELETE", "KEEP");
        if (!Confirm.Asked(Delete, ask))
        {
            return Outcome.Asking;
        }

        try
        {
            File.Delete(FileOf(suit, name));
        }
        catch (Exception error)
        {
            Log.Warn($"{_spec.Prefix}.looks.delete", $"The look couldn't be deleted. {error.Message}");
            return Outcome.Failed("CAN'T DELETE");
        }

        if (PutOnName(suit) == name)
        {
            Settings.Set(_spec.Store, suit, "");
        }

        Refresh();
        return Outcome.Done("DELETED");
    }

    string Write(string suit, Dictionary<string, SuitLook> looks)
    {
        var text = new StringBuilder();
        text.Append("# A Knightfall ").Append(_spec.Noun).AppendLine(" look. It belongs in the folder of the " + _spec.Noun + " it was made for.");
        text.AppendLine("# Each part is colour, strength (0 to 100) and brightness (0.2 to 4).");
        text.Append(_spec.Noun).Append(" = ").AppendLine(Quote(suit));
        text.AppendLine();
        text.AppendLine("[parts]");
        foreach (var (key, look) in looks.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(Quote(key)).Append(" = ").AppendLine(Quote(look.ToString()));
        }

        return text.ToString();
    }

    string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    Dictionary<string, SuitLook>? Read(string file)
    {
        try
        {
            var table = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file));
            var looks = new Dictionary<string, SuitLook>();
            if (table?.TryGetValue("parts", out var parts) == true && parts is TomlTable list)
            {
                foreach (var (key, value) in list)
                {
                    if (value is string text && SuitLook.TryParse(text, out var look) && !look.IsPlain)
                    {
                        looks[key] = look;
                    }
                }
            }

            return looks;
        }
        catch (Exception error)
        {
            Log.Warn($"{_spec.Prefix}.looks.read", $"{ModFiles.Shown(file)} couldn't be read. {error.Message}");
            return null;
        }
    }
}
