#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// The game's showcase lists every suit and Batmobile skin. These read it the way its menu does.
static class Showcase
{
    static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase);

    public static bool Unlocked(RPersistentOptions options, RGameInfo.FShowcaseItem item, string key)
    {
        if (item.Unlocked)
        {
            return true;
        }

        var needs = item.Prerequisite.ToString() ?? "";
        return options.IsOptionsFlagSet(GameText.Of(needs.Length == 0 ? $"Showcase_{key}_Unlocked" : needs));
    }

    // The showcase's own name for the skin, or the package name tidied up if it has none.
    public static string Label(string key, string skin)
    {
        if (Labels.TryGetValue(key, out var known))
        {
            return known;
        }

        // Not known to be a native that only reads its text, so it gets its own strings. It runs
        // once per skin, the label is kept after.
        var label = GameObject.GetLocalisedString(GameText.Once("GFxUI"), GameText.Once("Showcase"), GameText.Once(key), false).ToString() ?? "";
        if (label.Length == 0 || label.StartsWith('?'))
        {
            label = skin.StartsWith("Playable_", StringComparison.OrdinalIgnoreCase) ? skin["Playable_".Length..].Replace('_', ' ') : skin;
        }

        label = label.Trim().ToUpperInvariant();
        Labels[key] = label;
        return label;
    }
}
