#nullable enable

using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > MARKERS, THREATS. Marks what the game counts as threats on the island you are on, the
// same list its threat level shows. Watchtowers, checkpoints and explosive devices come from the
// island's own list, so they're marked anywhere on the island. APCs, drones and tanks only exist
// near you, so those are marked as they come. Each is a small tag drawn over the world, and one
// you can't see gets an arrow on the edge of the screen.
static class Threats
{
    enum Type
    {
        Watchtower,
        Checkpoint,
        Explosive,
        Apc,
        DroneTank,
        AerialDrone,
        Cobra,
    }

    static readonly string[] Names =
    {
        "WATCHTOWER", "CHECKPOINT", "EXPLOSIVE DEVICE", "COMMAND APC", "DRONE TANK", "AERIAL DRONE", "COBRA TANK",
    };

    static readonly Rgba[] Colors =
    {
        new(255, 168, 60),
        new(240, 76, 66),
        new(255, 220, 80),
        new(235, 90, 200),
        new(110, 200, 255),
        new(110, 200, 255),
        new(176, 140, 255),
    };

    const float Metre = Teleport.UnitsPerMetre;
    const float SitesEvery = 1f;
    const float VehiclesEvery = 0.25f;

    // The tags are kept this far in from the edges of the screen.
    public const float Edge = 44f;

    const float NameSize = 8f;
    const float NoteSize = 8f;
    public const float TagHeight = 22f;

    static readonly Rgba Ink = new(8, 10, 14);
    static readonly Rgba White = new(255, 255, 255);

    // A fixed threat, at its spot on the map without the city's shift. A moving one carries its
    // vehicle, which is read again each frame.
    readonly record struct Threat(Type Type, Vector3 At, Actor? Actor);

    static List<Threat> _sites = new();
    static List<Threat> _vehicles = new();
    static float _sitesAt = -100f;
    static float _vehiclesAt = -100f;
    static bool _listed;

    // Each site's map spot by its icon name, read once per game.
    static Dictionary<string, Vector3>? _spots;
    static RGameInfo? _spotsFrom;

    // Distances drawn as text. Every new string costs the game a little memory for good, so each
    // one is made once.
    static readonly Dictionary<int, string> Distances = new();

    public static readonly Toggle On = new(
        "world.markers.threats", "THREAT MARKERS",
        "Marks every threat the game counts on your island, through walls and at any distance. APCs, drones and tanks only show once they are near you.")
    {
        Tick = Look,
        Restore = Forget,
    };

    static readonly Toggle ShowWatchtowers = ShowType("watchtowers", "WATCHTOWERS");
    static readonly Toggle ShowCheckpoints = ShowType("checkpoints", "CHECKPOINTS");
    static readonly Toggle ShowExplosives = ShowType("explosives", "EXPLOSIVE DEVICES");
    static readonly Toggle ShowApcs = ShowType("apcs", "COMMAND APCS");
    static readonly Toggle ShowDrones = ShowType("drones", "DRONES");
    static readonly Toggle ShowCobras = ShowType("cobras", "COBRA TANKS");

    public static readonly Toggle Arrows = new(
        "world.markers.threat_arrows", "EDGE ARROWS",
        "Threats off screen or behind you get an arrow on the edge of the screen, pointing the way.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Catalog.Section Section = new(
        "THREATS", On, ShowWatchtowers, ShowCheckpoints, ShowExplosives, ShowApcs, ShowDrones, ShowCobras, Arrows);

    public static bool Showing => On.On && (_sites.Count > 0 || _vehicles.Count > 0);

    static Toggle ShowType(string id, string label) =>
        new("world.markers.show_" + id, "SHOW " + label, $"Marks {label.ToLowerInvariant()}.", on: true)
        {
            Preference = true,
        };

    static bool Shown(Type type) => type switch
    {
        Type.Watchtower => ShowWatchtowers.On,
        Type.Checkpoint => ShowCheckpoints.On,
        Type.Explosive => ShowExplosives.On,
        Type.Apc => ShowApcs.On,
        Type.Cobra => ShowCobras.On,
        _ => ShowDrones.On,
    };

    static void Forget()
    {
        _sites = new List<Threat>();
        _vehicles = new List<Threat>();
        _sitesAt = -100f;
        _vehiclesAt = -100f;
        _listed = false;
    }

    // Every game tick while on. The island's list is read once a second and vehicles a few times
    // a second.
    static void Look()
    {
        if (World.Info is not { } game)
        {
            return;
        }

        if (MathF.Abs(World.Now - _sitesAt) >= SitesEvery)
        {
            _sitesAt = World.Now;
            _listed = Listed(game);
            _sites = _listed ? Sites(game) : new List<Threat>();
        }

        if (!_listed)
        {
            _vehicles = new List<Threat>();
        }
        else if (MathF.Abs(World.Now - _vehiclesAt) >= VehiclesEvery)
        {
            _vehiclesAt = World.Now;
            _vehicles = Vehicles(game);
        }
    }

    // Whether the game would show its threat list here at all. It shows nothing before the
    // threat level is switched on, or while the gas jams it.
    static bool Listed(RGameInfo game)
    {
        if (game.IsChallengeMode() || !Game.TryGetWorldInfo(out var world) || world.GRI is not RGameRI { FlagManager: { IsValid: true } flags })
        {
            return false;
        }

        return flags.GetGlobalFlag("ThreatLevelActivated")
            && !flags.GetGlobalFlag("ThreatLevelSuppressed")
            && !(flags.GetGlobalFlag("GasActiveInOverworld") && !flags.GetGlobalFlag("CityX_09_Ivy_Dead_Completed"));
    }

    // The island's watchtowers, checkpoints and explosive devices that the game still counts.
    static List<Threat> Sites(RGameInfo game)
    {
        var found = new List<Threat>();
        if (game.PopulationManager is not { IsValid: true } people || Spots(game) is not { } spots)
        {
            return found;
        }

        var island = people.CurrentDistrictId;
        var boxes = people.ThreatAssetContainers;
        for (var i = 0; i < boxes.Count; i++)
        {
            if (boxes[i] is not { IsValid: true } box || (int)box.CityBlock != island)
            {
                continue;
            }

            Add(found, spots, box.WatchtowersInZone, Type.Watchtower, "SS_WatchTower_");
            Add(found, spots, box.CheckpointsInZone, Type.Checkpoint, "SS_Checkpoint_");
            Add(found, spots, box.BeaconsInZone, Type.Explosive, "SS_Drones_Beacon_");
        }

        return found;
    }

    // An asset counts the way the game counts it for the threat level. Its map icon is named
    // after the letter that ends the asset's name, so WatchTower_Y_N is SS_WatchTower_N.
    static void Add<T>(List<Threat> found, Dictionary<string, Vector3> spots, TArray<T> assets, Type type, string icon)
        where T : RThreatBaseAsset
    {
        for (var i = 0; i < assets.Count; i++)
        {
            if (assets[i] is not { IsValid: true } asset || asset.bDoesntCountTowardsThreat || !asset.ShouldActivate() || asset.IsDestroyed())
            {
                continue;
            }

            var name = asset.Name.ToString() ?? "";
            var letter = name[(name.LastIndexOf('_') + 1)..];
            if (spots.TryGetValue(icon + letter, out var at))
            {
                found.Add(new Threat(type, at, null));
            }
            else
            {
                Log.Warn("threats.spot", $"THREAT MARKERS: no map spot for {name}, so it isn't marked.");
            }
        }
    }

    static Dictionary<string, Vector3>? Spots(RGameInfo game)
    {
        if (_spots is not null && ReferenceEquals(_spotsFrom, game))
        {
            return _spots;
        }

        var list = game.MapElementItemList;
        if (list.Count == 0)
        {
            return null;
        }

        var spots = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            var row = list[i];
            var name = row.ItemName.ToString() ?? "";
            if (name.StartsWith("SS_", StringComparison.Ordinal))
            {
                spots.TryAdd(name, new Vector3(row.X, row.Y, row.Z));
            }
        }

        _spots = spots;
        _spotsFrom = game;
        return spots;
    }

    // Militia vehicles that are still fighting. Parked tanks are only scenery.
    static List<Threat> Vehicles(RGameInfo game)
    {
        var found = new List<Threat>();
        var vehicles = game.Vehicles;
        for (var i = 0; i < vehicles.Count; i++)
        {
            if (vehicles[i] is not RVehicleNPC { IsValid: true, bDeleteMe: false, bHidden: false, IsFriendly: false, IsHackedFriendly: false } vehicle
                || vehicle.Health <= 0 || vehicle is RVehicleStaticTank or RVehicleNPCBatmobile)
            {
                continue;
            }

            Type? type = vehicle switch
            {
                RVehicleHeavyTank => Type.Cobra,
                RVehicleTank => Type.DroneTank,
                RVehicleFlying => Type.AerialDrone,
                _ => IsApc(vehicle) ? Type.Apc : null,
            };

            if (type is { } kind)
            {
                found.Add(new Threat(kind, Vector3.Zero, vehicle));
            }
        }

        return found;
    }

    static bool IsApc(RVehicleNPC vehicle) =>
        vehicle.ObjectArchetype is RVehicleNPC { IsValid: true } template
        && (template.Name.ToString() ?? "").Contains("APC", StringComparison.OrdinalIgnoreCase);

    // Drawn while the hologram is framed for this view, flat on the screen.
    public static void Render(float time)
    {
        if (!On.On || (_sites.Count == 0 && _vehicles.Count == 0))
        {
            return;
        }

        Hologram.Pop();
        Draw.Place(0f, 0f, 1f);
        Draw.Fade(1f);
        var shift = Teleport.CityOffset;
        var eye = Hologram.Eye;

        foreach (var site in _sites)
        {
            if (Shown(site.Type))
            {
                Mark(site.Type, site.At + shift, eye, time);
            }
        }

        foreach (var threat in _vehicles)
        {
            if (Shown(threat.Type) && Things.Alive(threat.Actor) && threat.Actor is { bHidden: false } actor)
            {
                var shape = Things.ShapeOf(actor);
                Mark(threat.Type, shape.Middle + Vector3.UnitZ * shape.Half.Z, eye, time);
            }
        }
    }

    static void Mark(Type type, Vector3 at, Vector3 eye, float time)
    {
        var color = Colors[(int)type];
        var distance = Distance(Vector3.Distance(at, eye));
        var width = Draw.ScreenWidth;
        var height = Draw.ScreenHeight;

        if (Hologram.Screen(at, out var pixels))
        {
            var spot = pixels / Draw.Scale;
            if (spot.X >= Edge && spot.X <= width - Edge && spot.Y >= Edge && spot.Y <= height - Edge)
            {
                Pin(spot, color, time);
                Tag(spot.X + 12f, spot.Y - TagHeight / 2f, Names[(int)type], distance, color);
                return;
            }
        }

        if (Arrows.On)
        {
            Arrow(Hologram.Direction(at), Names[(int)type], distance, color);
        }
    }

    // A diamond on the spot, with a fainter one that keeps growing out of it.
    static void Pin(Vector2 at, Rgba color, float time)
    {
        Diamond(at, 6f, color);
        Draw.Quad(at.X, at.Y - 2.5f, at.X + 2.5f, at.Y, at.X, at.Y + 2.5f, at.X - 2.5f, at.Y, color);

        var phase = time * 0.7f % 1f;
        Diamond(at, 6f + 10f * phase, color with { A = (byte)(150f * (1f - phase)) });
    }

    static void Diamond(Vector2 at, float size, Rgba color)
    {
        Draw.Line(at.X, at.Y - size, at.X + size, at.Y, color);
        Draw.Line(at.X + size, at.Y, at.X, at.Y + size, color);
        Draw.Line(at.X, at.Y + size, at.X - size, at.Y, color);
        Draw.Line(at.X - size, at.Y, at.X, at.Y - size, color);
    }

    // The name over the distance, on a dark strip with the threat's colour down its left side.
    static void Tag(float left, float top, string name, string distance, Rgba color)
    {
        var width = MathF.Max(Draw.TrackedWidth(name, NameSize, 1.5f), Draw.TextWidth(distance, NoteSize)) + 12f;
        Draw.Rect(left, top, width, TagHeight, Ink with { A = 170 });
        Draw.Rect(left, top, 2f, TagHeight, color);
        Draw.Tracked(name, left + 6f, top + 3f, NameSize, 1.5f, White);
        Draw.Text(distance, left + 6f, top + 12f, NoteSize, color);
    }

    // An arrow on the edge of the screen in the threat's direction, with its tag just inside it.
    public static void Arrow(Vector2 direction, string name, string distance, Rgba color)
    {
        var middle = new Vector2(Draw.ScreenWidth / 2f, Draw.ScreenHeight / 2f);
        var reach = new Vector2(middle.X - Edge, middle.Y - Edge);
        var stretch = MathF.Min(
            MathF.Abs(direction.X) > 1e-4f ? reach.X / MathF.Abs(direction.X) : float.MaxValue,
            MathF.Abs(direction.Y) > 1e-4f ? reach.Y / MathF.Abs(direction.Y) : float.MaxValue);
        var tip = middle + direction * stretch;

        const float length = 12f;
        const float half = 7f;
        var back = tip - direction * length;
        var side = new Vector2(-direction.Y, direction.X) * half;
        Draw.Triangle(tip.X, tip.Y, back.X + side.X, back.Y + side.Y, back.X - side.X, back.Y - side.Y, color);

        // The tag sits inward from the arrow, on whichever side keeps it on screen.
        var width = MathF.Max(Draw.TrackedWidth(name, NameSize, 1.5f), Draw.TextWidth(distance, NoteSize)) + 12f;
        var anchor = back - direction * 8f;
        var left = direction.X > 0.5f ? anchor.X - width : direction.X < -0.5f ? anchor.X : anchor.X - width / 2f;
        var top = direction.Y > 0.5f ? anchor.Y - TagHeight : direction.Y < -0.5f ? anchor.Y : anchor.Y - TagHeight / 2f;
        Tag(left, top, name, distance, color);
    }

    // Metres rounded to tens, or tenths of a kilometre from one up.
    public static string Distance(float units)
    {
        var metres = units / Metre;
        var key = metres >= 1000f ? 100000 + (int)MathF.Round(metres / 100f) : (int)MathF.Round(metres / 10f) * 10;
        if (!Distances.TryGetValue(key, out var text))
        {
            text = key >= 100000
                ? ((key - 100000) / 10f).ToString("0.0", CultureInfo.InvariantCulture) + " KM"
                : key.ToString(CultureInfo.InvariantCulture) + " M";
            Distances[key] = text;
        }

        return text;
    }
}
