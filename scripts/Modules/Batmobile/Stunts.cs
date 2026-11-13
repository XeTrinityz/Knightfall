#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using EjectType = BmSDK.BmGame.RVehicleBatmobileBase.EBatmobileEjectType;

namespace Knightfall;

// BATMOBILE > TUNING, the STUNTS rows. The car's physics is native, so jumps and floating act on
// its body directly, and air control and the eject only change the values the game reads.
static class Stunts
{
    // Chosen by feel.
    const float JumpSpeed = 1200f;

    // Just off a kerb or a bump still counts as on the ground.
    const float AirborneGrace = 0.3f;

    static readonly Held<RVehicleBatmobileBase, EjectType> Eject = new("super eject", car => car.MaxEjectType, (car, type) => car.MaxEjectType = type);
    static readonly Held<RVehicleSimBatmobile, float> YawMost = new("air yaw", sim => sim.AirControlYawTorqueFactorMax, (sim, value) => sim.AirControlYawTorqueFactorMax = value);
    static readonly Held<RVehicleSimBatmobile, float> YawLeast = new("air yaw", sim => sim.AirControlYawTorqueFactorMin, (sim, value) => sim.AirControlYawTorqueFactorMin = value);
    static readonly Held<RVehicleSimBatmobile, float> PitchMost = new("air pitch", sim => sim.AirControlPitchTorqueFactorMax, (sim, value) => sim.AirControlPitchTorqueFactorMax = value);
    static readonly Held<RVehicleSimBatmobile, float> PitchLeast = new("air pitch", sim => sim.AirControlPitchTorqueFactorMin, (sim, value) => sim.AirControlPitchTorqueFactorMin = value);

    static readonly Func<float, float> Steered = was => was * (float)AirControl!.Value;

    public static readonly Command Jump = new(
        "batmobile.driving.jump", "BATMOBILE JUMP",
        "Launches the Batmobile into the air. It comes with a hotkey, J, which you can change like any other.",
        Launch)
    {
        Block = Car.NotDriving,
    };

    public static readonly Slider JumpPower = new(
        "batmobile.driving.jump_power", "JUMP POWER",
        "How high BATMOBILE JUMP throws the car. 1X is about a car's length.",
        min: 0.5, max: 4, step: 0.25, value: 1, unit: "X");

    public static readonly Slider AirGravity = new(
        "batmobile.driving.air_gravity", "AIR GRAVITY",
        "How hard the Batmobile falls once its wheels leave the ground, for longer, floatier jumps. On the road it is the game's own.",
        min: 0.1, max: 1, step: 0.05, value: 1, unit: "X")
    {
        Tick = Float,
    };

    public static readonly Slider AirControl = new(
        "batmobile.driving.air_control", "AIR CONTROL",
        "How hard you can turn and tip the Batmobile in the air. 1X is the game's.",
        min: 1, max: 4, step: 0.25, value: 1, unit: "X")
    {
        Tick = Steer,
        Restore = ReleaseSteering,
    };

    public static readonly Toggle MaxEject = new(
        "batmobile.driving.max_super_eject", "MAX SUPER EJECT",
        "Holding the eject button always builds up to the highest super eject, as if fully upgraded.")
    {
        Tick = HoldEject,
        Restore = Eject.Release,
    };

    static Outcome Launch()
    {
        if (World.Driving is not { } car)
        {
            return Outcome.Failed("NOT IN CAR");
        }

        if (car.bControlledBySeqAct || car.MatineeControlRefCount != 0)
        {
            return Outcome.Failed("NOT NOW");
        }

        // Only from the ground, so it can't be used to climb into the sky.
        if (!car.HasWheelsOnGround() && car.AirborneTime > AirborneGrace)
        {
            return Outcome.Unchanged("IN THE AIR");
        }

        if (car.CollisionComponent is not { IsValid: true } body)
        {
            return Outcome.Failed("NO CAR");
        }

        body.SetRBLinearVelocity(new Vector3(0f, 0f, JumpSpeed * (float)JumpPower.Value), true);
        return Outcome.Done("UP");
    }

    // Part of gravity is given back each frame the car is in the air. Not while the winch holds
    // it, the game keeps it on a wall then.
    static void Float()
    {
        if (World.Driving is not { } car || car.HasWheelsOnGround() || Car.Sim(car) is not { Abseiling: false } ||
            car.CollisionComponent is not { IsValid: true } body || !Game.TryGetWorldInfo(out var world))
        {
            return;
        }

        var scaling = world.RBPhysicsGravityScaling > 0f ? world.RBPhysicsGravityScaling : 1f;
        var gravity = -world.GetGravityZ() * scaling;
        var lift = gravity * (1f - (float)AirGravity.Value) * World.Delta;
        body.SetRBLinearVelocity(new Vector3(0f, 0f, lift), true);
    }

    // The native air control turns the car by these, scaled by the stick, while at most one wheel
    // is down.
    static void Steer()
    {
        if (Car.Find() is not { } car || Car.Sim(car) is not { } sim)
        {
            return;
        }

        YawMost.Hold(sim, Steered);
        YawLeast.Hold(sim, Steered);
        PitchMost.Hold(sim, Steered);
        PitchLeast.Hold(sim, Steered);
    }

    static void ReleaseSteering()
    {
        YawMost.Release();
        YawLeast.Release();
        PitchMost.Release();
        PitchLeast.Release();
    }

    // The game only reads this, when the eject button is held.
    static void HoldEject()
    {
        if (Car.Find() is { } car)
        {
            Eject.Hold(car, EjectType.EBET_SuperEject2);
        }
    }
}
