#nullable enable

using BmSDK;

namespace Knightfall;

// Game data that comes in a package of its own, like a playable character or a suit.
static class Packages
{
    // Kept free on top of what a load needs, for what the world streams in around it.
    public const int Spare = 12000;

    // Used when a package's size can't be read. The largest character is about 33,000 objects.
    const int UnknownCost = 36000;

    // Each package's size, read from its file once.
    static readonly Dictionary<string, int> Sizes = new(StringComparer.OrdinalIgnoreCase);

    // Whether the object table can take a package, with the spare room on top. The name is the
    // file's, with or without the _SF a cooked package has on the end.
    public static bool HasRoom(string package) => Game.ObjectHeadroom >= Size(package) + Spare;

    // Whether the packages that aren't in memory yet can all be loaded now, one after another.
    // Memory is only searched once room is short, so a row's lock can ask every frame.
    public static bool HasRoom(IReadOnlyList<string> packages)
    {
        var headroom = Game.ObjectHeadroom;
        var all = 0;
        foreach (var package in packages)
        {
            all += Size(package);
        }

        if (headroom >= all + Spare)
        {
            return true;
        }

        var needed = 0;
        foreach (var package in packages)
        {
            if (!InMemory(package))
            {
                needed += Size(package);
            }
        }

        return headroom >= needed + Spare;
    }

    // The world of a map package, like a character's showcase. It is in memory exactly while
    // what is placed in the map is.
    public static string World(string package) => package + ".TheWorld";

    // In memory a cooked package goes by its file name, with or without the _SF.
    public static bool InMemory(string package)
    {
        var bare = package.EndsWith("_SF", StringComparison.OrdinalIgnoreCase) ? package[..^3] : package;
        return Game.FindObject<Package>(bare + "_SF") is { IsValid: true } || Game.FindObject<Package>(bare) is { IsValid: true };
    }

    static int Size(string package)
    {
        if (!Sizes.TryGetValue(package, out var size))
        {
            size = GameFiles.Exports(package);
            Sizes[package] = size;
        }

        return size;
    }

    // Loads a package for something that goes without when it can't be had. What names it in
    // the log, like "Firefly's flying moves". False when it isn't installed, there was no room or
    // the load failed. Wanted are paths of objects the caller needs from it. A package can stay in
    // memory after what was in it has been collected, so the first one missing goes to the SDK,
    // which reads the file again for it.
    public static bool TryLoad(string package, string what, string key, params string[] wanted)
    {
        var missing = Array.Find(wanted, path => Game.FindObject<GameObject>(path) is not { IsValid: true });
        if (wanted.Length > 0 && missing is null)
        {
            return true;
        }

        try
        {
            if (Game.TryLoadPackage(package, out _, needs: missing))
            {
                return true;
            }
        }
        catch (Exception error)
        {
            Log.Error(key, $"{char.ToUpperInvariant(what[0])}{what[1..]} failed to load", error);
            return false;
        }

        var why = !GameFiles.HasPackage(package) ? "its package isn't installed"
            : !HasRoom(package) ? "there was no room"
            : $"{missing} wasn't in it";
        Log.Info($"Couldn't load {what}, {why}.");
        return false;
    }

    // A package's main object has the package's own name. It is found in memory, or its package
    // is loaded first. Throws with short text for the log when it can't be had.
    public static T Main<T>(string name)
        where T : GameObject, IGameObject
    {
        var path = $"{name}.{name}";
        if (Game.FindObject<T>(path) is { IsValid: true } loaded)
        {
            return loaded;
        }

        if (Game.ObjectHeadroom < Cost<T>(name) + Spare)
        {
            // The engine collects on its next tick. Asking again a moment later can work.
            if (Game.TryGetWorldInfo(out var world))
            {
                world.ForceGarbageCollection(true);
            }

            throw new InvalidOperationException("the game is close to its object limit");
        }

        try
        {
            Game.TryLoadPackage(name, out _);
        }
        catch (Exception error)
        {
            Log.Error("packages.load", $"Package {name} failed to load", error);
        }

        return Game.FindObject<T>(path) is { IsValid: true } found ? found : throw new InvalidOperationException($"{name} is not installed or did not load");
    }

    // How many objects loading it would add. Nothing when it is in memory already.
    public static int Cost<T>(string name)
        where T : GameObject, IGameObject
    {
        if (Game.FindObject<T>($"{name}.{name}") is { IsValid: true })
        {
            return 0;
        }

        return GameFiles.Exports(name) is > 0 and var exports ? exports : UnknownCost;
    }
}
