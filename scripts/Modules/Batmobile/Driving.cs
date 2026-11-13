#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using PickupMode = BmSDK.BmGame.RVehicleBatmobileBase.EBatmobilePickupMode;
using TyreType = BmSDK.Engine.SVehicleWheel.ETyreType;

namespace Knightfall;

// BATMOBILE > DRIVING and TUNING. The car's physics is native, so speed acts on its body
// directly, the way the game's own Batmobile cheats do, and the native tuning values are only
// scaled.
static class Driving
{
    const float GetInReach = 1400f;

    // Chosen by feel. The native engine model can't be read.
    const float EngineAccel = 1500f;
    const float EngineCruise = 4500f;
    const float EngineSpeedLimit = 18000f;

    const float TippedDegrees = 75f;
    const float TippedSpeed = 400f;
    const float RightAfterSeconds = 1.2f;
    const float RightGapSeconds = 2f;
    const float RightLift = 150f;

    // The game's own grip for each tyre, which TYRE GRIP multiplies.
    const float RoadGrip = 0.8f;
    const float BattleGrip = 1f;

    static readonly Held<RVehicleBatmobileBase, bool> EmpVulnerable = new("EMP", car => car.VulnerableToEMP, (car, on) => car.VulnerableToEMP = on);
    static readonly Held<RVehicleBatmobileBase, bool> CarBoost = new("unlimited boost", car => car.InfiniteBoost, (car, on) => car.InfiniteBoost = on);
    static readonly Held<RVehicleSimBatmobile, bool> SimBoost = new("unlimited boost", sim => sim.bInfiniteBoost, (sim, on) => sim.bInfiniteBoost = on);
    static readonly Held<RVehicleSimBatmobile, float> TopSpeed = new("top speed", sim => sim.ScaleTopSpeed, (sim, speed) => sim.ScaleTopSpeed = speed);
    static readonly Held<RVehicleSimBatmobile, float> BattleTopSpeed = new("battle mode speed", sim => sim.BattleModeMaxSpeed, (sim, speed) => sim.BattleModeMaxSpeed = speed);
    static readonly Held<RVehicleBatmobileBase, bool> DodgeUpgrade = new("double dodge", car => car.bDoubleDodgeUpgrade, (car, on) => car.bDoubleDodgeUpgrade = on);
    static readonly Held<RVehicleBatmobileBase, float> RamMost = new("ram damage", car => car.ImpactMaxDamage, (car, damage) => car.ImpactMaxDamage = damage);

    // Zero means the game doesn't use the value, so it is left at zero.
    static readonly Func<float, float> RaisedTopSpeed = was => was > 0.01f ? was * (float)EnginePower!.Value : was;
    static readonly Func<float, float> RaisedBattleSpeed = was => was > 0.01f ? was * (float)BattleSpeed!.Value : was;
    static readonly Func<float, float> RaisedRam = was => was * (float)RamDamage!.Value;

    static nint _wholeCar;
    static int _wholeHealth;
    static double _gripApplied = 100;
    static float _tippedFor;
    static float _rightedAt = float.MinValue;

    public static readonly Command CallCar = new(
        "batmobile.driving.call", "CALL BATMOBILE",
        "Calls the Batmobile to you, or gets you in if it is parked close by.",
        Call)
    {
        Block = Car.InCar,
    };

    public static readonly Command Recover = new(
        "batmobile.driving.recover", "RECOVER BATMOBILE",
        "Puts a stuck or flipped Batmobile back on the road.",
        RecoverCar)
    {
        Block = Car.Missing,
    };

    public static readonly Command ForceExit = new(
        "batmobile.driving.force_exit", "FORCE EXIT",
        "Gets you out of the Batmobile when the game won't let you, like a mission that wants a prisoner delivered first.",
        Exit)
    {
        Block = Car.NotDriving,
    };

    public static readonly Command Repair = new(
        "batmobile.driving.repair", "REPAIR AND RECHARGE",
        "Fixes all of the Batmobile's damage and fills its weapon power.",
        RepairCar)
    {
        Block = Car.Missing,
    };

    public static readonly Toggle Indestructible = new(
        "batmobile.driving.indestructible", "INDESTRUCTIBLE",
        "The Batmobile's armour stays full, so it can't be destroyed.")
    {
        Tick = KeepWhole,
    };

    public static readonly Toggle EmpProof = new(
        "batmobile.driving.emp_proof", "EMP-PROOF",
        "EMP can't shut the Batmobile down, so it keeps driving and can always be called.")
    {
        Tick = ShrugOffEmp,
        Restore = EmpVulnerable.Release,
    };

    public static readonly Toggle SelfRighting = new(
        "batmobile.driving.self_righting", "SELF-RIGHTING",
        "When the Batmobile lands on its roof or side and stays there, it is put back on its wheels a second later.")
    {
        Tick = RightWhenTipped,
    };

    public static readonly Toggle UnlimitedBoost = new(
        "batmobile.driving.unlimited_boost", "UNLIMITED BOOST",
        "The afterburner never runs out or needs to recharge.")
    {
        Tick = KeepBoost,
        Restore = ReleaseBoost,
    };

    public static readonly Slider EnginePower = new(
        "batmobile.driving.engine_power", "ENGINE POWER",
        "Harder acceleration and a higher top speed, boost included. 1X is the game's engine.",
        min: 1, max: 4, step: 0.25, value: 1, unit: "X")
    {
        Tick = PowerUp,
        Restore = TopSpeed.Release,
    };

    public static readonly Slider TyreGrip = new(
        "batmobile.driving.tyre_grip", "TYRE GRIP",
        "How hard the tyres hold the road. Lower slides and drifts more, higher corners flat. 1X is the game's own.",
        min: 0.25, max: 3, step: 0.25, value: 1, unit: "X")
    {
        Tick = ApplyGrip,
        Restore = ResetGrip,
    };

    public static readonly Slider RamDamage = new(
        "batmobile.driving.ram_damage", "RAM DAMAGE",
        "Multiplies the damage the Batmobile does when it rams militia vehicles and getaway cars. 1X is the game's.",
        min: 1, max: 10, step: 0.5, value: 1, unit: "X")
    {
        Tick = HoldRam,
        Restore = RamMost.Release,
    };

    public static readonly Slider BattleSpeed = new(
        "batmobile.driving.battle_speed", "BATTLE MODE SPEED",
        "How fast the Batmobile moves in battle mode. 1X is the game's speed.",
        min: 1, max: 3, step: 0.25, value: 1, unit: "X")
    {
        Tick = HoldBattleSpeed,
        Restore = BattleTopSpeed.Release,
    };

    public static readonly Toggle DoubleDodge = new(
        "batmobile.driving.double_dodge", "DOUBLE DODGE",
        "Battle mode's dodge works twice in a row, as with its upgrade, without buying it.")
    {
        Tick = HoldDodge,
        Restore = DodgeUpgrade.Release,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("ACCESS", CallCar, Recover, ForceExit, Repair),
        new("AUTOPILOT", Autopilot.ToWaypoint, Autopilot.ToObjective, Autopilot.Stop, Autopilot.CanBoost),
        new("REMOTE CONTROL", Remote.Drive, Remote.InFights),
    };

    public static readonly Catalog.Section[] Tuning =
    {
        new("PROTECTION", Indestructible, EmpProof, SelfRighting),
        new("PERFORMANCE", UnlimitedBoost, EnginePower, TyreGrip, RamDamage),
        new("BATTLE MODE", BattleAnySkin.Option, BattleSpeed, DoubleDodge),
        new("STUNTS", Stunts.Jump, Stunts.JumpPower, Stunts.AirGravity, Stunts.AirControl, Stunts.MaxEject),
    };

    static Outcome Call()
    {
        if (World.Controller is not { } controller || World.Player is not { Health: > 0 } player)
        {
            return Outcome.NoPlayer;
        }

        if (World.Driving is not null)
        {
            return Outcome.Unchanged("IN THE CAR");
        }

        var car = Car.Find();

        // A car the story is holding or driving stays the story's.
        if (car is not null && (car.IsStolen || car.bControlledBySeqAct))
        {
            return Outcome.Failed("BUSY");
        }

        if (car is not null && (car.BatmobileHitWater || car.BatmobileAboutToHitWater))
        {
            return Outcome.Failed("IN THE WATER");
        }

        if (car is not null && car.Driver is null && Vector3.Distance(car.Location, player.Location) <= GetInReach && car.CanEnterVehicle(player))
        {
            car.GetInBatmobile(player);
            return Outcome.Done("GETTING IN");
        }

        return controller.CallBatmobile() switch
        {
            PickupMode.BPM_None or PickupMode.BPM_Unavailable => Outcome.Failed("CAN'T COME"),
            PickupMode.BPM_BlockedByHeavyTanks => Outcome.Failed("TANKS NEARBY"),
            _ => Outcome.Done("ON ITS WAY"),
        };
    }

    static Outcome RecoverCar()
    {
        if (Car.Find() is not { } car)
        {
            return Outcome.Failed("NO CAR");
        }

        return car.RescueMe(false) ? Outcome.Done() : Outcome.Failed("REFUSED");
    }

    // The game's block on getting out is only checked when the exit button is pressed. A forced
    // leave doesn't ask, so the block itself is never touched.
    static Outcome Exit()
    {
        if (World.Driving is not { } car)
        {
            return Outcome.Failed("NOT IN CAR");
        }

        return car.DriverLeave(true, false) || car.DriverLeave(true, true) ? Outcome.Done("OUT") : Outcome.Failed("REFUSED");
    }

    static Outcome RepairCar()
    {
        if (Car.Find() is not { } car)
        {
            return Outcome.Failed("NO CAR");
        }

        car.GiveMaxHealth();
        car.GiveMaxEnergy();
        return Outcome.Done();
    }

    // Every hit on the car comes through here. With INDESTRUCTIBLE on it is passed on with no
    // damage, the way the game's own god mode does it, so one big hit can't destroy the car
    // between two ticks.
    [Redirect(typeof(RVehicleBatmobileBase), nameof(RVehicleBatmobileBase.TakeDamage))]
    static void TakeDamage(RVehicleBatmobileBase self, int damage, Controller instigatedBy, Vector3 hitLocation, Vector3 momentum, Class damageType, Actor.FTraceHitInfo hitInfo, Actor damageCauser)
    {
        try
        {
            self.TakeDamage(Indestructible.On ? 0 : damage, instigatedBy, hitLocation, momentum, damageType, hitInfo, damageCauser);
        }
        catch (Exception error)
        {
            Log.Error("driving.hit", "A hit on the Batmobile failed part way", error);
        }
    }

    // Armour upgrades add to the base health, one amount per level.
    static void KeepWhole()
    {
        if (Car.Find() is not { Health: > 0 } car)
        {
            return;
        }

        if (car.Ptr != _wholeCar)
        {
            _wholeCar = car.Ptr;
            _wholeHealth = 0;
        }

        var levels = car.ArmourLevels;
        var bonus = levels.Count == 0 ? 0 : levels[Math.Clamp(car.CurrentArmourLevel, 0, levels.Count - 1)];
        _wholeHealth = Math.Max(_wholeHealth, Math.Max(car.Health, car.HealthMax + bonus));
        if (car.Health < _wholeHealth)
        {
            car.Health = _wholeHealth;
        }
    }

    static void ShrugOffEmp()
    {
        if (Car.Find() is not { } car)
        {
            return;
        }

        EmpVulnerable.Hold(car, false);
        if (car.DisabledByEMP)
        {
            car.SetDisabledByEMP(false);
        }
    }

    static void RightWhenTipped()
    {
        if (World.Driving is not { } car || Car.Sim(car) is { Abseiling: true } || car.MatineeControlRefCount != 0 ||
            (car.bControlledBySeqAct && !Autopilot.Active))
        {
            // A scene poses the car itself, and the winch can hold it on a wall with no wheels
            // down. Both are left to the game.
            _tippedFor = 0f;
            return;
        }

        var turn = car.Rotation;
        var tipped = MathF.Abs(Wrap(turn.Roll)) > TippedDegrees || MathF.Abs(Wrap(turn.Pitch)) > TippedDegrees;
        var stuck = tipped && car.Velocity.Length() < TippedSpeed && car.GetNumWheelsOnGround() < 2;
        _tippedFor = stuck ? _tippedFor + World.Delta : 0f;

        if (_tippedFor < RightAfterSeconds || MathF.Abs(World.Now - _rightedAt) < RightGapSeconds || car.Mesh is not { IsValid: true } mesh)
        {
            return;
        }

        _tippedFor = 0f;
        _rightedAt = World.Now;
        mesh.SetRBRotation(new Rotator(0f, turn.Yaw, 0f), FName.None);
        mesh.SetRBPosition(car.Location + new Vector3(0f, 0f, RightLift), FName.None);
        mesh.SetRBAngularVelocity(Vector3.Zero, false);
    }

    static float Wrap(float degrees)
    {
        degrees %= 360f;
        return degrees > 180f ? degrees - 360f : degrees < -180f ? degrees + 360f : degrees;
    }

    static void KeepBoost()
    {
        if (Car.Find() is not { } car)
        {
            return;
        }

        CarBoost.Hold(car, true);
        if (Car.Sim(car) is { } sim)
        {
            SimBoost.Hold(sim, true);
        }
    }

    static void ReleaseBoost()
    {
        CarBoost.Release();
        SimBoost.Release();
    }

    // A higher top speed alone barely shows, so the car is also pushed along while the throttle
    // is down. Not while the autopilot drives or brakes, it sets its own speed.
    static void PowerUp()
    {
        if (World.Driving is not { } car || Car.Sim(car) is not { } sim)
        {
            return;
        }

        TopSpeed.Hold(sim, RaisedTopSpeed);

        var throttle = car.Throttle;
        if (throttle <= 0.05f || Autopilot.Active || Autopilot.Braking || !car.HasWheelsOnGround() || car.IsInBattleMode() ||
            car.CollisionComponent is not { IsValid: true } body)
        {
            return;
        }

        var scale = (float)EnginePower.Value;
        var forward = car.Rotation.ToDirection();
        var along = Vector3.Dot(car.Velocity, forward);
        var limit = MathF.Min(EngineCruise * scale, EngineSpeedLimit);
        if (along < -50f || along >= limit)
        {
            return;
        }

        var gain = MathF.Min((scale - 1f) * EngineAccel * throttle * World.Delta, limit - along);
        body.SetRBLinearVelocity(forward * gain, true);
    }

    // Grip belongs to the tyre type and not to one car, so it outlasts a new car and a reload.
    static void ApplyGrip()
    {
        if (TyreGrip.Value != _gripApplied)
        {
            SetGrip(TyreGrip.Value);
        }
    }

    static void ResetGrip() => SetGrip(1);

    static void SetGrip(double scale)
    {
        var mod = (float)scale;
        RVehicleBatmobileBase.DebugSetTireFrictionMod(TyreType.ETyreType_Batmobile, RoadGrip * mod);
        RVehicleBatmobileBase.DebugSetTireFrictionMod(TyreType.ETyreType_BatmobileBattleMode, BattleGrip * mod);
        _gripApplied = scale;
    }

    static void HoldRam()
    {
        if (Car.Find() is { } car)
        {
            RamMost.Hold(car, RaisedRam);
        }
    }

    static void HoldBattleSpeed()
    {
        if (Car.Find() is { } car && Car.Sim(car) is { } sim)
        {
            BattleTopSpeed.Hold(sim, RaisedBattleSpeed);
        }
    }

    static void HoldDodge()
    {
        if (Car.Find() is { } car)
        {
            DodgeUpgrade.Hold(car, true);
        }
    }
}
