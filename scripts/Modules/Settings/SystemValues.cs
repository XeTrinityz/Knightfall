#nullable enable

using System.Globalization;
using System.Text;

namespace Knightfall;

// The game's render settings, the ones its graphics menu doesn't reach. They're changed live with
// the game's own "scale set" command. That command also saves every live value to
// BmSystemSettings.ini, so after each change the game's own values are written back there, and a
// copy is kept in the mod folder so a crash can be put right on the next start.
static class SystemValues
{
    const string Section = "[SystemSettings]";
    const string Kept = "system_values.txt";

    // The game's value of each setting we've changed, as the ini had it.
    static readonly Dictionary<string, string> Originals = new(StringComparer.OrdinalIgnoreCase);

    // The settings a row is holding right now. Any other in Originals was left live by a crash.
    static readonly HashSet<string> Holding = new(StringComparer.OrdinalIgnoreCase);

    static string Ini => Path.Combine(GameFiles.Game, "Config", "BmSystemSettings.ini");

    static string Copy => Path.Combine(GameFiles.Mod, Kept);

    // A crash leaves our values live in the ini. They're put back to the game's before it reads them again.
    public static void Repair()
    {
        try
        {
            if (!File.Exists(Copy))
            {
                return;
            }

            foreach (var line in File.ReadAllLines(Copy))
            {
                var split = line.IndexOf('=');
                if (split > 0)
                {
                    Originals[line[..split]] = line[(split + 1)..];
                }
            }

            if (Originals.Count > 0)
            {
                WriteBack();
                Log.Info($"Put {Originals.Count} game render settings back in BmSystemSettings.ini after the last session.");
            }
        }
        catch (Exception error)
        {
            Log.Error("system_values", "The game's render settings could not be put back", error);
        }
    }

    // The game read its settings before the mod started, so values a crash left behind are still
    // live. They're sent back as soon as there's a player, unless a row holds them again first.
    public static void Tick()
    {
        if (World.Controller is null || Originals.Count == Holding.Count)
        {
            return;
        }

        foreach (var name in Originals.Keys.Where(name => !Holding.Contains(name)).ToArray())
        {
            Restore(name);
        }
    }

    public static bool Set(string name, float value)
    {
        if (World.Controller is not { } controller)
        {
            return false;
        }

        if (!Originals.ContainsKey(name))
        {
            if (Read(name) is not { } original)
            {
                Log.Warn("system_values." + name, $"{name} isn't in BmSystemSettings.ini, so it was left alone.");
                return false;
            }

            Originals[name] = original;
            Save();
        }

        Holding.Add(name);
        controller.ConsoleCommand(GameText.Once($"scale set {name} {value.ToString("0.###", CultureInfo.InvariantCulture)}"));
        WriteBack();
        return true;
    }

    public static void Restore(string name)
    {
        if (!Originals.TryGetValue(name, out var original))
        {
            return;
        }

        // With no player the command can't be sent. The value stays noted and Tick sends it once
        // there is one.
        Holding.Remove(name);
        if (World.Controller is not { } controller)
        {
            return;
        }

        controller.ConsoleCommand(GameText.Once($"scale set {name} {original}"));
        Originals.Remove(name);

        // The command saved every live value, including ones other rows still hold.
        WriteBack(name, original);
        WriteBack();
        Save();
    }

    public static void RestoreAll()
    {
        foreach (var name in Originals.Keys.ToArray())
        {
            Restore(name);
        }
    }

    static void Save()
    {
        try
        {
            if (Originals.Count == 0)
            {
                File.Delete(Copy);
                return;
            }

            File.WriteAllLines(Copy, Originals.Select(pair => $"{pair.Key}={pair.Value}"));
        }
        catch (Exception error)
        {
            Log.Error("system_values", "The game's render settings could not be noted", error);
        }
    }

    static string? Read(string name)
    {
        string? found = null;
        Walk(name, value =>
        {
            found = value;
            return value;
        });
        return found;
    }

    static void WriteBack()
    {
        foreach (var (name, original) in Originals)
        {
            WriteBack(name, original);
        }
    }

    static void WriteBack(string name, string original) => Walk(name, _ => original);

    // Goes through the ini's own section, which the live settings come from, and lets change pick
    // the value for the setting. Other sections hold the presets and are left alone. The file is
    // rewritten only when something changed, line for line in its own encoding.
    static void Walk(string name, Func<string, string> change)
    {
        if (!File.Exists(Ini))
        {
            return;
        }

        var encoding = Encoding.Latin1;
        var text = File.ReadAllText(Ini, encoding);
        var lines = text.Split('\n');
        var inside = false;
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith('['))
            {
                inside = line.Trim().Equals(Section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var split = line.IndexOf('=');
            if (!inside || split <= 0 || !line[..split].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(split + 1)..];
            var wanted = change(value);
            if (wanted != value)
            {
                lines[i] = line[..(split + 1)] + wanted + (lines[i].EndsWith('\r') ? "\r" : "");
                changed = true;
            }

            break;
        }

        if (changed)
        {
            File.WriteAllText(Ini, string.Join('\n', lines), encoding);
        }
    }
}
