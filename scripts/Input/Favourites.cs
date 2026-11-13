#nullable enable

namespace Knightfall;

// The rows on the favourites radial, marked from the menu. Kept in settings.toml as the options'
// ids, in the order they were added.
static class Favourites
{
    const string Section = "settings.radial";
    const string Key = "favourites";

    static readonly List<Feature> Marked = new();

    public static int Count => Marked.Count;

    public static bool Has(Feature feature) => Marked.Contains(feature);

    // Only rows the radial can use straight away.
    public static bool Allowed(Feature? feature) => Hotkeys.Bindable(feature);

    public static void Load()
    {
        Marked.Clear();
        if (Settings.Kept(Section, Key) is not string text)
        {
            return;
        }

        foreach (var id in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Catalog.Find(id) is { } feature && Allowed(feature) && !Marked.Contains(feature))
            {
                Marked.Add(feature);
            }
        }
    }

    public static void Flip(Feature feature)
    {
        if (!Marked.Remove(feature))
        {
            Marked.Add(feature);
        }

        Save();
    }

    public static Outcome Clear()
    {
        var count = Marked.Count;
        Marked.Clear();
        Save();
        return count > 0 ? Outcome.Done($"{count} REMOVED") : Outcome.Unchanged("NONE MARKED");
    }

    // In the order RADIAL ORDER asks for.
    public static List<Feature> Ordered(int order)
    {
        return order switch
        {
            1 => Marked.OrderBy(feature => feature.Plain, StringComparer.Ordinal).ToList(),
            2 => Marked.OrderBy(feature => IndexOf(feature)).ToList(),
            _ => Marked.ToList(),
        };

        static int IndexOf(Feature feature)
        {
            for (var i = 0; i < Catalog.All.Count; i++)
            {
                if (ReferenceEquals(Catalog.All[i], feature))
                {
                    return i;
                }
            }

            return int.MaxValue;
        }
    }

    // The RADIAL tab lists them, so it is built again if it is the one on screen.
    static void Save()
    {
        Settings.Set(Section, Key, string.Join(",", Marked.Select(feature => feature.Id)));
        Hotkeys.Relabel();
        Menu.Reshow(Radial.On);
    }
}
