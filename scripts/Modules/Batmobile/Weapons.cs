#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// BATMOBILE > WEAPONS, the ARSENAL rows, and the car's weapons for the other rows to use.
static class Weapons
{
    const float RefreshSeconds = 1f;

    // A wait the game still counts down, but too short to notice. Zero would make the HUD's
    // recharge display divide by zero.
    const float Moment = 0.05f;

    static readonly Held<RVehicleBatmobileBase, bool> PowerCheat = new("weapon power", car => car.CheatBatmobilePower, (car, on) => car.CheatBatmobilePower = on);
    static readonly Held<RBatmobileMissileLauncher, float> BarrageWait = new("barrage cooldown", launcher => launcher.WeaponCoolDown, (launcher, wait) => launcher.WeaponCoolDown = wait);
    static readonly Held<RBatmobileRockets, float> RocketReload = new("rocket reload", rockets => rockets.ReloadTime, (rockets, wait) => rockets.ReloadTime = wait);
    static readonly Held<RBatmobileCannon, float> HeatUp = new("cannon heat", cannon => cannon.HeatUpPerSecond, (cannon, heat) => cannon.HeatUpPerSecond = heat);
    static readonly Held<RBatmobileCannon, int> Magazine = new("cannon magazine", cannon => cannon.AmmoPerMagazine, (cannon, shells) => cannon.AmmoPerMagazine = shells);
    static readonly Held<RBatmobileCannon, bool> Drift = new("cannon spread", cannon => cannon.bLoseAccuracyOverTime, (cannon, on) => cannon.bLoseAccuracyOverTime = on);
    static readonly Held<RBatmobileCannon, float> SpreadAngle = new("cannon spread angle", cannon => cannon.InaccuracyAngle, (cannon, angle) => cannon.InaccuracyAngle = angle);
    static readonly Held<RBatmobileCannon, float> Accuracy = new("cannon accuracy", cannon => cannon.CurrentAccuracyZeroToOne, (cannon, share) => cannon.CurrentAccuracyZeroToOne = share);
    static readonly Held<RBatmobileCannon, int> GunDamage = new("gun damage", cannon => cannon.ChainGunDamage, (cannon, damage) => cannon.ChainGunDamage = damage);

    static readonly Func<int, int> Harder = was => (int)MathF.Round(was * (float)GunDamageScale!.Value);

    static readonly List<RVehicleWeapon> Found = new();
    static nint _foundFor;
    static float _foundAt = float.MinValue;

    public static readonly Command ArmEverything = new(
        "batmobile.weapons.arm_everything", "ARM EVERY WEAPON",
        "Gives the Batmobile every battle mode and pursuit weapon, bought or not, with no cooldowns. Nothing is saved, so they last until a reload or skin change.",
        Arm)
    {
        Block = Car.Missing,
    };

    public static readonly Toggle UnlimitedPower = new(
        "batmobile.weapons.unlimited_power", "UNLIMITED WEAPON POWER",
        "The weapon generator stays full, so the missile barrage and every weapon that runs on power are always ready.")
    {
        Tick = KeepPower,
        Restore = PowerCheat.Release,
    };

    public static readonly Toggle NoCooldowns = new(
        "batmobile.weapons.no_cooldowns", "NO COOLDOWNS",
        "The 60mm cannon, EMP, missiles, mines, rockets and the other special weapons are ready again straight after firing.")
    {
        Tick = ClearCooldowns,
        Restore = ReleaseCooldowns,
    };

    public static readonly Toggle NoReload = new(
        "batmobile.weapons.no_reload", "NO RELOAD OR OVERHEAT",
        "The Batmobile's main gun never runs out of shells, never stops to reload and never overheats.")
    {
        Tick = KeepCannonCool,
        Restore = ReleaseCannonCool,
    };

    public static readonly Toggle NoSpread = new(
        "batmobile.weapons.no_spread", "NO GUN SPREAD",
        "The main gun stays dead on target however long you hold the trigger.")
    {
        Tick = KeepCannonSteady,
        Restore = ReleaseCannonSteady,
    };

    public static readonly Slider GunDamageScale = new(
        "batmobile.weapons.gun_damage", "GUN DAMAGE",
        "Multiplies how hard the Batmobile's main gun hits. 1X is the game's.",
        min: 1, max: 10, step: 0.5, value: 1, unit: "X")
    {
        Tick = HoldGunDamage,
        Restore = GunDamage.Release,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("ARSENAL", ArmEverything, UnlimitedPower, NoCooldowns, NoReload, NoSpread, GunDamageScale),
        new("MISSILES", Missiles.BarrageSize, Missiles.InstantLock, Missiles.PursuitDamage),
        new("AUTO COMBAT", AutoCombat.Fight, AutoCombat.Range, AutoCombat.ShootDown),
        new("ON FOOT", Missiles.TakedownReady),
    };

    // Every weapon the car carries, found by owner so ones fitted later are picked up too.
    public static List<RVehicleWeapon> Fitted(RVehicleBatmobileBase car)
    {
        if (car.Ptr == _foundFor && MathF.Abs(World.Now - _foundAt) < RefreshSeconds)
        {
            return Found;
        }

        _foundFor = car.Ptr;
        _foundAt = World.Now;
        Found.Clear();
        foreach (var weapon in Game.FindObjects<RVehicleWeapon>())
        {
            if (weapon.IsValid && !weapon.bDeleteMe && weapon.Owner is { } owner && owner.Ptr == car.Ptr)
            {
                Found.Add(weapon);
            }
        }

        return Found;
    }

    public static T? First<T>(RVehicleBatmobileBase car) where T : RVehicleWeapon
    {
        foreach (var weapon in Fitted(car))
        {
            if (weapon is T { IsValid: true } found)
            {
                return found;
            }
        }

        return null;
    }

    // The game's own cheat for the car. It adds every weapon with the force flag, then clears the
    // cooldowns. The car doesn't have to be driven.
    static Outcome Arm()
    {
        if (Car.Find() is not { } car)
        {
            return Outcome.Failed("NO CAR");
        }

        var before = Count(car);
        car.DebugGiveAllGadgets();
        _foundAt = float.MinValue;
        var added = Count(car) - before;
        return added > 0 ? Outcome.Done(added == 1 ? "1 ADDED" : $"{added} ADDED") : Outcome.Unchanged("ALL FITTED");
    }

    static int Count(RVehicleBatmobileBase car)
    {
        var count = 0;
        var battle = car.ActualWeapons;
        var pursuit = car.ActualPMWeapons;
        for (var i = 0; i < battle.Count; i++)
        {
            count += battle[i] is { IsValid: true } ? 1 : 0;
        }

        for (var i = 0; i < pursuit.Count; i++)
        {
            count += pursuit[i] is { IsValid: true } ? 1 : 0;
        }

        return count;
    }

    // The game's own infinite power cheat. With it set, the generator fills itself every frame.
    static void KeepPower()
    {
        if (Car.Find() is { } car)
        {
            PowerCheat.Hold(car, true);
        }
    }

    // Most weapons count a cooldown down by themselves. The barrage and the rockets wait on a
    // timer instead, so their wait is shortened. The main gun's countdown is its rate of fire,
    // and the winch, scanner and boost aren't weapons, so they are left alone.
    static void ClearCooldowns()
    {
        if (Car.Find() is not { } car)
        {
            return;
        }

        foreach (var weapon in Fitted(car))
        {
            switch (weapon)
            {
                case RBatmobileCannon or RBatmobileWinch or RBatmobileSonar or RBatmobileBooster:
                    continue;
                case RBatmobileMissileLauncher launcher:
                    BarrageWait.Hold(launcher, wait => MathF.Min(wait, Moment));
                    break;
                case RBatmobileRockets rockets:
                    RocketReload.Hold(rockets, wait => MathF.Min(wait, Moment));
                    break;
            }

            if (weapon.CoolDown > 0f)
            {
                weapon.CoolDown = 0f;
            }

            if (weapon.CurrentRechargeTime > 0f)
            {
                weapon.CurrentRechargeTime = 0f;
            }
        }
    }

    static void ReleaseCooldowns()
    {
        BarrageWait.Release();
        RocketReload.Release();
    }

    // Heat only builds in the game's native firing code, by this much a shot. A magazine size of
    // none is the game's own way of saying the gun never reloads.
    static void KeepCannonCool()
    {
        if (Car.Find() is not { } car || First<RBatmobileCannon>(car) is not { } cannon)
        {
            return;
        }

        HeatUp.Hold(cannon, 0f);
        Magazine.Hold(cannon, 0);
        if (cannon.CurrentHeatLevel > 0f)
        {
            cannon.CurrentHeatLevel = 0f;
        }

        // Damage that drains the generator can still overheat the gun outright.
        if (cannon.bOverheating)
        {
            cannon.StopOverHeat();
        }
    }

    static void ReleaseCannonCool()
    {
        HeatUp.Release();
        Magazine.Release();
    }

    // The native firing code spreads each shot by this angle. The game only changes it while
    // spread builds over time, so with that off it stays at none.
    static void KeepCannonSteady()
    {
        if (Car.Find() is not { } car || First<RBatmobileCannon>(car) is not { } cannon)
        {
            return;
        }

        Drift.Hold(cannon, false);
        SpreadAngle.Hold(cannon, 0f);
        Accuracy.Hold(cannon, 1f);
    }

    static void ReleaseCannonSteady()
    {
        Drift.Release();
        SpreadAngle.Release();
        Accuracy.Release();
    }

    static void HoldGunDamage()
    {
        if (Car.Find() is { } car && First<RBatmobileCannon>(car) is { } cannon)
        {
            GunDamage.Hold(cannon, Harder);
        }
    }
}
