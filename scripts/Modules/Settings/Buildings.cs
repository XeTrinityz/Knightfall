#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SETTINGS > GAME, BUILDING DETAIL. Far off, the city swaps whole buildings for rough stand-ins,
// each at its own distance. Scaling those distances keeps the real buildings further out, where
// their district is loaded. Districts stream in as you move, so the city is looked over again now
// and then.
static class Buildings
{
    const double Settle = 0.4;
    const double LeastRescan = 5.0;

    // Keeps rescans to about half a percent of the time.
    const double RescanShare = 200.0;

    sealed class Part
    {
        public required PrimitiveComponent Component;
        public float Original;
        public float Current;
    }

    static readonly Dictionary<nint, Part> Parts = new();
    static double _scaleSet = -1;
    static long _nextScan;
    static RGameInfo? _world;

    public static readonly Slider Detail = new(
        "settings.game.building_detail", "BUILDING DETAIL",
        "Keeps full detail buildings further out instead of the rough stand-ins, where the district is loaded. Costs frame rate, and stutters briefly when changed.",
        min: 0, max: 8, step: 0.5, value: 0, unit: "X", zero: "GAME")
    {
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
        var settled = (now - Detail.TouchedAt) / (double)Stopwatch.Frequency >= Settle;
        if ((_scaleSet != Detail.Value && settled) || (_scaleSet == Detail.Value && now >= _nextScan))
        {
            Scan((float)Detail.Value);
            _scaleSet = Detail.Value;
        }
    }

    static void Release()
    {
        Scan(0f);
        Parts.Clear();
        _scaleSet = -1;
    }

    // A known part with a distance we didn't set is new, or the game changed it, so that becomes
    // its own distance to scale from.
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

            if (!Parts.TryGetValue(part.Ptr, out var known) || MathF.Abs(distance - known.Current) > 0.5f)
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

        _nextScan = Stopwatch.GetTimestamp() + (long)(Math.Max(LeastRescan, watch.Elapsed.TotalSeconds * RescanShare) * Stopwatch.Frequency);
    }
}
