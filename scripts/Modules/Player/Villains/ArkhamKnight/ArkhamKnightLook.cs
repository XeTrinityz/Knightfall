#nullable enable

using System.Numerics;
using System.Runtime.CompilerServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// How the Arkham Knight looks and holds his rifle.
//
// His look is the Red Hood pack's Arkham Knight suit, made to be played in and built on Batman's
// own skeleton. Without the pack he wears the showcase model instead.
//
// His own rifle only ships in the cutscene before the Joker's last act, which is loaded once
// while he is on. His aiming stance is the militia sniper's, from the small package the game
// streams in for sniper thugs. If his rifle can't be loaded he carries the militia's.
static partial class ArkhamKnight
{
    const string Suit = "Playable_DLCRedhood_ArkhamKnight";
    const string SuitLabel = "ARKHAM KNIGHT (PLAYABLE)";
    const string ShowcaseLook = "Showcase_Characters_ArkhamKnight";

    // Every part of his look is named for him.
    const string LookName = "Arkham_Knight";

    const string StancePackage = "StreamedWeapon_CombatSniper_SF";
    const string RiflePackage = "JokerBoss_B1_Cine_Ch9r_PreEnd";
    const string RiflePath = "Red_Hood_Weaponry.Mesh.Red_Hood_Weaponry_SNIPER";
    const string MilitiaRiflePath = "Sniper_Rifle.Mesh.Sniper_Rifle_3P";
    static readonly string[] StanceSetPaths = { "Anim_Thug_Rifle.Code.TGRifle_Movement", "Anim_Thug_Rifle.Code.TGRifle_Sniper" };
    static readonly FName AimMove = new("Stand_Aiming_Idle");

    const string Spine = "Spine";
    const string GunBone = "Gundummy";

    // Seconds to raise or lower the rifle, and how far raised it shows in his hands.
    const float RaiseSeconds = 0.2f;
    const float Drawn = 0.5f;

    static bool WearsSuit => GameFiles.HasPackage(Suit);

    static bool _loadTried;
    static AnimSet? _aimSet;
    static SkeletalMesh? _rifleMesh;
    static FName _muzzle;
    static SkeletalMeshComponent? _body;
    static SkeletalMeshComponent? _rifle;
    static SkeletalMeshComponent? _rifleOn;

    // He aims from his whole upper body, so everything from the spine up takes the sniper's pose,
    // as does the gun bone. His legs stay free to walk.
    static readonly VillainPose Arms = new(RaiseSeconds, bone => bone.Contains(Spine, StringComparison.OrdinalIgnoreCase)
        || bone.StartsWith(GunBone, StringComparison.OrdinalIgnoreCase))
    {
        LeanAt = Spine,
        Barrel = Barrel,
    };

    static readonly Func<Vector3, Vector3> AimAt = gun => Aim.PointFor(gun);

    static void Load()
    {
        if (_loadTried)
        {
            return;
        }

        _loadTried = true;

        // His rifle and laser come first. The stance's kit takes the laser from them.
        LoadRifle();
        LoadStance();
        PickRifle();
        if (_aimSet is null)
        {
            Log.Warn("arkham_knight.pose", "The sniper's aiming stance wasn't found, so the Arkham Knight's arms stay as Batman's while he aims.");
        }

        if (_rifleMesh is null)
        {
            Log.Warn("arkham_knight.rifle", "No sniper rifle was found, so the Arkham Knight's hands stay empty while he aims.");
        }
    }

    static void LoadStance()
    {
        // Only what was kept outlives a load, so anything missing loads it again.
        if ((FindStance() is null || !KitLoaded()) && !LoadPackage(StancePackage, "the sniper's stance"))
        {
            return;
        }

        _aimSet = Kept(FindStance());
        LoadRifleKit();
    }

    static AnimSet? FindStance()
    {
        foreach (var path in StanceSetPaths)
        {
            if (Game.FindObject<AnimSet>(path) is { IsValid: true } set && set.FindAnimSequence(AimMove) is { IsValid: true })
            {
                return set;
            }
        }

        return null;
    }

    // Only what was kept outlives a load, so anything missing loads it again.
    static void LoadRifle()
    {
        if (Game.FindObject<SkeletalMesh>(RiflePath) is not { IsValid: true } || Game.FindObject<ParticleSystem>(LaserPath) is not { IsValid: true })
        {
            LoadPackage(RiflePackage, "his rifle");
        }
    }

    // His own rifle, or the militia's when his couldn't be loaded.
    static void PickRifle()
    {
        _rifleMesh = Kept(Game.FindObject<SkeletalMesh>(RiflePath)) ?? Kept(Game.FindObject<SkeletalMesh>(MilitiaRiflePath));
        _muzzle = _rifleMesh is null ? FName.None : MuzzleOf(_rifleMesh);
    }

    static bool LoadPackage(string package, string what) =>
        Packages.TryLoad(package, $"the Arkham Knight's {what}", "arkham_knight.load");

    // The socket at the end of the barrel. Rifles name it for the muzzle or its flash. Failing
    // that, the socket furthest out along the barrel is taken.
    static FName MuzzleOf(SkeletalMesh mesh)
    {
        var sockets = mesh.Sockets;
        var best = FName.None;
        var furthest = float.MinValue;
        for (var i = 0; i < sockets.Count; i++)
        {
            if (sockets[i] is not { IsValid: true } socket)
            {
                continue;
            }

            var name = socket.SocketName.ToString() ?? "";
            if (name.Contains("Muzzle", StringComparison.OrdinalIgnoreCase) || name.Contains("Flash", StringComparison.OrdinalIgnoreCase))
            {
                return socket.SocketName;
            }

            if (socket.RelativeLocation.X > furthest)
            {
                furthest = socket.RelativeLocation.X;
                best = socket.SocketName;
            }
        }

        return best;
    }

    static T? Kept<T>(T? thing)
        where T : GameObject => Borrowed.KeepFound(thing, Lender);

    // The worn body, while the worn look is his.
    static SkeletalMeshComponent? KnightBody()
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

    static void Pose(RPawnPlayer? player)
    {
        var body = player is null ? null : KnightBody();
        var redHood = _pistols && _pistolSet is not null;
        Arms.Hold = redHood ? PistolAimAt : null;
        Arms.Update(player, body, redHood ? _pistolSet : _aimSet, redHood ? PistolAim : AimMove, Aim.On, AimAt, !redHood);
        Rifle(player, body);
        Pistols(player, body);
    }

    static void Rifle(RPawnPlayer? player, SkeletalMeshComponent? body)
    {
        var drawn = !_pistols && player is not null && body is not null && _rifleMesh is { IsValid: true } && Arms.Raised >= Drawn;
        if (drawn && (_rifle is not { IsValid: true } || _rifleOn is not { IsValid: true } || _rifleOn.Ptr != body!.Ptr))
        {
            Holster();
            var rifle = Made.Tracked(new SkeletalMeshComponent(player!));
            rifle.SetActorCollision(false, false);
            rifle.SetSkeletalMesh(_rifleMesh!, false);
            rifle.bCastDynamicShadow = body!.bCastDynamicShadow;
            rifle.SetLightingChannels(body.LightingChannels);
            rifle.SetDepthPriorityGroup(body.DepthPriorityGroup);
            rifle.SetOwnerNoSee(body.bOwnerNoSee);
            body.AttachComponent(rifle, GunBone, Vector3.Zero, default, Vector3.One);
            _rifle = rifle;
            _rifleOn = body;
        }

        if (_rifle is { IsValid: true } shown && shown.HiddenGame == drawn)
        {
            shown.SetHidden(!drawn);
        }
    }

    // The muzzle of what he holds, and the way it points. With his pistols up it is the right one.
    static (Vector3 From, Vector3 Way)? Barrel() => _pistols ? GunBarrel(_rightGun) : RifleBarrel();

    // The socket's own turn is used when it lies along the barrel, otherwise the line from his grip
    // out to the muzzle.
    static (Vector3 From, Vector3 Way)? RifleBarrel()
    {
        if (_rifle is not { IsValid: true, HiddenGame: false } rifle || _rifleOn is not { IsValid: true } holder
            || _muzzle.Index == FName.None.Index || !rifle.GetSocketWorldLocationAndRotation(_muzzle, out var at, out var turn, 0))
        {
            return null;
        }

        var grip = holder.GetBoneLocation(GunBone, 0);
        var along = at - grip;
        if (along.LengthSquared() < 1f)
        {
            return (at, turn.ToDirection());
        }

        along = Vector3.Normalize(along);
        var way = turn.ToDirection();
        way = Vector3.Dot(way, along) > 0.8f ? way : along;
        return (Tip(rifle, at, way), way);
    }

    // How far the barrel runs above the light under it, in the rifle's own units.
    const float BarrelAboveLight = 7f;

    // The socket sits out in front of the rifle, where its light shines. The muzzle is where the
    // light's line leaves the rifle's own box, raised up to the barrel.
    static Vector3 Tip(SkeletalMeshComponent rifle, Vector3 socket, Vector3 way)
    {
        if (_rifleMesh is not { IsValid: true } mesh)
        {
            return socket;
        }

        var place = Unsafe.As<GameObject.FMatrix, Matrix4x4>(ref rifle.LocalToWorld);
        if (!Matrix4x4.Invert(place, out var back))
        {
            return socket;
        }

        var local = Vector3.Transform(socket, back);
        var along = Vector3.TransformNormal(way, back);
        if (along.LengthSquared() < 1e-8f)
        {
            return socket;
        }

        along = Vector3.Normalize(along);
        var box = mesh.Bounds;
        var furthest = Vector3.Dot(box.Origin, along) + MathF.Abs(box.BoxExtent.X * along.X) + MathF.Abs(box.BoxExtent.Y * along.Y) + MathF.Abs(box.BoxExtent.Z * along.Z);
        var tip = local + along * (furthest - Vector3.Dot(local, along)) + Vector3.UnitZ * BarrelAboveLight;
        return Vector3.Transform(tip, place);
    }

    static void Holster()
    {
        if (_rifle is { IsValid: true } rifle && _rifleOn is { IsValid: true } holder)
        {
            holder.DetachComponent(rifle);
        }

        _rifle = null;
        _rifleOn = null;
    }

    static void Unload()
    {
        Arms.Clear();
        Holster();
        UnloadRifleKit();
        UnloadStrike();
        UnloadPistols();
        _body = null;
        _aimSet = null;
        _rifleMesh = null;
        _muzzle = FName.None;
        _loadTried = false;
        Borrowed.Let(Lender);
    }
}
