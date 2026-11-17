#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// PLAYER > GADGETS rows that change one value of the disruptor, remote charge, voice synthesizer,
// explosive gel, freeze blast or smoke pellet. Gadget values are held and put back. Values on
// something the game makes for one use are changed as it is made.
static class GadgetTuning
{
    static readonly Held<RDisruptorSniper, int> ChargeLimit = new("disruptor charges", gun => gun.iAntiEquipmentMaximumAmmo, (gun, charges) => gun.iAntiEquipmentMaximumAmmo = charges);
    static readonly Held<RDisruptorSniper, int> MagazineLimit = new("disruptor magazine", gun => gun.MaxAmmo, (gun, charges) => gun.MaxAmmo = charges);
    static readonly Held<RExplosiveGelBomb, float> BombFuse = new("gel fuse", bomb => bomb.TimedExplosiveDelay, (bomb, time) => bomb.TimedExplosiveDelay = time);
    static readonly Held<RExplosiveGelBomb, bool> BombStun = new("gel stun", bomb => bomb.bDurationUpgrade, (bomb, on) => bomb.bDurationUpgrade = on);
    static readonly Held<RExplosiveGooMine, bool> MineStun = new("gel mine stun", mine => mine.bDurationUpgrade, (mine, on) => mine.bDurationUpgrade = on);
    static readonly Held<RFreezeGrenadeCommonProjectile, float> TriggerWidth = new("freeze trigger", grenade => grenade.fProximityCylinderRadius, (grenade, radius) => grenade.fProximityCylinderRadius = radius);

    static readonly Func<int, int> ChargesRaised = static was => was + (int)ExtraCharges!.Value;
    static readonly Func<float, float> FuseScaled = static was => was * (float)GelFuse!.Value;
    static readonly Func<float, float> TriggerScaled = static was => was * (float)FreezeTrigger!.Value;

    static string? NoDisruptor() => Equipment.Missing(static pawn => pawn.DisruptorSniper, "NO DISRUPTOR");

    static string? NoCharge() => Equipment.Missing(static pawn => pawn.MagneticBlast, "NO REMOTE CHARGE");

    static string? NoGel() => Equipment.Missing(static pawn => pawn.GooSpray, "NO EXPLOSIVE GEL");

    static string? NoFreeze() => Equipment.Missing(static pawn => pawn.FreezeSpray, "NO FREEZE BLAST");

    public static readonly Scale<RDisruptorSniper> Reload = new("disruptor reload",
        (tick, restore) => new Slider(
            "player.gadgets.disruptor_reload", "DISRUPTOR RELOAD TIME",
            "How long the disruptor takes to win a charge back. Lower is quicker. 1X is the game's.",
            min: 0.1, max: 2, step: 0.05, value: 1, unit: "X")
        {
            Block = NoDisruptor,
            Tick = tick,
            Restore = restore,
        },
        () => World.Player?.DisruptorSniper,
        Fields.Of<RDisruptorSniper>(gun => gun.ReplenishTime, (gun, value) => gun.ReplenishTime = value));

    public static readonly Scale<RDisruptorSniper> Swap = new("disruptor swap",
        (tick, restore) => new Slider(
            "player.gadgets.disruptor_swap", "DISRUPTOR TARGET SWAP DELAY",
            "How long the disruptor waits before it can switch to another kind of target. NONE switches at once.",
            min: 0, max: 2, step: 0.25, value: 1, unit: "X", zero: "NONE")
        {
            Block = NoDisruptor,
            Tick = tick,
            Restore = restore,
        },
        () => World.Player?.DisruptorSniper,
        Fields.Of<RDisruptorSniper>(gun => gun.fDelayBetweenMagazineChanges, (gun, value) => gun.fDelayBetweenMagazineChanges = value));

    public static readonly Slider ExtraCharges = new(
        "player.gadgets.disruptor_charges", "EXTRA DISRUPTOR CHARGES",
        "More charges in the disruptor. They fill back up as it recharges.",
        min: 0, max: 8, step: 1, value: 0, zero: "OFF")
    {
        Block = static () => NoDisruptor() ?? (Gadgets.UnlimitedDisruptor.On ? "UNLIMITED IS ON" : null),
        Tick = KeepCharges,
        Restore = () =>
        {
            ChargeLimit.Release();
            MagazineLimit.Release();
        },
    };

    public static readonly Scale<RMagneticBlast> ChargeRange = new("remote charge range",
        (tick, restore) => new Slider(
            "player.gadgets.charge_range", "REMOTE CHARGE RANGE",
            "How far the field of a remote charge reaches around where it lands.",
            min: 0.5, max: 4, step: 0.25, value: 1, unit: "X")
        {
            Block = NoCharge,
            Tick = tick,
            Restore = restore,
        },
        () => World.Player?.MagneticBlast,
        Fields.Of<RMagneticBlast>(gun => gun.MagBlastMaxRange, (gun, value) => gun.MagBlastMaxRange = value),
        Fields.Of<RMagneticBlast>(gun => gun.MagBlastCloseRange, (gun, value) => gun.MagBlastCloseRange = value));

    public static readonly Scale<RMagneticBlast> ChargePush = new("remote charge push",
        (tick, restore) => new Slider(
            "player.gadgets.charge_push", "REMOTE CHARGE PUSH",
            "How hard a remote charge throws armoured thugs.",
            min: 0.5, max: 4, step: 0.25, value: 1, unit: "X")
        {
            Block = NoCharge,
            Tick = tick,
            Restore = restore,
        },
        () => World.Player?.MagneticBlast,
        Fields.Of<RMagneticBlast>(gun => gun.ArmouredThugImpulse, (gun, value) => gun.ArmouredThugImpulse = value));

    public static readonly Toggle NoSynthPenalty = new(
        "player.gadgets.no_synth_penalty", "NO SYNTH PENALTY",
        "Taking out a thug you gave an order to no longer puts the voice synthesizer on a long wait, and no longer stops it working in that room.")
    {
        Block = static () => Equipment.Missing(static pawn => pawn.VoiceSynthesiser, "NO VOICE SYNTHESIZER"),
    };

    public static readonly Scale<RGooSpray> GelDelay = new("gel delay",
        (tick, restore) => new Slider(
            "player.gadgets.gel_delay", "GEL DETONATION DELAY",
            "How long the gel waits between blowing one charge and the next. AT ONCE blows every charge together.",
            min: 0, max: 2, step: 0.25, value: 1, unit: "X", zero: "AT ONCE")
        {
            Block = NoGel,
            Tick = tick,
            Restore = restore,
        },
        () => World.Player?.GooSpray,
        Fields.Of<RGooSpray>(gun => gun.FirstExplodeTime, (gun, value) => gun.FirstExplodeTime = value),
        Fields.Of<RGooSpray>(gun => gun.SubsequentExplodeDelay, (gun, value) => gun.SubsequentExplodeDelay = value));

    public static readonly Slider GelFuse = new(
        "player.gadgets.gel_fuse", "GEL FUSE TIME",
        "How long a charge stuck to a thug takes to blow on its own.",
        min: 0.25, max: 4, step: 0.25, value: 1, unit: "X")
    {
        Block = NoGel,
        Tick = KeepFuse,
        Restore = BombFuse.Release,
    };

    public static readonly Toggle LongGelStun = new(
        "player.gadgets.long_gel_stun", "LONG GEL STUN",
        "Thugs caught in a gel blast stay down longer, without the upgrade.")
    {
        Block = NoGel,
        Tick = KeepGelStun,
        Restore = () =>
        {
            BombStun.Release();
            MineStun.Release();
        },
    };

    public static readonly Slider FreezeDuration = new(
        "player.gadgets.freeze_duration", "FREEZE DURATION",
        "How long a thug stays frozen by your freeze blast. 1X is the game's.",
        min: 0.5, max: 5, step: 0.25, value: 1, unit: "X")
    {
        Block = NoFreeze,
    };

    public static readonly Slider FreezeTrigger = new(
        "player.gadgets.freeze_trigger", "FREEZE TRIGGER RADIUS",
        "How close a thug has to get to set off a freeze blast you left on the ground.",
        min: 0.5, max: 4, step: 0.25, value: 1, unit: "X")
    {
        Block = NoFreeze,
        Tick = KeepTrigger,
        Restore = TriggerWidth.Release,
    };

    public static readonly Slider SmokeSize = new(
        "player.gadgets.smoke_size", "SMOKE CLOUD SIZE",
        "How far a smoke pellet's cloud reaches, where it hides you and stuns thugs. The smoke itself looks the same.",
        min: 0.5, max: 3, step: 0.25, value: 1, unit: "X")
    {
        Block = static () => Equipment.Missing(static pawn => pawn.SmokeBomb, "NO SMOKE PELLETS"),
    };

    static void KeepCharges()
    {
        if (World.Player?.DisruptorSniper is not { IsValid: true } gun)
        {
            return;
        }

        ChargeLimit.Hold(gun, ChargesRaised);
        MagazineLimit.Hold(gun, ChargesRaised);
    }

    static IEnumerable<RExplosiveGelBomb> Bombs()
    {
        if (World.Player?.GooSpray?.BombArchetype is { IsValid: true } archetype)
        {
            yield return archetype;
        }

        foreach (var bomb in Game.FindObjects<RExplosiveGelBomb>())
        {
            if (bomb.IsValid)
            {
                yield return bomb;
            }
        }
    }

    static void KeepFuse()
    {
        foreach (var bomb in Bombs())
        {
            BombFuse.Hold(bomb, FuseScaled);
        }
    }

    static void KeepGelStun()
    {
        foreach (var bomb in Bombs())
        {
            BombStun.Hold(bomb, true);
        }

        foreach (var mine in Game.FindObjects<RExplosiveGooMine>())
        {
            if (mine.IsValid)
            {
                MineStun.Hold(mine, true);
            }
        }
    }

    static IEnumerable<RFreezeGrenadeCommonProjectile> Grenades()
    {
        if (World.Player?.FreezeSpray is { IsValid: true } spray)
        {
            if (spray.SpawnProjectileArchetype is RFreezeGrenadeCommonProjectile { IsValid: true } first)
            {
                yield return first;
            }

            if (spray.SpawnSecondaryProjectileArchetype is RFreezeGrenadeCommonProjectile { IsValid: true } second)
            {
                yield return second;
            }
        }

        foreach (var grenade in Game.FindObjects<RFreezeGrenadeCommonProjectile>())
        {
            if (grenade.IsValid)
            {
                yield return grenade;
            }
        }
    }

    static void KeepTrigger()
    {
        foreach (var grenade in Grenades())
        {
            TriggerWidth.Hold(grenade, TriggerScaled);
        }
    }

    // Every freeze starts here and sets its own length, so the player's is stretched after.
    [Redirect(typeof(RBMBehaviour_FreezeBlastHit), nameof(RBMBehaviour_FreezeBlastHit.Initialise))]
    static void FreezeStarted(RBMBehaviour_FreezeBlastHit self, RPawnPlayer instigator, Vector3 hitNorm, bool bInstantFreeze, RFreezeSprayProjectile proj, bool bShouldAlwaysRagdoll)
    {
        Redirects.RunOriginal();
        try
        {
            if (!FreezeDuration.IsDefault && instigator is { IsValid: true } && instigator.Ptr == World.Player?.Ptr)
            {
                self.FreezeTime *= (float)FreezeDuration.Value;
            }
        }
        catch (Exception error)
        {
            Log.Error("gadgets.freeze_duration", "The freeze length could not be changed", error);
        }
    }

    // A strike that would lock the gadget out puts the gadget's wait and the room back as they
    // were. The game's count of the player's challenge for it is left alone.
    [Redirect(typeof(RVoiceSynthesiser), nameof(RVoiceSynthesiser.NotifyPawnDied))]
    static void NotifyPawnDied(RVoiceSynthesiser self, RPawnVillain deadPawn)
    {
        if (!NoSynthPenalty.On)
        {
            Redirects.RunOriginal();
            return;
        }

        var wait = self.CurrentRechargeTime;
        var strikes = self.iCurrentNumberOfStrikes;
        var room = deadPawn is { IsValid: true } ? deadPawn.RoomState : null;
        var noticed = room is { IsValid: true } && room.bVoiceSynthUseDetected;
        Redirects.RunOriginal();
        try
        {
            self.CurrentRechargeTime = wait;
            self.iCurrentNumberOfStrikes = strikes;
            if (room is { IsValid: true } && !noticed)
            {
                room.ResetVoiceSynthesiserUseDetected();
            }
        }
        catch (Exception error)
        {
            Log.Error("gadgets.synth_penalty", "The voice synthesizer's wait could not be put back", error);
        }
    }

    // The cloud's radius is read while it starts, so it is raised before the game does that. The
    // upgrade sets it again from its own multiplier, which is scaled too.
    public static void SizeCloud(RSmokeScreen cloud)
    {
        var scale = (float)SmokeSize.Value;
        cloud.StunRadius *= scale;
        cloud.fUpgradeRadiusMultiplier *= scale;
        cloud.SmokeScreenAudioRadiusInner *= scale;
        cloud.SmokeScreenAudioRadiusOuter *= scale;
    }
}
