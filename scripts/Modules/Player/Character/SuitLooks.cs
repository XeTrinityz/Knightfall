#nullable enable

using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Knightfall;

// PLAYER > SUIT, the LOOKS rows. A look is every part's colour for one suit, kept as a file in
// data/looks/<suit>/, so it can be worn again, put on by itself with the suit, or shared.
static class SuitLooks
{
    // Which look each suit puts on when it's worn, by suit.
    public const string PutOnStore = "player.suit_put_on";

    static readonly (string Id, string Label)[] None = { ("", "NONE") };

    public static readonly Choice Saved = new(
        "player.suit.saved_look", "SAVED LOOK",
        "Looks saved for the suit you wear. Each is a file in data/looks under the suit's name, so it can be shared.",
        None, saved: false)
    {
        Opening = Refresh,
        Changed = ShowPutOn,
        Block = NoneSaved,
        Preference = true,
    };

    public static readonly Command SaveNew = new(
        "player.suit.save_look", "SAVE LOOK",
        "Saves this suit's colours as a new look.",
        SaveLook)
    {
        Block = () => SuitPaint.Suit.Length == 0 ? "NO SUIT" : SuitPaint.Painted ? null : "NO COLOURS SET",
    };

    public static readonly Command WearSaved = new(
        "player.suit.wear_look", "WEAR SAVED LOOK",
        "Puts the picked look on this suit. UNDO takes it back off.",
        WearLook)
    {
        Block = NoneSaved,
    };

    public static readonly Toggle PutOn = new(
        "player.suit.wear_on_put_on", "WEAR WHEN PUT ON",
        "Puts the picked look on every time you put this suit on. One look per suit.",
        saved: false)
    {
        Changed = SetPutOn,
        Block = NoneSaved,
        Preference = true,
    };

    public static readonly Command Delete = new(
        "player.suit.delete_look", "DELETE SAVED LOOK",
        "Deletes the picked look's file.",
        DeleteLook)
    {
        Block = NoneSaved,
    };

    static string? NoneSaved() => SuitPaint.Suit.Length == 0 ? "NO SUIT" : Saved.CurrentId.Length == 0 ? "NONE SAVED" : null;

    static string Folder(string suit) => Path.Combine(ModFiles.Looks, suit);

    static string FileOf(string suit, string name) => Path.Combine(Folder(suit), name + ".toml");

    static string Shown(string name) => name.Replace('_', ' ').ToUpperInvariant();

    // Lists this suit's looks again, keeping the pick when it's still there.
    public static void Refresh()
    {
        var suit = SuitPaint.Suit;
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
            Log.Warn("suit.looks.list", $"Saved looks couldn't be listed. {error.Message}");
        }

        var keep = Saved.CurrentId;
        var list = names.Count == 0 ? None : names.Select(name => (name, Shown(name))).ToArray();
        var index = Array.FindIndex(list, option => option.Item1 == keep);
        Saved.SetOptions(list, Math.Max(0, index));
        ShowPutOn();
    }

    static void ShowPutOn() => PutOn.Show(Saved.CurrentId.Length > 0 && PutOnName(SuitPaint.Suit) == Saved.CurrentId);

    static string PutOnName(string suit) => suit.Length > 0 && Settings.Kept(PutOnStore, suit) is string name ? name : "";

    static void SetPutOn()
    {
        var suit = SuitPaint.Suit;
        if (suit.Length == 0 || Saved.CurrentId.Length == 0)
        {
            return;
        }

        if (PutOn.On)
        {
            Settings.Set(PutOnStore, suit, Saved.CurrentId);
        }
        else if (PutOnName(suit) == Saved.CurrentId)
        {
            Settings.Set(PutOnStore, suit, "");
        }
    }

    // The look to put on as this suit is worn, or null for none.
    public static Dictionary<string, SuitLook>? PutOnLooks(string suit)
    {
        var name = PutOnName(suit);
        return name.Length > 0 && File.Exists(FileOf(suit, name)) ? Read(FileOf(suit, name)) : null;
    }

    // Asks for a name, starting from the next free LOOK number.
    static Outcome SaveLook()
    {
        var suit = SuitPaint.Suit;
        if (SuitPaint.Snapshot().Count == 0)
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
    static string FileName(string name)
    {
        var text = new string(name.Trim().ToLowerInvariant().Select(letter => char.IsAsciiLetterOrDigit(letter) ? letter : '_').ToArray());
        while (text.Contains("__"))
        {
            text = text.Replace("__", "_");
        }

        return text.Trim('_');
    }

    static Outcome Store(string suit, string typed)
    {
        var name = FileName(typed);
        if (name.Length == 0)
        {
            return Outcome.Failed("TYPE A NAME");
        }

        if (suit != SuitPaint.Suit)
        {
            return Outcome.Failed("SUIT CHANGED");
        }

        var looks = SuitPaint.Snapshot();
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
            Log.Warn("suit.looks.save", $"The look couldn't be saved. {error.Message}");
            return Outcome.Failed("CAN'T SAVE");
        }

        Log.Info($"Saved suit look {ModFiles.Shown(FileOf(suit, name))}.");
        Saved.SetOptions(new[] { (name, Shown(name)) }, 0);
        Refresh();
        var done = Outcome.Done("SAVED AS " + Shown(name));
        SaveNew.Report(done);
        return done;
    }

    static Outcome WearLook()
    {
        var file = FileOf(SuitPaint.Suit, Saved.CurrentId);
        if (!File.Exists(file))
        {
            Refresh();
            return Outcome.Failed("FILE GONE");
        }

        if (Read(file) is not { } looks)
        {
            return Outcome.Failed("CAN'T READ");
        }

        SuitPaint.Wear(looks);
        return Outcome.Done("WORN");
    }

    static Outcome DeleteLook()
    {
        var suit = SuitPaint.Suit;
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
            Log.Warn("suit.looks.delete", $"The look couldn't be deleted. {error.Message}");
            return Outcome.Failed("CAN'T DELETE");
        }

        if (PutOnName(suit) == name)
        {
            Settings.Set(PutOnStore, suit, "");
        }

        Refresh();
        return Outcome.Done("DELETED");
    }

    static string Write(string suit, Dictionary<string, SuitLook> looks)
    {
        var text = new StringBuilder();
        text.AppendLine("# A Knightfall suit look. It belongs in data/looks/<suit>/, under the suit it was made for.");
        text.AppendLine("# Each part is colour, strength (0 to 100) and brightness (0.2 to 4).");
        text.Append("suit = ").AppendLine(Quote(suit));
        text.AppendLine();
        text.AppendLine("[parts]");
        foreach (var (key, look) in looks.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(Quote(key)).Append(" = ").AppendLine(Quote(look.ToString()));
        }

        return text.ToString();
    }

    static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static Dictionary<string, SuitLook>? Read(string file)
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
            Log.Warn("suit.looks.read", $"{ModFiles.Shown(file)} couldn't be read. {error.Message}");
            return null;
        }
    }
}
