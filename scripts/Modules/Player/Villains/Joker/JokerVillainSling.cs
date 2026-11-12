#nullable enable

using System.Numerics;
using System.Runtime.CompilerServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// His shotgun slung across his back while it isn't in his hands. It hangs from his upper spine.
// That bone's own axes aren't known ahead, so the gun goes on with no offset for a frame first,
// and where it sits then shows the bone's place and turn. The sling is worked out from those:
// the grip low on his back and the barrel up over his right shoulder.
static partial class JokerVillain
{
    static readonly FName SlingBone = new("Bip01_Spine2");

    // Where the grip sits from the bone, along his facing, his right and up, and the way the
    // barrel points, the same way.
    const float SlungBack = 11f;
    const float SlungRight = -6f;
    const float SlungUp = -8f;
    const float BarrelUp = 1f;
    const float BarrelRight = 0.45f;
    const float BarrelBack = 0.1f;

    static SkeletalMeshComponent? _slung;
    static SkeletalMeshComponent? _slungOn;
    static bool _slungPlaced;

    static void Sling(RPawnPlayer? player, SkeletalMeshComponent? body, bool drawn)
    {
        var wanted = player is not null && body is not null && _gunMesh is { IsValid: true } && BackGun.On && !drawn;
        if (wanted && (_slung is not { IsValid: true } || _slungOn is not { IsValid: true } || _slungOn.Ptr != body!.Ptr))
        {
            Unsling();
            var gun = Made.Tracked(new SkeletalMeshComponent(player!));
            gun.SetActorCollision(false, false);
            gun.SetSkeletalMesh(_gunMesh!, false);
            gun.bCastDynamicShadow = body!.bCastDynamicShadow;
            gun.SetLightingChannels(body.LightingChannels);
            gun.SetDepthPriorityGroup(body.DepthPriorityGroup);
            gun.SetOwnerNoSee(body.bOwnerNoSee);
            gun.SetHidden(true);
            body.AttachComponent(gun, SlingBone, Vector3.Zero, default, Vector3.One);
            _slung = gun;
            _slungOn = body;
            _slungPlaced = false;
            return;
        }

        // A gun left on a body that has since been taken out of the world is not touched.
        if (_slung is not { IsValid: true, bAttached: true } slung || _slungOn is not { IsValid: true, bAttached: true } holder)
        {
            return;
        }

        if (wanted && !_slungPlaced && player is not null)
        {
            Place(player, holder, slung);
            return;
        }

        if (slung.HiddenGame == (wanted && _slungPlaced))
        {
            slung.SetHidden(!(wanted && _slungPlaced));
        }
    }

    // Reads the bone's place from the gun as it sits on it with no offset, and puts the gun back
    // on with the sling's offset in the bone's own terms.
    static void Place(RPawnPlayer player, SkeletalMeshComponent holder, SkeletalMeshComponent slung)
    {
        var place = Unsafe.As<GameObject.FMatrix, Matrix4x4>(ref slung.LocalToWorld);
        var x = new Vector3(place.M11, place.M12, place.M13);
        var y = new Vector3(place.M21, place.M22, place.M23);
        var z = new Vector3(place.M31, place.M32, place.M33);
        var size = x.Length();
        if (size < 1e-4f || y.LengthSquared() < 1e-8f || z.LengthSquared() < 1e-8f)
        {
            return;
        }

        x /= size;
        y = Vector3.Normalize(y);
        z = Vector3.Normalize(z);
        var bone = new Vector3(place.M41, place.M42, place.M43);

        var ahead = player.Rotation.ToDirection();
        ahead = Vector3.Normalize(ahead with { Z = 0f });
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, ahead));
        var grip = bone - ahead * SlungBack + right * SlungRight + Vector3.UnitZ * SlungUp;
        var barrel = Vector3.Normalize(Vector3.UnitZ * BarrelUp + right * BarrelRight - ahead * BarrelBack);

        var off = grip - bone;
        var at = new Vector3(Vector3.Dot(off, x), Vector3.Dot(off, y), Vector3.Dot(off, z)) / size;
        var way = new Vector3(Vector3.Dot(barrel, x), Vector3.Dot(barrel, y), Vector3.Dot(barrel, z));

        holder.DetachComponent(slung);
        holder.AttachComponent(slung, SlingBone, at, Rotator.FromDirection(way), Vector3.One);
        _slungPlaced = true;
    }

    static void Unsling()
    {
        if (_slung is { IsValid: true, bAttached: true } gun && _slungOn is { IsValid: true, bAttached: true } holder)
        {
            holder.DetachComponent(gun);
        }

        _slung = null;
        _slungOn = null;
        _slungPlaced = false;
    }
}
