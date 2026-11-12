#nullable enable

namespace Knightfall;

// Where the game keeps its packages on disk. Asking the game to load one that isn't there can
// leave it in a broken state, so some options look here first.
static class GameFiles
{
    static string[] _folders = Array.Empty<string>();

    // BmGame, and the mod's own folder.
    public static string Game { get; private set; } = "";

    public static string Mod { get; private set; } = "";

    // The mod sits in BmGame/Mods, next to the base game's packages. Each DLC has its own folder
    // of them one level up. Skin and car mods put theirs anywhere under a DLC folder, and the game
    // finds them there, so every folder under DLC that holds a playable or showcase package counts,
    // as does every DLC's own CookedPCConsole folder.
    public static void Find(string modFolder)
    {
        var found = new List<string>();
        try
        {
            var game = Path.GetFullPath(Path.Combine(modFolder, "..", ".."));
            Game = game;
            Mod = Path.GetFullPath(modFolder);
            Add(found, Path.Combine(game, "CookedPCConsole"));

            var packs = Path.GetFullPath(Path.Combine(game, "..", "DLC"));
            if (Directory.Exists(packs))
            {
                var deep = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
                foreach (var cooked in Directory.EnumerateDirectories(packs, "CookedPCConsole", deep))
                {
                    if (!found.Contains(cooked, StringComparer.OrdinalIgnoreCase))
                    {
                        found.Add(cooked);
                    }
                }

                foreach (var kind in new[] { "Playable_*.upk", "Showcase_*.upk" })
                {
                    foreach (var file in Directory.EnumerateFiles(packs, kind, deep))
                    {
                        if (Path.GetDirectoryName(file) is { } folder && !found.Contains(folder, StringComparer.OrdinalIgnoreCase))
                        {
                            found.Add(folder);
                        }
                    }
                }
            }
        }
        catch (Exception error)
        {
            Log.Error("game_files", "The game's package folders could not be listed", error);
        }

        _folders = found.ToArray();
    }

    public static bool HasPackage(string name)
    {
        foreach (var folder in _folders)
        {
            if (File.Exists(Path.Combine(folder, name + "_SF.upk")) || File.Exists(Path.Combine(folder, name + ".upk")))
            {
                return true;
            }
        }

        return false;
    }

    // How many objects a package adds when it loads, read from the start of its file. 0 when it
    // can't be told.
    public static int Exports(string name)
    {
        const uint packageTag = 0x9E2A83C1;
        const int most = 400000;

        foreach (var folder in _folders)
        {
            foreach (var file in new[] { Path.Combine(folder, name + "_SF.upk"), Path.Combine(folder, name + ".upk") })
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                try
                {
                    using var reader = new BinaryReader(File.OpenRead(file));
                    if (reader.ReadUInt32() != packageTag)
                    {
                        return 0;
                    }

                    // Version and header size, then the folder name, the flags and the name table.
                    // The export count comes after those. A negative length means wide text.
                    reader.BaseStream.Seek(8, SeekOrigin.Current);
                    var length = reader.ReadInt32();
                    reader.BaseStream.Seek(length >= 0 ? length : -2L * length, SeekOrigin.Current);
                    reader.BaseStream.Seek(12, SeekOrigin.Current);
                    var exports = reader.ReadInt32();
                    return exports is > 0 and < most ? exports : 0;
                }
                catch (Exception error)
                {
                    Log.Error("game_files.exports", $"Package {name} could not be sized", error);
                    return 0;
                }
            }
        }

        return 0;
    }

    static void Add(List<string> folders, string folder)
    {
        if (Directory.Exists(folder))
        {
            folders.Add(folder);
        }
    }
}
