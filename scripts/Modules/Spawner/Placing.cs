#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// SPAWNER, the PLACE rows every spawning tab shares, and the spots they work out. Each spot is
// open dry ground you can see from where you stand, so nothing lands inside a wall or the bay.
static class Placing
{
    const float Metre = Teleport.UnitsPerMetre;

    // How far the crosshair looks for somewhere to put things.
    const float Reach = 20000f;

    // A spot may be this far above or below the middle of the group.
    const float StepUp = 300f;
    const float StepDown = 1000f;

    const float Eyes = 60f;

    // A spot that doesn't work is tried again nudged this far, round the compass.
    const float Nudge = 150f;
    const int NudgeTries = 6;

    public static readonly Choice Where = new(
        "spawner.place.where", "WHERE",
        "IN FRONT puts spawns on the ground ahead of you, DISTANCE away. CROSSHAIR puts them where you're aiming.",
        new[] { ("front", "IN FRONT"), ("crosshair", "CROSSHAIR") })
    {
        Preference = true,
    };

    public static readonly Slider Distance = new(
        "spawner.place.distance", "DISTANCE",
        "How far in front of you spawns appear when WHERE is IN FRONT.",
        min: 2, max: 40, step: 1, value: 6, unit: " M")
    {
        Preference = true,
    };

    public static readonly Slider Count = new(
        "spawner.place.count", "HOW MANY",
        "How many each SPAWN makes. They stand in a block, SPACING apart.",
        min: 1, max: 12, step: 1, value: 1)
    {
        Preference = true,
    };

    public static readonly Slider Spacing = new(
        "spawner.place.spacing", "SPACING",
        "The gap between them when HOW MANY is more than one.",
        min: 1, max: 15, step: 0.5, value: 2, unit: " M")
    {
        Preference = true,
    };

    public static readonly Choice Facing = new(
        "spawner.place.facing", "FACING",
        "Which way spawns face: toward you, the way you're facing, or any way.",
        new[] { ("you", "TOWARD YOU"), ("same", "YOUR WAY"), ("random", "RANDOM") })
    {
        Preference = true,
    };

    public static readonly Catalog.Section Section = new("PLACE", Where, Distance, Count, Spacing, Facing);

    public readonly record struct Spot(Vector3 Floor, Rotator Facing);

    // Up to count spots, fewer when there isn't room. Empty when the middle can't be found. Room
    // keeps big spawns at least that far from you, from walls you aim at and from each other.
    public static List<Spot> Spots(Pawn mover, int count, float room = 0f)
    {
        var spots = new List<Spot>();
        if (World.Controller is not { } controller)
        {
            return spots;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);

        // CROSSHAIR goes where the camera looks, IN FRONT the way you face.
        Vector3 ahead;
        Vector3 middle;
        if (Where.Index == 1)
        {
            ahead = Teleport.Flat(view.ToDirection());
            Blink.Aim(mover, eye, eye + view.ToDirection() * Reach, out var aimed, out _);
            middle = aimed - ahead * MathF.Max(Nudge, room / 2f);
        }
        else
        {
            ahead = Teleport.Flat(mover.Rotation.ToDirection());
            middle = mover.Location + ahead * MathF.Max((float)Distance.Value * Metre, room);
        }

        var side = new Vector3(-ahead.Y, ahead.X, 0f);
        var columns = (int)MathF.Ceiling(MathF.Sqrt(count));
        var gap = MathF.Max((float)Spacing.Value * Metre, room);
        var sight = mover.Location + new Vector3(0f, 0f, Eyes);
        for (var i = 0; i < count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            var at = middle + side * (column - (columns - 1) / 2f) * gap + ahead * row * gap;
            if (Find(mover, at, middle.Z, sight) is { } floor)
            {
                spots.Add(new Spot(floor, Face(floor, mover)));
            }
        }

        return spots;
    }

    static Vector3? Find(Pawn mover, Vector3 at, float height, Vector3 sight)
    {
        for (var attempt = 0; attempt <= NudgeTries; attempt++)
        {
            var angle = MathF.Tau * attempt / NudgeTries;
            var spot = attempt == 0 ? at : at + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * Nudge;
            if (Teleport.Floor(mover, spot, height + StepUp, height - StepDown, out var floor) && Teleport.Dry(floor)
                && mover.FastTrace(floor + new Vector3(0f, 0f, Eyes), sight, Vector3.Zero, false))
            {
                return floor;
            }
        }

        return null;
    }

    // BmSDK's Rotator is in degrees.
    static Rotator Face(Vector3 floor, Pawn mover)
    {
        var yaw = Facing.Index switch
        {
            0 => MathF.Atan2(mover.Location.Y - floor.Y, mover.Location.X - floor.X) * 180f / MathF.PI,
            1 => mover.Rotation.Yaw,
            _ => Random.Shared.NextSingle() * 360f,
        };
        return new Rotator(0f, yaw, 0f);
    }
}
