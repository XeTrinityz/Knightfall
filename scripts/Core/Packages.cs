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

    // Whether the object table can take a package, with the spare room on top. The name is the
    // file's, with or without the _SF a cooked package has on the end.
    public static bool HasRoom(string package) => ObjectTable.Headroom >= GameFiles.Exports(package) + Spare;

    // Loads a package for something that goes without when it can't be had. What names it in
    // the log, like "Firefly's flying moves". False when there was no room or the load failed.
    public static bool TryLoad(string package, string what, string key)
    {
        if (!HasRoom(package))
        {
            Log.Info($"No room to load {what}.");
            return false;
        }

        try
        {
            Game.LoadPackage(package);
            return true;
        }
        catch (Exception error)
        {
            Log.Error(key, $"{char.ToUpperInvariant(what[0])}{what[1..]} failed to load", error);
            return false;
        }
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

        if (ObjectTable.Headroom < Cost<T>(name) + Spare)
        {
            // The engine collects on its next tick. Asking again a moment later can work.
            if (Game.TryGetWorldInfo(out var world))
            {
                world.ForceGarbageCollection(true);
            }

            throw new InvalidOperationException("the game is close to its object limit");
        }

        // Cooked packages are named with _SF on the end. A few are not.
        foreach (var file in new[] { name + "_SF", name })
        {
            try
            {
                if (Game.LoadPackage(file) is { IsValid: true })
                {
                    break;
                }
            }
            catch (Exception error)
            {
                Log.Error("packages.load", $"Package {file} failed to load", error);
            }
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
