#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// How Poison Ivy raises her hand. She has no moves of her own anywhere in the game, so her right
// arm takes Batman's disruptor aim, which holds one hand out at the target. It ships in Startup,
// so nothing has to be loaded for it.
static partial class PoisonIvy
{
    const string LookPackage = "Showcase_Characters_PoisonIvy";
    const string AimSetPath = "Anim_Batman_Gadget.Code.BM_Drone_Disruptor";
    static readonly FName AimMove = new("Drone_Disruptor_Idle");

    // Every part of her look is named for her.
    const string LookName = "Poison_Ivy";

    const string RightShoulder = "R_Clavicle";

    // Seconds to raise or lower her hand.
    const float RaiseSeconds = 0.2f;

    static bool _aimTried;
    static AnimSet? _aimSet;
    static SkeletalMeshComponent? _body;

    // Her right arm from the shoulder down takes the pose. The rest of her moves as you do.
    static readonly VillainPose Arm = new(RaiseSeconds, bone => bone.Contains(RightShoulder, StringComparison.OrdinalIgnoreCase));

    static AnimSet? AimSet()
    {
        if (!_aimTried)
        {
            _aimTried = true;
            _aimSet = Game.FindObject<AnimSet>(AimSetPath);
            if (_aimSet is not { IsValid: true })
            {
                _aimSet = null;
                Log.Warn("poison_ivy.pose", "Batman's disruptor aim wasn't found, so Poison Ivy's arm stays down while she aims.");
            }
        }

        return _aimSet is { IsValid: true } ? _aimSet : null;
    }

    // The worn body, while the worn look is hers.
    static SkeletalMeshComponent? IvyBody()
    {
        if (Disguise.WornBody is not { } body)
        {
            _body = null;
            return null;
        }

        if (_body is null || _body.Ptr != body.Ptr)
        {
            _body = body.SkeletalMesh is { IsValid: true } mesh && mesh.GetPathName().Contains(LookName, StringComparison.OrdinalIgnoreCase) ? body : null;
        }

        return _body;
    }

    // Her hand is thrown forward for a moment as she casts, then held out again.
    static void Pose(RPawnPlayer? player)
    {
        var casting = World.Now < _castUntil;
        Arm.Update(player, player is null ? null : IvyBody(), player is null ? null : AimSet(), casting ? CastMove : AimMove, Aim.On || casting, loop: !casting);
    }

    static void Unload()
    {
        UnloadVine();
        UnloadTrap();
        UnloadVoice();
        Borrowed.Let(Lender);
        Arm.Clear();
        _body = null;
        _aimSet = null;
        _aimTried = false;
    }
}
