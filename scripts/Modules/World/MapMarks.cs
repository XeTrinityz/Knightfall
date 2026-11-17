#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > MARKERS, MAP ICONS. Draws the game's map items in the world, from the same list the map
// and TELEPORT > PLACES use: Riddler items, races, AR challenges and Most Wanted spots. Each is a
// pin with a tag, only for the nearest on screen, so the city isn't covered.
static class MapMarks
{
    const float Metre = Teleport.UnitsPerMetre;
    const float LookEvery = 1f;

    sealed record Kind(string Id, string Name, string Tag, Rgba Color);

    static readonly Kind[] Kinds =
    {
        new("trophies", "RIDDLER TROPHIES", "TROPHY", new Rgba(90, 220, 130)),
        new("breakables", "RIDDLER BREAKABLES", "BREAKABLE", new Rgba(140, 235, 90)),
        new("riddles", "RIDDLES", "RIDDLE", new Rgba(60, 200, 170)),
        new("bombs", "RIDDLER BOMBS", "BOMB", new Rgba(255, 220, 80)),
        new("races", "RACES", "RACE", new Rgba(100, 180, 255)),
        new("ar", "AR CHALLENGES", "AR CHALLENGE", new Rgba(180, 140, 255)),
        new("most_wanted", "MOST WANTED", "MOST WANTED", new Rgba(240, 80, 70)),
    };

    readonly record struct Pin(Vector3 At, int Kind);

    static List<Pin> _pins = new();
    static float _lookedAt = -100f;

    public static readonly Toggle On = new(
        "world.markers.map_icons", "MAP ICON MARKERS",
        "Marks the game's map items in the world, through walls: Riddler items, races, AR challenges and Most Wanted spots. Only the nearest ones on screen get a pin.")
    {
        Tick = Look,
        Restore = Forget,
    };

    static readonly Toggle[] Show = Kinds
        .Select(kind => new Toggle("world.markers.map_" + kind.Id, "SHOW " + kind.Name, $"Marks {kind.Name.ToLowerInvariant()}.", on: true) { Preference = true })
        .ToArray();

    public static readonly Toggle Left = new(
        "world.markers.map_left", "ONLY WHAT'S LEFT",
        "Leaves out Riddler items you have picked up, and Most Wanted spots the map doesn't show yet.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Slider Range = new(
        "world.markers.map_range", "MAP ICON RANGE",
        "How far away a map item still gets a pin.",
        min: 50, max: 2000, step: 50, value: 400, unit: " M")
    {
        Preference = true,
    };

    public static readonly Slider Most = new(
        "world.markers.map_most", "MAP ICONS SHOWN",
        "The most pins at once, nearest first.",
        min: 5, max: 60, step: 5, value: 20)
    {
        Preference = true,
    };

    public static readonly Catalog.Section Section = new(
        "MAP", new Feature[] { On }.Concat(Show).Concat(new Feature[] { Left, Range, Most }).ToArray());

    public static bool Showing => On.On && _pins.Count > 0;

    static void Forget()
    {
        _pins = new List<Pin>();
        _lookedAt = -100f;
    }

    // Gathered once a second. Only the nearest ones inside the range are kept.
    static void Look()
    {
        if (World.Paused || World.Player is not { } player || MathF.Abs(World.Now - _lookedAt) < LookEvery)
        {
            return;
        }

        _lookedAt = World.Now;
        var shift = Teleport.CityOffset;
        var range = (float)Range.Value * Metre;
        var found = new List<(float Away, Pin Pin)>();
        for (var k = 0; k < Kinds.Length; k++)
        {
            if (!Show[k].On || Places.Marked(Kinds[k].Id, Left.On) is not { } spots)
            {
                continue;
            }

            foreach (var at in spots)
            {
                var away = Vector3.Distance(at + shift, player.Location);
                if (away <= range)
                {
                    found.Add((away, new Pin(at + shift, k)));
                }
            }
        }

        found.Sort(static (a, b) => a.Away.CompareTo(b.Away));
        _pins = found.Take((int)Most.Value).Select(item => item.Pin).ToList();
    }

    // Drawn while the hologram is framed for this view, flat on the screen.
    public static void Render(float time)
    {
        if (!Showing)
        {
            return;
        }

        Hologram.Pop();
        Draw.Place(0f, 0f, 1f);
        Draw.Fade(1f);
        var eye = Hologram.Eye;
        foreach (var pin in _pins)
        {
            if (!Hologram.Screen(pin.At, out var pixels))
            {
                continue;
            }

            var spot = pixels / Draw.Scale;
            if (spot.X < Threats.Edge || spot.X > Draw.ScreenWidth - Threats.Edge || spot.Y < Threats.Edge || spot.Y > Draw.ScreenHeight - Threats.Edge)
            {
                continue;
            }

            var kind = Kinds[pin.Kind];
            Threats.Pin(spot, kind.Color, time);
            Threats.Tag(spot.X + 12f, spot.Y - Threats.TagHeight / 2f, kind.Tag, Threats.Distance(Vector3.Distance(pin.At, eye)), kind.Color);
        }
    }
}
