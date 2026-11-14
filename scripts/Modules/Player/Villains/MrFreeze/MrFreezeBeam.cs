#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Mr. Freeze's cryo beam, held on fire while he aims. It is drawn the way his tank fight draws
// it, a light cone stretched from his blaster to what it touches, with his blaster's own charge
// glow at the muzzle.
//
// A thug held in the beam is frozen solid with Batman's own freeze blast hit, so the ice, the
// thaw and the frozen takedowns are all the game's. One the freeze blast can't freeze is knocked
// down instead once he has been in it a while. Drones, tanks and street cars lose health while
// it is on them. Parked cars are left alone, since the game only knows how to burn them.
static partial class MrFreeze
{
    const string ConeMeshName = "HelicopterSearchLight";
    const string ConeLookPath = "DLC_VFX_Freeze.Shaders.Freeze_TargetingLaser_M_INST";
    const string ChargePath = "DLC_VFX_Freeze.Cine.Particles.Freeze_Blast_Weapon_Charge_P";
    const string BeamSoundPath = "Ww_DLC14_SEQ_Freeze_Beam.Play_DLC14_SEQ_Freeze_Beam_01";

    static readonly FName MuzzleSocket = new("Gun_VFX");
    static readonly FName RightHand = new("Bip01_R_Hand");

    // The cone's own size, from his tank fight: 100 across where it ends and 360 long, along its
    // Z. It is turned a quarter up from the way it points.
    const float ConeWidth = 100f;
    const float ConeLength = 360f;
    const float ConePitch = 90f;

    // How far the beam reaches, and how wide it is where it ends.
    const float BeamReach = 3000f;
    const float BeamEndWidth = 40f;

    // How close to the beam a thug has to be for it to touch him, and how often it is checked.
    const float BeamTouch = 90f;
    const float HitEvery = 0.1f;

    // Seconds in the beam before a thug freezes solid, or is knocked down when he can't freeze.
    // Out of it this long and he starts warming up from nothing.
    const float ChillToFreeze = 0.4f;
    const float ChillToFall = 1.2f;
    const float ChillForget = 0.5f;
    const float FallForce = 700f;

    // Vehicles it touches lose this share of their full health this often.
    const float CarShare = 0.03f;
    const float CarEvery = 0.25f;
    const float CarReach = 60f;

    // A puff of ice where it touches, this often.
    const float ImpactEvery = 0.35f;

    // Full coolant lasts this many seconds of firing and refills in this many, starting this
    // long after he stops. Run dry, the beam waits for this share to come back. The suit warns
    // when it falls to the last.
    const float DrainSeconds = 8f;
    const float RefillSeconds = 5f;
    const float RefillDelay = 0.6f;
    const float Restart = 0.3f;
    const float LowCoolant = 0.25f;

    static float _coolant = 1f;
    static bool _venting;
    static bool _warnedLow;
    static float _refillAt;

    sealed class Chill
    {
        public float Amount;
        public float Last;
    }

    static StaticMesh? _coneMesh;
    static MaterialInterface? _coneLook;
    static ParticleSystem? _chargeLook;
    static AkEvent? _beamSound;

    static StaticMeshComponent? _cone;
    static RPawnPlayer? _coneOn;
    static Emitter? _charge;
    static RPawnPlayer? _soundOn;
    static bool _firing;
    static Vector3 _beamEnd;
    static float _nextHit;
    static float _nextCar;
    static float _nextImpact;
    static readonly Dictionary<nint, Chill> Chilled = new();
    static readonly List<nint> Warmed = new();

    static void LoadBeam()
    {
        _coneLook = Kept(Game.FindObject<MaterialInterface>(ConeLookPath));
        _beamSound = Kept(Game.FindObject<AkEvent>(BeamSoundPath));
        foreach (var mesh in Game.FindObjects<StaticMesh>())
        {
            if (mesh.IsValid && (mesh.Name.ToString() ?? "").Equals(ConeMeshName, StringComparison.OrdinalIgnoreCase))
            {
                _coneMesh = Kept(mesh);
                break;
            }
        }

        if (_coneMesh is null || _coneLook is null)
        {
            Log.Warn("mr_freeze.beam", $"Mr. Freeze's beam {(_coneMesh is null ? "cone" : "colour")} wasn't found, so his beam is only its glow.");
        }

        if (_beamSound is null)
        {
            Log.Warn("mr_freeze.beam_sound", "Mr. Freeze's beam sound wasn't found, so his beam is silent.");
        }
    }

    static void Beam(RPawnPlayer player)
    {
        var now = World.Now;
        var on = Cool(now);
        if (!on)
        {
            if (_firing)
            {
                StopBeam();
            }

            return;
        }

        if (!_firing)
        {
            _firing = true;
            _chargeLook ??= Kept(Game.FindObject<ParticleSystem>(ChargePath));
            _nextHit = 0f;
            _nextCar = 0f;
            _nextImpact = 0f;
            if (now >= _nextBeamLine)
            {
                _nextBeamLine = now + BeamLineEvery;
                Say("beam");
            }
        }

        var from = Muzzle(player, out _);
        var way = Aim.Target - from;
        var along = way.LengthSquared() > 1f ? Vector3.Normalize(way) : Teleport.Flat(player.Rotation.ToDirection());
        var end = from + along * MathF.Min(way.Length(), BeamReach);
        _beamEnd = Crosshair.Line(player, from, end, Crosshair.Meets.Level, out var wall) ? wall.Spot : end;

        BeamSound(player, true);
        if (now >= _nextHit)
        {
            _nextHit = now + HitEvery;
            Touch(player, from, along);
        }

        if (now >= _nextCar)
        {
            _nextCar = now + CarEvery;
            Villains.Wreck(player, _beamEnd, CarReach, CarShare, 0);
        }

        if (now >= _nextImpact && player.FreezeSpray is { IsValid: true, IceExplodeEffect: { IsValid: true } puff }
            && Game.TryGetWorldInfo(out var world) && world.GRI?.ObjectPool is { IsValid: true } pool)
        {
            _nextImpact = now + ImpactEvery;
            pool.SpawnEmitter(puff, _beamEnd, Rotator.FromDirection(-along), 0, 1f);
        }
    }

    // The beam runs on the suit's coolant. Firing drains it, and it refills once he stops for a
    // moment. Run dry, the suit vents and the beam is off until enough has come back. Low, the
    // suit's computer warns him. True while the beam can fire this tick.
    static bool Cool(float now)
    {
        var delta = World.Delta;
        if (_venting && _coolant >= Restart)
        {
            _venting = false;
            Record(Restored);
        }

        if (Aim.Fire && !_venting && _coolant > 0f)
        {
            _coolant = MathF.Max(0f, _coolant - delta / DrainSeconds);
            _refillAt = now + RefillDelay;
            if (_coolant <= 0f)
            {
                _venting = true;
                Record(Venting);
                Say("warning", always: true);
                return false;
            }

            if (!_warnedLow && _coolant <= LowCoolant)
            {
                _warnedLow = true;
                Say("warning", always: true);
            }

            return true;
        }

        if (now >= _refillAt)
        {
            _coolant = MathF.Min(1f, _coolant + delta / RefillSeconds);
            if (_coolant > LowCoolant)
            {
                _warnedLow = false;
            }
        }

        return false;
    }

    // Where the beam leaves his blaster. Without the muzzle it leaves his right hand.
    static Vector3 Muzzle(RPawnPlayer player, out Rotator turn)
    {
        if (Disguise.WornWith(MuzzleSocket) is { } part && part.GetSocketWorldLocationAndRotation(MuzzleSocket, out var at, out turn, 0))
        {
            return at;
        }

        turn = player.Rotation;
        return FreezeBody() is { } body ? body.GetBoneLocation(RightHand, 0) : player.Location;
    }

    // Thugs along the beam that it can reach.
    static void Touch(RPawnPlayer player, Vector3 from, Vector3 along)
    {
        var length = Vector3.Distance(from, _beamEnd);
        var now = World.Now;
        foreach (var thug in Crowd.StandingEnemies())
        {
            var off = thug.Location - from;
            var ahead = Vector3.Dot(off, along);
            if (ahead < 0f || ahead > length + BeamTouch || Vector3.DistanceSquared(thug.Location, from + along * ahead) > BeamTouch * BeamTouch
                || !player.FastTrace(thug.Location, from, Vector3.Zero, false))
            {
                continue;
            }

            if (Frozen(thug))
            {
                Chilled.Remove(thug.Ptr);
                continue;
            }

            if (!Chilled.TryGetValue(thug.Ptr, out var chill))
            {
                chill = new Chill();
                Chilled[thug.Ptr] = chill;
            }

            if (now - chill.Last > ChillForget)
            {
                chill.Amount = 0f;
            }

            chill.Amount += HitEvery;
            chill.Last = now;
            if (thug.bCanTakeFreezeHit)
            {
                if (chill.Amount >= ChillToFreeze && Freeze(player, thug, along))
                {
                    Chilled.Remove(thug.Ptr);
                    _frozen++;
                    Say("frozen");
                }
            }
            else if (chill.Amount >= ChillToFall && Villains.Knock(player, thug, 1, Vector3.Normalize(along + Vector3.UnitZ * 0.3f), FallForce, fall: true))
            {
                Chilled.Remove(thug.Ptr);
            }
        }

        foreach (var (pointer, chill) in Chilled)
        {
            if (now - chill.Last > ChillForget)
            {
                Warmed.Add(pointer);
            }
        }

        foreach (var pointer in Warmed)
        {
            Chilled.Remove(pointer);
        }

        Warmed.Clear();
    }

    static bool Frozen(RPawnVillain thug) => thug.AIController is { IsValid: true } brain && brain.GetBehaviour(0) is RBMBehaviour_FreezeBlastHit;

    // The game's own freeze blast hit, with the same checks its blast makes first: not in a move
    // with Batman, not being taken down, and not the mech's pilot.
    static bool Freeze(RPawnPlayer player, RPawnVillain thug, Vector3 along)
    {
        if (thug.AIController is not { IsValid: true } brain || !thug.bHostile || brain.GetBehaviour(0) is RBMBehaviour_BeingTakenDown
            || (thug.CombatMove is { IsValid: true } move && player.CombatMove is { IsValid: true } mine && move.Ptr == mine.Ptr)
            || (thug.CannotBeTargettedByPlayer is { IsValid: true } blocked && player.Controller is { } controller && blocked.Ptr == controller.Ptr)
            || (thug.Class.Name.ToString() ?? "").EndsWith("RiddlerMechPilot", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!brain.HandleFreezeBlastHit(player, -along, null!, false))
        {
            return false;
        }

        if (brain.GetBehaviour(0) is RBMBehaviour_FreezeBlastHit)
        {
            thug.StartFreezeBlastLegEffect();
            thug.StartFreezeBlastTorsoEffect();
        }

        return true;
    }

    // The cone and the glow are placed after every mesh is posed, so they sit on the blaster
    // where it is shown this frame.
    public static void Follow()
    {
        if (!_firing || World.Player is not { IsValid: true } player)
        {
            return;
        }

        var from = Muzzle(player, out var muzzleTurn);
        Villains.Show(ref _charge, _chargeLook, from, muzzleTurn);
        if (_coneMesh is not { IsValid: true } || _coneLook is not { IsValid: true })
        {
            return;
        }

        if (_cone is not { IsValid: true } || _coneOn is not { IsValid: true } || _coneOn.Ptr != player.Ptr)
        {
            Uncone();
            MakeCone(player);
        }

        var way = _beamEnd - from;
        var length = way.Length();
        if (_cone is not { IsValid: true } cone || length < 1f)
        {
            return;
        }

        var turn = Rotator.FromDirection(way / length);
        cone.SetTranslation(from);
        cone.SetRotation(new Rotator(turn.Pitch + ConePitch, turn.Yaw, turn.Roll));
        var across = BeamEndWidth / ConeWidth;
        cone.SetScale3D(new Vector3(across, across, length / ConeLength));
        if (cone.HiddenGame)
        {
            cone.SetHidden(false);
        }
    }

    static void MakeCone(RPawnPlayer player)
    {
        var cone = Made.Tracked(new StaticMeshComponent(player));
        cone.SetStaticMesh(_coneMesh!, false);
        cone.SetActorCollision(false, false);
        for (var i = 0; i < cone.GetNumElements(); i++)
        {
            cone.SetMaterial(i, _coneLook!);
        }

        cone.bCastDynamicShadow = false;
        cone.SetAbsolute(true, true, true);
        cone.SetHidden(true);
        player.AttachComponent(cone);
        _cone = cone;
        _coneOn = player;
    }

    static void Uncone()
    {
        if (_cone is { IsValid: true } cone && _coneOn is { IsValid: true } owner)
        {
            owner.DetachComponent(cone);
        }

        _cone = null;
        _coneOn = null;
    }

    static void BeamSound(RPawnPlayer? player, bool on)
    {
        var wanted = on && player is { IsValid: true } && _beamSound is { IsValid: true } ? player : null;
        if (ReferenceEquals(wanted, _soundOn))
        {
            return;
        }

        if (_soundOn is { IsValid: true } was && _beamSound is { IsValid: true } loop)
        {
            was.RemoveLoopingAudioEvent(loop);
        }

        _soundOn = wanted;
        wanted?.AddLoopingAudioEvent(_beamSound!);
    }

    static void StopBeam()
    {
        _firing = false;
        BeamSound(null, false);
        Villains.PutOut(ref _charge);
        if (_cone is { IsValid: true } cone && !cone.HiddenGame)
        {
            cone.SetHidden(true);
        }

        Chilled.Clear();
    }

    static void UnloadBeam()
    {
        StopBeam();
        Uncone();
        _coneMesh = null;
        _coneLook = null;
        _chargeLook = null;
        _beamSound = null;
    }
}
