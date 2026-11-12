#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;
using BmSDK.Framework;

namespace Knightfall;

// BATCLAW ZIP KICK. Firing the Batclaw at a thug hands him to the claw's thug move, which is the
// pull on most claws. With this on it is Robin's flying kick, so the player zips straight in. The
// pawn is lent his ZipKickFlight stance and kick animations, and the hit is dealt here, because
// the game reads its damage from Robin's own claw class.
//
// Robin's own claw fires a line first and the kick pins it from hand to thug. Copying that whole
// route on another claw was tried and dropped as too fragile. Here no line is shown at all.
static class ZipKick
{
    const string Lender = "zip kick";
    const string RobinAsset = "Playable_Robin0";
    const string KickPath = "Robin_Special_Moves.BatClawAttack";
    const int KickDamage = 2;
    const float KickPush = 3000f;
    const string PoseCopyName = "KnightfallPoseConfig_ZipKick";

    static readonly string[] RobinPackages = { "Playable_Robin0_SF", "Playable_Robin_SF" };
    static readonly FName FlightStance = new("ZipKickFlight");
    static readonly FName Spine = new("Bip01_Spine2");

    static readonly Held<RHarpoonGun, RSpecialMoveConfig> ThugMove = new("batclaw thug move", gun => gun.HarpoonThugMove, (gun, move) => gun.HarpoonThugMove = move);

    static RSpecialMoveConfig_RobinBatClawAttack? _move;
    static AnimSet? _kick;
    static RPoseConfig? _poses;
    static bool _failed;
    static RPawnPlayer? _dressed;

    public static readonly Toggle Option = new(
        "player.gadgets.zip_kick", "BATCLAW ZIP KICK",
        "Your Batclaw does Robin's zip kick. Fire it at a thug and you zip straight in with a flying kick instead of pulling him over. It counts toward your combo.")
    {
        Tick = Keep,
        Restore = Unload,
    };

    static void Keep()
    {
        if (World.Player is not { } pawn)
        {
            return;
        }

        // A new pawn, like after a reload of the save, starts bare.
        if (_dressed is not null && (!_dressed.IsValid || _dressed.Ptr != pawn.Ptr))
        {
            _dressed = null;
            ThugMove.Release();
        }

        if (!Loaded() || pawn.HarpoonGun is not { IsValid: true } gun || gun is RRobinBatClaw)
        {
            return;
        }

        ThugMove.Hold(gun, _move!);
        if (_dressed is null)
        {
            _dressed = pawn;
            Lending.AddPoses(pawn, _poses!);
            Lending.AddAnimSet(pawn, _kick!, Lender);
        }
    }

    static bool Loaded()
    {
        if (_move is { IsValid: true } && _kick is { IsValid: true } && _poses is { IsValid: true })
        {
            return true;
        }

        // Tried once each time the option is switched on, so a missing package isn't loaded over
        // and over.
        if (_failed)
        {
            return false;
        }

        try
        {
            var robin = Borrowed.Character(RobinAsset, RobinPackages);
            Borrowed.Keep(robin, Lender);
            var config = robin.AnimationConfig is { IsValid: true } found ? Borrowed.Keep(found, Lender) : throw new InvalidOperationException("Robin's animation data is missing");
            _move = Game.FindObject<RSpecialMoveConfig_RobinBatClawAttack>(KickPath) ?? throw new InvalidOperationException("Robin's zip kick move is missing");
            Borrowed.Keep(_move, Lender);
            _kick = _move.PlayerAnimset is { IsValid: true } kick ? Borrowed.Keep(kick, Lender) : throw new InvalidOperationException("Robin's zip kick animations are missing");
            _poses = Lending.TrimmedPoses(config, PoseCopyName, stance => stance.Index == FlightStance.Index) ?? throw new InvalidOperationException("Robin's flight stance is missing");
            Borrowed.Keep(_poses, Lender);
            return true;
        }
        catch (InvalidOperationException problem)
        {
            _failed = true;
            Log.Warn("zip_kick", $"BATCLAW ZIP KICK can't work: {problem.Message}.");
            return false;
        }
    }

    static void Unload()
    {
        ThugMove.Release();
        if (_dressed is { IsValid: true } pawn)
        {
            if (_poses is { IsValid: true } poses)
            {
                Lending.RemovePoses(pawn, poses);
            }

            if (_kick is { IsValid: true } kick)
            {
                Lending.RemoveAnimSet(pawn, kick, Lender);
            }
        }

        _dressed = null;
        _move = null;
        _kick = null;
        _poses = null;
        _failed = false;
        Borrowed.Let(Lender);
    }

    static bool Borrowing(RSpecialMoveInstance_RobinBatClawAttack kick, out RPawnPlayer pawn)
    {
        pawn = null!;
        if (!Option.On || kick.PlayerPawn is not { IsValid: true } player || player.HarpoonGun is RRobinBatClaw)
        {
            return false;
        }

        pawn = player;
        return true;
    }

    // The launch pins the claw's rope from the player's hand to the thug. On Robin that rope is
    // live from the shot he just fired. Here nothing was fired, so the rope would show its old,
    // stale shape. The claw head's rope is taken away for the length of the launch, so the launch
    // finds none and leaves it alone.
    [Redirect("BmScript.RSpecialMoveInstance_RobinBatClawAttack:Launch:BeginState", typeof(RSpecialMoveInstance_RobinBatClawAttack))]
    static void LaunchBegin(RSpecialMoveInstance_RobinBatClawAttack self, FName previousState)
    {
        RHarpoonProjectile? head = null;
        RSimpleRopeComponent? rope = null;
        try
        {
            if (Borrowing(self, out var pawn)
                && pawn.HarpoonGun is { IsValid: true } gun
                && gun.Projectile.Count > 0
                && gun.Projectile[0] is { IsValid: true } found)
            {
                head = found;
                rope = found.RopeComponent;
                found.RopeComponent = null!;
            }
        }
        catch (Exception error)
        {
            Log.Error("zip_kick.rope", "The claw's rope could not be set aside for the zip kick", error);
        }

        try
        {
            Redirects.RunOriginal();
        }
        finally
        {
            if (head is { IsValid: true } && rope is not null)
            {
                head.RopeComponent = rope;
            }
        }
    }

    // The game's version reads the damage from RRobinBatClaw(HarpoonGun), which is None on any
    // other claw, so the kick hit nobody. The same hit is dealt here with Robin's damage.
    [Redirect(typeof(RSpecialMoveInstance_RobinBatClawAttack), nameof(RSpecialMoveInstance_RobinBatClawAttack.TakeDownVillain))]
    static void TakeDownVillain(RSpecialMoveInstance_RobinBatClawAttack self)
    {
        var handled = false;
        try
        {
            handled = Borrowing(self, out var pawn) && Kick(self, pawn);
        }
        catch (Exception error)
        {
            Log.Error("zip_kick.hit", "The zip kick hit failed", error);
        }

        if (!handled)
        {
            Redirects.RunOriginal();
        }
    }

    static bool Kick(RSpecialMoveInstance_RobinBatClawAttack self, RPawnPlayer pawn)
    {
        // Blocked, or nobody to kick. The game's version does nothing then either.
        if (self.TargetPawn is not RPawnVillain { IsValid: true } villain || self.VillainBlocked)
        {
            return true;
        }

        var close = self.KickAnimType == RSpecialMoveInstance_RobinBatClawAttack.KickAnim.KickAnim_ShortKick;
        var velocity = pawn.Velocity;
        var heading = velocity.LengthSquared() > 1f ? Vector3.Normalize(velocity) : Vector3.Zero;

        var hit = new RPawnCombat.FDamageInfo
        {
            Attacker = pawn,
            DamageAmount = KickDamage,
            BoneName = Spine,
            Impulse = (heading + new Vector3(0f, 0f, 0.8f)) * KickPush,
            DamageType = close ? RDmgType_Ragdoll.StaticClass() : RDmgType_ZipKick.StaticClass(),
            bWillKnockOverOtherPawns = !close,
            bForceQuickMotor = true,
            StunTime = 5f,
        };

        var result = villain.DamagedBy(hit);
        if (result is RPawnCombat.DamageResult.DMG_Hit or RPawnCombat.DamageResult.DMG_KO
            && pawn.Controller is RPlayerControllerCombat { IsValid: true } controller
            && controller.IsInCombat(false))
        {
            controller.IncCombo(villain, 1, 0, false);
            controller.MovePerformed(RGameInfo.EComboMoveType.ECMT_RBZipKick, villain, false, 0, 0);
        }

        pawn.PlaySound_Impact(villain, true, false, false, true, true, false, false, false, false, false);
        return true;
    }
}
