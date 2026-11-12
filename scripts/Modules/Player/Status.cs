#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > STATUS. Most of it steers the game at the moment it asks or acts. GOD MODE and the
// armour grant change stored values, and those are put back when the option goes off.
static class Status
{
    const float RegenDelaySeconds = 3f;
    const float ArmourCheckSeconds = 0.5f;
    const int ArmourPools = 2;

    // Made once. Looking a name up in the game's name table is not free.
    static readonly FName KillZone = new("RDmgType_KillZone");

    static readonly Held<RPlayerController, bool> GodFlag = new("god mode", controller => controller.bGodMode, (controller, on) => controller.bGodMode = on);

    static nint _regenPawn;
    static int _regenLastHealth;
    static float _regenCalm;
    static float _regenOwed;
    static float _armourCheckIn;
    static double _damageOwed;

    public static readonly Toggle GodMode = new(
        "player.status.god_mode", "GOD MODE",
        "Nothing can hurt you, whoever you play as, and the Batmobile can't be damaged.")
    {
        Tick = KeepGodMode,
        Restore = GodFlag.Release,
    };

    public static readonly Toggle CantDie = new(
        "player.status.cant_die", "CAN'T DIE",
        "Hits still hurt, but they can never finish you.");

    public static readonly Command RefillHealth = new(
        "player.status.refill_health", "REFILL HEALTH",
        "Heals you fully and tops up your armour. Also repairs the Batmobile.",
        Heal);

    public static readonly Slider DamageTaken = new(
        "player.status.damage_taken", "DAMAGE TAKEN",
        "Scales the damage you take. 1X is normal and 0X is none.",
        min: 0, max: 5, step: 0.1, value: 1, unit: "X");

    public static readonly Slider HealthRegen = new(
        "player.status.health_regen", "HEALTH REGEN",
        "Heals this much of your health every second, once you have gone 3 seconds without losing any.",
        min: 0, max: 25, step: 1, value: 0, unit: "% / S", zero: "OFF")
    {
        Tick = Regen,
    };

    public static readonly Toggle InfiniteArmour = new(
        "player.status.infinite_armour", "INFINITE ARMOUR",
        "Your armour is full again after every hit. You get a full set if you haven't bought any.")
    {
        Tick = KeepArmourFull,
        Restore = RecountArmour,
    };

    public static readonly Command RefillArmour = new(
        "player.status.refill_armour", "REFILL ARMOUR",
        "Tops up the armour you have bought.",
        TopUpArmour);

    public static readonly Toggle NoStagger = new(
        "player.status.no_stagger", "NO STAGGER",
        "Hits don't knock you out of what you are doing.");

    public static readonly Toggle NoStuns = new(
        "player.status.no_stuns", "NO STUNS",
        "Shocks, stun sticks and lasers don't stun you.");

    public static readonly Catalog.Section[] Sections =
    {
        new("SURVIVAL", GodMode, CantDie, RefillHealth, DamageTaken, HealthRegen),
        new("ARMOUR", InfiniteArmour, RefillArmour),
        new("HITS", NoStagger, NoStuns),
        new("STEALTH", Stealth.SilentFootsteps, Stealth.UnseenByDrones, Stealth.UnseenByThugs),
    };

    // Every hit on the player comes through here. The damage is changed on the way in, then the
    // game's own version runs with it.
    [Redirect(typeof(RPawnPlayer), nameof(RPawnPlayer.TakeDamage))]
    static void TakeDamage(RPawnPlayer self, int damage, Controller instigatedBy, Vector3 hitLocation, Vector3 momentum, Class damageType, Actor.FTraceHitInfo hitInfo, Actor damageCauser)
    {
        var passed = damage;
        try
        {
            passed = Incoming(self, damage, damageType);
        }
        catch (Exception error)
        {
            Log.Error("status.damage", "Damage could not be adjusted, so the hit went through unchanged", error);
        }

        try
        {
            self.TakeDamage(passed, instigatedBy, hitLocation, momentum, damageType, hitInfo, damageCauser);
            if (InfiniteArmour.On)
            {
                FillArmour(self, grant: true);
            }
        }
        catch (Exception error)
        {
            Log.Error("status.hit", "A hit on the player failed part way", error);
        }
    }

    // The game asks this before it plays a hit reaction.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.IgnoreHitReaction))]
    static bool IgnoreHitReaction(RPlayerController self)
    {
        try
        {
            return NoStagger.On || self.IgnoreHitReaction();
        }
        catch (Exception error)
        {
            Log.Error("status.stagger", "The hit reaction check failed", error);
            return false;
        }
    }

    // Stuns, shocks and lasers each start in one of these three. Skipping the call skips the stun.
    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.Stunned))]
    static void Stunned(RPlayerControllerCombat self, bool dontPlaySfx)
    {
        try
        {
            if (!NoStuns.On)
            {
                self.Stunned(dontPlaySfx);
            }
        }
        catch (Exception error)
        {
            Log.Error("status.stunned", "A stun failed part way", error);
        }
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.Zapped))]
    static void Zapped(RPlayerControllerCombat self)
    {
        try
        {
            if (!NoStuns.On)
            {
                self.Zapped();
            }
        }
        catch (Exception error)
        {
            Log.Error("status.zapped", "A shock failed part way", error);
        }
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.Lasered))]
    static void Lasered(RPlayerControllerCombat self)
    {
        try
        {
            if (!NoStuns.On)
            {
                self.Lasered();
            }
        }
        catch (Exception error)
        {
            Log.Error("status.lasered", "A laser hit failed part way", error);
        }
    }

    static int Incoming(RPawnPlayer pawn, int damage, Class damageType)
    {
        // Kill zones have to kill, or the player is left stuck out of bounds.
        if (damage <= 0 || IsKillZone(damageType))
        {
            return damage;
        }

        var scaled = damage;
        if (!DamageTaken.IsDefault)
        {
            // Fractions are carried over to the next hit, so a low setting still adds up over
            // many small hits and doesn't round them all down to nothing.
            _damageOwed += damage * DamageTaken.Value;
            scaled = (int)_damageOwed;
            _damageOwed -= scaled;
        }

        if (CantDie.On)
        {
            // The game only ever lowers damage after this point, so this can't be undone. It
            // uses the same cap itself for moves Batman can't die in.
            var health = pawn.Health;
            if (health > 0)
            {
                scaled = Math.Min(scaled, health - 1);
            }
        }

        return Math.Max(scaled, 0);
    }

    static bool IsKillZone(Class damageType)
    {
        if (damageType is null)
        {
            return false;
        }

        foreach (var type in damageType.EnumerateSupersAndSelf())
        {
            if (type.Name.Index == KillZone.Index)
            {
                return true;
            }
        }

        return false;
    }

    // The game's own god mode. Every playable character and the Batmobile check the controller's
    // flag, and a hit is dropped before it does anything.
    static void KeepGodMode()
    {
        if (World.Controller is { } controller)
        {
            GodFlag.Hold(controller, true);
        }

        if (World.Player is { Health: > 0 } pawn && pawn.Health < pawn.MaxHealth)
        {
            pawn.Health = pawn.MaxHealth;
            pawn.HealthUpdated();
        }
    }

    // The game's own cheat. It also tops up the armour and the Batmobile.
    static Outcome Heal()
    {
        if (World.Player is not { Health: > 0 } pawn)
        {
            return Outcome.NoPlayer;
        }

        Game.GetCheatManager().RestoreHealth();

        // The cheat doesn't tell the HUD or the save.
        pawn.HealthUpdated();
        return Outcome.Done();
    }

    // Anything that lowers health starts the wait again, not just hits.
    static void Regen()
    {
        if (World.Player is not { } pawn)
        {
            return;
        }

        var health = pawn.Health;
        var most = pawn.MaxHealth;
        if (pawn.Ptr != _regenPawn || health < _regenLastHealth)
        {
            _regenPawn = pawn.Ptr;
            _regenCalm = 0f;
            _regenOwed = 0f;
        }

        _regenLastHealth = health;
        if (health <= 0 || most <= 0 || health >= most)
        {
            return;
        }

        _regenCalm += World.Delta;
        if (_regenCalm < RegenDelaySeconds)
        {
            return;
        }

        _regenOwed += most * (float)(HealthRegen.Value / 100.0) * World.Delta;
        var whole = (int)_regenOwed;
        if (whole < 1)
        {
            return;
        }

        _regenOwed -= whole;
        pawn.Health = Math.Min(most, health + whole);
        _regenLastHealth = pawn.Health;

        // The game saves the player's health and tells the HUD from here.
        pawn.HealthUpdated();
    }

    // Covers switching the option on, a new suit of armour, and coming back from a reload.
    static void KeepArmourFull()
    {
        _armourCheckIn -= World.Delta;
        if (_armourCheckIn > 0f)
        {
            return;
        }

        _armourCheckIn = ArmourCheckSeconds;
        if (World.Player is { } pawn)
        {
            FillArmour(pawn, grant: true);
        }
    }

    static Outcome TopUpArmour()
    {
        if (World.Player is not { Health: > 0 } pawn)
        {
            return Outcome.NoPlayer;
        }

        var maxima = pawn.CurrentArmourMaxima;
        var owned = false;
        for (var i = 0; i < Math.Min(ArmourPools, maxima.Count); i++)
        {
            owned |= maxima[i] > 0;
        }

        if (!owned)
        {
            return Outcome.Failed("NO ARMOUR");
        }

        return FillArmour(pawn, grant: false) ? Outcome.Done() : Outcome.Unchanged("FULL");
    }

    // There are two pools, one for melee and one for bullets. A pool the player hasn't bought
    // has a maximum of -1. With grant, it is given the size of the best upgrade. Returns whether
    // anything changed.
    static bool FillArmour(RPawnPlayer pawn, bool grant)
    {
        var levels = pawn.CurrentArmourLevels;
        var maxima = pawn.CurrentArmourMaxima;
        var pools = Math.Min(ArmourPools, Math.Min(levels.Count, maxima.Count));
        var changed = false;

        for (var i = 0; i < pools; i++)
        {
            var type = (RPawnPlayer.EArmourType)i;
            var most = maxima[i];
            if (most <= 0)
            {
                if (!grant)
                {
                    continue;
                }

                most = BestUpgrade(i == 0 ? pawn.MeleeArmourUpgrades : pawn.BallisticArmourUpgrades);
                pawn.SetArmourMaximum(type, most);
                changed = true;
            }

            if (levels[i] < most)
            {
                pawn.SetArmourCurrent(type, most);
                changed = true;
            }
        }

        return changed;
    }

    static int BestUpgrade(TArray<int> upgrades)
    {
        // 100 is what the game's own debug command grants.
        const int fallback = 100;
        var best = 0;
        for (var i = 0; i < upgrades.Count; i++)
        {
            best = Math.Max(best, upgrades[i]);
        }

        return best > 0 ? best : fallback;
    }

    // The game works the armour out again from the upgrades the player really owns. It is the
    // call the game makes itself after an upgrade is bought, and it leaves health alone.
    static void RecountArmour()
    {
        World.Player?.UpdateMaximumHealthAndArmour(false, false);
    }
}
