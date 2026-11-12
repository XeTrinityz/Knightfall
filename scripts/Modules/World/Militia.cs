#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// WORLD > ENEMIES, the tank and turret rows in MILITIA.
static class Militia
{
    const float HackRange = 15000f;

    // The game's own hack ends with the tank blowing itself up on this timer.
    const string HackTimer = "DroneHackedSuicide";

    // Longer than any session, for FOREVER.
    const float Forever = 1e7f;

    // A hack that didn't take is put right this long after it.
    const float CheckAfter = 1f;

    static readonly float[] Durations = { 30f, 60f, 120f, 300f, Forever };

    sealed record Hack(RVehicleTank Tank, RVehicleBehaviour Behaviour, float CheckAt);

    static readonly List<Hack> Checking = new();

    public static readonly Command HackTanks = new(
        "world.enemies.hack_tanks", "HACK NEARBY TANKS",
        "Every militia tank within 150 m switches sides and fights the others for you, like the Drone Hacker with no limit. Tanks a mission controls are left alone.",
        HackNear);

    public static readonly Choice HackTime = new(
        "world.enemies.hack_time", "HACK DURATION",
        "How long hacked tanks stay on your side. Like the Drone Hacker's, they blow themselves up when the time runs out.",
        new[] { ("30", "30 S"), ("60", "1 MIN"), ("120", "2 MIN"), ("300", "5 MIN"), ("forever", "FOREVER") })
    {
        Preference = true,
    };

    public static readonly Command ReleaseTanks = new(
        "world.enemies.release_tanks", "RELEASE HACKED TANKS",
        "Every hacked tank goes back to the militia without blowing up.",
        Release);

    public static readonly Command DisableTurrets = new(
        "world.enemies.disable_turrets", "DISABLE ALL TURRETS",
        "Shuts down every sentry gun and turret in the loaded part of the city, watchtowers and airships too, the way a disruptor hit does. They stay off.",
        Disable);

    public static readonly Command DestroySentries = new(
        "world.enemies.destroy_sentries", "DESTROY SENTRY GUNS",
        "Blows up every sentry gun in the loaded part of the city, and they stay destroyed.",
        Destroy);

    static Outcome HackNear()
    {
        if (World.Player is not { IsValid: true } you || World.Info?.VehicleCombatManager is not { IsValid: true } vehicles)
        {
            return Outcome.NoPlayer;
        }

        var seconds = Durations[Math.Clamp(HackTime.Index, 0, Durations.Length - 1)];
        var hacked = 0;
        var skipped = 0;

        // Only tanks in the game's own list of vehicles in a fight, the ones its Batmobile weapons
        // aim at. A parked or sleeping tank has no behaviour, and a mission's own tanks lost
        // their AI to the switch and froze for the rest of the session.
        foreach (var known in vehicles.VehicleEnemyList)
        {
            if (known.Enemy is not RVehicleTank { IsValid: true, bDeleteMe: false, bHidden: false, IsFriendly: false, IsHackedFriendly: false } tank
                || tank is RVehicleStaticTank || tank.Health <= 0
                || Vector3.DistanceSquared(tank.Location, you.Location) > HackRange * HackRange)
            {
                continue;
            }

            if (tank.CurrentBehaviour is not { IsValid: true } behaviour || tank.SpawningKismet is { IsValid: true })
            {
                skipped++;
                continue;
            }

            // What the Drone Hacker does to each of its targets.
            tank.EndDroneHack();
            tank.SwitchAllegiance(seconds);
            Checking.Add(new Hack(tank, behaviour, World.Now + CheckAfter));
            hacked++;
        }

        if (hacked > 0)
        {
            return Outcome.Done($"{hacked} HACKED");
        }

        return Outcome.Failed(skipped > 0 ? "ONLY MISSION TANKS" : "NO TANKS FIGHTING");
    }

    // Runs every tick. A tank the switch left with no AI at all gets its own back.
    public static void Check()
    {
        for (var i = Checking.Count - 1; i >= 0; i--)
        {
            var (tank, behaviour, checkAt) = Checking[i];
            if (World.Now < checkAt && World.Now >= checkAt - CheckAfter)
            {
                continue;
            }

            Checking.RemoveAt(i);
            if (!tank.IsValid || tank.bDeleteMe || tank.Health <= 0)
            {
                continue;
            }

            // What broke in Oracle: the switch dropped the tank's AI without giving it a friendly
            // one, and it froze. Only that is put right.
            if (tank.CurrentBehaviour is { IsValid: true })
            {
                continue;
            }

            Log.Warn("militia.hack." + tank.Ptr, $"A hack left {tank.Class.Name} with no AI, so its own was put back.");
            tank.ClearTimer(new FName(HackTimer));
            World.Info?.VehicleCombatManager?.SetVehicleAsFriendly(tank, false);
            if (behaviour.IsValid)
            {
                tank.GotoBehaviour(behaviour);
            }
        }
    }

    public static void Forget() => Checking.Clear();

    // Every hacked tank, also ones hacked before a reload or by the Batmobile itself. A hack sets
    // IsHackedFriendly. IsFriendly is a different flag, the one police cars have.
    static Outcome Release()
    {
        var count = 0;
        foreach (var tank in Game.FindObjects<RVehicleTank>())
        {
            if (tank.IsValid && !tank.bDeleteMe && !tank.IsClassDefaultObject && tank.Health > 0 && tank.IsHackedFriendly)
            {
                tank.ClearTimer(new FName(HackTimer));
                tank.BatmobileHackTimeout();
                count++;
            }
        }

        Checking.Clear();
        return count > 0 ? Outcome.Done($"{count} RELEASED") : Outcome.Unchanged("NONE HACKED");
    }

    static Outcome Disable()
    {
        var count = 0;
        foreach (var gun in Game.FindObjects<RSentryGunBase>())
        {
            if (gun.IsValid && !gun.bDeleteMe && !gun.IsClassDefaultObject && !gun.bHidden && !gun.bDisabled && !gun.bGoneRagdoll)
            {
                gun.DisableSentryGun();
                count++;
            }
        }

        foreach (var turret in Game.FindObjects<RTurretBase>())
        {
            if (turret.IsValid && !turret.bDeleteMe && !turret.IsClassDefaultObject && !turret.bHidden && !turret.bDisabled)
            {
                turret.Disrupted();
                count++;
            }
        }

        return count > 0 ? Outcome.Done($"{count} OFF") : Outcome.Unchanged("NONE WORKING");
    }

    // The game's own destroy. Only the fall afterwards marks a gun as destroyed for good, so a
    // gun still standing after the blast is made to fall. Hidden guns are still packed away.
    static Outcome Destroy()
    {
        var count = 0;
        foreach (var gun in Game.FindObjects<RSentryGunBase>())
        {
            if (!gun.IsValid || gun.bDeleteMe || gun.IsClassDefaultObject || gun.bHidden || gun.bGoneRagdoll)
            {
                continue;
            }

            gun.Explode(true);
            if (!gun.bGoneRagdoll)
            {
                gun.GoPhysicsAndTriggerEvent();
            }

            count++;
        }

        return count > 0 ? Outcome.Done($"{count} DESTROYED") : Outcome.Unchanged("NONE LEFT");
    }
}
