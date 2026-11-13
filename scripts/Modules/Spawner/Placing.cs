#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// SPAWNER, the PLACE rows every spawning tab shares, and the spots they work out. Each spot is
// open dry ground that can be seen from where you look, so nothing lands inside a wall or the bay.
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

    // How many floors down a spot looks for one that can be seen.
    const int Floors = 3;

    const int InFront = 0;
    const int Crosshair = 1;

    public static readonly Choice Where = new(
        "spawner.place.where", "WHERE",
        "IN FRONT puts spawns on the ground ahead of you, DISTANCE away. CROSSHAIR puts them where you're aiming. AROUND YOU puts them in a ring round you, DISTANCE out.",
        new[] { ("front", "IN FRONT"), ("crosshair", "CROSSHAIR"), ("around", "AROUND YOU") })
    {
        Preference = true,
    };

    public static readonly Slider Distance = new(
        "spawner.place.distance", "DISTANCE",
        "How far from you spawns appear when WHERE is IN FRONT or AROUND YOU.",
        min: 2, max: 40, step: 1, value: 6, unit: " M")
    {
        Preference = true,
        Block = static () => Where.Index == Crosshair ? "WHERE IS CROSSHAIR" : null,
    };

    public static readonly Slider Count = new(
        "spawner.place.count", "HOW MANY",
        "How many each SPAWN makes. They stand in a block, or a ring with AROUND YOU, SPACING apart.",
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

    // Works out where the next SPAWN goes. Gives back why not when nothing can be spawned now,
    // and null with at least one spot when it can. There are HOW MANY spots, fewer when MOST AT
    // ONCE or the ground leaves less room. Room keeps big spawns at least that far from you,
    // from walls you aim at and from each other.
    public static Outcome? Plan(float room, out Pawn mover, out List<Spot> spots)
    {
        spots = new List<Spot>();
        mover = null!;
        if (Teleport.Mover is not { } pawn || World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        mover = pawn;
        if (controller.bCinematicMode)
        {
            return Outcome.Failed("NOT IN A SCENE");
        }

        var left = Spawned.Room;
        if (left == 0)
        {
            return Spawned.Full;
        }

        var count = Math.Min((int)Count.Value, left);
        var gap = MathF.Max((float)Spacing.Value * Metre, room);
        controller.GetPlayerViewPoint(out var eye, out var view);
        switch (Where.Index)
        {
            case Crosshair:
                // Where the camera looks, and each spot has to be in its sight.
                var looking = Teleport.Flat(view.ToDirection());
                if (!Blink.Aim(pawn, eye, eye + view.ToDirection() * Reach, out var aimed, out _))
                {
                    return Outcome.Failed("AIM AT THE GROUND");
                }

                Grid(pawn, spots, aimed - looking * MathF.Max(Nudge, room / 2f), looking, count, gap, eye);
                break;

            case InFront:
                var ahead = Teleport.Flat(pawn.Rotation.ToDirection());
                Grid(pawn, spots, pawn.Location + ahead * MathF.Max((float)Distance.Value * Metre, room), ahead, count, gap, pawn.Location + new Vector3(0f, 0f, Eyes));
                break;

            default:
                Ring(pawn, spots, MathF.Max((float)Distance.Value * Metre, room), count, gap);
                break;
        }

        return spots.Count > 0 ? null : Outcome.Failed("NO ROOM THERE");
    }

    // Rows across and back from the middle, the first row nearest you.
    static void Grid(Pawn mover, List<Spot> spots, Vector3 middle, Vector3 ahead, int count, float gap, Vector3 sight)
    {
        var side = new Vector3(-ahead.Y, ahead.X, 0f);
        var columns = (int)MathF.Ceiling(MathF.Sqrt(count));
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
    }

    // Evenly round you, the first one straight ahead. The ring grows until they fit the gap apart.
    static void Ring(Pawn mover, List<Spot> spots, float radius, int count, float gap)
    {
        var here = mover.Location;
        var sight = here + new Vector3(0f, 0f, Eyes);
        var first = mover.Rotation.Yaw * MathF.PI / 180f;
        radius = MathF.Max(radius, count * gap / MathF.Tau);
        for (var i = 0; i < count; i++)
        {
            var angle = first + MathF.Tau * i / count;
            var at = here + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * radius;
            if (Find(mover, at, here.Z, sight) is { } floor)
            {
                spots.Add(new Spot(floor, Face(floor, mover)));
            }
        }
    }

    static Vector3? Find(Pawn mover, Vector3 at, float height, Vector3 sight)
    {
        for (var attempt = 0; attempt <= NudgeTries; attempt++)
        {
            var angle = MathF.Tau * attempt / NudgeTries;
            var spot = attempt == 0 ? at : at + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * Nudge;

            // Under a bridge or an awning the first floor down is its top, which can't be seen,
            // so the floors below it get a look too.
            var top = height + StepUp;
            for (var i = 0; i < Floors && Teleport.Floor(mover, spot, top, height - StepDown, out var floor); i++)
            {
                if (Teleport.Dry(floor) && mover.FastTrace(floor + new Vector3(0f, 0f, Eyes), sight, Vector3.Zero, false))
                {
                    return floor;
                }

                top = floor.Z - 5f;
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
