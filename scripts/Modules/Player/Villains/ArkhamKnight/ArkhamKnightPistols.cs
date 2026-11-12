#nullable enable

using System.Numerics;
using System.Runtime.CompilerServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// The Arkham Knight's twin pistols. SNIPER RIFLE and TWIN PISTOLS pick what aim and fire use. His
// own pistols ship in the cutscene of him and Red Hood in chapter eight. They are held in Red
// Hood's handgun aim from the Red Hood story pack, which sits in its whole playable package, so
// that is only loaded once he first takes them up and only when there is room. Without it he holds
// them in his rifle stance. Hold fire to shoot right, left, right in turn. Each hit staggers a
// thug and a few put them down.
static partial class ArkhamKnight
{
    const string GunPackage = "Hideout_A3_Cine_Ch8_Red_Hood_Part1";
    const string LeftGunPath = "Red_Hood_Weaponry.Mesh.Red_Hood_Weaponry_LEFT";
    const string RightGunPath = "Red_Hood_Weaponry.Mesh.Red_Hood_Weaponry_RIGHT";
    const string PistolFlashPath = "VFX_Cinematics.Particles.General.Handgun_muzzleflash";

    const string RedHoodPackage = "Playable_DLCRedHood_SF";
    const string PistolSetPath = "DLC0_Anim_RedHood_Gadget.Code.RH_HandGun";
    // His handgun shot, held still where the arm is out and the gun points ahead.
    static readonly FName PistolAim = new("Ricochet_Stand_Throw");
    const float PistolAimAt = 0.18f;
    const string PistolSoundPath = "Ww_MDLC00_CHR_Redhood_Guns.Play_RH_Gun_Fire_Single";
    const string PistolTrailPath = "DLC_VFX_Combat.Particles.VFX__RHood_Bullet_Trail_NEW";
    const string RightFlashPath = "PFX_RedHood.Emmiter.RedHood_GUNS_muzzleflash";
    const string LeftFlashPath = "PFX_RedHood.Emmiter.RedHood_GUNS_muzzleflash_Left";
    const string RicochetPath = "PFX_RedHood.Emitter.VFX_BulletRicochet";
    const string PistolHitSoundPath = "Ww_MDLC00_CHR_Redhood_Guns.Play_RH_Bullet_Impact_Human";

    // How far up the gun's height from its middle the barrel runs.
    const float BarrelHigh = 0.45f;

    // Where each pistol sits on its gun bone so it lies in the grip, found by eye in game.
    static readonly Vector3 RightGunAt = new(0f, 0f, -5f);
    static readonly Vector3 LeftGunAt = new(-7f, 0f, -3f);
    static readonly Rotator LeftGunTurn = new(-45f, 0f, 0f);

    const float PistolEvery = 0.14f;
    const float RightTrailSkip = 40f;
    const float LeftTrailSkip = 150f;
    const float PistolKick = 3f;
    const int HitsToDrop = 3;
    const int PistolDamage = 250;
    const float PistolPush = 350f;
    const float PistolThrow = 700f;
    const float PistolVehicleShare = 0.03f;
    const float PistolSplash = 20f;
    const float PistolHearing = 3500f;

    static bool _pistols;
    static bool _pistolsTried;
    static AnimSet? _pistolSet;
    static SkeletalMesh? _leftGunMesh;
    static SkeletalMesh? _rightGunMesh;
    static ParticleSystem? _pistolFlash;
    static ParticleSystem? _rightFlash;
    static ParticleSystem? _leftFlash;
    static ParticleSystem? _pistolTrail;
    static ParticleSystem? _ricochet;
    static AkEvent? _pistolSound;
    static AkEvent? _pistolHitSound;
    static SkeletalMeshComponent? _leftGun;
    static SkeletalMeshComponent? _rightGun;
    static SkeletalMeshComponent? _gunsOn;
    static FName _leftHand = FName.None;
    static bool _rightNext = true;
    static float _nextPistol;
    static readonly Dictionary<nint, int> Hits = new();

    static Outcome UseRifle()
    {
        if (!Option.On)
        {
            return Outcome.Failed("ARKHAM KNIGHT IS OFF");
        }

        _pistols = false;
        return Outcome.Done("SNIPER RIFLE");
    }

    static Outcome UsePistols()
    {
        if (!Option.On)
        {
            return Outcome.Failed("ARKHAM KNIGHT IS OFF");
        }

        LoadPistols();
        if (_leftGunMesh is null || _rightGunMesh is null)
        {
            return Outcome.Failed("PISTOLS NOT FOUND");
        }

        _pistols = true;
        return Outcome.Done(_pistolSet is null ? "PISTOLS, RIFLE STANCE" : "TWIN PISTOLS");
    }

    static void LoadPistols()
    {
        if (_pistolsTried)
        {
            return;
        }

        _pistolsTried = true;
        if (Game.FindObject<SkeletalMesh>(LeftGunPath) is not { IsValid: true } || Game.FindObject<SkeletalMesh>(RightGunPath) is not { IsValid: true }
            || Game.FindObject<ParticleSystem>(PistolFlashPath) is not { IsValid: true })
        {
            LoadPackage(GunPackage, "pistols");
        }

        if (FindPistolSet() is null && GameFiles.HasPackage(RedHoodPackage))
        {
            LoadPackage(RedHoodPackage, "handgun aim from Red Hood's pack");
        }

        _leftGunMesh = Kept(Game.FindObject<SkeletalMesh>(LeftGunPath));
        _rightGunMesh = Kept(Game.FindObject<SkeletalMesh>(RightGunPath));
        _pistolFlash = Kept(Game.FindObject<ParticleSystem>(PistolFlashPath));
        _rightFlash = Kept(Game.FindObject<ParticleSystem>(RightFlashPath));
        _leftFlash = Kept(Game.FindObject<ParticleSystem>(LeftFlashPath));
        _pistolTrail = Kept(Game.FindObject<ParticleSystem>(PistolTrailPath));
        _ricochet = Kept(Game.FindObject<ParticleSystem>(RicochetPath));
        _pistolSet = Kept(FindPistolSet());
        _pistolSound = Kept(Game.FindObject<AkEvent>(PistolSoundPath));
        _pistolHitSound = Kept(Game.FindObject<AkEvent>(PistolHitSoundPath));
        if (_pistolSet is null)
        {
            Log.Info("Red Hood's handgun aim isn't loaded, so the Arkham Knight holds his pistols in his rifle stance.");
        }
    }

    static AnimSet? FindPistolSet() =>
        Game.FindObject<AnimSet>(PistolSetPath) is { IsValid: true } set && set.FindAnimSequence(PistolAim) is { IsValid: true } ? set : null;

    // Both pistols go in his hands while they are raised, the right on his gun bone and the left
    // on the gun bone of his other hand, or that hand when it has none.
    static void Pistols(RPawnPlayer? player, SkeletalMeshComponent? body)
    {
        var drawn = _pistols && player is not null && body is not null && _leftGunMesh is { IsValid: true } && _rightGunMesh is { IsValid: true }
            && Arms.Raised >= Drawn;
        if (drawn && (_leftGun is not { IsValid: true } || _rightGun is not { IsValid: true } || _gunsOn is not { IsValid: true } || _gunsOn.Ptr != body!.Ptr))
        {
            PutAwayPistols();
            _leftHand = LeftHand(body!);
            _rightGun = Gun(player!, body!, _rightGunMesh!, new FName(GunBone), RightGunAt, default);
            _leftGun = Gun(player!, body!, _leftGunMesh!, _leftHand, LeftGunAt, LeftGunTurn);
            _gunsOn = body;
        }

        foreach (var gun in new[] { _leftGun, _rightGun })
        {
            if (gun is { IsValid: true } shown && shown.HiddenGame == drawn)
            {
                shown.SetHidden(!drawn);
            }
        }
    }

    static SkeletalMeshComponent Gun(RPawnPlayer player, SkeletalMeshComponent body, SkeletalMesh mesh, FName bone, Vector3 at, Rotator turn)
    {
        var gun = Made.Tracked(new SkeletalMeshComponent(player));
        gun.SetActorCollision(false, false);
        gun.SetSkeletalMesh(mesh, false);
        gun.bCastDynamicShadow = body.bCastDynamicShadow;
        gun.SetLightingChannels(body.LightingChannels);
        gun.SetDepthPriorityGroup(body.DepthPriorityGroup);
        gun.SetOwnerNoSee(body.bOwnerNoSee);
        body.AttachComponent(gun, bone, at, turn, Vector3.One);
        return gun;
    }

    // A second gun bone when the body has one, otherwise the left hand.
    static FName LeftHand(SkeletalMeshComponent body)
    {
        FName? hand = null;
        var count = body.LocalAtoms.Count;
        for (var i = 0; i < count; i++)
        {
            var name = body.GetBoneName(i);
            var text = name.ToString() ?? "";
            if (text.StartsWith(GunBone, StringComparison.OrdinalIgnoreCase) && !text.Equals(GunBone, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"The Arkham Knight's left pistol goes on {text}.");
                return name;
            }

            if (hand is null && text.Contains("Hand", StringComparison.OrdinalIgnoreCase)
                && (text.StartsWith("L", StringComparison.OrdinalIgnoreCase) || text.Contains("_L", StringComparison.OrdinalIgnoreCase) || text.Contains("Left", StringComparison.OrdinalIgnoreCase)))
            {
                hand = name;
            }
        }

        Log.Info($"The Arkham Knight's left pistol goes on {hand?.ToString() ?? "his gun bone"}.");
        return hand ?? new FName(GunBone);
    }

    // A pistol's muzzle and the way it points. The barrel runs along the gun's longest side and
    // ends on the side away from the grip, which sits where the gun is held. The muzzle is on the
    // barrel's line, high on the gun as a pistol's barrel is.
    static (Vector3 From, Vector3 Way)? GunBarrel(SkeletalMeshComponent? gun)
    {
        if (gun is not { IsValid: true, HiddenGame: false, SkeletalMesh: { IsValid: true } mesh } shown)
        {
            return null;
        }

        var box = mesh.Bounds;
        var middle = box.Origin;
        var size = box.BoxExtent;
        var side = size.X >= size.Y && size.X >= size.Z ? Vector3.UnitX : size.Y >= size.Z ? Vector3.UnitY : Vector3.UnitZ;
        var half = Vector3.Dot(size, side);
        var ahead = Vector3.Dot(middle, side) >= 0f ? side : -side;
        var up = side == Vector3.UnitZ ? Vector3.UnitX : Vector3.UnitZ;
        var tip = middle - side * Vector3.Dot(middle, side) + ahead * (MathF.Abs(Vector3.Dot(middle, side)) + half)
            + up * (Vector3.Dot(size, up) * BarrelHigh);

        var place = Unsafe.As<GameObject.FMatrix, Matrix4x4>(ref shown.LocalToWorld);
        var way = Vector3.TransformNormal(ahead, place);
        if (way.LengthSquared() < 1e-6f)
        {
            return null;
        }

        return (Vector3.Transform(tip, place), Vector3.Normalize(way));
    }

    static void ShootPistols(RPawnPlayer player)
    {
        // Looked at every tick, so the crosshair knows when it is on a thug.
        _onThug = false;
        if (Aim.On && GunBarrel(_rightGun) is { } held)
        {
            AimPoint(held.From);
        }

        var now = World.Now;
        if (!Aim.Fire || Arms.Raised < 0.9f || now < _nextPistol)
        {
            return;
        }

        var right = _rightNext;
        var gun = right ? _rightGun : _leftGun;
        _rightNext = !_rightNext;
        if (GunBarrel(gun) is not { } muzzle)
        {
            return;
        }

        _nextPistol = now + PistolEvery;
        Arms.Kick(PistolKick);
        var from = muzzle.From;
        var way = Way(from, muzzle.Way);
        var reach = PistolRound(player, from, way);
        // Red Hood's own flashes and trail when his pack is loaded. The sniper's trail starts its
        // round well behind where it is placed, which shows coming out the back of a pistol.
        Brief((right ? _rightFlash : _leftFlash) ?? _pistolFlash ?? _flashLook, from, muzzle.Way, FlashSeconds, null);
        // Seen over his right shoulder the left pistol is behind his head, so its round would
        // cross the screen from there. Its trail starts where the round comes out past him.
        var skip = MathF.Min(right ? RightTrailSkip : LeftTrailSkip, reach * 0.5f);
        Brief(_pistolTrail ?? _trailLook, from + way * skip, way, TrailSeconds, from + way * reach);
        if (reach < Range)
        {
            Brief(_ricochet, from + way * reach, -way, FlashSeconds * 4f, null);
        }

        Play(player, _pistolSound ?? _fireSound);
        Villains.Alert(player, from, PistolHearing);
    }

    // How far the shot got. The first thug in the way staggers, and goes down after a few hits.
    static float PistolRound(RPawnPlayer player, Vector3 from, Vector3 way)
    {
        var reach = Line(player, from, way);
        var car = VehicleInLine(from, way, reach);
        if (InLine.Count > 0 && (car is not { } blocking || InLine[0].Ahead < blocking.Ahead))
        {
            var (ahead, thug) = InLine[0];
            Hits.TryGetValue(thug.Ptr, out var hits);
            hits++;
            var fall = hits >= HitsToDrop;
            if (fall)
            {
                Hits.Remove(thug.Ptr);
            }
            else
            {
                if (Hits.Count > 64)
                {
                    Hits.Clear();
                }

                Hits[thug.Ptr] = hits;
            }

            Villains.Knock(player, thug, PistolDamage, way, fall ? PistolThrow : PistolPush, fall);
            _hitAt = World.Now;
            if (_pistolHitSound is { IsValid: true } smack && thug.IsValid)
            {
                thug.StartAudioEvent(smack);
            }

            return ahead;
        }

        if (car is { } struck)
        {
            var damage = Math.Max(1, (int)MathF.Round(Math.Max(struck.Car.Health, struck.Car.HealthMax) * PistolVehicleShare));
            struck.Car.TakeDamage(damage, player.Controller, from + way * struck.Ahead, way * 100f, RDmgType_Explosion.StaticClass(), default, player);
            return struck.Ahead;
        }

        if (reach < Range)
        {
            Struck.Clear();
            Villains.Break(player, from + way * reach, PistolSplash, Struck);
        }

        return reach;
    }

    static void PutAwayPistols()
    {
        if (_gunsOn is { IsValid: true } holder)
        {
            foreach (var gun in new[] { _leftGun, _rightGun })
            {
                if (gun is { IsValid: true })
                {
                    holder.DetachComponent(gun);
                }
            }
        }

        _leftGun = null;
        _rightGun = null;
        _gunsOn = null;
    }

    static void UnloadPistols()
    {
        PutAwayPistols();
        _pistols = false;
        _pistolsTried = false;
        _pistolSet = null;
        _leftGunMesh = null;
        _rightGunMesh = null;
        _pistolFlash = null;
        _rightFlash = null;
        _leftFlash = null;
        _pistolTrail = null;
        _ricochet = null;
        _pistolSound = null;
        _pistolHitSound = null;
        _leftHand = FName.None;
        _rightNext = true;
        _nextPistol = 0f;
        Hits.Clear();
    }
}
