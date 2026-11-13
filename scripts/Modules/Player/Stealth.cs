#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// The stealth rows of PLAYER > STATUS.
static class Stealth
{
    // Thugs see natively, from four sight ranges. Held at 1 unit they see nothing.
    const float BlindRange = 1f;

    static readonly Held<RPawnPlayer, bool> SilentFlag = new("footstep noise", pawn => pawn.bSilentRunning, (pawn, on) => pawn.bSilentRunning = on);
    static readonly Held<RPawnVillain, float> InstantSight = new("thug sight", villain => villain.InstantSightRange, (villain, range) => villain.InstantSightRange = range);
    static readonly Held<RPawnVillain, float> FocusedSight = new("thug sight", villain => villain.FocusedSightRange, (villain, range) => villain.FocusedSightRange = range);
    static readonly Held<RPawnVillain, float> PeripheralSight = new("thug sight", villain => villain.PeripheralSightRange, (villain, range) => villain.PeripheralSightRange = range);
    static readonly Held<RPawnVillain, float> ExtendedSight = new("thug sight", villain => villain.ExtendedSightRange, (villain, range) => villain.ExtendedSightRange = range);

    public static readonly Toggle SilentFootsteps = new(
        "player.status.silent_footsteps", "SILENT FOOTSTEPS",
        "Thugs can't hear your footsteps, even when you run.")
    {
        Tick = KeepSilent,
        Restore = SilentFlag.Release,
    };

    public static readonly Toggle UnseenByDrones = new(
        "player.status.unseen_by_drones", "UNSEEN BY DRONES",
        "Sentry guns, flying drones and helicopter searchlights can't see you. Thugs and the small drones they fly still can.");

    public static readonly Toggle UnseenByThugs = new(
        "player.status.unseen_by_thugs", "UNSEEN BY THUGS",
        "Thugs can't see you and any hunting you lose you. Street thugs fighting you run off. They still hear noise, and story fights carry on.")
    {
        Tick = Blind,
        Restore = Unblind,
    };

    // The game only reads this flag before it makes footstep noise, so nothing else changes.
    static void KeepSilent()
    {
        if (World.Player is { } pawn)
        {
            SilentFlag.Hold(pawn, true);
        }
    }

    // The game writes these ranges itself, for snipers zooming in and the like. Held turns any of
    // those into what gets put back later.
    static void Blind()
    {
        foreach (var villain in Game.FindObjects<RPawnVillain>())
        {
            if (!villain.IsValid || villain.bDeleteMe)
            {
                continue;
            }

            // A thug recruited as a bodyguard gets his sight back.
            if (Bodyguards.Is(villain))
            {
                InstantSight.Release(villain);
                FocusedSight.Release(villain);
                PeripheralSight.Release(villain);
                ExtendedSight.Release(villain);
                continue;
            }

            InstantSight.Hold(villain, BlindRange);
            FocusedSight.Hold(villain, BlindRange);
            PeripheralSight.Hold(villain, BlindRange);
            ExtendedSight.Hold(villain, BlindRange);
            LoseTrack(villain);
        }
    }

    // A thug who spotted you keeps hunting while his sight level is above 0. At 0 the game drops
    // the hunt its own way and he goes searching.
    static void LoseTrack(RPawnVillain villain)
    {
        if (villain.AIController is not { IsValid: true } brain || brain.FindAlertForPlayer() is not { IsValid: true } alert)
        {
            return;
        }

        if (alert.SightLevel > 0f)
        {
            alert.SightLevel = 0f;
        }

        if (alert.bVisible)
        {
            alert.bVisible = false;
        }
    }

    static void Unblind()
    {
        InstantSight.Release();
        FocusedSight.Release();
        PeripheralSight.Release();
        ExtendedSight.Release();
    }

    // Thugs ask with themselves as the checker. Sentry guns ask with none.
    static bool HiddenFrom(RBMPawnAI checker) => checker is { IsValid: true } ? UnseenByThugs.On : UnseenByDrones.On;

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.ScriptIsHidden))]
    static bool ScriptIsHidden(RPlayerController self, RBMPawnAI checker, bool combatCheck)
    {
        try
        {
            return HiddenFrom(checker) || self.ScriptIsHidden(checker, combatCheck);
        }
        catch (Exception error)
        {
            Log.Error("stealth.script_hidden", "The hidden check failed", error);
            return false;
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.IsHidden))]
    static bool IsHidden(RPlayerController self, RBMPawnAI checker, bool combatCheck, bool ignoreCinematicCheck)
    {
        try
        {
            return (UnseenByThugs.On && checker is { IsValid: true }) || self.IsHidden(checker, combatCheck, ignoreCinematicCheck);
        }
        catch (Exception error)
        {
            Log.Error("stealth.hidden", "The hidden check failed", error);
            return false;
        }
    }

    // Helicopters and flying drones ask this, as they do during conversations.
    [Redirect(typeof(RHelicopterBase), nameof(RHelicopterBase.IsPlayerInvisible))]
    static bool IsPlayerInvisible(RHelicopterBase self)
    {
        try
        {
            return UnseenByDrones.On || self.IsPlayerInvisible();
        }
        catch (Exception error)
        {
            Log.Error("stealth.helicopter", "The helicopter sight check failed", error);
            return false;
        }
    }

    // While Batman interrogates someone, street thugs break off a fight and run. Saying one is
    // going on is how thugs already fighting you are made to leave.
    [Redirect(typeof(RBMBehaviour_Combat), nameof(RBMBehaviour_Combat.InterrogationInProgress))]
    static bool InterrogationInProgress(RBMBehaviour_Combat self)
    {
        try
        {
            return UnseenByThugs.On || self.InterrogationInProgress();
        }
        catch (Exception error)
        {
            Log.Error("stealth.interrogation", "The interrogation check failed", error);
            return false;
        }
    }
}
