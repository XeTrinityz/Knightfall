#nullable enable

using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > WEATHER, the SNOW rows. Mr. Freeze's Season of Infamy mission snows on the whole city by
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

    const string NeedsPack = "NEEDS SEASON OF INFAMY";

    // The story can stream levels too, so the entry is checked this often. The snow volume only
    // turns up once its level has streamed in, so it is looked for on the same beat.
    const float CheckEvery = 1f;

    // Only for an entry the game made, which the story may also switch.
    static readonly Held<LevelStreaming, bool> Streamed = new(
        "snow level", level => level.bShouldBeLoaded && level.bShouldBeVisible, (level, on) =>
        {
            level.bShouldBeLoaded = on;
            level.bShouldBeVisible = on;
        });

    // A new amount only shows once the volume's parts are attached again, like the rain's.
    static readonly Held<SnowVolume, float> Density = new(
        "snow amount", volume => volume.SnowComponent.SnowSettings.Density, (volume, density) =>
        {
            volume.SnowComponent.SnowSettings.Density = density;
            volume.SetSnowSettings();
        });

    static LevelStreaming? _ours;
    static float _checkedAt = -1f;
    static float _amountAt = -1f;
    static double _amount = 1;
    static bool _holding;
    static bool _told;
    static bool? _installed;

    public static readonly Toggle Snowing = new(
        "world.weather.snow", "SNOW",
        "Snow falls over the whole city with the cold look of Mr. Freeze's Season of Infamy mission. It needs that DLC. The rain stops while it snows.")
    {
        Block = static () => Installed ? null : NeedsPack,
    };

    public static readonly Slider Amount = new(
        "world.weather.snow_amount", "SNOW AMOUNT",
        "How thickly the snow falls while SNOW is on.",
        min: 0.25, max: 4, step: 0.25, value: 1, unit: "X")
    {
        Block = static () => !Installed ? NeedsPack : Snowing.On ? null : "SNOW IS OFF",
    };

    public static readonly Catalog.Section Section = new("SNOW", Snowing, Amount);

    // The rows ask every frame they are on screen, so the files are only looked for once.
    static bool Installed => _installed ??= GameFiles.HasPackage(Level);

    // Whether SNOW can do anything here. The level is placed for the city, so it only goes into
    // the city's map. Without it the rain is left alone.
    public static bool Falling => Snowing.On && Installed && World.InCity;

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

        if (!Installed)
        {
            if (!_told)
            {
                _told = true;
                Log.Warn("snow.missing", "SNOW: it needs the Season of Infamy DLC, which isn't installed.");
            }

            return;
        }

        _holding = true;
        if (MathF.Abs(World.Now - _checkedAt) >= CheckEvery)
        {
            _checkedAt = World.Now;
            Stream();
        }

        Thicken();
    }

    public static void Release()
    {
        Density.Release();
        Streamed.Release();
        if (_ours is { IsValid: true } ours && !ours.bIsRequestingUnloadAndRemoval)
        {
            ours.RequestUnloadAndRemoval();
        }

        _ours = null;
        _holding = false;
        _checkedAt = -1f;
        _amountAt = -1f;
        _amount = 1;
    }

    // Keeps the snow level loaded and shown.
    static void Stream()
    {
        if (!World.InCity || !Game.TryGetWorldInfo(out var world) || !world.IsValid)
        {
            return;
        }

        if (_ours is { IsValid: true } ours && !ours.bIsRequestingUnloadAndRemoval)
        {
            ours.bShouldBeLoaded = true;
            ours.bShouldBeVisible = true;
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
        }
        else if (Add(world) is { } added)
        {
            _ours = added;
        }
    }

    // SNOW AMOUNT, on every snow volume the world has. Straight away when the row changes, and on
    // a beat for a volume that has just streamed in.
    static void Thicken()
    {
        var amount = Amount.Value;
        if (amount == _amount && MathF.Abs(World.Now - _amountAt) < CheckEvery)
        {
            return;
        }

        _amount = amount;
        _amountAt = World.Now;
        if (Amount.IsDefault)
        {
            Density.Release();
            return;
        }

        foreach (var volume in Game.FindObjects<SnowVolume>())
        {
            if (volume.IsValid && !volume.bDeleteMe && volume.SnowComponent is { IsValid: true })
            {
                Density.Hold(volume, was => was * (float)Amount.Value);
            }
        }
    }

    static LevelStreaming? Add(WorldInfo world)
    {
        var entry = new LevelStreamingKismet(world, OurName + Environment.TickCount64);
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
        var levels = world.StreamingLevels;
        levels.Add(entry);

        // The list hands back the wrapper the SDK keeps track of, which is the one to hold on to.
        return levels.Count > 0 && levels[levels.Count - 1] is { IsValid: true } listed && listed.Ptr == entry.Ptr ? listed : entry;
    }
}
