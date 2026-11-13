#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace Knightfall;

// PLAYER > COMBAT. Switches the game already has are held on the objects that read them and put
// back exactly as they were. The rest steer the game's own combat calls as they happen.
static class Combat
{
    // A combo is still reset for these, so it banks when a fight ends or Batman leaves it.
    static readonly HashSet<string> BankingReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "Combat xp spawned", "Possessed Bmbl", "Enter Batmobile", "Entering Bmbl", "Interrogating", "Interrogation",
    };

    static readonly Held<RPawnCombat, bool> HeavyFlag = new("heavy counters", pawn => pawn.bForceHeavyCounter, (pawn, on) => pawn.bForceHeavyCounter = on);
    static readonly Held<RPawnCombat, bool> QuickKoFlag = new("last enemy", pawn => pawn.bQuickKOIfLastEnemy, (pawn, on) => pawn.bQuickKOIfLastEnemy = on);
    static readonly Held<RPawnCombat, bool> RagdollFlag = new("ragdoll hits", pawn => pawn.bAlwaysRagdollWhenHit, (pawn, on) => pawn.bAlwaysRagdollWhenHit = on);
    static readonly Held<RBMWeapon, bool> DropFlag = new("weapon drop", weapon => weapon.DropOnFirstHit, (weapon, on) => weapon.DropOnFirstHit = on);
    static readonly Held<RPawnPlayerCombat, bool> SuitFlag = new("batsuit", pawn => pawn.bUpgradedBatsuit, (pawn, on) => pawn.bUpgradedBatsuit = on);

    // Shared with AUTO FIGHT, which keeps special takedowns ready during a fight.
    static readonly Held<RPersistentDebugData, bool> TakedownFlag = new("special takedowns", data => data.Debug_Combat_CanAlwaysTakedown, (data, on) => data.Debug_Combat_CanAlwaysTakedown = on);

    const float FearCheckSeconds = 0.25f;

    static RPersistentDebugData? _debugData;
    static float _fearCheckIn;

    public static readonly Toggle DodgeGunfire = new(
        "player.combat.dodge_gunfire", "AUTO DODGE GUNFIRE",
        "Gunmen still shoot at you, but every shot misses.");

    public static readonly Toggle ComboNeverResets = new(
        "player.combat.combo_never_resets", "COMBO NEVER RESETS",
        "Getting hit, missing or waiting too long doesn't break your combo in a fight. It still banks when the fight ends.");

    public static readonly Slider ComboMultiplier = new(
        "player.combat.combo_multiplier", "COMBO MULTIPLIER",
        "Each hit adds this many to your combo and fills the special takedown meter this much faster.",
        min: 1, max: 10, step: 1, value: 1, unit: "X");

    public static readonly Toggle Batsuit = PlayerFlag(
        "batsuit_v804", "BATSUIT V8.04",
        "Gives you the upgraded batsuit early, with fear multi-takedowns and its other combat upgrades.",
        SuitFlag);

    public static readonly Toggle FreeflowFocus = DebugFlag(
        "freeflow_focus", "FREEFLOW FOCUS ANYTIME",
        "Freeflow focus is ready in every fight, with no combo needed.",
        data => data.Debug_Combat_AlwaysFreeflowFocusMode, (data, on) => data.Debug_Combat_AlwaysFreeflowFocusMode = on);

    public static readonly Toggle HeavyCounters = new(
        "player.combat.heavy_counters", "HEAVY COUNTERS",
        "Every counter lands at full strength, even outside a combo, and enemies who would block it can't.")
    {
        Restore = HeavyFlag.Release,
    };

    public static readonly Toggle SlowMoGroupAttacks = DebugFlag(
        "slowmo_group_attacks", "SLOW-MO GROUP ATTACKS",
        "Time always slows when several enemies attack you together.",
        data => data.Debug_Combat_AlwaysSlowdownSimultaneousAttacks, (data, on) => data.Debug_Combat_AlwaysSlowdownSimultaneousAttacks = on);

    public static readonly Toggle CounterIcons = new(
        "player.combat.counter_icons", "ALWAYS SHOW COUNTER ICONS",
        "Shows the counter warnings over attackers in New Game + and Knightmare too.");

    public static readonly Toggle OneHitKo = new(
        "player.combat.one_hit_ko", "ONE HIT KO",
        "Any hit you land on an enemy knocks them out. Shields and armour still have to come off first.");

    public static readonly Toggle LastEnemyFightsOn = new(
        "player.combat.last_enemy_fights_on", "LAST ENEMY FIGHTS ON",
        "The last enemy takes as many hits as the rest, instead of dropping early for the slow motion finish.")
    {
        Restore = QuickKoFlag.Release,
    };

    public static readonly Toggle RagdollOnEveryHit = new(
        "player.combat.ragdoll_every_hit", "RAGDOLL ON EVERY HIT",
        "Enemies go flying from any hit.")
    {
        Restore = RagdollFlag.Release,
    };

    public static readonly Toggle KnockOffHelmets = DebugFlag(
        "knock_off_helmets", "KNOCK OFF HELMETS",
        "Every strike knocks an armoured enemy's helmet off.",
        data => data.Debug_Combat_bAlwaysRemoveHelmets, (data, on) => data.Debug_Combat_bAlwaysRemoveHelmets = on);

    public static readonly Toggle DropWeaponsWhenHit = new(
        "player.combat.drop_weapons_when_hit", "DROP WEAPONS WHEN HIT",
        "Thugs drop the weapon in their hands the first time you hit them.")
    {
        Restore = DropFlag.Release,
    };

    public static readonly Toggle HideStunStars = DebugFlag(
        "hide_stun_stars", "HIDE STUN STARS",
        "Stunned enemies don't show the circling stars.",
        data => data.Debug_Combat_DisableStunStars, (data, on) => data.Debug_Combat_DisableStunStars = on);

    public static readonly Toggle SpecialTakedownsAnytime = new(
        "player.combat.special_takedowns_anytime", "SPECIAL TAKEDOWNS ANYTIME",
        "Special combo takedowns are ready whenever you want them, with no combo to build.")
    {
        Tick = KeepTakedownsReady,
        Restore = TakedownFlag.Release,
    };

    public static readonly Toggle FearTakedownsAnytime = new(
        "player.combat.fear_takedowns_anytime", "FEAR TAKEDOWNS ANYTIME",
        "Fear takedowns are charged again as soon as you use them, with no silent takedown needed. Needs the V8.04 batsuit.")
    {
        Tick = KeepFearReady,
        Block = static () => PlayerLocks.NewBatsuit ? null : "NEEDS BATSUIT V8.04",
    };

    public static readonly Toggle QuickStealthTakedowns = DebugFlag(
        "quick_stealth_takedowns", "QUICK STEALTH TAKEDOWNS",
        "Sneaking up on a thug always gives the fast knockout the game uses on armoured thugs, never the slow choke hold.",
        data => data.Debug_Player_ForceSilentCombatTakedowns, (data, on) => data.Debug_Player_ForceSilentCombatTakedowns = on);

    public static readonly Toggle ForceEnvironmentTakedowns = DebugFlag(
        "force_environment_takedowns", "FORCE ENVIRONMENT TAKEDOWNS",
        "Takedowns use a nearby wall or object whenever there is one.",
        data => data.Debug_Combat_ForceEnvironmentUse, (data, on) => data.Debug_Combat_ForceEnvironmentUse = on);

    public static readonly Toggle DualTakedowns = DebugFlag(
        "dual_takedowns", "ALWAYS ALLOW DUAL TAKEDOWNS",
        "With a partner beside you, dual takedowns are always on offer.",
        data => data.Debug_Combat_CanAlwaysDualPlay, (data, on) => data.Debug_Combat_CanAlwaysDualPlay = on);

    public static readonly Catalog.Section[] Sections =
    {
        new("AUTOMATIC", AutoFight.Option, AutoDefence.AutoCounter, AutoDefence.AutoDodgeRed, DodgeGunfire),
        new("FIGHTING", ComboNeverResets, ComboMultiplier, Batsuit, FreeflowFocus, HeavyCounters, SlowMoGroupAttacks, CounterIcons),
        new("YOUR HITS", OneHitKo, SuperPunch.Mode, SuperPunch.Force, LastEnemyFightsOn, RagdollOnEveryHit, KnockOffHelmets, DropWeaponsWhenHit, HideStunStars),
        new("TAKEDOWNS", SpecialTakedownsAnytime, FearTakedownsAnytime, QuickStealthTakedowns, ForceEnvironmentTakedowns, DualTakedowns),
    };

    // A flag on the game's debug settings, the same switches its own debug menu flips.
    static Toggle DebugFlag(string key, string label, string help, Func<RPersistentDebugData, bool> read, Action<RPersistentDebugData, bool> write)
    {
        var held = new Held<RPersistentDebugData, bool>(key.Replace('_', ' '), read, write);
        return new Toggle("player.combat." + key, label, help)
        {
            Tick = () =>
            {
                if (DebugData() is { } data)
                {
                    held.Hold(data, true);
                }
            },
            Restore = held.Release,
        };
    }

    static Toggle PlayerFlag(string key, string label, string help, Held<RPawnPlayerCombat, bool> held)
    {
        return new Toggle("player.combat." + key, label, help)
        {
            Tick = () =>
            {
                if (World.Player is { } pawn)
                {
                    held.Hold(pawn, true);
                }
            },
            Restore = held.Release,
        };
    }

    public static void KeepTakedownsReady()
    {
        if (DebugData() is { } data)
        {
            TakedownFlag.Hold(data, true);
        }
    }

    // The game's own recharge, the call a silent takedown ends with. Never in the middle of a
    // chain, which counts its targets down from the same number.
    static void KeepFearReady()
    {
        _fearCheckIn -= World.Delta;
        if (_fearCheckIn > 0f)
        {
            return;
        }

        _fearCheckIn = FearCheckSeconds;
        if (World.Controller is not { bPerformingFearTakedown: false } controller
            || controller.OriginalPawn is not { IsValid: true, bUpgradedBatsuit: true })
        {
            return;
        }

        if (controller.iNumberOfRemainingFearTakedowns < controller.GetMaximumNumberOfFearTakedowns()
            || !controller.DoesPlayerHaveEnoughEnergyForAFearTakedown())
        {
            controller.ResetNumberOfRemainingFearTakedownsToFull();
        }
    }

    // AUTO FIGHT's fight is over. The player's own switch keeps them ready if it is on.
    public static void ReleaseTakedowns()
    {
        if (!SpecialTakedownsAnytime.On)
        {
            TakedownFlag.Release();
        }
    }

    // The game asks the engine for this object every time it checks a flag.
    public static RPersistentDebugData? DebugData()
    {
        if (_debugData is not { IsValid: true })
        {
            _debugData = _Engine.GetDebugData() as RPersistentDebugData;
        }

        return _debugData is { IsValid: true } ? _debugData : null;
    }

    // One pass over the enemies for every switch that lives on them. New enemies are caught on
    // the tick they appear, before they can fight.
    public static void HoldEnemies()
    {
        var heavy = HeavyCounters.On;
        var fightOn = LastEnemyFightsOn.On;
        var ragdoll = RagdollOnEveryHit.On;
        var drop = DropWeaponsWhenHit.On;
        if (!heavy && !fightOn && !ragdoll && !drop)
        {
            return;
        }

        foreach (var villain in Game.FindObjects<RPawnVillain>())
        {
            if (!villain.IsValid || villain.bDeleteMe)
            {
                continue;
            }

            // A thug recruited as a bodyguard is on the player's side from then on.
            if (Bodyguards.Is(villain))
            {
                HeavyFlag.Release(villain);
                QuickKoFlag.Release(villain);
                RagdollFlag.Release(villain);
                if (villain.CurrWeapon is { IsValid: true } carried)
                {
                    DropFlag.Release(carried);
                }

                continue;
            }

            if (heavy)
            {
                HeavyFlag.Hold(villain, true);
            }

            if (fightOn)
            {
                QuickKoFlag.Hold(villain, false);
            }

            if (ragdoll)
            {
                RagdollFlag.Hold(villain, true);
            }

            if (drop && villain.CurrWeapon is { IsValid: true } weapon)
            {
                DropFlag.Hold(weapon, true);
            }
        }
    }

    static bool IsPlayer(Actor? actor)
    {
        if (actor is not { IsValid: true })
        {
            return false;
        }

        return actor.Ptr == World.Player?.Ptr || actor.Ptr == World.Driving?.Ptr;
    }

    // Every reset goes through here with a reason. Inside a fight only the ones that bank the
    // combo are let through.
    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.ClearCombo))]
    static void ClearCombo(RPlayerControllerCombat self, FString reason, bool forceDropWeapon)
    {
        var keep = false;
        try
        {
            keep = ComboNeverResets.On && self.ComboSize > 0 && !BankingReasons.Contains(reason.ToString() ?? "") && self.IsInCombat(false);
        }
        catch (Exception error)
        {
            Log.Error("combat.combo_reset", "The combo reset check failed, so the combo was reset", error);
        }

        if (!keep)
        {
            Redirects.RunOriginal();
        }
    }

    // The game adds its own amount first. Whatever it added to the combo and the special meter is
    // then added again for each extra step of the multiplier.
    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.IncCombo))]
    static void IncCombo(RPlayerControllerCombat self, RPawnCombat pawnAttacked, int incAmount, int incSpecialMoveCount, bool noBatmobileTakedown)
    {
        var extra = (int)ComboMultiplier.Value - 1;
        if (extra <= 0)
        {
            Redirects.RunOriginal();
            return;
        }

        var combo = self.ComboSize;
        var meter = self.AdrenalineValue;
        Redirects.RunOriginal();

        try
        {
            Multiply(self, combo, meter, extra);
        }
        catch (Exception error)
        {
            Log.Error("combat.combo_multiplier", "The combo multiplier failed", error);
        }
    }

    static void Multiply(RPlayerControllerCombat controller, int comboBefore, float meterBefore, int extra)
    {
        var reached = controller.ComboSize;
        var gain = reached - comboBefore;
        if (gain <= 0)
        {
            return;
        }

        var total = reached + gain * extra;
        controller.ComboSize = total;
        controller.CummulativeComboAchieved += gain * extra;
        controller.MaxComboSize = Math.Max(controller.MaxComboSize, total);

        // The game only tracks these while a combo is under way, which the first add shows.
        if (controller.CurrMaxComboSize >= reached)
        {
            controller.CurrMaxComboSize = Math.Max(controller.CurrMaxComboSize, total);
            controller.TotalMaxComboSize = Math.Max(controller.TotalMaxComboSize, total);
        }

        // Scripted goals like "reach a combo of 20" listen for this.
        var goals = controller.ComboValueReachedActions;
        for (var i = 0; i < goals.Count; i++)
        {
            if (goals[i] is { IsValid: true } goal)
            {
                goal.ReachedComboValue(reached, total);
            }
        }

        var least = controller.GetComboMinLength();
        if (reached < least && total >= least)
        {
            controller.CombosAchieved++;
            controller.EnterComboFlow();
        }

        // The game's own call caps the meter and plays the "takedown ready" cue.
        var meterGain = controller.AdrenalineValue - meterBefore;
        if (meterGain > 0f)
        {
            controller.IncAdrenaline(meterGain * extra);
        }

        controller.UpdateComboOnHUD();
    }

    // Leaving the enemy on 1 health just before the game works out the hit means any damage
    // knocks them out. A hit that does no damage, like a block, gets their health back. SUPER
    // PUNCH changes the hit itself, so then the game's call is made with the changed hit.
    [Redirect(typeof(RPawnVillain), nameof(RPawnVillain.DamagedBy))]
    static RPawnCombat.DamageResult DamagedBy(RPawnVillain self, RPawnCombat.FDamageInfo dmgInfo)
    {
        var before = 0;
        try
        {
            if (OneHitKo.On && self.Health > 1 && dmgInfo.DamageAmount > 0f && IsPlayer(dmgInfo.Attacker))
            {
                before = self.Health;
                self.Health = 1;
            }
        }
        catch (Exception error)
        {
            Log.Error("combat.one_hit_ko", "ONE HIT KO could not set up the hit", error);
        }

        var launch = System.Numerics.Vector3.Zero;
        try
        {
            launch = SuperPunch.Shape(self, ref dmgInfo);
        }
        catch (Exception error)
        {
            Log.Error("combat.super_punch", "SUPER PUNCH could not set up the hit", error);
        }

        var result = launch == System.Numerics.Vector3.Zero
            ? Redirects.RunOriginal<RPawnCombat.DamageResult>()
            : SuperPunch.Hit(self, dmgInfo, launch);

        if (before > 0 && self.IsValid && self.Health == 1)
        {
            self.Health = before;
        }

        return result;
    }

    // The shot still plays. The game only skips the damage when it is told the shot misses.
    [Redirect(typeof(RPawnVillain), nameof(RPawnVillain.GunTick_Shooting))]
    static void GunTick_Shooting(RPawnVillain self, bool forceMiss, AlertInstance.VisibilityCategory amountVisible, bool canFire)
    {
        var miss = false;
        try
        {
            miss = !forceMiss && DodgeGunfire.On && IsPlayer(self.DamageTarget);
        }
        catch (Exception error)
        {
            Log.Error("combat.gunfire", "The gunfire check failed, so the shot went ahead", error);
        }

        if (miss)
        {
            self.GunTick_Shooting(true, amountVisible, canFire);
        }
        else
        {
            Redirects.RunOriginal();
        }
    }

    // The game hides the icons in New Game +. A scripted scene that turns them off is still obeyed.
    [Redirect(typeof(RBMCombatManager), nameof(RBMCombatManager.CanShowCounterIcons))]
    static bool CanShowCounterIcons(RBMCombatManager self)
    {
        var shown = Redirects.RunOriginal<bool>();
        try
        {
            return shown || (CounterIcons.On && self.DisableCounterIconSequenceAction is not { IsValid: true });
        }
        catch (Exception error)
        {
            Log.Error("combat.counter_icons", "The counter icon check failed", error);
            return shown;
        }
    }
}
