#nullable enable

using System.Numerics;
using System.Text;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// TELEPORT > PLACES. The places on the game's map, from the list the game itself loads from
// BmGame.ini, named by the game's own text so they follow its language.
static class Places
{
    sealed record Place(string Label, Vector3 At);

    // Exact is false for areas and landmarks, which land on the highest ground there.
    sealed record Kind(string Id, string Label, bool Exact, Func<string, bool> Takes)
    {
        public readonly List<Place> Items = new();
    }

    static readonly HashSet<string> CityMaps = new(StringComparer.OrdinalIgnoreCase) { "Cityx", "Cityy", "Cityz", "Batentry" };

    static readonly Kind[] Kinds =
    {
        new("areas", "AREAS", false, type => type is "LabelTextBigArea" or "LabelTextArea"),
        new("landmarks", "LANDMARKS", false, type => type is "LabelTextFeature1" or "BuildingMarker"),
        new("trophies", "RIDDLER TROPHIES", true, type => type == "RiddlerPickup"),
        new("breakables", "RIDDLER BREAKABLES", true, type => type == "RiddlerBreakable"),
        new("riddles", "RIDDLES", true, type => type == "RiddlerCameo"),
        new("bombs", "RIDDLER BOMBS", true, type => type == "RiddlerBomb"),
        new("races", "RACES", true, type => type == "NigmaRace"),
        new("ar", "AR CHALLENGES", true, type => type == "ARChallenge"),
        new("most_wanted", "MOST WANTED", true, type => type.StartsWith("MostWanted", StringComparison.Ordinal)),
    };

    static readonly (string, string)[] Nothing = { ("none", "NONE") };

    // The named parts of each island, like KINGSTON or CHINATOWN, without the islands themselves.
    static readonly List<Place> Districts = new();

    static bool _built;
    static int _shown = -1;

    public static readonly Choice Category = new(
        "teleport.places.category", "CATEGORY",
        "Which kind of place PLACE lists.",
        Kinds.Select(kind => (kind.Id, kind.Label)).ToArray())
    {
        Preference = true,
    };

    public static readonly Choice Pick = new(
        "teleport.places.place", "PLACE",
        "Collectibles are named by the area they are in.",
        Nothing, saved: false);

    public static readonly Command GoThere = new(
        "teleport.places.go", "TELEPORT TO PLACE",
        "Takes you to the place picked above. Areas and landmarks land you on the highest ground there.",
        () => Go(GoThere!, Picked()));

    public static readonly Command MarkThere = new(
        "teleport.places.waypoint", "WAYPOINT TO PLACE",
        "Sets your map waypoint on the place picked above, the way the map does.",
        () => Mark(Picked()));

    public static readonly Command Nearest = new(
        "teleport.places.nearest", "NEAREST IN CATEGORY",
        "Takes you to the nearest place of the category picked above.",
        () => Go(Nearest!, Closest()));

    public static readonly Command Anywhere = new(
        "teleport.places.random", "RANDOM IN CATEGORY",
        "Takes you to a random place of the category picked above, and shows it in PLACE.",
        () => Go(Anywhere!, Random()));

    static readonly Random Dice = new();

    public static readonly Catalog.Section[] Sections =
    {
        new("PLACES", Category, Pick, GoThere, MarkThere, Nearest, Anywhere),
        new("SAVED SPOTS", Spots.Spot, Spots.SaveHere, Spots.GoThere, Spots.MarkThere, Spots.Clear),
    };

    // Runs every tick. The list under PLACE follows CATEGORY.
    public static void Tick()
    {
        if (!_built && World.Info is { } info)
        {
            _built = Build(info);
            if (_built)
            {
                Spots.Refresh();
            }
        }

        if (!_built || _shown == Category.Index)
        {
            return;
        }

        _shown = Category.Index;
        var items = Kinds[_shown].Items;
        Pick.SetOptions(items.Count == 0 ? Nothing : items.Select((place, i) => (i.ToString(), place.Label)).ToArray(), 0);
    }

    // The name of the part of the city nearest a point.
    public static string AreaNear(Vector3 at)
    {
        var best = "";
        var nearest = float.MaxValue;
        foreach (var area in Districts)
        {
            var distance = Flat(area.At - at);
            if (distance < nearest)
            {
                nearest = distance;
                best = area.Label;
            }
        }

        return best;
    }

    static (Place Place, bool Exact)? Picked()
    {
        var kind = Kinds[Category.Index];
        return int.TryParse(Pick.CurrentId, out var i) && i >= 0 && i < kind.Items.Count ? (kind.Items[i], kind.Exact) : null;
    }

    static (Place Place, bool Exact)? Closest()
    {
        var kind = Kinds[Category.Index];
        Pawn? mover = World.Driving is { IsValid: true } driving ? driving : World.Player;
        if (mover is null || kind.Items.Count == 0)
        {
            return null;
        }

        var here = mover.Location - Teleport.CityOffset;
        var best = kind.Items.MinBy(place => Flat(place.At - here))!;
        return (best, kind.Exact);
    }

    static (Place Place, bool Exact)? Random()
    {
        var kind = Kinds[Category.Index];
        if (kind.Items.Count == 0)
        {
            return null;
        }

        // Shown in PLACE, so WAYPOINT TO PLACE and TELEPORT TO PLACE can find it again.
        var index = Dice.Next(kind.Items.Count);
        if (_shown == Category.Index)
        {
            Pick.SetTo(index);
        }

        return (kind.Items[index], kind.Exact);
    }

    static Outcome Go(Command command, (Place Place, bool Exact)? target) =>
        target is { } goal ? Teleport.Start(command, goal.Place.At + Teleport.CityOffset, goal.Exact) : Outcome.Failed("NONE PICKED");

    static Outcome Mark((Place Place, bool Exact)? target)
    {
        if (target is not { } goal)
        {
            return Outcome.Failed("NONE PICKED");
        }

        if (World.Info is null)
        {
            return Outcome.NoPlayer;
        }

        var at = goal.Place.At + Teleport.CityOffset;
        Markers.Set(at);
        return Outcome.Done("SET");
    }

    static bool Build(RGameInfo info)
    {
        var list = info.MapElementItemList;
        if (list.Count == 0)
        {
            return false;
        }

        // Areas first, so the others can be named by them.
        var rows = new List<(string Type, string Name, Vector3 At)>();
        for (var i = 0; i < list.Count; i++)
        {
            var row = list[i];
            if (CityMaps.Contains(row.MapName.ToString() ?? ""))
            {
                rows.Add((row.ItemType.ToString() ?? "", row.ItemName.ToString() ?? "", new Vector3(row.X, row.Y, row.Z)));
            }
        }

        foreach (var kind in Kinds)
        {
            kind.Items.Clear();
        }

        Districts.Clear();
        var islands = new HashSet<string>();
        foreach (var (type, name, _) in rows)
        {
            if (type == "LabelTextBigArea" && Localized("LevelDesc", name, "FRONTEND") is { } island)
            {
                islands.Add(island);
            }
        }

        // Only places the game has a name for. The rest are working names never meant to be seen.
        foreach (var (type, name, at) in rows)
        {
            for (var k = 0; k < 2; k++)
            {
                if (Kinds[k].Takes(type) && Localized("LevelDesc", name, "FRONTEND") is { } label)
                {
                    AddOnce(Kinds[k], label, at);
                    if (type == "LabelTextArea" && !islands.Contains(label))
                    {
                        Districts.Add(new Place(label, at));
                    }
                }
            }
        }

        foreach (var kind in Kinds)
        {
            kind.Items.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
        }

        // Collectibles are numbered within the area they are in. Most Wanted spots are named by
        // their mission.
        var counts = new Dictionary<string, int>();
        foreach (var (type, _, at) in rows.OrderBy(row => row.At.X).ThenBy(row => row.At.Y))
        {
            for (var k = 2; k < Kinds.Length; k++)
            {
                var kind = Kinds[k];
                if (!kind.Takes(type))
                {
                    continue;
                }

                var group = kind.Id == "most_wanted" ? Mission(type) : AreaNear(at);
                var key = kind.Id + "|" + group;
                counts[key] = counts.GetValueOrDefault(key) + 1;
                kind.Items.Add(new Place(group + " " + counts[key], at));
            }
        }

        foreach (var kind in Kinds.Skip(2))
        {
            kind.Items.Sort((a, b) => Compare(a.Label, b.Label));
        }

        return true;
    }

    static void AddOnce(Kind kind, string label, Vector3 at)
    {
        if (label.Length > 0 && !kind.Items.Exists(place => place.Label == label))
        {
            kind.Items.Add(new Place(label, at));
        }
    }

    // The mission's own title, like THE LINE OF DUTY.
    static string Mission(string type)
    {
        var section = type["MostWanted".Length..];
        if (section.Length == 0)
        {
            return "MOST WANTED";
        }

        return Localized(section, "H", "MostWanted") is { } title ? title.ToUpperInvariant() : Pretty(section);
    }

    // The game's own text lookup. A key it hasn't got comes back wrapped in question marks.
    static string? Localized(string section, string key, string package)
    {
        var text = GameObject.Localize(GameText.Of(section), GameText.Of(key), GameText.Of(package)).ToString();
        return string.IsNullOrWhiteSpace(text) || text.StartsWith('?') ? null : text.Trim();
    }

    // CityZ_Building_GCPD reads as CITYZ BUILDING GCPD, and PenguinCache as PENGUIN CACHE.
    static string Pretty(string text)
    {
        var built = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var letter = text[i];
            if (letter == '_')
            {
                built.Append(' ');
                continue;
            }

            if (i > 0 && char.IsUpper(letter) && char.IsLower(text[i - 1]))
            {
                built.Append(' ');
            }

            built.Append(char.ToUpperInvariant(letter));
        }

        return built.ToString();
    }

    // KINGSTON 2 before KINGSTON 10.
    static int Compare(string a, string b)
    {
        var aSplit = a.LastIndexOf(' ');
        var bSplit = b.LastIndexOf(' ');
        var byName = string.CompareOrdinal(a[..Math.Max(0, aSplit)], b[..Math.Max(0, bSplit)]);
        if (byName != 0)
        {
            return byName;
        }

        return int.TryParse(a[(aSplit + 1)..], out var aNumber) && int.TryParse(b[(bSplit + 1)..], out var bNumber)
            ? aNumber.CompareTo(bNumber)
            : string.CompareOrdinal(a, b);
    }

    static float Flat(Vector3 way) => new Vector2(way.X, way.Y).Length();
}
