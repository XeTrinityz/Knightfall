#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// BATMOBILE > WEAPONS, the AUTO COMBAT rows. Every shot goes through the weapon's own fire
// routine, the one a button press runs, so it keeps the game's damage, effects, power cost and
// reloads.
static class AutoCombat
{
    const float ScanSeconds = 0.5f;
    const float ShootDownSeconds = 0.1f;
    const float UnitsPerMetre = 100f;
    const int BarrageAtLeast = 3;

    // From just above the roof to a drone's middle.
    static readonly Vector3 SightFrom = new(0f, 0f, 200f);
    static readonly Vector3 SightTo = new(0f, 0f, 60f);

    static readonly FName Holstered = new("Holstered");
    static readonly List<RVehicleNPC> Foes = new();
    static readonly List<(RVehicleNPC Foe, bool OnMe, float Distance)> Ranked = new();
    static float _scannedAt = float.MinValue;
    static float _downAt = float.MinValue;

    public static readonly Toggle Fight = new(
        "batmobile.weapons.auto_combat", "AUTO COMBAT",
        "The Batmobile fights for you. In battle mode it shoots drones and fires barrages at groups. In pursuit it fires at your locked target. Tanks are left to you.")
    {
        Tick = Act,
        Restore = Foes.Clear,
    };

    public static readonly Slider Range = new(
        "batmobile.weapons.auto_combat_range", "AUTO COMBAT RANGE",
        "How far away AUTO COMBAT picks its targets.",
        min: 40, max: 300, step: 10, value: 120, unit: " M")
    {
        Block = static () => Fight.On ? null : "AUTO COMBAT IS OFF",
    };

    public static readonly Toggle ShootDown = new(
        "batmobile.weapons.shoot_down", "SHOOT DOWN MISSILES",
        "Militia missiles homing in on you or the Batmobile are shot out of the sky, in the car or on foot.")
    {
        Tick = ShootDownMissiles,
    };

    static void Act()
    {
        // Not while a mission or a scene drives the car.
        if (World.Driving is not { } car || car.bControlledBySeqAct || car.MatineeControlRefCount != 0)
        {
            Foes.Clear();
            return;
        }

        if (!car.IsInBattleMode())
        {
            Foes.Clear();
            Pursue(car);
            return;
        }

        if (MathF.Abs(World.Now - _scannedAt) >= ScanSeconds)
        {
            _scannedAt = World.Now;
            Scan(car);
        }

        Foes.RemoveAll(foe => !Hostile(foe));
        if (Foes.Count > 0 && !Barrage(car))
        {
            Cannon(car);
        }
    }

    // Drones attacking the car first, then the nearest.
    static void Scan(RVehicleBatmobileBase car)
    {
        Foes.Clear();
        Ranked.Clear();
        var reach = (float)Range.Value * UnitsPerMetre;
        foreach (var foe in Game.FindObjects<RVehicleNPC>())
        {
            if (foe is not (RVehicleTank or RVehicleFlying) || foe is RVehicleHeavyTank or RVehicleStaticTank || !Hostile(foe))
            {
                continue;
            }

            var distance = Vector3.Distance(foe.Location, car.Location);
            if (distance > reach)
            {
                continue;
            }

            var attacking = foe.IsAttacking();
            if (!attacking && !foe.IsAwareOfBatmobile())
            {
                continue;
            }

            Ranked.Add((foe, attacking && foe.GetTargetActor() is { } target && target.Ptr == car.Ptr, distance));
        }

        Ranked.Sort((a, b) => a.OnMe != b.OnMe ? (a.OnMe ? -1 : 1) : a.Distance.CompareTo(b.Distance));
        foreach (var (foe, _, _) in Ranked)
        {
            Foes.Add(foe);
        }
    }

    static bool Hostile(RVehicleNPC foe) =>
        foe is { IsValid: true, bDeleteMe: false, bHidden: false, DroneHacked: false, IsFriendly: false, CivilianVehicle: false, Health: > 0 };

    // The cannon fires at whatever its lock-on holds, so the lock is pointed at the target first.
    // The reload is the game's own, counted down by the cannon.
    static void Cannon(RVehicleBatmobileBase car)
    {
        if (Weapons.First<RBatmobileHeavyCannon>(car) is not { } cannon || cannon.CoolDown > 0f || cannon.ChargingShot ||
            car.WeaponGeneratorPower < cannon.PowerConsumption)
        {
            return;
        }

        foreach (var foe in Foes)
        {
            if (!car.FastTrace(foe.Location + SightTo, car.Location + SightFrom, Vector3.Zero, false))
            {
                continue;
            }

            cannon.CurrentLockOnTarget = foe;
            cannon.CurrentLockOn = 1f;
            if (cannon.FireNow())
            {
                cannon.CoolDown += cannon.FiringInterval;
            }

            return;
        }
    }

    // Only with a full generator, so the player's own barrage isn't drained. The game's own
    // barrage then picks its size from how many targets it is given.
    static bool Barrage(RVehicleBatmobileBase car)
    {
        if (Foes.Count < BarrageAtLeast || Weapons.First<RBatmobileMissileLauncher>(car) is not { } launcher || launcher.PowerConsumption <= 0f ||
            launcher.CurrentTargets.Count > 0 || !launcher.IsInState(Holstered))
        {
            return false;
        }

        var most = car.MaxWeaponGeneratorPower;
        var level = car.CurrentGeneratorLevel;
        if (level < 0 || level >= most.Count || car.WeaponGeneratorPower < most[level] - 0.01f)
        {
            return false;
        }

        // How the game works out the biggest barrage the power allows.
        var levels = launcher.MaxTargetsPerLevel;
        var charge = Math.Min((int)car.WeaponGeneratorPower - (int)launcher.PowerConsumption, levels.Count - 1);
        var size = charge < 0 ? 0 : Math.Min(levels[charge], Foes.Count);
        if (size < BarrageAtLeast)
        {
            return false;
        }

        var targets = launcher.CurrentTargets;
        for (var i = 0; i < size; i++)
        {
            targets.Add(Foes[i]);
        }

        launcher.FireMissilesNow();
        return true;
    }

    // The pursuit missile locks on by itself. It is fired once the lock is full, never at a
    // civilian car.
    static void Pursue(RVehicleBatmobileBase car)
    {
        if (Weapons.First<RBatmobileEMPMissile>(car) is not { } launcher || launcher.CoolDown > 0f || launcher.LockOnTime < 1f ||
            launcher.LockOnTarget is not { IsValid: true, bDeleteMe: false, CivilianVehicle: false, IsFriendly: false, IsPenguinVan: false, Health: > 0 } target ||
            Vector3.Distance(target.Location, car.Location) > (float)Range.Value * UnitsPerMetre || !launcher.IsInState(Holstered))
        {
            return;
        }

        launcher.FireNow();
    }

    // The game's own way a missile is shot down, as when the main gun hits it. It refuses while a
    // missile has only just been launched, so those are tried again on a later scan.
    static void ShootDownMissiles()
    {
        if (MathF.Abs(World.Now - _downAt) < ShootDownSeconds || World.Controller is not { } controller)
        {
            return;
        }

        _downAt = World.Now;
        var player = World.Player;
        var car = Car.Find();
        Actor? me = World.Driving is { } driving ? driving : player;
        if (me is null)
        {
            return;
        }

        foreach (var missile in Game.FindObjects<RTankMissile>())
        {
            if (!missile.IsValid || missile.bDeleteMe || missile.WasShotOutOfSky || missile.Target is not { } target ||
                !(ReferenceEquals(target, player) || ReferenceEquals(target, car)))
            {
                continue;
            }

            missile.TakeDamageFromWeapon(1, controller, missile.Location, Vector3.UnitZ, Vector3.Zero, RDmgType_BatmobileChainGun.StaticClass(), default, me, false);
        }
    }
}
