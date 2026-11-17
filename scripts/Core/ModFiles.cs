#nullable enable

namespace Knightfall;

// Where Knightfall keeps its files. The mod folder itself holds only Knightfall.dll and mod.toml.
// Everything the mod writes goes under data.
static class ModFiles
{
    public static string Root { get; private set; } = "";

    public static string Data => Path.Combine(Root, "data");

    public static string Settings => Path.Combine(Data, "settings.toml");

    public static string Edits => Path.Combine(Data, "edits.toml");

    public static string Log => Path.Combine(Data, "logs", "Knightfall.log");

    public static string PreviousLog => Path.Combine(Data, "logs", "Knightfall.prev.log");

    public static string Looks => Path.Combine(Data, "looks");

    // Only used when the game's own folder in Documents can't be found.
    public static string Saves => Path.Combine(Data, "saves");

    public static string Backup(string name) => Path.Combine(Data, "backups", name);

    public static string Export(string name) => Path.Combine(Data, "exports", name);

    public static void Use(string modFolder)
    {
        Root = Path.GetFullPath(modFolder);
    }

    // The folders are made when something first goes in them, so call this before writing.
    public static string Ready(string file)
    {
        if (Path.GetDirectoryName(file) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        return file;
    }

    // The path as a player finds it from the mod folder, for messages.
    public static string Shown(string file)
    {
        return Path.GetRelativePath(Root, file).Replace('\\', '/');
    }
}
