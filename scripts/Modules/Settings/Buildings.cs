#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SETTINGS > GAME, BUILDING DETAIL. Far off, the city swaps whole buildings for rough stand-ins,
// each at its own distance. Scaling those distances keeps the real buildings further out, where
// their district is loaded. Districts stream in as you move, so the city is looked over again
// when the levels on show change, and now and then besides.
static class Buildings
{
    const double Settle = 0.4;
    const double LeastRescan = 5.0;

    // Keeps rescans to about half a percent of the time.
    const double RescanShare = 200.0;

    // How long the city can go without a look while no level comes or goes.
    const double MostRescan = 60.0;

    // How often the levels on show are counted while waiting.
    const double LevelsLook = 1.0;

    sealed class Part
    {
        public required PrimitiveComponent Component;
        public float Original;
        public float Current;
    }

    static readonly Dictionary<nint, Part> Parts = new();
    static double _scaleSet = -1;
    static long _nextScan;
    static long _lastScan;
    static long _levels;
    static bool _again;
    static RGameInfo? _world;

    public static readonly Slider Detail = new(
        "settings.game.building_detail", "BUILDING DETAIL",
        "Keeps full detail buildings further out instead of the rough stand-ins, where the district is loaded. Costs frame rate, and stutters briefly when changed and as new districts load in.",
        min: 0, max: 8, step: 0.5, value: 0, unit: "X", zero: "GAME")
    {
        Preference = true,
        Tick = Hold,
        Restore = Release,
    };

    static void Hold()
    {
        // A new world has new buildings, and none of the old ones to put back.
        if (!ReferenceEquals(_world, World.Info))
        {
            _world = World.Info;
            Parts.Clear();
            _scaleSet = -1;
        }

        var now = Stopwatch.GetTimestamp();
        if (_scaleSet != Detail.Value)
        {
            if ((now - Detail.TouchedAt) / (double)Stopwatch.Frequency >= Settle)
            {
                Scan((float)Detail.Value);
                _scaleSet = Detail.Value;
                _levels = Levels();
                _again = false;
            }

            return;
        }

        if (now < _nextScan)
        {
            return;
        }

        // New buildings only come with a level. One that has just come on show may still be
        // putting its parts in, so the city gets one more look after each change.
        var levels = Levels();
        var changed = levels != _levels;
        if (changed || _again || (now - _lastScan) / (double)Stopwatch.Frequency >= MostRescan)
        {
            _levels = levels;
            _again = changed;
            Scan((float)Detail.Value);
        }
        else
        {
            _nextScan = now + (long)(LevelsLook * Stopwatch.Frequency);
        }
    }

    static void Release()
    {
        Scan(0f);
        Parts.Clear();
        _scaleSet = -1;
        _again = false;
    }

    // A number that changes when a streamed level comes on show or goes.
    static long Levels()
    {
        if (!Game.TryGetWorldInfo(out var world) || !world.IsValid)
        {
            return 0;
        }

        long mark = 0;
        foreach (var level in world.StreamingLevels)
        {
            if (level is { IsValid: true, bIsVisible: true })
            {
                mark += level.Ptr;
            }
        }

        return mark;
    }

    // A known part with a distance we didn't set is new, or the game changed it, so that becomes
    // its own distance to scale from. BmSDK keeps one wrapper per live object, so another
    // wrapper at a known address is a new part in the place of one that is gone.
    static void Scan(float scale)
    {
        var watch = Stopwatch.StartNew();
        foreach (var part in Game.FindObjects<PrimitiveComponent>())
        {
            if (!part.IsValid || part.IsClassDefaultObject)
            {
                continue;
            }

            // A part of an actor that's being deleted can't be touched safely.
            if (part.Owner is { } owner && (!owner.IsValid || owner.bDeleteMe || owner.bPendingDelete))
            {
                continue;
            }

            var distance = part.MassiveLODDistance;
            if (distance <= 0f)
            {
                continue;
            }

            if (!Parts.TryGetValue(part.Ptr, out var known) || !ReferenceEquals(known.Component, part) || MathF.Abs(distance - known.Current) > 0.5f)
            {
                known = new Part { Component = part, Original = distance, Current = distance };
                Parts[part.Ptr] = known;
            }

            var wanted = scale > 0f ? known.Original * scale : known.Original;
            if (MathF.Abs(distance - wanted) <= 0.5f)
            {
                continue;
            }

            part.MassiveLODDistance = wanted;
            known.Current = wanted;
            if (part.bAttached)
            {
                part.ForceUpdate(false);
            }
        }

        _lastScan = Stopwatch.GetTimestamp();
        _nextScan = _lastScan + (long)(Math.Max(LeastRescan, watch.Elapsed.TotalSeconds * RescanShare) * Stopwatch.Frequency);
    }
}
