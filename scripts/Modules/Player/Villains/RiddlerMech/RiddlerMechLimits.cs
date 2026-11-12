#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// What Batman can't do while he is the mech. Each is the game's own switch for it, so the hidden
// Batman underneath never fights, glides, climbs or rolls and drags the mech with him. He only
// walks, runs, falls and lands.
static partial class RiddlerMech
{
    static readonly Held<RPlayerController, bool> NoGlide = new("mech glide", c => c.bCapeGlideDisabled, (c, off) => c.bCapeGlideDisabled = off);
    static readonly Held<RPlayerController, bool> NoLedges = new("mech ledges", c => c.bLedgeGrabDisabled, (c, off) => c.bLedgeGrabDisabled = off);
    static readonly Held<RPlayerController, bool> NoTakedowns = new("mech takedowns", c => c.bDisableTakedowns, (c, off) => c.bDisableTakedowns = off);
    static readonly Held<RPlayerController, bool> NoEdgeJumps = new("mech edge jumps", c => c.bDisableAutoJumping, (c, off) => c.bDisableAutoJumping = off);
    static readonly Held<RPlayerController, bool> NoQuickTurn = new("mech quick turn", c => c.bDisableRunInstantTurn, (c, off) => c.bDisableRunInstantTurn = off);
    static readonly Held<RPlayerControllerCombat, bool> NoCombat = new("mech combat", c => c.bDisallowCombat, (c, off) => c.bDisallowCombat = off);
    static readonly Held<RPlayerControllerCombat, bool> NoCapeStun = new("mech cape stun", c => c.bCapeStunDisabled, (c, off) => c.bCapeStunDisabled = off);
    static readonly Held<RGameInfo, bool> NoGrapple = new("mech grapple", game => game.bNoGrapple, (game, off) => game.bNoGrapple = off);

    // Thugs can grab Batman, and with his fighting off he can't break free. Missions use this
    // switch to stop thugs choosing that attack.
    static readonly Held<RBMCombatManager, bool> NoGrabs = new("mech grabs", manager => manager.bDisableGrabAttack, (manager, off) => manager.bDisableGrabAttack = off);
    static readonly Held<RPawnPlayer, bool> NoRailings = new("mech railings", pawn => pawn.bCanUseRailings, (pawn, can) => pawn.bCanUseRailings = can);
    static readonly Held<RPawnPlayer, bool> NoPawnGlide = new("mech pawn glide", pawn => pawn.bCanCapeGlide, (pawn, can) => pawn.bCanCapeGlide = can);
    static readonly Held<RPawnPlayer, bool> NoClimbing = new("mech climbing", pawn => pawn.bCanClimb, (pawn, can) => pawn.bCanClimb = can);
    static readonly Held<RPawnPlayer, bool> NoPawnTakedowns = new("mech pawn takedowns", pawn => pawn.bDisableAllTakedowns, (pawn, off) => pawn.bDisableAllTakedowns = off);

    // Landing, the game picks a roll or a running landing by speed and height. Every one of them
    // is swapped for the plain landing, which leaves him where he lands.
    static readonly Held<RPawnPlayer, RSpecialMoveConfig?>[] Landings =
    {
        new("mech landing 1", pawn => pawn.LandMoves_1, (pawn, move) => pawn.LandMoves_1 = move!),
        new("mech landing 2", pawn => pawn.LandMoves_2, (pawn, move) => pawn.LandMoves_2 = move!),
        new("mech landing 3", pawn => pawn.LandMoves_3, (pawn, move) => pawn.LandMoves_3 = move!),
        new("mech landing 4", pawn => pawn.LandMoves_4, (pawn, move) => pawn.LandMoves_4 = move!),
        new("mech landing 5", pawn => pawn.LandMoves_5, (pawn, move) => pawn.LandMoves_5 = move!),
    };

    static readonly Held<RPawnPlayerConfig, RSpecialMoveConfig?> DeepLanding =
        new("mech deep landing", config => config.DeepFallLandRunSuccess, (config, move) => config.DeepFallLandRunSuccess = move!);

    // Crouching has no switch. Its button only raises a flag the game reads for itself, so the
    // game's default crouch keys are kept from it. A crouch already on is stood up from.
    const string CrouchKey = "LeftControl";
    static bool _crouchTaken;

    static void Limit(RPawnPlayer player, RPlayerController controller)
    {
        NoGlide.Hold(controller, true);
        NoLedges.Hold(controller, true);
        NoTakedowns.Hold(controller, true);
        NoEdgeJumps.Hold(controller, true);
        NoQuickTurn.Hold(controller, true);
        if (controller is RPlayerControllerCombat combat)
        {
            NoCombat.Hold(combat, true);
            NoCapeStun.Hold(combat, true);
        }

        if (World.Info is { IsValid: true } game)
        {
            NoGrapple.Hold(game, true);
            if (game.CombatManager is { IsValid: true } manager)
            {
                NoGrabs.Hold(manager, true);
            }
        }

        NoRailings.Hold(player, false);
        NoPawnGlide.Hold(player, false);
        NoClimbing.Hold(player, false);
        NoPawnTakedowns.Hold(player, true);

        if (player.LandMoves_0 is { IsValid: true } plain)
        {
            foreach (var landing in Landings)
            {
                landing.Hold(player, plain);
            }
        }

        if (player.AnimationConfig is { IsValid: true } config && config.DeepFallLandSuccess is { IsValid: true } deep)
        {
            DeepLanding.Hold(config, deep);
        }

        TakeCrouch(true);
        if (player.bWantsToCrouch)
        {
            player.bWantsToCrouch = false;
        }
    }

    static void TakeCrouch(bool take)
    {
        if (take == _crouchTaken)
        {
            return;
        }

        _crouchTaken = take;
        if (take)
        {
            Swallowed.Take(CrouchKey);
            Swallowed.Take(Pad.Button.RT);
        }
        else
        {
            Swallowed.Give(CrouchKey);
            Swallowed.Give(Pad.Button.RT);
        }
    }

    static void Unlimit()
    {
        NoGlide.Release();
        NoLedges.Release();
        NoTakedowns.Release();
        NoEdgeJumps.Release();
        NoQuickTurn.Release();
        NoCombat.Release();
        NoCapeStun.Release();
        NoGrapple.Release();
        NoGrabs.Release();
        NoRailings.Release();
        NoPawnGlide.Release();
        NoClimbing.Release();
        NoPawnTakedowns.Release();
        foreach (var landing in Landings)
        {
            landing.Release();
        }

        DeepLanding.Release();
        TakeCrouch(false);
    }

    // Batman's climbing, vaulting, swinging and the like, which no switch covers. They all start
    // here, from the move's own settings, and are turned down while he is the mech. Landings and
    // the moves that move him through doors and the like still run.
    static bool Refused(RSpecialMoveConfig move) => RefusedRailing(move) || move is RSpecialMoveConfig_Jump or RSpecialMoveConfig_CapeBreakFall
        or RSpecialMoveConfig_CatchLedge or RSpecialMoveConfig_HoldLedge or RSpecialMoveConfig_DropToLedgeHang or RSpecialMoveConfig_JumpToPerchLedge
        or RSpecialMoveConfig_LedgeShimmy or RSpecialMoveConfig_Shimmy or RSpecialMoveConfig_DiveOverRailing or RSpecialMoveConfig_DiveSwingToGlide
        or RSpecialMoveConfig_GlideKick or RSpecialMoveConfig_GlideWallStick or RSpecialMoveConfig_Slide or RSpecialMoveConfig_SlideToCover
        or RSpecialMoveConfig_SwingToChutePoint or RSpecialMoveConfig_SwingToGlide or RSpecialMoveConfig_SwingToLedge or RSpecialMoveConfig_SwingToVantagePoint
        or RSpecialMoveConfig_VantageGrapple or RSpecialMoveConfig_FireGrapple or RSpecialMoveConfig_FireBatClaw or RSpecialMoveConfig_LineLauncher
        or RSpecialMoveConfig_LineLauncherFlipAndAim or RSpecialMoveConfig_WhipRappel_Batman or RSpecialMoveConfig_GrateToPerchLedge
        or RSpecialMoveConfig_PerchLedgeToGrate or RSpecialMoveConfig_FloorGrateFar;

    // Batman's railing moves live in their own set. Climbing onto one is a running move no class
    // tells apart, so the set is known by its name.
    const string RailingMoves = "Batman_Agility_Moves.Railing.";

    static bool RefusedRailing(RSpecialMoveConfig move) => move.GetPathName().StartsWith(RailingMoves, StringComparison.OrdinalIgnoreCase);

    // Up on a railing the only way down is one of his jumps. Should he get up there anyway,
    // nothing is refused, so he is never stuck.
    static bool OnRailing(RPlayerController controller) =>
        (controller.GetStateName().ToString() ?? "").Contains("Railing", StringComparison.OrdinalIgnoreCase);

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.TriggerSpecialMove))]
    static void TriggerSpecialMove(RPlayerController self, RSpecialMoveConfig specMove, REnvironmentCheckTicker.FEnvironmentSpecialMoveLocator loc, bool forceReplicateToClient)
    {
        if (_mech is not null && Option.On && specMove is { IsValid: true } && Refused(specMove) && !OnRailing(self))
        {
            return;
        }

        Redirects.RunOriginal();
    }
}
