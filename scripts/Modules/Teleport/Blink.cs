#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// TELEPORT > MOVE, the short moves. They stay inside ground that's already loaded, so they happen
// straight away with no fade, and they never go through a wall.
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

    // BLINK FORWARD follows the ground this far up, and drops any distance down to it.
    const float StepUp = 200f;
    const float StepDown = 5000f;

    // The car's eyes, above its middle, for seeing a wall ahead.
    const float CarSight = 100f;

    // How far round you TO THE STREET looks, in metres, and how much lower than your feet it has
    // to be.
    static readonly float[] StreetRings = { 8f, 15f, 25f, 40f, 60f };
    const int StreetSpokes = 12;
    const float StreetDrop = 300f;
    const float SameLevel = 100f;

    // Invisible boxes and people are seen through when aiming.
    const int Layers = 12;

    public static readonly Command ToCrosshair = new(
        "teleport.move.to_crosshair", "BLINK TO CROSSHAIR",
        "Takes you to where the camera is aimed, on foot or in the Batmobile. Aim at a wall to land at its foot, or on top when the top is just above where you aimed.",
        AtCrosshair);

    public static readonly Command Forward = new(
        "teleport.move.forward", "BLINK FORWARD",
        "Moves you ahead the way you face and stops short of any wall. In the air you stay in the air and keep gliding. The Batmobile keeps its speed.",
        Ahead);

    public static readonly Slider Range = new(
        "teleport.move.range", "BLINK RANGE",
        "How far both blinks reach.",
        min: 25, max: 500, step: 25, value: 100, unit: " M")
    {
        Preference = true,
    };

    public static readonly Command SkyDrop = new(
        "teleport.move.sky_drop", "SKY DROP",
        "Lifts you straight up so you can glide away. Not in the Batmobile.",
        Up);

    public static readonly Slider DropHeight = new(
        "teleport.move.sky_drop_height", "SKY DROP HEIGHT",
        "How high SKY DROP lifts you.",
        min: 25, max: 300, step: 25, value: 100, unit: " M")
    {
        Preference = true,
    };

    public static readonly Command ToRoof = new(
        "teleport.move.to_roof", "TO THE ROOF",
        "Puts you on the highest ground where you stand.",
        Roof);

    public static readonly Command ToGround = new(
        "teleport.move.to_ground", "DROP TO GROUND",
        "Puts you on the first ground below you, from a glide or a fall too. In the Batmobile as well.",
        Ground);

    public static readonly Command ToStreet = new(
        "teleport.move.to_street", "TO THE STREET",
        "Puts you on the lowest open ground near you. Never inside a building or in the water.",
        Street);

    public static readonly Catalog.Section[] Sections =
    {
        new("BLINK", ToCrosshair, Forward, Range),
        new("UP AND DOWN", SkyDrop, DropHeight, ToRoof, ToGround, ToStreet),
    };

    static Outcome AtCrosshair()
    {
        if (Teleport.Mover is not { } mover || World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        var aim = view.ToDirection();
        var end = eye + aim * ((float)Range.Value * Metre + Vector3.Distance(eye, mover.Location));
        var facing = new Rotator(0f, view.Yaw, 0f);

        // Nothing in reach, so down to the ground under the far end.
        if (!Aim(mover, eye, end, out var spot, out var normal))
        {
            return Down(mover, end, end.Z, facing);
        }

        if (normal.Z >= Teleport.Level)
        {
            return Land(mover, spot, facing);
        }

        // A wall, or the underside of something.
        var room = Room(mover);
        var outward = new Vector2(normal.X, normal.Y).LengthSquared() > 0.01f ? Teleport.Flat(normal) : -Teleport.Flat(aim);
        var top = spot.Z + LedgeReach;
        var inside = (spot - outward * (room + LedgeInset)) with { Z = top };
        var outside = (spot + outward * room) with { Z = top };
        if (mover.FastTrace(inside, outside, Vector3.Zero, false) && Teleport.Floor(mover, inside, top, spot.Z, out var ledge))
        {
            return Land(mover, ledge, facing);
        }

        return Down(mover, spot + outward * room, spot.Z, facing);
    }

    static Outcome Ahead()
    {
        if (Teleport.Mover is not { } mover)
        {
            return Outcome.NoPlayer;
        }

        var car = mover is RVehicleBatmobileBase;
        var way = Teleport.Flat(mover.Rotation.ToDirection());
        var room = Room(mover);
        var length = (float)Range.Value * Metre;
        var eyes = mover.Location + new Vector3(0f, 0f, car ? CarSight : 0f);
        if (Aim(mover, eyes, eyes + way * (length + room), out var wall, out _))
        {
            length = Vector3.Distance(eyes, wall) - room;
        }

        if (length < Shortest)
        {
            return Outcome.Failed("WALL AHEAD");
        }

        var to = mover.Location + way * length;
        var facing = new Rotator(0f, mover.Rotation.Yaw, 0f);
        if (!car && InAir(mover))
        {
            return Teleport.Hop(mover, to, facing, false);
        }

        return Teleport.Floor(mover, to, mover.Location.Z + StepUp, mover.Location.Z - StepDown, out var floor)
            ? Land(mover, floor, facing)
            : Outcome.Failed("NO GROUND");
    }

    static Outcome Up()
    {
        if (World.Driving is not null)
        {
            return Outcome.Unchanged("NOT IN THE CAR");
        }

        if (World.Player is not { IsValid: true } player)
        {
            return Outcome.NoPlayer;
        }

        var to = player.Location + new Vector3(0f, 0f, (float)DropHeight.Value * Metre);
        if (!player.FastTrace(to, player.Location, Vector3.Zero, false))
        {
            return Outcome.Failed("ROOF ABOVE");
        }

        var outcome = Teleport.Hop(player, to, new Rotator(0f, player.Rotation.Yaw, 0f), false);
        if (outcome.Kind == OutcomeKind.Done)
        {
            player.Velocity = Vector3.Zero;
        }

        return outcome;
    }

    static Outcome Roof()
    {
        if (Teleport.Mover is not { } mover)
        {
            return Outcome.NoPlayer;
        }

        if (!Teleport.Floor(mover, mover.Location, Teleport.Sky, Teleport.Deep, out var floor))
        {
            return Outcome.Failed("NO GROUND");
        }

        return floor.Z <= Feet(mover) + SameLevel
            ? Outcome.Unchanged("ALREADY ON TOP")
            : Land(mover, floor, new Rotator(0f, mover.Rotation.Yaw, 0f));
    }

    static Outcome Ground()
    {
        if (Teleport.Mover is not { } mover)
        {
            return Outcome.NoPlayer;
        }

        if (!Teleport.Floor(mover, mover.Location, mover.Location.Z, Teleport.Deep, out var floor))
        {
            return Outcome.Failed("NO GROUND");
        }

        return floor.Z >= Feet(mover) - SameLevel
            ? Outcome.Unchanged("ALREADY DOWN")
            : Land(mover, floor, new Rotator(0f, mover.Rotation.Yaw, 0f));
    }

    // Each spot round you is looked at from the sky, so what's found there is always out in the
    // open. The lowest wins, and the nearest of those about as low, since the rings go outward.
    static Outcome Street()
    {
        if (Teleport.Mover is not { } mover)
        {
            return Outcome.NoPlayer;
        }

        var here = mover.Location;
        var highest = Feet(mover) - StreetDrop;
        Vector3? best = null;
        foreach (var ring in StreetRings)
        {
            for (var i = 0; i < StreetSpokes; i++)
            {
                var angle = MathF.Tau * i / StreetSpokes;
                var at = here + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * ring * Metre;
                if (!Teleport.Floor(mover, at, Teleport.Sky, Teleport.Deep, out var floor) || floor.Z > highest || !Teleport.Dry(floor))
                {
                    continue;
                }

                if (best is not { } lowest || floor.Z < lowest.Z - SameLevel)
                {
                    best = floor;
                }
            }
        }

        return best is { } street ? Land(mover, street, new Rotator(0f, mover.Rotation.Yaw, 0f)) : Outcome.Unchanged("ALREADY DOWN");
    }

    static Outcome Down(Pawn mover, Vector3 at, float top, Rotator facing) =>
        Teleport.Floor(mover, at, top, Teleport.Deep, out var floor) ? Land(mover, floor, facing) : Outcome.Failed("NO GROUND");

    static Outcome Land(Pawn mover, Vector3 floor, Rotator facing) =>
        Teleport.Dry(floor) ? Teleport.Hop(mover, floor, facing, true) : Outcome.Failed("WATER");

    // The first solid thing between two points, seen through invisible boxes and people.
    public static bool Aim(Actor tracer, Vector3 from, Vector3 to, out Vector3 spot, out Vector3 normal)
    {
        var way = Vector3.Normalize(to - from);
        var start = from;
        for (var i = 0; i < Layers; i++)
        {
            var hit = tracer.Trace(out spot, out normal, to, start, Component.ECollisionFilter.COF_TraceWorld, Vector3.Zero, out _, 0);
            if (hit is null)
            {
                break;
            }

            if (hit is not Volume && hit is not Pawn)
            {
                return true;
            }

            start = spot + way * 5f;
            if (Vector3.Dot(to - start, way) <= 0f)
            {
                break;
            }
        }

        spot = to;
        normal = default;
        return false;
    }

    static float Room(Pawn mover) =>
        mover is RVehicleBatmobileBase ? CarRoom : (mover.CylinderComponent is { IsValid: true } body ? body.CollisionRadius : 35f) + FootRoom;

    static float Feet(Pawn mover) => mover.Location.Z - Teleport.Lift(mover);

    static bool InAir(Pawn mover) => !Teleport.Floor(mover, mover.Location, mover.Location.Z, Feet(mover) - 60f, out _);
}
