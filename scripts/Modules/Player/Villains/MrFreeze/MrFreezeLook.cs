#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// How Mr. Freeze raises his blaster. His aiming pose only ships in the Season of Infamy level
// where he helps Batman fight the tanks, so that level is loaded as a package while MR. FREEZE
// is on, the way DISGUISE loads a showcase. Nothing in it runs. Only the pose is taken from it.
//
// While he aims, his arms and blaster take that pose over the worn look.
static partial class MrFreeze
{
    const string LookPackage = "Showcase_Characters_DLCMr_Freeze";
    const string MissionPackage = "CityX_98_Ch_F2_Static_LOD1";
    const string AimSetPath = "DLC_Anim_Freeze_IC.Freeze.Freeze_Aiming";
    static readonly FName AimMove = new("Aim_Idle");

    // Every part of his suit is named for him.
    const string SuitName = "Freeze";

    const string Shoulder = "Clavicle";
    const string BlasterBone = "Freeze_Blaster";
    const string GunBone = "Gundummy";

    // Seconds to raise or lower the blaster.
    const float RaiseSeconds = 0.15f;

    static bool _loadTried;
    static AnimSet? _aimSet;
    static SkeletalMeshComponent? _body;

    // Bones under either shoulder, and his blaster's own, take the aiming pose.
    static readonly VillainPose Arms = new(RaiseSeconds, bone => bone.Contains(Shoulder, StringComparison.OrdinalIgnoreCase)
        || bone.StartsWith(BlasterBone, StringComparison.OrdinalIgnoreCase) || bone.StartsWith(GunBone, StringComparison.OrdinalIgnoreCase));

    static void Load()
    {
        if (_loadTried)
        {
            return;
        }

        _loadTried = true;

        // Only what was kept outlives the level's load, so anything missing loads it again.
        var missing = Game.FindObject<AnimSet>(AimSetPath) is not { IsValid: true } || Game.FindObject<MaterialInterface>(ConeLookPath) is not { IsValid: true }
            || Game.FindObject<AkEvent>(BeamSoundPath) is not { IsValid: true } || Game.FindObject<ParticleSystem>(SpirePath) is not { IsValid: true };
        if (missing && !Packages.TryLoad(MissionPackage, "Mr. Freeze's aiming pose", "mr_freeze.load"))
        {
            return;
        }

        _aimSet = Kept(Game.FindObject<AnimSet>(AimSetPath));
        LoadBeam();
        LoadBlast();
        if (_aimSet is null)
        {
            Log.Warn("mr_freeze.pose", "Mr. Freeze's aiming pose wasn't found, so his arms stay as Batman's while he aims.");
        }
    }

    static T? Kept<T>(T? thing)
        where T : GameObject => Borrowed.KeepFound(thing, Lender);

    // The worn body, while the worn look is his.
    static SkeletalMeshComponent? FreezeBody()
    {
        if (Disguise.WornBody is not { } body)
        {
            _body = null;
            return null;
        }

        if (_body is null || _body.Ptr != body.Ptr)
        {
            _body = body.SkeletalMesh is { IsValid: true } mesh && mesh.GetPathName().Contains(SuitName, StringComparison.OrdinalIgnoreCase) ? body : null;
        }

        return _body;
    }

    static void Pose(RPawnPlayer? player) => Arms.Update(player, player is null ? null : FreezeBody(), _aimSet, AimMove, Aim.On);

    static void Unload()
    {
        Arms.Clear();
        UnloadBeam();
        UnloadBlast();
        _body = null;
        _aimSet = null;
        _loadTried = false;
        Borrowed.Let(Lender);
    }
}
