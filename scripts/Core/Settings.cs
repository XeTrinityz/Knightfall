#nullable enable

using System.Globalization;
using System.Text;
using Tomlyn;

namespace Knightfall;

// settings.toml holds the player's choices. Code asks for a value with its default, and every
// value asked for is written back, so the file always lists everything that can be changed.
static class Settings
{
    const double SaveDelaySeconds = 1;

    sealed class Section
    {
        public string Comment = "";
        public readonly List<string> Keys = new();
        public readonly Dictionary<string, object> Values = new();
    }

    static readonly List<string> Order = new();
    static readonly Dictionary<string, Section> Sections = new();
    static TomlTable _file = new();
    static string _path = "";
    static bool _changed;
    static DateTime _changedAt;

    public static void Load(string path)
    {
        _path = path;
        _file = new TomlTable();
        Order.Clear();
        Sections.Clear();

        if (!File.Exists(path))
        {
            Mark();
            return;
        }

        try
        {
            _file = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? new TomlTable();
        }
        catch (Exception error)
        {
            KeepBroken(path);
            Log.Warn("settings.read", $"settings.toml could not be read, so defaults are used. Your file was kept as settings.bad.toml. {error.Message}");
            Mark();
        }
    }

    // Reads the file again, as at the start, for after it was edited with the game open.
    public static void Reload() => Load(_path);

    public static void Describe(string section, string comment)
    {
        Open(section).Comment = comment;
    }

    public static string Text(string section, string key, string fallback)
    {
        if (FromFile(section, key) is not string value)
        {
            value = fallback;
            Mark();
        }

        Remember(section, key, value);
        return value;
    }

    // The value as the file had it, or null. Nothing is written back for it.
    public static object? Raw(string section, string key)
    {
        return FromFile(section, key);
    }

    // The value as last set, or as the file had it, or null. Kept for the next save when it's
    // there, and nothing is added when it isn't, for keys most options never have.
    public static object? Kept(string section, string key)
    {
        if (Sections.TryGetValue(section, out var known) && known.Values.TryGetValue(key, out var set))
        {
            return set;
        }

        var value = FromFile(section, key);
        if (value is not null)
        {
            Remember(section, key, value);
        }

        return value;
    }

    // Keeps every value of a section the file has, for sections whose keys are only read now and
    // then, like a setting per disguise.
    public static void KeepAll(string section)
    {
        var table = _file;
        foreach (var part in section.Split('.'))
        {
            if (!table.TryGetValue(part, out var next) || next is not TomlTable inner)
            {
                return;
            }

            table = inner;
        }

        foreach (var (key, value) in table)
        {
            if (value is not TomlTable)
            {
                Kept(section, key);
            }
        }
    }

    public static void Set(string section, string key, object value)
    {
        var before = Open(section).Values.TryGetValue(key, out var held) ? held : FromFile(section, key);
        Remember(section, key, value);

        if (!Equals(before, value))
        {
            Mark();
        }
    }

    // Puts a value back to its default after the one in the file turned out to be unusable.
    public static void Reset(string section, string key, string fallback)
    {
        Remember(section, key, fallback);
        Mark();
    }

    // Forgets every value, as if settings.toml had never been there. Everything asked for after
    // this gets its default, and the file is written again with only that.
    public static void Wipe()
    {
        _file = new TomlTable();
        Order.Clear();
        Sections.Clear();
        Mark();
    }

    // Holding a slider would otherwise rewrite the file many times a second.
    public static void SaveWhenSettled()
    {
        if (_changed && (DateTime.UtcNow - _changedAt).TotalSeconds >= SaveDelaySeconds)
        {
            Save();
        }
    }

    public static void Save()
    {
        if (!_changed || _path.Length == 0)
        {
            return;
        }

        _changed = false;

        var text = new StringBuilder();
        text.AppendLine("# Knightfall settings. Close the game before you edit this file, or use RELOAD SETTINGS FILE");
        text.AppendLine("# on SETTINGS > GENERAL straight after you save it.");

        foreach (var name in Order)
        {
            var section = Sections[name];
            if (section.Keys.Count == 0)
            {
                continue;
            }

            text.AppendLine();

            foreach (var line in section.Comment.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                text.Append("# ").AppendLine(line.Trim());
            }

            text.Append('[').Append(name).AppendLine("]");

            foreach (var key in section.Keys)
            {
                text.Append(key).Append(" = ").AppendLine(Format(section.Values[key]));
            }
        }

        try
        {
            // Written to a second file first, so a crash halfway never leaves a cut-off settings.toml.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, text.ToString());
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception error)
        {
            Log.Error("settings.write", "settings.toml could not be saved", error);
        }
    }

    static void Mark()
    {
        _changed = true;
        _changedAt = DateTime.UtcNow;
    }

    // A section name with dots, like player.status, is a table inside a table in the file.
    static object? FromFile(string section, string key)
    {
        var table = _file;
        foreach (var part in section.Split('.'))
        {
            if (!table.TryGetValue(part, out var next) || next is not TomlTable inner)
            {
                return null;
            }

            table = inner;
        }

        return table.TryGetValue(key, out var value) ? value : null;
    }

    static Section Open(string name)
    {
        if (!Sections.TryGetValue(name, out var section))
        {
            section = new Section();
            Sections[name] = section;
            Order.Add(name);
        }

        return section;
    }

    static void Remember(string sectionName, string key, object value)
    {
        var section = Open(sectionName);
        if (!section.Values.ContainsKey(key))
        {
            section.Keys.Add(key);
        }

        section.Values[key] = value;
    }

    static string Format(object value)
    {
        return value switch
        {
            bool flag => flag ? "true" : "false",
            long whole => whole.ToString(CultureInfo.InvariantCulture),
            double number => FormatNumber(number),
            _ => Quote(value.ToString() ?? ""),
        };
    }

    static string FormatNumber(double number)
    {
        var text = number.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    static string Quote(string text)
    {
        return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    static void KeepBroken(string path)
    {
        try
        {
            File.Copy(path, Path.Combine(Path.GetDirectoryName(path) ?? "", "settings.bad.toml"), overwrite: true);
        }
        catch (Exception)
        {
            // The copy is a courtesy. Defaults still load without it.
        }
    }
}
