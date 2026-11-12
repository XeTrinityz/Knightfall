#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace Knightfall;

// PLAYER > GADGETS. Values are held on the gadgets that read them and put back exactly. Upgrade
// grants are undone by asking the game to count the upgrades the player owns again.
static class Gadgets
{
    const string TankBlastPath = "VFX_Vehicles.Particles.Tank.HeavyTank_Explosion";
    const float TankBlastRetrySeconds = 5f;
    const float HackSolveSeconds = 0.75f;
    const float GelPush = 2000f;

    static readonly FName RecentGelDamage = new("RecentGelDamage");
    static readonly FName SmokeScreenOver = new("SmokeScreenOver");

    static readonly Held<RInventoryGadget, bool> RechargeFlag = new("gadget recharge", gadget => gadget.bCanEquipWhileRecharging, (gadget, on) => gadget.bCanEquipWhileRecharging = on);
    static readonly Held<RInventoryGadget, bool> VentFlag = new("gadget vents", gadget => gadget.bUsableFromCrawlSpace, (gadget, on) => gadget.bUsableFromCrawlSpace = on);
    static readonly Held<RInventoryGadget, bool> CoverFlag = new("gadget cover", gadget => gadget.bUsableFromCornerCover, (gadget, on) => gadget.bUsableFromCornerCover = on);
    static readonly Held<RInventoryGadget, bool> VantageFlag = new("gadget vantage", gadget => gadget.bUsableFromVantagePoints, (gadget, on) => gadget.bUsableFromVantagePoints = on);
    static readonly Held<RInventoryGadget, bool> PredatorFlag = new("gadget predator", gadget => gadget.bCanUseFromSilentPredator, (gadget, on) => gadget.bCanUseFromSilentPredator = on);

    static readonly Held<RExplosiveGooMine, bool> MineLethal = new("gel mine lethal", mine => mine.bLethalUpgrade, (mine, on) => mine.bLethalUpgrade = on);
    static readonly Held<RExplosiveGooMine, int> MineDamage = new("gel mine damage", mine => mine.GelDamage, (mine, damage) => mine.GelDamage = damage);
    static readonly Held<RExplosiveGelBomb, float> BombRadius = new("gel radius", bomb => bomb.ExplosionRadius, (bomb, radius) => bomb.ExplosionRadius = radius);
    static readonly Held<RExplosiveGooMine, float> MineRadius = new("gel mine radius", mine => mine.ExplosionRadius, (mine, radius) => mine.ExplosionRadius = radius);
    static readonly Held<RFreezeSpray, float> ClusterRadius = new("freeze cluster radius", spray => spray.fFreezeClusterBlastRadius, (spray, radius) => spray.fFreezeClusterBlastRadius = radius);
    static readonly Held<RHarpoonGun, float> ClawDefault = new("batclaw range", gun => gun.HarpoonRange, (gun, range) => gun.HarpoonRange = range);
    static readonly Held<RLevelVolume, float> ClawOverride = new("batclaw area range", volume => volume.OverrideBatclawRange, (volume, range) => volume.OverrideBatclawRange = range);
    static readonly Held<RBMWeaponRanged, ParticleSystem> JammedBlast = new("jammed gun blast", weapon => weapon.JammedExplosionVFX, (weapon, blast) => weapon.JammedExplosionVFX = blast);

    static ParticleSystem? _tankBlast;
    static float _tankBlastLookedAt = float.MinValue;
    static nint _hack;
    static float _hackSince;

    public static readonly Command UnlockAll = new(
        "player.gadgets.unlock_all", "UNLOCK ALL GADGETS",
        "Puts every gadget on your gadget wheel.",
        Unlock);

    public static readonly Toggle UseWhileRecharging = new(
        "player.gadgets.use_while_recharging", "USE WHILE RECHARGING",
        "Gadgets can be picked and used again while they are still recharging.")
    {
        Tick = () => HoldGadgets(RechargeFlag),
        Restore = RechargeFlag.Release,
    };

    public static readonly Toggle VentsAndCover = new(
        "player.gadgets.vents_and_cover", "GADGETS IN VENTS AND COVER",
        "Use every gadget from vents, corner cover, vantage points and silent predator.")
    {
        Tick = () => HoldGadgets(VentFlag, CoverFlag, VantageFlag, PredatorFlag),
        Restore = () =>
        {
            VentFlag.Release();
            CoverFlag.Release();
            VantageFlag.Release();
            PredatorFlag.Release();
        },
    };

    public static readonly Toggle UnlimitedDisruptor = new(
        "player.gadgets.unlimited_disruptor", "UNLIMITED DISRUPTOR CHARGES",
        "The disruptor never runs out, so you can sabotage every gun, mine, drone and medic in an area.")
    {
        Tick = RefillDisruptor,
    };

    public static readonly Toggle DisruptAnything = new(
        "player.gadgets.disrupt_anything", "DISRUPT ANY TARGET",
        "The disruptor works on every target without its upgrades. Drones, mines, medic packs, stun sticks, cloaked thugs and more.")
    {
        Tick = GrantDisruptorTargets,
        Restore = RecountDisruptorTargets,
    };

    public static readonly Toggle AutoPasswordHack = new(
        "player.gadgets.auto_password_hack", "AUTO PASSWORD HACK",
        "Password cracks with the remote hacking device solve themselves a moment after they start.");

    public static readonly Toggle LethalGel = new(
        "player.gadgets.lethal_gel", "LETHAL EXPLOSIVE GEL",
        "A thug with gel stuck to him is knocked out when it blows, anywhere and as often as you like. Vantage point mines do too. Brutes and bosses shrug it off.")
    {
        Tick = () => HoldMines(MineLethal, true),
        Restore = MineLethal.Release,
    };

    public static readonly Slider GelDamage = new(
        "player.gadgets.gel_damage", "GEL DAMAGE",
        "Health a gel blast takes off every thug caught in it. A street thug has about 25. OFF is the normal game, where the blast only knocks them down.",
        min: 0, max: 200, step: 5, value: 0, unit: " HP", zero: "OFF")
    {
        Tick = () => HoldMines(MineDamage, (int)GelDamage!.Value),
        Restore = MineDamage.Release,
    };

    public static readonly Slider GelRadius = new(
        "player.gadgets.gel_radius", "GEL BLAST RADIUS",
        "How far a gel blast reaches, for charges and vantage point mines alike.",
        min: 0.5, max: 4, step: 0.25, value: 1, unit: "X")
    {
        Tick = ScaleGelRadius,
        Restore = () =>
        {
            BombRadius.Release();
            MineRadius.Release();
        },
    };

    public static readonly Toggle AllFreezeUpgrades = new(
        "player.gadgets.all_freeze_upgrades", "ALL FREEZE BLAST UPGRADES",
        "The freeze blast acts as if every upgrade is bought. Cluster blast, longer freeze and proximity triggers.")
    {
        Tick = GrantFreezeUpgrades,
        Restore = RecountFreezeUpgrades,
    };

    public static readonly Slider FreezeClusterRadius = new(
        "player.gadgets.freeze_cluster_radius", "FREEZE CLUSTER RADIUS",
        "How wide the freeze blast's cluster spreads, once you have the cluster upgrade.",
        min: 0.5, max: 4, step: 0.25, value: 1, unit: "X")
    {
        Tick = ScaleFreezeCluster,
        Restore = ClusterRadius.Release,
    };

    public static readonly Slider BatclawRange = new(
        "player.gadgets.batclaw_range", "BATCLAW RANGE",
        "How far the Batclaw reaches, for thugs, vents and everything else it grabs.",
        min: 0.5, max: 5, step: 0.25, value: 1, unit: "X")
    {
        Tick = ScaleBatclaw,
        Restore = () =>
        {
            ClawDefault.Release();
            ClawOverride.Release();
        },
    };

    public static readonly Slider SmokeDuration = new(
        "player.gadgets.smoke_duration", "SMOKE CLOUD DURATION",
        "How long the cloud from your smoke pellet lasts. Upgrades still add their bit on top.",
        min: 0.5, max: 5, step: 0.25, value: 1, unit: "X");

    public static readonly Choice JammedGunBlast = new(
        "player.gadgets.jammed_gun_blast", "JAMMED GUN BLAST",
        "How a gun you jammed with the disruptor blows up. TANK uses the drone tank explosion, once drone tanks have been nearby.",
        new[] { ("normal", "NORMAL"), ("tank", "TANK") })
    {
        Tick = UseTankBlast,
        Restore = JammedBlast.Release,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("USE", UnlockAll, UseWhileRecharging, VentsAndCover),
        new("DISRUPTOR", UnlimitedDisruptor, DisruptAnything),
        new("HACKING", AutoPasswordHack),
        new("EXPLOSIVE GEL", LethalGel, GelDamage, GelRadius),
        new("FREEZE BLAST", AllFreezeUpgrades, FreezeClusterRadius),
        new("BATCLAW", BatclawRange, ZipKick.Option),
        new("EFFECTS", SmokeDuration, JammedGunBlast),
    };

    // The game's cheat gives to whatever the controller is driving, so in the Batmobile it arms the
    // car and gives the character nothing. The car has ARM EVERY WEAPON for that, so here the
    // character's own version is called, as the cheat does on foot. BmSDK wraps the pawn as its
    // real class, so this reaches that character's override.
    static Outcome Unlock()
    {
        if (World.Player is not { } pawn || World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        pawn.DebugGiveAllGadgets();
        controller.GadgetsUpdated();
        return Outcome.Done();
    }

    static void HoldGadgets(params Held<RInventoryGadget, bool>[] flags)
    {
        foreach (var gadget in Game.FindObjects<RInventoryGadget>())
        {
            if (!gadget.IsValid)
            {
                continue;
            }

            foreach (var flag in flags)
            {
                flag.Hold(gadget, true);
            }
        }
    }

    static IEnumerable<RDisruptorSniper> Disruptors()
    {
        foreach (var disruptor in Game.FindObjects<RDisruptorSniper>())
        {
            if (disruptor.IsValid && !disruptor.bDeleteMe)
            {
                yield return disruptor;
            }
        }
    }

    // ReduceAmmo is skipped while this is on, so this only fills it once, and again for a new
    // disruptor.
    static void RefillDisruptor()
    {
        foreach (var disruptor in Disruptors())
        {
            if (disruptor.iAntiEquipmentMaximumAmmo > 0 && disruptor.iAntiEquipmentRemainingAmmo < disruptor.iAntiEquipmentMaximumAmmo)
            {
                disruptor.FullyRestoreAmmo();
            }
        }
    }

    [Redirect(typeof(RDisruptorSniper), nameof(RDisruptorSniper.ReduceAmmo))]
    static void ReduceAmmo(RDisruptorSniper self)
    {
        if (!UnlimitedDisruptor.On)
        {
            Redirects.RunOriginal();
        }
    }

    static void SetDisruptorTargets(RDisruptorSniper disruptor, bool on)
    {
        disruptor.bBatmanHasDroneDisruptorUpgrade = on;
        disruptor.bBatmanHasMineDisruptorUpgrade = on;
        disruptor.bBatmanHasMedicDisruptorUpgrade = on;
        disruptor.bBatmanHasStunStickDisruptorUpgrade = on;
        disruptor.bBatmanHasInvisibleThugDisruptorUpgrade = on;
        disruptor.bBatmanHasSentryGunDispenserDisruptorUpgrade = on;
        disruptor.bBatmanHasMinidroneControllerDisruptorUpgrade = on;
        disruptor.bBatmanHasDetectiveModeJammerDisruptorUpgrade = on;
        disruptor.bBatmanHasDetectiveModeDetectorDisruptorUpgrade = on;
        disruptor.bBatmanHasRHDBlindDroneUpgrade = on;
    }

    static void GrantDisruptorTargets()
    {
        foreach (var disruptor in Disruptors())
        {
            if (!disruptor.bBatmanHasDroneDisruptorUpgrade || !disruptor.bBatmanHasRHDBlindDroneUpgrade || !disruptor.bBatmanHasMedicDisruptorUpgrade)
            {
                SetDisruptorTargets(disruptor, true);
            }
        }
    }

    // The game's own count only ever turns these on, so they are cleared first.
    static void RecountDisruptorTargets()
    {
        foreach (var disruptor in Disruptors())
        {
            SetDisruptorTargets(disruptor, false);
            disruptor.UpdateDisruptorUpgrades();
        }
    }

    // The minigame movie owns the passwords. When the last one is found it makes these two calls,
    // and the special move takes it from there.
    [Redirect("BmGame.RSpecialMoveInstance_OmnitronPasswordCracking:Resonating:UpdateSpecialMove", typeof(RSpecialMoveInstance_OmnitronPasswordCracking))]
    static bool UpdatePasswordCrack(RSpecialMoveInstance_OmnitronPasswordCracking self, float deltaTime)
    {
        var result = Redirects.RunOriginal<bool>();
        try
        {
            if (AutoPasswordHack.On
                && self.TheOmnitronMovie is { IsValid: true, MinigameComplete: false } movie
                && self.PlayerPawn is { IsValid: true } pawn
                && pawn.Ptr == World.Player?.Ptr)
            {
                if (self.Ptr != _hack)
                {
                    _hack = self.Ptr;
                    _hackSince = World.Now;
                }
                else if (World.Now - _hackSince >= HackSolveSeconds)
                {
                    _hack = 0;
                    movie.XI_PasswordFound();
                    movie.XI_OutroComplete();
                }
            }
        }
        catch (Exception error)
        {
            Log.Error("gadgets.password_hack", "The password hack could not be solved", error);
        }

        return result;
    }

    static void HoldMines<TValue>(Held<RExplosiveGooMine, TValue> held, TValue value)
    {
        foreach (var mine in Game.FindObjects<RExplosiveGooMine>())
        {
            if (mine.IsValid && !mine.bDeleteMe)
            {
                held.Hold(mine, value);
            }
        }
    }

    static void ScaleGelRadius()
    {
        var scale = (float)GelRadius.Value;
        foreach (var bomb in Game.FindObjects<RExplosiveGelBomb>())
        {
            if (bomb.IsValid && !bomb.bDeleteMe)
            {
                BombRadius.Hold(bomb, was => was * scale);
            }
        }

        foreach (var mine in Game.FindObjects<RExplosiveGooMine>())
        {
            if (mine.IsValid && !mine.bDeleteMe)
            {
                MineRadius.Hold(mine, was => was * scale);
            }
        }
    }

    // The game builds the hit for every thug in the blast, then sets its damage to 0, so a gel
    // charge never hurts anyone. The ones it really reached have its RecentGelDamage timer just
    // started, and those get the hit here.
    [Redirect(typeof(RExplosiveGelBomb), nameof(RExplosiveGelBomb.DamageVictim))]
    static void DamageVictim(RExplosiveGelBomb self, RPawnVillain victim)
    {
        Redirects.RunOriginal();
        try
        {
            if (!LethalGel.On && GelDamage.IsDefault)
            {
                return;
            }

            if (!victim.IsValid || victim.bGoodAsDead || victim.Health <= 0 || !CanBeGelled(victim))
            {
                return;
            }

            var started = victim.GetTimerCount(RecentGelDamage, null);
            if (started < 0f || started > 0.001f)
            {
                return;
            }

            var stuck = self.StuckOnPawn is { IsValid: true } target && target.Ptr == victim.Ptr;
            var damage = LethalGel.On && stuck ? victim.Health + 100 : (int)GelDamage.Value;
            if (damage <= 0)
            {
                return;
            }

            var push = Vector3.Normalize(victim.Location - self.Location + Vector3.UnitZ) * GelPush;
            victim.TakeDamage(damage, self.GooSprayer?.Controller!, victim.Location, push, RDmgType_ExplosiveGel.StaticClass(), default, self);
        }
        catch (Exception error)
        {
            Log.Error("gadgets.gel", "A gel blast could not deal its damage", error);
        }
    }

    // Brutes, robots, the Riddler's mech and bosses stay immune, as they are to the game's own gel.
    static bool CanBeGelled(RPawnVillain victim) => victim.CombatPawnType is
        RPawnCombat.ECombatPawnType.ECPT_Thug
        or RPawnCombat.ECombatPawnType.ECPT_ToughThug
        or RPawnCombat.ECombatPawnType.ECPT_Lieutenant
        or RPawnCombat.ECombatPawnType.ECPT_Venom
        or RPawnCombat.ECombatPawnType.ECPT_Ninja
        or RPawnCombat.ECombatPawnType.ECPT_Outcast
        or RPawnCombat.ECombatPawnType.ECPT_Tonfa;

    static IEnumerable<RFreezeSpray> FreezeSprays()
    {
        foreach (var spray in Game.FindObjects<RFreezeSpray>())
        {
            if (spray.IsValid && !spray.bDeleteMe)
            {
                yield return spray;
            }
        }
    }

    // The cluster upgrade is also what lets the cluster be thrown, through bCanFireSecondary.
    static void GrantFreezeUpgrades()
    {
        foreach (var spray in FreezeSprays())
        {
            if (!spray.bPlayerHasFreezeClusterUpgrade || !spray.bCanFireSecondary || !spray.bPlayerHasFreezeClusterProximityUpgrade
                || !spray.bPlayerHasFreezeDurationUpgrade || !spray.bPlayerHasFreezeGrenadeProximityUpgrade)
            {
                spray.bPlayerHasFreezeClusterUpgrade = true;
                spray.bCanFireSecondary = true;
                spray.bPlayerHasFreezeClusterProximityUpgrade = true;
                spray.bPlayerHasFreezeDurationUpgrade = true;
                spray.bPlayerHasFreezeGrenadeProximityUpgrade = true;
            }
        }
    }

    // Like the disruptor, the game's count only turns these on.
    static void RecountFreezeUpgrades()
    {
        foreach (var spray in FreezeSprays())
        {
            spray.bPlayerHasFreezeClusterUpgrade = false;
            spray.bPlayerHasFreezeClusterProximityUpgrade = false;
            spray.bPlayerHasFreezeDurationUpgrade = false;
            spray.bPlayerHasFreezeGrenadeProximityUpgrade = false;
            spray.UpdateFreezeGrenadeUpgrades();
        }
    }

    static void ScaleFreezeCluster()
    {
        var scale = (float)FreezeClusterRadius.Value;
        foreach (var spray in FreezeSprays())
        {
            ClusterRadius.Hold(spray, was => was * scale);
        }
    }

    // Each time it is aimed, the Batclaw takes its range from its class default, or from the
    // area's own range if it has one. So those are scaled and the game works out the rest.
    static void ScaleBatclaw()
    {
        var scale = (float)BatclawRange.Value;
        foreach (var gun in Game.FindObjects<RHarpoonGun>())
        {
            if (gun.IsValid && gun.Class.DefaultObject is RHarpoonGun { IsValid: true } defaults)
            {
                ClawDefault.Hold(defaults, was => was * scale);
            }
        }

        if (Game.TryGetWorldInfo(out var world) && world.GRI is RGameRI { CurrentLevelVolume: { IsValid: true } area })
        {
            ClawOverride.Hold(area, was => was > 0f ? was * scale : was);
        }
    }

    // Spawned at its default length, which PostBeginPlay times. InitSmoke then adds the upgrade.
    [Redirect(typeof(RSmokeScreen), nameof(RSmokeScreen.InitSmoke))]
    static void InitSmoke(RSmokeScreen self, bool hitWall, bool smokePellet)
    {
        Redirects.RunOriginal();
        try
        {
            if (smokePellet && !SmokeDuration.IsDefault && self.IsValid)
            {
                self.SmokeScreenDuration *= (float)SmokeDuration.Value;
                self.SetTimer(self.SmokeScreenDuration, false, SmokeScreenOver, false, null);
            }
        }
        catch (Exception error)
        {
            Log.Error("gadgets.smoke", "The smoke cloud length could not be changed", error);
        }
    }

    // The tank effect is only loaded once drone tanks have streamed in, so it is looked for again
    // now and then until it turns up.
    static void UseTankBlast()
    {
        if (_tankBlast is not { IsValid: true })
        {
            if (World.Now - _tankBlastLookedAt < TankBlastRetrySeconds && World.Now >= _tankBlastLookedAt)
            {
                return;
            }

            _tankBlastLookedAt = World.Now;
            _tankBlast = Game.FindObject<ParticleSystem>(TankBlastPath);
            if (_tankBlast is not { IsValid: true })
            {
                return;
            }
        }

        foreach (var weapon in Game.FindObjects<RBMWeaponRanged>())
        {
            if (weapon.IsValid)
            {
                JammedBlast.Hold(weapon, _tankBlast);
            }
        }
    }
}
