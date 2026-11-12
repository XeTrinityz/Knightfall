#nullable enable

using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > WEATHER, the WIND rows. Capes, loose paper and flags follow the city's wind sources,
// each with two winds of its own. The game's global wind setting is always zero, so the sources
// are what change. Each keeps its own values and gets them back exactly.
static class Wind
{
    // Sources stream in with the city, so they're looked for this often.
    const float LookEvery = 1f;

    enum F
    {
        Strength1, Strength2, Gusts1, Gusts2, Yaw1, Yaw2,
        Count,
    }

    sealed record Entry(WindDirectionalSourceComponent Source, float[] Was, float[] Wrote);

    static readonly Dictionary<nint, Entry> Sources = new();
    static float _lookedAt = -1f;
    static string _applied = "";

    public static readonly Slider Strength = new(
        "world.weather.wind_strength", "WIND STRENGTH",
        "How hard the wind blows your cape, loose paper and flags. CALM stops it.",
        min: 0, max: 5, step: 0.25, value: 1, unit: "X", zero: "CALM");

    public static readonly Slider Direction = new(
        "world.weather.wind_direction", "WIND DIRECTION",
        "Turns the wind round from the way the game has it blowing, in degrees.",
        min: -180, max: 180, step: 15, value: 0, zero: "GAME");

    // Faster than this and the cape's cloth gives up and draws black.
    public static readonly Slider Gusts = new(
        "world.weather.wind_gusts", "GUSTS",
        "How quickly the wind rises and falls. STEADY keeps it even.",
        min: 0, max: 2, step: 0.25, value: 1, unit: "X", zero: "STEADY");

    public static readonly Catalog.Section Section = new("WIND", Strength, Direction, Gusts);

    public static void Tick()
    {
        if (Strength.IsDefault && Direction.IsDefault && Gusts.IsDefault)
        {
            if (Sources.Count > 0)
            {
                Release();
            }

            return;
        }

        var wanted = $"{Strength.ValueText},{Direction.ValueText},{Gusts.ValueText}";
        if (wanted == _applied && MathF.Abs(World.Now - _lookedAt) < LookEvery)
        {
            return;
        }

        _lookedAt = World.Now;
        _applied = wanted;
        foreach (var source in Game.FindObjects<WindDirectionalSourceComponent>())
        {
            if (source.IsValid && !source.IsClassDefaultObject)
            {
                Apply(source);
            }
        }
    }

    public static void Release()
    {
        foreach (var entry in Sources.Values)
        {
            if (entry.Source.IsValid)
            {
                Write(entry.Source, entry.Was);
            }
        }

        Sources.Clear();
        _applied = "";
    }

    static void Apply(WindDirectionalSourceComponent source)
    {
        var now = Read(source);
        if (!Sources.TryGetValue(source.Ptr, out var entry) || !ReferenceEquals(entry.Source, source))
        {
            entry = new Entry(source, now, now);
        }
        else if (!Same(now, entry.Wrote))
        {
            // The game changed this wind itself, so that's the new one to go back to.
            entry = entry with { Was = now };
        }

        var want = (float[])entry.Was.Clone();
        var strength = (float)Strength.Value;
        var gusts = (float)Gusts.Value;
        var turn = (float)Direction.Value;
        want[(int)F.Strength1] = entry.Was[(int)F.Strength1] * strength;
        want[(int)F.Strength2] = entry.Was[(int)F.Strength2] * strength;
        want[(int)F.Gusts1] = entry.Was[(int)F.Gusts1] * gusts;
        want[(int)F.Gusts2] = entry.Was[(int)F.Gusts2] * gusts;
        want[(int)F.Yaw1] = entry.Was[(int)F.Yaw1] + turn;
        want[(int)F.Yaw2] = entry.Was[(int)F.Yaw2] + turn;
        if (!Same(now, want))
        {
            Write(source, want);
        }

        Sources[source.Ptr] = entry with { Wrote = want };
    }

    // BmSDK's Rotator is in degrees.
    static float[] Read(WindDirectionalSourceComponent source)
    {
        var at = new float[(int)F.Count];
        at[(int)F.Strength1] = source.Wind1Strength;
        at[(int)F.Strength2] = source.Wind2Strength;
        at[(int)F.Gusts1] = source.Wind1Frequency;
        at[(int)F.Gusts2] = source.Wind2Frequency;
        at[(int)F.Yaw1] = source.Wind1Direction.Yaw;
        at[(int)F.Yaw2] = source.Wind2Direction.Yaw;
        return at;
    }

    static void Write(WindDirectionalSourceComponent source, float[] at)
    {
        source.Wind1Strength = at[(int)F.Strength1];
        source.Wind2Strength = at[(int)F.Strength2];
        source.Wind1Frequency = at[(int)F.Gusts1];
        source.Wind2Frequency = at[(int)F.Gusts2];
        source.Wind1Direction.Yaw = at[(int)F.Yaw1];
        source.Wind2Direction.Yaw = at[(int)F.Yaw2];
    }

    static bool Same(float[] a, float[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (MathF.Abs(a[i] - b[i]) > 0.001f)
            {
                return false;
            }
        }

        return true;
    }
}
