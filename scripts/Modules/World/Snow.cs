#nullable enable

using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > WEATHER, the SNOW row. Mr. Freeze's Season of Infamy mission snows on the whole city by
// streaming in a weather level of its own. It holds the snow volume, with a height map of the city
// so it doesn't snow indoors, and the cold look that goes with it. This streams the same level in.
// When the game has no entry for it in the world's list, one of ours is added, and taken out again
// with the engine's own unload and removal when the snow stops. The rain is held off by its own
// owner, and it can't fall while the snow volume is in the world.
static class Snow
{
    const string Level = "BatEntry__City_Ch_F234_LOD2";

    // Our entries are named with this, so a reloaded script can tell them from the game's.
    const string OurName = "KnightfallSnow_";

    // The story can stream levels too, so the entry is checked this often.
    const float CheckEvery = 1f;

    // Only for an entry the game made, which the story may also switch.
    static readonly Held<LevelStreaming, bool> Streamed = new(
        "snow level", level => level.bShouldBeLoaded && level.bShouldBeVisible, (level, on) =>
        {
            level.bShouldBeLoaded = on;
            level.bShouldBeVisible = on;
        });

    static LevelStreaming? _ours;
    static float _checkedAt = -1f;
    static bool _holding;
    static bool _told;

    public static readonly Toggle Snowing = new(
        "world.weather.snow", "SNOW",
        "Snow falls over the whole city with the cold look of Mr. Freeze's Season of Infamy mission. It needs that DLC. The rain stops while it snows.");

    public static readonly Catalog.Section Section = new("SNOW", Snowing);

    public static void Tick()
    {
        if (!Snowing.On)
        {
            if (_holding)
            {
                Release();
            }

            _told = false;
            return;
        }

        if (MathF.Abs(World.Now - _checkedAt) < CheckEvery)
        {
            return;
        }

        _checkedAt = World.Now;
        if (!GameFiles.HasPackage(Level))
        {
            if (!_told)
            {
                _told = true;
                Log.Warn("snow.missing", "SNOW: it needs the Season of Infamy DLC, which isn't installed.");
                Toasts.Show("SNOW", "NEEDS SEASON OF INFAMY", OutcomeKind.Failed, Snowing);
            }

            return;
        }

        // The level is placed for the city, so it only goes into the city's map.
        if (!Game.TryGetWorldInfo(out var world) || !world.IsValid || !world.GetPathName(null).StartsWith("BatEntry.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_ours is { IsValid: true } ours && !ours.bIsRequestingUnloadAndRemoval)
        {
            ours.bShouldBeLoaded = true;
            ours.bShouldBeVisible = true;
            _holding = true;
            return;
        }

        LevelStreaming? games = null;
        foreach (var level in world.StreamingLevels)
        {
            if (level is not { IsValid: true } || !string.Equals(level.PackageName.ToString(), Level, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // One of ours from before a reload is still being taken out. A new one waits for that.
            if ((level.Name.ToString() ?? "").StartsWith(OurName, StringComparison.Ordinal))
            {
                if (level.bIsRequestingUnloadAndRemoval)
                {
                    return;
                }

                _ours = level;
                return;
            }

            games = level;
        }

        if (games is not null)
        {
            Streamed.Hold(games, true);
            _holding = true;
        }
        else if (Add(world) is { } added)
        {
            _ours = added;
            _holding = true;
        }
    }

    public static void Release()
    {
        Streamed.Release();
        if (_ours is { IsValid: true } ours && !ours.bIsRequestingUnloadAndRemoval)
        {
            ours.RequestUnloadAndRemoval();
        }

        _ours = null;
        _holding = false;
        _checkedAt = -1f;
    }

    static LevelStreaming? Add(WorldInfo world)
    {
        var entry = Made.Tracked(new LevelStreamingKismet(world, OurName + Environment.TickCount64));
        if (!entry.IsValid)
        {
            Log.Warn("snow.entry", "SNOW: the snow level couldn't be added to the city's streaming list.");
            return null;
        }

        // The streaming code loads by the text name. The FString is made by the game and the entry
        // keeps it.
        entry.PackageName = new FName(Level);
        entry.PackageNameAsString = new FString(Level);

        // An entry with no name makes the game stop with a missing package error, so it is only
        // listed once both names read back.
        if (!string.Equals(entry.PackageName.ToString(), Level, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.PackageNameAsString.ToString(), Level, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("snow.entry", "SNOW: the snow level's name didn't stick, so it wasn't added.");
            return null;
        }

        entry.bShouldBeLoaded = true;
        entry.bShouldBeVisible = true;
        world.StreamingLevels.Add(entry);
        return entry;
    }
}
