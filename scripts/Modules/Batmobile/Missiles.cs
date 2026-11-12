#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// BATMOBILE > WEAPONS, the MISSILES and ON FOOT rows.
static class Missiles
{
    // A pursuit missile can be passed to the game's spawn handler again when it changes target,
    // so the last few are remembered and scaled once.
    const int Remembered = 16;

    static readonly Held<RBatmobileEMPMissile, float> LockTime = new("missile lock", launcher => launcher.MissileLockOnDuration, (launcher, time) => launcher.MissileLockOnDuration = time);
    static readonly Held<RBatmobileEMPMissile, float> CivilianLockTime = new("civilian missile lock", launcher => launcher.CivilianMissileLockOnDuration, (launcher, time) => launcher.CivilianMissileLockOnDuration = time);

    static readonly List<RProjectile> Scaled = new();

    static RBatmobileMissileLauncher? _barrage;
    static int _barrageWas;
    static int _barrageWrote;

    static RPlayerControllerCombat? _filled;

    public static readonly Slider BarrageSize = new(
        "batmobile.weapons.barrage_size", "MISSILE BARRAGE SIZE",
        "How many targets a full barrage locks on to and how many rockets it fires. Part-charged barrages keep the game's size. GAME keeps your upgrade's size.",
        min: 0, max: 40, step: 4, value: 0, zero: "GAME")
    {
        Tick = HoldBarrage,
        Restore = PutBarrageBack,
    };

    public static readonly Toggle InstantLock = new(
        "batmobile.weapons.instant_lock", "INSTANT MISSILE LOCK",
        "The pursuit missile locks on to cars, APCs and anything else it can target the moment they are in your sights.")
    {
        Tick = HoldLock,
        Restore = ReleaseLock,
    };

    public static readonly Slider PursuitDamage = new(
        "batmobile.weapons.pursuit_damage", "PURSUIT MISSILE DAMAGE",
        "Multiplies how hard each pursuit missile hits, so APCs and fleeing cars go down in fewer hits.",
        min: 1, max: 10, step: 0.5, value: 1, unit: "X");

    public static readonly Toggle TakedownReady = new(
        "batmobile.weapons.takedown_ready", "BATMOBILE TAKEDOWN READY",
        "In a fight on foot, the Batmobile's takedown is always charged, so it's ready whenever the car is close enough to help.")
    {
        Tick = FillTakedown,
        Restore = () => _filled = null,
    };

    // The launcher's levels are how many targets each charge of power allows, and a volley fires
    // the whole count of the first level that covers its locks. Only the top level is changed,
    // and never below the one under it, so part-charged barrages stay the game's.
    static void HoldBarrage()
    {
        if (Car.Find() is not { } car || Weapons.First<RBatmobileMissileLauncher>(car) is not { } launcher)
        {
            return;
        }

        var levels = launcher.MaxTargetsPerLevel;
        var top = levels.Count - 1;
        if (top < 0)
        {
            return;
        }

        if (!ReferenceEquals(launcher, _barrage))
        {
            PutBarrageBack();
            _barrage = launcher;
            _barrageWas = _barrageWrote = levels[top];
        }
        else if (levels[top] != _barrageWrote)
        {
            // An upgrade changed it while ours was in place.
            _barrageWas = levels[top];
        }

        var below = top > 0 ? levels[top - 1] : 1;
        var wanted = Math.Max((int)BarrageSize.Value, below);
        if (levels[top] != wanted)
        {
            levels[top] = wanted;
        }

        _barrageWrote = wanted;
    }

    static void PutBarrageBack()
    {
        if (_barrage is { IsValid: true } launcher && launcher.MaxTargetsPerLevel is { Count: > 0 } levels)
        {
            levels[levels.Count - 1] = _barrageWas;
        }

        _barrage = null;
    }

    // A lock time of none locks on the first frame a target is in sight.
    static void HoldLock()
    {
        if (Car.Find() is { } car && Weapons.First<RBatmobileEMPMissile>(car) is { } launcher)
        {
            LockTime.Hold(launcher, 0f);
            CivilianLockTime.Hold(launcher, 0f);
        }
    }

    static void ReleaseLock()
    {
        LockTime.Release();
        CivilianLockTime.Release();
    }

    // The game sets each pursuit missile's damage here, as it leaves the car.
    [Redirect(typeof(RBatmobileEMPMissile), nameof(RBatmobileEMPMissile.OnProjectileSpawned))]
    static void OnProjectileSpawned(RBatmobileEMPMissile self, RProjectile projectile, Actor target)
    {
        Redirects.RunOriginal();

        var scale = (float)PursuitDamage.Value;
        if (scale <= 1f || projectile is not { IsValid: true } || Scaled.Exists(each => ReferenceEquals(each, projectile)))
        {
            return;
        }

        Scaled.RemoveAll(each => !each.IsValid);
        if (Scaled.Count >= Remembered)
        {
            Scaled.RemoveAt(0);
        }

        Scaled.Add(projectile);
        projectile.Damage *= scale;
    }

    // Using the takedown empties the meter, so it is filled again straight away.
    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.SpendBatmobileTakedownAdrenaline))]
    static void SpendBatmobileTakedownAdrenaline(RPlayerControllerCombat self)
    {
        if (!TakedownReady.On)
        {
            Redirects.RunOriginal();
            return;
        }

        self.BatmobileTakedownAdrenalineValue = self.GetBatmobileTakedownAdrenalineSpecialLimit();
    }

    // Every fight starts with it full. This fills it once for a fight already going when the
    // option is turned on.
    static void FillTakedown()
    {
        if (World.Controller is not RPlayerControllerCombat { IsValid: true } controller || ReferenceEquals(controller, _filled))
        {
            return;
        }

        _filled = controller;
        var full = controller.GetBatmobileTakedownAdrenalineSpecialLimit();
        if (controller.BatmobileTakedownAdrenalineValue < full)
        {
            controller.BatmobileTakedownAdrenalineValue = full;
            controller.ShowBatmobileTakedownAdrenaline();
        }
    }
}
