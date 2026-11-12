#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// Things taken from another character's packages, like Robin's zip kick for the Batclaw. They are
// rooted while any option uses them, so the engine can't free them, and let go once none does.
static class Borrowed
{
    // The animation player can still point at a borrowed animation for a moment after it is taken
    // off the pawn. Freed under it, the game crashed on the next pawn change.
    const double GraceSeconds = 5;

    // Room for the next character load, which is up to about 32,000 objects.
    const int MinHeadroom = 64000;

    sealed class Entry
    {
        public required GameObject Target;
        public readonly HashSet<string> Owners = new(StringComparer.Ordinal);
        public long LetAt;
    }

    static readonly Dictionary<nint, Entry> Kept = new();
    static readonly List<nint> Done = new();
    static bool _adopted;

    public static T Keep<T>(T target, string owner)
        where T : GameObject
    {
        if (!target.IsValid)
        {
            return target;
        }

        if (!Kept.TryGetValue(target.Ptr, out var entry))
        {
            entry = new Entry { Target = target };
            Kept[target.Ptr] = entry;
            target.AddToRoot();
        }

        entry.Owners.Add(owner);
        entry.LetAt = 0;
        return target;
    }

    // The same for something looked up that may not be there. Nothing comes back for nothing.
    public static T? KeepFound<T>(T? target, string owner)
        where T : GameObject => target is { IsValid: true } ? Keep(target, owner) : null;

    // What no other option still uses is let go after the grace time.
    public static void Let(string owner)
    {
        var due = Stopwatch.GetTimestamp() + (long)(GraceSeconds * Stopwatch.Frequency);
        foreach (var entry in Kept.Values)
        {
            if (entry.Owners.Remove(owner) && entry.Owners.Count == 0)
            {
                entry.LetAt = due;
            }
        }
    }

    public static void Tick()
    {
        if (!_adopted)
        {
            _adopted = true;
            Adopt();
        }

        if (Kept.Count == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        foreach (var (pointer, entry) in Kept)
        {
            if (entry.Owners.Count == 0 && entry.LetAt != 0 && now >= entry.LetAt)
            {
                Done.Add(pointer);
            }
        }

        foreach (var pointer in Done)
        {
            var target = Kept[pointer].Target;
            Kept.Remove(pointer);
            if (target.IsValid)
            {
                target.RemoveFromRoot();
            }
        }

        Done.Clear();
    }

    // A hot reload starts this class over. What is still rooted is handed to the next copy by
    // path, so it can let go of it, or keep it if an option takes it again.
    public static void Release()
    {
        var paths = new List<string>();
        var times = new List<long>();
        var soon = Stopwatch.GetTimestamp() + (long)(GraceSeconds * Stopwatch.Frequency);
        foreach (var entry in Kept.Values)
        {
            if (entry.Target.IsValid)
            {
                paths.Add(entry.Target.GetPathName());
                times.Add(entry.LetAt != 0 ? entry.LetAt : soon);
            }
        }

        Survive.Set(Survive.BorrowedPaths, paths.ToArray());
        Survive.Set(Survive.BorrowedLetAt, times.ToArray());
        Kept.Clear();
    }

    static void Adopt()
    {
        var paths = Survive.Get<string[]?>(Survive.BorrowedPaths, null);
        var times = Survive.Get<long[]?>(Survive.BorrowedLetAt, null);
        Survive.Set<string[]?>(Survive.BorrowedPaths, null);
        Survive.Set<long[]?>(Survive.BorrowedLetAt, null);
        if (paths is null || times is null)
        {
            return;
        }

        for (var i = 0; i < Math.Min(paths.Length, times.Length); i++)
        {
            GameObject? target = null;
            try
            {
                target = GameObject.StaticFindObjectChecked<GameObject>(null, null, paths[i], false);
            }
            catch
            {
                // Gone already, nothing left to let go.
            }

            if (target is { IsValid: true } && !Kept.ContainsKey(target.Ptr))
            {
                Kept[target.Ptr] = new Entry { Target = target, LetAt = times[i] };
            }
        }
    }

    // A character's main asset, loading their packages first if it isn't in memory. Throws with
    // short text for the log when it can't.
    public static RAddContentPlayerCharacter Character(string asset, params string[] packages)
    {
        if (FindCharacter(asset) is { } found)
        {
            return found;
        }

        if (ObjectTable.Headroom < MinHeadroom)
        {
            // The engine collects on its next tick. Asking again a moment later can work.
            if (Game.TryGetWorldInfo(out var world))
            {
                world.ForceGarbageCollection(true);
            }

            throw new InvalidOperationException("the game is close to its object limit, so the package was not loaded");
        }

        // Some packages are cooked with _SF on the end, and the game's loader adds it itself.
        foreach (var package in packages)
        {
            foreach (var name in package.EndsWith("_SF", StringComparison.OrdinalIgnoreCase) ? new[] { package } : new[] { package, package + "_SF" })
            {
                try
                {
                    if (Game.LoadPackage(name) is { IsValid: true })
                    {
                        break;
                    }
                }
                catch (Exception error)
                {
                    Log.Error("borrowed.package", $"Package {name} failed to load", error);
                }
            }
        }

        return FindCharacter(asset) ?? throw new InvalidOperationException($"{asset} is not installed or did not load");
    }

    static RAddContentPlayerCharacter? FindCharacter(string asset)
    {
        foreach (var character in Game.FindObjects<RAddContentPlayerCharacter>())
        {
            if (character.IsValid && string.Equals(character.Name.ToString(), asset, StringComparison.OrdinalIgnoreCase))
            {
                return character;
            }
        }

        return null;
    }
}
