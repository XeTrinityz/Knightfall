#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// How the Joker draws his shotgun. The gun, its sounds and his aiming pose come from the game's
// own playable Joker, whose package is loaded while JOKER is on. Nothing in it runs. Only what he
// needs is taken from it.
//
// While he aims, his upper body takes that pose over the worn look and the gun is in his hand.
static partial class JokerVillain
{
    // The showcase's Joker is the cutscene model, a separate coat over a shirt, and the shirt
    // flickers through once he moves like a player. The playable Joker's suit is one made to be
    // played in.
    const string Suit = "Playable_Joker_Std";
    const string SuitLabel = "JOKER (PLAYABLE)";

    const string CharacterPackage = "Playable_Joker";

    // His gun moves live in the movement set the playable Joker uses. The sets named for his gun are
    // empty in this package.
    const string AimSetPath = "Anim_Joker.JK_Movement_Set";
    static readonly FName AimMove = new("Aim_Idle");
    const string GunPath = "Joker_Gun.Mesh.Joker_Gun";
    const string DrawSoundPath = "ww_chr_joker_foley_gun.Play_Joker_Foley_Gun_Up";
    const string StowSoundPath = "ww_chr_joker_foley_gun.Play_Joker_Foley_Gun";

    // Every part of his look is named for him.
    const string SuitName = "Joker";

    const string Spine = "Spine";
    const string GunBone = "Gundummy";

    // Seconds to raise or lower the gun, and how far raised it shows in his hand.
    const float RaiseSeconds = 0.15f;
    const float Drawn = 0.5f;

    static bool _loadTried;
    static AnimSet? _aimSet;
    static SkeletalMesh? _gunMesh;
    static AkEvent? _drawSound;
    static AkEvent? _stowSound;
    static SkeletalMeshComponent? _body;
    static SkeletalMeshComponent? _gun;
    static SkeletalMeshComponent? _gunOn;
    static bool _aiming;

    // He aims from his whole upper body, leaning into the gun, so everything from the spine up takes
    // the aiming pose, as does the gun bone. His legs stay free to walk.
    static readonly VillainPose Arms = new(RaiseSeconds, bone => bone.Contains(Spine, StringComparison.OrdinalIgnoreCase)
        || bone.StartsWith(GunBone, StringComparison.OrdinalIgnoreCase))
    {
        LeanAt = Spine,
        Barrel = Barrel,
    };

    static readonly Func<Vector3, Vector3> AimAt = gun => Aim.PointFor(gun);

    // The gun's socket at the end of its barrel, which points along it.
    static readonly FName Muzzle = new("Muzzle");

    static void Load()
    {
        if (_loadTried)
        {
            return;
        }

        _loadTried = true;

        // Only what was kept outlives a load, so anything missing loads it again.
        var missing = Game.FindObject<AnimSet>(AimSetPath) is not { IsValid: true } || Game.FindObject<SkeletalMesh>(GunPath) is not { IsValid: true }
            || Game.FindObject<AkEvent>(DrawSoundPath) is not { IsValid: true } || Game.FindObject<AkEvent>(StowSoundPath) is not { IsValid: true }
            || !GunLoaded();
        if (missing && !Packages.TryLoad(CharacterPackage + "_SF", "the Joker's shotgun", "joker.load"))
        {
            return;
        }

        _aimSet = Game.FindObject<AnimSet>(AimSetPath) is { IsValid: true } set && set.FindAnimSequence(AimMove) is { IsValid: true } ? Kept(set) : null;
        _gunMesh = Kept(Game.FindObject<SkeletalMesh>(GunPath));
        _drawSound = Kept(Game.FindObject<AkEvent>(DrawSoundPath));
        _stowSound = Kept(Game.FindObject<AkEvent>(StowSoundPath));
        LoadGun();
        LoadGas();
        if (_aimSet is null)
        {
            Log.Warn("joker.pose", "The Joker's aiming pose wasn't found, so his arms stay as Batman's while he aims.");
        }

        if (_gunMesh is null)
        {
            Log.Warn("joker.gun", "The Joker's shotgun wasn't found, so his hands stay empty while he aims.");
        }
    }

    static T? Kept<T>(T? thing)
        where T : GameObject => Borrowed.KeepFound(thing, Lender);

    // The worn body, while the worn look is his.
    static SkeletalMeshComponent? JokerBody()
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

    static void Pose(RPawnPlayer? player)
    {
        var body = player is null ? null : JokerBody();
        Arms.Update(player, body, _aimSet, AimMove, Aim.On, AimAt);
        Gun(player, body);
        DrawSound(player);
    }

    static void Gun(RPawnPlayer? player, SkeletalMeshComponent? body)
    {
        var drawn = player is not null && body is not null && _gunMesh is { IsValid: true } && Arms.Raised >= Drawn;
        if (drawn && (_gun is not { IsValid: true } || _gunOn is not { IsValid: true } || _gunOn.Ptr != body!.Ptr))
        {
            Holster();
            var gun = Made.Tracked(new SkeletalMeshComponent(player!));
            gun.SetActorCollision(false, false);
            gun.SetSkeletalMesh(_gunMesh!, false);
            gun.bCastDynamicShadow = body!.bCastDynamicShadow;
            gun.SetLightingChannels(body.LightingChannels);
            gun.SetDepthPriorityGroup(body.DepthPriorityGroup);
            gun.SetOwnerNoSee(body.bOwnerNoSee);
            body.AttachComponent(gun, GunBone, Vector3.Zero, default, Vector3.One);
            _gun = gun;
            _gunOn = body;
        }

        if (_gun is { IsValid: true, bAttached: true } shown && shown.HiddenGame == drawn)
        {
            shown.SetHidden(!drawn);
        }

        Sling(player, body, drawn);
    }

    // The muzzle, and the way the barrel points. The socket's own turn is used when it lies along
    // the barrel, otherwise the line from his grip out to the muzzle.
    static (Vector3 From, Vector3 Way)? Barrel()
    {
        if (_gun is not { IsValid: true, HiddenGame: false } gun || _gunOn is not { IsValid: true } holder 
            || !gun.GetSocketWorldLocationAndRotation(Muzzle, out var at, out var turn, 0))
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
        return (at, Vector3.Dot(way, along) > 0.8f ? way : along);
    }

    static void DrawSound(RPawnPlayer? player)
    {
        var aiming = player is not null && Aim.On && _aimSet is not null;
        if (aiming == _aiming)
        {
            return;
        }

        _aiming = aiming;
        if (player is not null && (aiming ? _drawSound : _stowSound) is { IsValid: true } sound)
        {
            player.StartAudioEvent(sound);
        }
    }

    static void Holster()
    {
        if (_gun is { IsValid: true, bAttached: true } gun && _gunOn is { IsValid: true, bAttached: true } holder)
        {
            holder.DetachComponent(gun);
        }

        _gun = null;
        _gunOn = null;
    }

    static void Unload()
    {
        Arms.Clear();
        Holster();
        UnloadGun();
        UnloadGas();
        UnloadGang();
        _aiming = false;
        _body = null;
        _aimSet = null;
        _gunMesh = null;
        _drawSound = null;
        _stowSound = null;
        _loadTried = false;
        Borrowed.Let(Lender);
    }
}
