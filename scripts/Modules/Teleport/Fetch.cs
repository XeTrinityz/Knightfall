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
        Block = static () => World.Driving is not null ? "IN THE CAR" : Teleport.Away ?? (Teleport.HasCar ? null : "NO CAR"),
    };

    public static readonly Command BringCar = new(
        "teleport.move.bring_batmobile", "BRING THE BATMOBILE",
        "Puts the Batmobile beside you straight away, on the ground you're on, facing the way you look.",
        Bring)
    {
        Block = static () => World.Driving is not null ? "IN THE CAR" : Teleport.HasCar ? null : "NO CAR",
    };

    public static readonly Catalog.Section Section = new("BATMOBILE", GoToCar, BringCar);

    static Outcome ToCar()
    {
        if (World.Driving is not null)
        {
            return Outcome.Unchanged("IN THE CAR");
        }

        if (World.Player is not { IsValid: true } player)
        {
            return Outcome.NoPlayer;
        }

        if (Car.Find() is not { } car)
        {
            return Outcome.Failed("NO CAR");
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

        return Teleport.Start(GoToCar!, target, true);
    }

    static Outcome Bring()
    {
        if (World.Driving is not null)
        {
            return Outcome.Unchanged("IN THE CAR");
        }

        if (World.Player is not { IsValid: true } player || World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        if (Car.Find() is not { } car)
        {
            return Outcome.Failed("NO CAR");
        }

        if (Vector3.Distance(car.Location, player.Location) < AlreadyNear)
        {
            return Outcome.Unchanged("ALREADY HERE");
        }

        controller.GetPlayerViewPoint(out _, out var view);
        var look = Teleport.Flat(view.ToDirection());
        var feet = player.Location.Z - Teleport.Lift(player);
        foreach (var ring in CarRings)
        {
            foreach (var turn in CarTurns)
            {
                var at = (player.Location + Turn(look, turn) * ring) with { Z = feet };
                if (Fits(player, at, look, out var floor) && Teleport.Dry(floor))
                {
                    var outcome = Teleport.Hop(car, floor, new Rotator(0f, view.Yaw, 0f), true);
                    return outcome.Kind == OutcomeKind.Done ? Outcome.Done("HERE") : outcome;
                }
            }
        }

        return Outcome.Failed("NO ROOM");
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
