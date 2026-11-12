#nullable enable

using BmSDK;

namespace Knightfall;

// Keys and pad buttons kept from the game, counted. The menu and the free camera can both keep
// the same one, and it only goes back to the game once both have let go.
static class Swallowed
{
    static readonly Dictionary<Keys, int> KeyCounts = new();
    static readonly Dictionary<string, int> NameCounts = new(StringComparer.OrdinalIgnoreCase);

    public static void Take(Keys key)
    {
        if (Step(KeyCounts, key, 1) == 1)
        {
            Keyboard.Swallow(key);
        }
    }

    public static void Give(Keys key)
    {
        if (Step(KeyCounts, key, -1) == 0)
        {
            Keyboard.Unswallow(key);
        }
    }

    // An input by the game's own name, like a stick's axis.
    public static void Take(string name)
    {
        if (Step(NameCounts, name, 1) == 1)
        {
            Keyboard.Swallow(name);
        }
    }

    public static void Give(string name)
    {
        if (Step(NameCounts, name, -1) == 0)
        {
            Keyboard.Unswallow(name);
        }
    }

    // A pad button goes by every name the game knows it by.
    public static void Take(Pad.Button button)
    {
        foreach (var name in Pad.EngineNames(button))
        {
            if (Step(NameCounts, name, 1) == 1)
            {
                Keyboard.Swallow(name);
            }
        }
    }

    public static void Give(Pad.Button button)
    {
        foreach (var name in Pad.EngineNames(button))
        {
            if (Step(NameCounts, name, -1) == 0)
            {
                Keyboard.Unswallow(name);
            }
        }
    }

    // The new count, or -1 when there was nothing to give back.
    static int Step<T>(Dictionary<T, int> counts, T item, int by) where T : notnull
    {
        counts.TryGetValue(item, out var count);
        if (by < 0 && count == 0)
        {
            return -1;
        }

        count += by;
        if (count == 0)
        {
            counts.Remove(item);
        }
        else
        {
            counts[item] = count;
        }

        return count;
    }
}
