#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// TELEPORT > MOVE, the Batmobile rows. The game's own call drives the car over, so BRING moves it
// with the game's teleport instead, which lets the car settle itself on the ground.
static class Fetch
{
    // Where you stand beside the car. Its right side first, then the left, behind and in front.
    const float BesideCar = 350f;
    static readonly float[] AroundCar = { 90f, -90f, 180f, 0f };

    // Where the car is put round you. Rings going out, each tried from the right of where the
    // camera looks.
    static readonly float[] CarRings = { 700f, 1000f, 1400f };
    static readonly float[] CarTurns = { 90f, -90f, 45f, -45f, 0f, 135f, -135f, 180f };

    // The car's footprint is checked as three boxes along it, each dropped from above onto the
    // ground you're on.
    static readonly Vector3 CarBox = new(150f, 150f, 60f);
    const float CarHalfLength = 170f;
    const float CarHeadroom = 300f;
    const float CarDrop = 1000f;
    const float Uneven = 80f;

    const float AlreadyNear = 1500f;

    public static readonly Command GoToCar = new(
        "teleport.move.to_batmobile", "GO TO THE BATMOBILE",
        "Takes you to the Batmobile wherever it is. Far away it fades like a waypoint teleport.",
        ToCar)
    {
        Block = static () => Car.InCar() ?? Teleport.Away ?? (Teleport.HasCar ? null : "NO CAR"),
        Preview = static signs =>
        {
            if (Beside() is { Refused: null } plan && World.Player is { IsValid: true } player && Car.Find() is { } car)
            {
                var toward = car.Location - plan.At;
                signs.Add(WorldMarks.Stand(player, plan.At, MathF.Atan2(toward.Y, toward.X) * 180f / MathF.PI, "GO TO THE BATMOBILE", "BESIDE IT"));
            }
        },
    };

    public static readonly Command BringCar = new(
        "teleport.move.bring_batmobile", "BRING THE BATMOBILE",
        "Puts the Batmobile beside you straight away, on the ground you're on, facing the way you look.",
        Bring)
    {
        Block = static () => Car.InCar() ?? (Teleport.HasCar ? null : "NO CAR"),
        Preview = static signs =>
        {
            // The car is put there at once, so there's no trail from wherever it is now.
            var plan = Parking();
            signs.Add(plan.Refused is { } why ? WorldMarks.Refused(why, plan.At, "BRING THE BATMOBILE")
                : WorldMarks.Stand(plan.Car!, plan.At, plan.Yaw, "BRING THE BATMOBILE", "PARKS HERE") with { From = null });
        },
    };

    // Where a car row would put you or the car, or why it can't, at the spot it tried.
    readonly record struct Plan(Outcome? Refused, Vector3 At, float Yaw = 0f, RVehicleBatmobileBase? Car = null);

    public static readonly Catalog.Section Section = new("BATMOBILE", GoToCar, BringCar);

    static Outcome ToCar()
    {
        var plan = Beside();
        return plan.Refused ?? Teleport.Start(GoToCar!, plan.At, true);
    }

    // Where GO TO THE BATMOBILE puts you.
    static Plan Beside()
    {
        if (World.Driving is { } driving)
        {
            return new Plan(Outcome.Unchanged("IN THE CAR"), driving.Location);
        }

        if (World.Player is not { IsValid: true } player)
        {
            return new Plan(Outcome.NoPlayer, default);
        }

        if (Car.Find() is not { } car)
        {
            return new Plan(Outcome.Failed("NO CAR"), player.Location);
        }

        // Where the ground isn't loaded yet, the right side is taken on trust.
        var heading = Teleport.Flat(car.Rotation.ToDirection());
        var target = car.Location + Turn(heading, AroundCar[0]) * BesideCar;
        foreach (var turn in AroundCar)
        {
            var at = car.Location + Turn(heading, turn) * BesideCar;
            if (Teleport.Floor(player, at, car.Location.Z + 200f, car.Location.Z - 500f, out var floor) &&
                player.FastTrace(floor + new Vector3(0f, 0f, 100f), car.Location + new Vector3(0f, 0f, 50f), Vector3.Zero, false))
            {
                target = floor;
                break;
            }
        }

        return new Plan(null, target);
    }

    static Outcome Bring()
    {
        var plan = Parking();
        if (plan.Refused is { } why)
        {
            return why;
        }

        var outcome = Teleport.Hop(plan.Car!, plan.At, new Rotator(0f, plan.Yaw, 0f), true);
        return outcome.Kind == OutcomeKind.Done ? Outcome.Done("HERE") : outcome;
    }

    // Where BRING THE BATMOBILE puts the car, facing the way you look.
    static Plan Parking()
    {
        if (World.Driving is { } driving)
        {
            return new Plan(Outcome.Unchanged("IN THE CAR"), driving.Location);
        }

        if (World.Player is not { IsValid: true } player || !Crosshair.View(out _, out var way))
        {
            return new Plan(Outcome.NoPlayer, default);
        }

        var feet = player.Location.Z - Teleport.Lift(player);
        if (Car.Find() is not { } car)
        {
            return new Plan(Outcome.Failed("NO CAR"), player.Location with { Z = feet });
        }

        if (Vector3.Distance(car.Location, player.Location) < AlreadyNear)
        {
            return new Plan(Outcome.Unchanged("ALREADY HERE"), car.Location - Vector3.UnitZ * Teleport.Lift(car));
        }

        var look = Teleport.Flat(way);
        var yaw = MathF.Atan2(look.Y, look.X) * 180f / MathF.PI;
        foreach (var ring in CarRings)
        {
            foreach (var turn in CarTurns)
            {
                var at = (player.Location + Turn(look, turn) * ring) with { Z = feet };
                if (Fits(player, at, look, out var floor) && Teleport.Dry(floor))
                {
                    return new Plan(null, floor, yaw, car);
                }
            }
        }

        return new Plan(Outcome.Failed("NO ROOM"), (player.Location + look * CarRings[0]) with { Z = feet });
    }

    // Level ground with room for the car, in sight of the player so it isn't round a corner.
    static bool Fits(Pawn player, Vector3 at, Vector3 along, out Vector3 floor)
    {
        floor = default;
        if (!player.FastTrace(at + new Vector3(0f, 0f, 100f), player.Location, Vector3.Zero, false))
        {
            return false;
        }

        var lowest = float.MaxValue;
        var highest = float.MinValue;
        for (var i = -1; i <= 1; i++)
        {
            var spot = at + along * (i * CarHalfLength);
            var hit = player.Trace(out var touch, out var normal, spot - new Vector3(0f, 0f, CarDrop), spot + new Vector3(0f, 0f, CarHeadroom),
                Component.ECollisionFilter.COF_TraceWorld, CarBox, out _, 0);
            if (hit is null or Pawn || normal.Z < Teleport.Level)
            {
                return false;
            }

            // The box stops with its middle half its height above the ground.
            var ground = touch.Z - CarBox.Z;
            lowest = MathF.Min(lowest, ground);
            highest = MathF.Max(highest, ground);
            if (i == 0)
            {
                floor = touch with { Z = ground };
            }
        }

        return highest - lowest <= Uneven;
    }

    // Turned round the up axis by degrees, to the right for more.
    static Vector3 Turn(Vector3 flat, float degrees)
    {
        var (sin, cos) = MathF.SinCos(degrees * MathF.PI / 180f);
        return new Vector3(flat.X * cos - flat.Y * sin, flat.X * sin + flat.Y * cos, 0f);
    }
}
