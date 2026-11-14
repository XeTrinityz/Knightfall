#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// TELEPORT > MOVE, the short moves. They stay inside ground that's already loaded, so they happen
// straight away with no fade, and they never go through a wall. Each move is planned first, and
// the same plan is what WORLD MARKERS shows while its row is highlighted.
static class Blink
{
    const float Metre = Teleport.UnitsPerMetre;

    // A wall whose top is this little above where you aimed is a ledge to stand on.
    const float LedgeReach = 250f;
    const float LedgeInset = 80f;

    // Room kept from a wall, past the player's own width, and for the car.
    const float FootRoom = 25f;
    const float CarRoom = 300f;

    // Nearer than this isn't worth a blink.
    const float Shortest = 100f;

    // BLINK TO CROSSHAIR goes to whatever you aim at, as far off as the world is loaded. Aimed at
    // the sky, the cross that says so stands this far out.
    const float Reach = 200000f;
    const float Nothing = 3000f;

    // BLINK FORWARD follows the ground this far up, and drops any distance down to it.
    const float StepUp = 200f;
    const float StepDown = 5000f;

    // The car's eyes, above its middle, for seeing a wall ahead.
    const float CarSight = 100f;

    // How far round you TO THE STREET looks, in metres, and how much lower than your feet it has
    // to be.
    static readonly float[] StreetRings = { 8f, 15f, 25f, 40f, 60f };
    const float StreetDrop = 300f;
    const float SameLevel = 100f;

    public static readonly Command ToCrosshair = new(
        "teleport.move.to_crosshair", "BLINK TO CROSSHAIR",
        "Takes you to where the camera is aimed, on foot or in the Batmobile. Aim at a wall to land at its foot, or on top when the top is just above where you aimed. You can land on cars and props too.",
        static () => Go(AtCrosshair()))
    {
        Preview = Shows(AtCrosshair, "BLINK"),
    };

    public static readonly Command Forward = new(
        "teleport.move.forward", "BLINK FORWARD",
        "Moves you ahead the way you face, BLINK DISTANCE away, and stops short of any wall. In the air you stay in the air and keep gliding. The Batmobile keeps its speed.",
        static () => Go(Ahead()))
    {
        Preview = Shows(Ahead, "BLINK FORWARD"),
    };

    public static readonly Slider Range = new(
        "teleport.move.range", "BLINK DISTANCE",
        "How far BLINK FORWARD takes you.",
        min: 5, max: 500, step: 5, value: 100, unit: " M")
    {
        Preference = true,
        Preview = static signs => Forward.Preview!(signs),
    };

    public static readonly Command SkyDrop = new(
        "teleport.move.sky_drop", "SKY DROP",
        "Lifts you straight up so you can glide away. Not in the Batmobile.",
        static () =>
        {
            var outcome = Go(Up());
            if (outcome.Kind == OutcomeKind.Done && World.Player is { IsValid: true } player)
            {
                player.Velocity = Vector3.Zero;
            }

            return outcome;
        })
    {
        Block = Car.InCar,
        Preview = Shows(Up, "SKY DROP"),
    };

    public static readonly Slider DropHeight = new(
        "teleport.move.sky_drop_height", "SKY DROP HEIGHT",
        "How high SKY DROP lifts you.",
        min: 25, max: 300, step: 25, value: 100, unit: " M")
    {
        Preference = true,
        Preview = static signs => SkyDrop.Preview!(signs),
    };

    public static readonly Command ToRoof = new(
        "teleport.move.to_roof", "TO THE ROOF",
        "Puts you on the highest ground where you stand.",
        static () => Go(Roof()))
    {
        Preview = Shows(Roof, "TO THE ROOF"),
    };

    public static readonly Command ToGround = new(
        "teleport.move.to_ground", "DROP TO GROUND",
        "Puts you on the first ground below you, from a glide or a fall too. In the Batmobile as well.",
        static () => Go(Ground()))
    {
        Preview = Shows(Ground, "DROP TO GROUND"),
    };

    public static readonly Command ToStreet = new(
        "teleport.move.to_street", "TO THE STREET",
        "Puts you on the lowest open ground near you. Never inside a building or in the water.",
        static () => Go(Street()))
    {
        Preview = Shows(Street, "TO THE STREET"),
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("BLINK", ToCrosshair, Forward, Range),
        new("UP AND DOWN", SkyDrop, DropHeight, ToRoof, ToGround, ToStreet),
    };

    // Where a move would take you: the floor, or a point in the air when OnFloor is false. Note
    // says what kind of spot it is. A refused plan has the reason and the spot it tried.
    readonly record struct Trip(Pawn? Mover, Vector3 To, float Yaw, bool OnFloor, string Note, Outcome? Refused = null, bool KeepMotion = false);

    static Trip No(Outcome why, Vector3 at) => new(null, at, 0f, false, "", why);

    static Outcome Go(Trip trip) =>
        trip.Refused ?? Teleport.Hop(trip.Mover!, trip.To, new Rotator(0f, trip.Yaw, 0f), trip.OnFloor, trip.KeepMotion);

    static Action<List<WorldMarks.Sign>> Shows(Func<Trip> plan, string title) => signs =>
    {
        var trip = plan();
        if (trip.Refused is { } why)
        {
            signs.Add(WorldMarks.Refused(why, trip.To, title));
        }
        else if (trip.OnFloor)
        {
            signs.Add(WorldMarks.Stand(trip.Mover!, trip.To, trip.Yaw, title, trip.Note));
        }
        else
        {
            var drop = Teleport.Floor(trip.Mover!, trip.To, trip.To.Z, Teleport.Deep, out var under) ? trip.To.Z - under.Z : 0f;
            signs.Add(WorldMarks.Air(trip.Mover!, trip.To, drop, title, trip.Note));
        }
    };

    static Trip AtCrosshair()
    {
        if (Teleport.Mover is not { } mover || !Crosshair.View(out var eye, out var aim))
        {
            return No(Outcome.NoPlayer, default);
        }

        var yaw = MathF.Atan2(aim.Y, aim.X) * 180f / MathF.PI;
        if (!Crosshair.Line(mover, eye, eye + aim * Reach, Crosshair.Meets.Solid, out var hit))
        {
            return No(Outcome.Failed("AIM AT SOMETHING"), eye + aim * Nothing);
        }

        var spot = hit.Spot;
        var normal = hit.Normal;
        if (hit.Floor)
        {
            return Land(mover, spot, yaw, "WHERE YOU AIM");
        }

        // A wall, or the underside of something.
        var room = Room(mover);
        var outward = new Vector2(normal.X, normal.Y).LengthSquared() > 0.01f ? Teleport.Flat(normal) : -Teleport.Flat(aim);
        var top = spot.Z + LedgeReach;
        var inside = (spot - outward * (room + LedgeInset)) with { Z = top };
        var outside = (spot + outward * room) with { Z = top };
        if (mover.FastTrace(inside, outside, Vector3.Zero, false) && Teleport.Floor(mover, inside, top, spot.Z, out var ledge))
        {
            return Land(mover, ledge, yaw, "ON TOP");
        }

        return Down(mover, spot + outward * room, spot.Z, yaw, "AT ITS FOOT");
    }

    static Trip Ahead()
    {
        if (Teleport.Mover is not { } mover)
        {
            return No(Outcome.NoPlayer, default);
        }

        var car = mover is RVehicleBatmobileBase;
        var way = Teleport.Flat(mover.Rotation.ToDirection());
        var room = Room(mover);
        var length = (float)Range.Value * Metre;
        var eyes = mover.Location + new Vector3(0f, 0f, car ? CarSight : 0f);
        if (Crosshair.Line(mover, eyes, eyes + way * (length + room), Crosshair.Meets.Level, out var wall))
        {
            length = wall.Distance - room;
            if (length < Shortest)
            {
                return No(Outcome.Failed("WALL AHEAD"), wall.Spot);
            }
        }

        var to = mover.Location + way * length;
        var yaw = mover.Rotation.Yaw;
        if (!car && InAir(mover))
        {
            return new Trip(mover, to, yaw, false, "KEEP GLIDING", KeepMotion: true);
        }

        return Teleport.Floor(mover, to, mover.Location.Z + StepUp, mover.Location.Z - StepDown, out var floor)
            ? Land(mover, floor, yaw, "AHEAD")
            : No(Outcome.Failed("NO GROUND"), to);
    }

    static Trip Up()
    {
        if (World.Driving is { } driving)
        {
            return No(Outcome.Unchanged("NOT IN THE CAR"), driving.Location);
        }

        if (World.Player is not { IsValid: true } player)
        {
            return No(Outcome.NoPlayer, default);
        }

        var to = player.Location + new Vector3(0f, 0f, (float)DropHeight.Value * Metre);
        if (!player.FastTrace(to, player.Location, Vector3.Zero, false))
        {
            return No(Outcome.Failed("ROOF ABOVE"), Feet(player));
        }

        return new Trip(player, to, player.Rotation.Yaw, false, "GLIDE FROM HERE");
    }

    static Trip Roof()
    {
        if (Teleport.Mover is not { } mover)
        {
            return No(Outcome.NoPlayer, default);
        }

        if (!Teleport.Floor(mover, mover.Location, Teleport.Sky, Teleport.Deep, out var floor))
        {
            return No(Outcome.Failed("NO GROUND"), Feet(mover));
        }

        return floor.Z <= Feet(mover).Z + SameLevel
            ? No(Outcome.Unchanged("ALREADY ON TOP"), Feet(mover))
            : Land(mover, floor, mover.Rotation.Yaw, "HIGHEST GROUND");
    }

    static Trip Ground()
    {
        if (Teleport.Mover is not { } mover)
        {
            return No(Outcome.NoPlayer, default);
        }

        if (!Teleport.Floor(mover, mover.Location, mover.Location.Z, Teleport.Deep, out var floor))
        {
            return No(Outcome.Failed("NO GROUND"), Feet(mover));
        }

        return floor.Z >= Feet(mover).Z - SameLevel
            ? No(Outcome.Unchanged("ALREADY DOWN"), Feet(mover))
            : Land(mover, floor, mover.Rotation.Yaw, "FIRST GROUND BELOW");
    }

    // The lowest open ground round you wins, and the nearest of those about as low, since the
    // rings go outward.
    static Trip Street()
    {
        if (Teleport.Mover is not { } mover)
        {
            return No(Outcome.NoPlayer, default);
        }

        var highest = Feet(mover).Z - StreetDrop;
        Vector3? best = null;
        foreach (var floor in Teleport.FloorsAround(mover, mover.Location, StreetRings, Teleport.Spokes, spot => spot.Z <= highest && Teleport.Dry(spot)))
        {
            if (best is not { } lowest || floor.Z < lowest.Z - SameLevel)
            {
                best = floor;
            }
        }

        return best is { } street
            ? Land(mover, street, mover.Rotation.Yaw, "LOWEST OPEN GROUND")
            : No(Outcome.Unchanged("ALREADY DOWN"), Feet(mover));
    }

    static Trip Down(Pawn mover, Vector3 at, float top, float yaw, string note) =>
        Teleport.Floor(mover, at, top, Teleport.Deep, out var floor) ? Land(mover, floor, yaw, note) : No(Outcome.Failed("NO GROUND"), at);

    static Trip Land(Pawn mover, Vector3 floor, float yaw, string note) =>
        Teleport.Dry(floor) ? new Trip(mover, floor, yaw, true, note) : No(Outcome.Failed("WATER"), floor);

    static float Room(Pawn mover) =>
        mover is RVehicleBatmobileBase ? CarRoom : (mover.CylinderComponent is { IsValid: true } body ? body.CollisionRadius : 35f) + FootRoom;

    static Vector3 Feet(Pawn mover) => mover.Location - Vector3.UnitZ * Teleport.Lift(mover);

    static bool InAir(Pawn mover) => !Teleport.Floor(mover, mover.Location, mover.Location.Z, Feet(mover).Z - 60f, out _);
}
