#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// The mech's beam, played the way its fight plays it. Held, it charges up and holds the charge
// with the glow on its claw, turning to where you aim. Let go once charged and it fires. The
// beam's contact starts at its feet and runs out along the ground, and everyone it crosses is
// blown down. Its effects only ship in the orphanage level, which is loaded with the mech.
//
// A big drop also lands with the smash's dust and thump at its feet, knocking down those around.
static partial class RiddlerMech
{
    static readonly FName ChargeMove = new("Beam_Charge");
    static readonly FName ChargeHold = new("Beam_Charge_Idle");
    static readonly FName FireMove = new("Beam_Fire");
    static readonly FName ClawSocket = new("Claw");
    static readonly FName BeamStarts = new("StartBeam");
    static readonly FName BeamStops = new("StopBeam");

    const string BeamPath = "BFX_RiddlerMech.Particles.Beam_Fire";
    const string ImpactPath = "BFX_RiddlerMech.Particles.Beam_Impact";
    const string GlowPath = "BFX_RiddlerMech.Particles.Beam_Charge";
    const string DustPath = "BFX_RiddlerMech.Particles.GroundSmash";
    const string ThumpPath = "Ww_NPC_Riddler_Mech.Play_Riddler_Mech_Melee_Hit_Ground";

    // The game's beam runs out at 20 m a second and hits within 1 m of where it touches. It is
    // given a little more reach to the sides, and stops at 40 m.
    const float BeamSpeed = 2000f;
    const float BeamReach = 4000f;
    const float BeamWidth = 150f;
    const float BeamHeight = 220f;
    const int BeamDamage = 40;
    const float BeamForce = 1600f;
    const float BeamWait = 3f;
    const float BeamWreck = 0.5f;
    const int BeamParked = 60;
    const float LandWreck = 0.2f;
    const int LandParked = 30;

    // How fast it turns to where you aim, charging and firing.
    const float ChargeTurn = 360f;
    const float FireTurn = 60f;

    // A drop this high lands as a smash, reaching this far at the least and growing with the drop.
    const float LandHeight = 500f;
    const float LandReach = 300f;
    const float LandReachMost = 900f;
    const int LandDamage = 20;
    const float LandForce = 1200f;

    static ParticleSystem? _beamLook;
    static ParticleSystem? _impactLook;
    static ParticleSystem? _glowLook;
    static ParticleSystem? _dustLook;
    static AkEvent? _thump;

    static Emitter? _beam;
    static Emitter? _impact;
    static Emitter? _glow;

    static bool _charged;
    static float _beamFrom;
    static float _beamTo;
    static float _beamLength;
    static float _nextBeam;
    static readonly HashSet<nint> Struck = new();

    static bool _falling;
    static float _fallTop;

    static bool BeamLoaded() =>
        Game.FindObject<ParticleSystem>(BeamPath) is { IsValid: true } && Game.FindObject<ParticleSystem>(GlowPath) is { IsValid: true };

    static void LoadBeam()
    {
        _beamLook = Kept(Game.FindObject<ParticleSystem>(BeamPath));
        _impactLook = Kept(Game.FindObject<ParticleSystem>(ImpactPath));
        _glowLook = Kept(Game.FindObject<ParticleSystem>(GlowPath));
        _dustLook = Kept(Game.FindObject<ParticleSystem>(DustPath));
        _thump = Kept(Game.FindObject<AkEvent>(ThumpPath));
    }

    // The charge has just started.
    static void Charging()
    {
        _charged = false;
        Struck.Clear();
    }

    // How far the beam is charged, from nothing to full, for the HUD.
    static float Charge => _move switch
    {
        Move.Charge when _charged => 1f,
        Move.Charge when Played[Action] is { IsValid: true } played && played.GetAnimPlaybackLength() > 0f
            => Math.Clamp(played.CurrentTime / played.GetAnimPlaybackLength(), 0f, 1f),
        Move.Fire => 1f,
        _ => 0f,
    };

    static float BeamReady => Math.Clamp(1f - (_nextBeam - World.Now) / BeamWait, 0f, 1f);

    static void Beam(RPawnPlayer player, AnimNodeSequence played)
    {
        if (World.Controller is { } controller)
        {
            TurnTo(player, Teleport.Flat(controller.Rotation.ToDirection()), _move == Move.Fire ? FireTurn : ChargeTurn);
        }

        if (_move == Move.Charge)
        {
            Glow();
            if (!_charged && (!played.bPlaying || played.CurrentTime >= played.GetAnimPlaybackLength() - 0.01f))
            {
                _charged = true;
                Play(ChargeHold, loop: true);
            }

            if (Hotkeys.IsHeld(BeamKey))
            {
                return;
            }

            // Let go too soon and the charge is lost.
            if (!_charged)
            {
                Unact();
                return;
            }

            if (Play(FireMove, loop: false) is not { } firing)
            {
                Unact();
                return;
            }

            _move = Move.Fire;
            Speak(BeamVoice, BeamLines);
            Villains.PutOut(ref _glow);
            BeamTimes(firing, out _beamFrom, out _beamTo);
            _beamLength = 0f;
            return;
        }

        var now = played.CurrentTime;
        if (now >= _beamFrom && now <= _beamTo)
        {
            Fire(player);
        }
        else if (now > _beamTo)
        {
            Villains.PutOut(ref _beam);
            Villains.PutOut(ref _impact);
        }

        if (!played.bPlaying || now >= played.GetAnimPlaybackLength() - EndEarly)
        {
            Unact();
        }
    }

    static void BeamTimes(AnimNodeSequence played, out float from, out float to) =>
        BeamWindow(played.AnimSeq, played.GetAnimPlaybackLength(), out from, out to);

    // How far the beam gets before its firing move turns it off, since it only runs out while the
    // move has it on. Read once from the move.
    static float? _beamRange;

    static float BeamRange()
    {
        if (_beamRange is { } known)
        {
            return known;
        }

        if (Copies[Action] is not { IsValid: true } copy || copy.FindAnimSequence(FireMove) is not { IsValid: true } sequence)
        {
            return BeamReach;
        }

        var rate = sequence.RateScale > 0.01f ? sequence.RateScale : 1f;
        BeamWindow(sequence, sequence.SequenceLength, out var from, out var to);
        _beamRange = Math.Clamp(BeamSpeed * (to - from) / rate, 0f, BeamReach);
        return _beamRange.Value;
    }

    // When the beam is on in its firing move, from the move's own StartBeam and StopBeam.
    static void BeamWindow(AnimSequence? sequence, float length, out float from, out float to)
    {
        from = length * 0.15f;
        to = length * 0.85f;
        if (sequence is not { IsValid: true })
        {
            return;
        }

        var notifies = sequence.Notifies;
        for (var i = 0; i < notifies.Count; i++)
        {
            if (notifies[i].Notify is AnimNotify_Script { IsValid: true } named)
            {
                if (named.NotifyName == BeamStarts)
                {
                    from = notifies[i].Time;
                }
                else if (named.NotifyName == BeamStops)
                {
                    to = notifies[i].Time;
                }
            }
        }
    }

    static void TurnTo(RPawnPlayer player, Vector3 way, float rate)
    {
        if (way.LengthSquared() < 0.01f)
        {
            return;
        }

        var wanted = MathF.Atan2(way.Y, way.X) * (180f / MathF.PI);
        var turn = wanted - player.Rotation.Yaw;
        turn -= 360f * MathF.Round(turn / 360f);
        var most = rate * World.Delta;
        var facing = new Rotator(0f, player.Rotation.Yaw + Math.Clamp(turn, -most, most), 0f);
        player.SetRotation(facing);

        // The game turns the pawn toward the way it wants each tick, so that is set too.
        player.DesiredRotation = facing;
    }

    // The charge's glow, kept on the claw.
    static void Glow()
    {
        if (_mech is not { IsValid: true } mech || !mech.GetSocketWorldLocationAndRotation(ClawSocket, out var at, out var turn, 0))
        {
            return;
        }

        Villains.Show(ref _glow, _glowLook, at, turn);
    }

    // The beam from the claw to where it touches, which runs out along the ground ahead of the
    // mech, stopping at whatever is in the way.
    static void Fire(RPawnPlayer player)
    {
        if (_mech is not { IsValid: true } mech || !mech.GetSocketWorldLocationAndRotation(ClawSocket, out var claw, out _, 0))
        {
            return;
        }

        _beamLength = MathF.Min(_beamLength + BeamSpeed * World.Delta, BeamReach);
        var ahead = Teleport.Flat(player.Rotation.ToDirection());
        var feet = player.Location - Vector3.UnitZ * player.CylinderComponent.CollisionHeight;
        var aim = feet + ahead * _beamLength;
        var touch = aim;
        var facing = Vector3.UnitZ;
        if (Crosshair.Line(player, claw, aim, Crosshair.Meets.Level, out var wall))
        {
            touch = wall.Spot + wall.Normal * 20f;
            facing = wall.Normal;
        }

        Villains.Show(ref _beam, _beamLook, claw, Rotator.FromDirection(touch - claw), touch);
        Villains.Show(ref _impact, _impactLook, touch, Rotator.FromDirection(facing));
        Villains.Wreck(player, touch, BeamWidth, BeamWreck, BeamParked, Struck);
        Villains.Break(player, touch, BeamWidth, Struck);

        foreach (var thug in Crowd.StandingEnemies())
        {
            if (Struck.Contains(thug.Ptr))
            {
                continue;
            }

            var off = thug.Location - touch;
            if (off.X * off.X + off.Y * off.Y > BeamWidth * BeamWidth || MathF.Abs(off.Z) > BeamHeight)
            {
                continue;
            }

            Struck.Add(thug.Ptr);
            var push = Vector3.Normalize(ahead + Vector3.UnitZ * 0.6f);
            if (Villains.Knock(player, thug, BeamDamage, push, BeamForce, fall: true))
            {
                _solved++;
            }
        }
    }

    static void PutOutBeam()
    {
        Villains.PutOut(ref _glow);
        Villains.PutOut(ref _beam);
        Villains.PutOut(ref _impact);
        _charged = false;
    }

    // A big drop lands as a smash at its feet.
    static void Land(RPawnPlayer player)
    {
        var height = player.Location.Z;
        if (player.Physics == Actor.EPhysics.PHYS_Falling)
        {
            _fallTop = _falling ? MathF.Max(_fallTop, height) : height;
            _falling = true;
            return;
        }

        if (!_falling)
        {
            return;
        }

        _falling = false;
        var drop = _fallTop - height;
        if (player.Physics != Actor.EPhysics.PHYS_Walking || drop < LandHeight)
        {
            return;
        }

        var feet = player.Location - Vector3.UnitZ * player.CylinderComponent.CollisionHeight;
        if (_dustLook is { IsValid: true } dust && Game.TryGetWorldInfo(out var world) && world.GRI?.ObjectPool is { IsValid: true } pool)
        {
            pool.SpawnEmitter(dust, feet, new Rotator(0f, player.Rotation.Yaw, 0f), 0, 1f);
        }

        if (_thump is { IsValid: true } thump)
        {
            player.StartAudioEvent(thump);
        }

        var reach = Math.Clamp(LandReach + (drop - LandHeight) * 0.3f, LandReach, LandReachMost);
        var flattened = 0;
        foreach (var thug in Crowd.StandingEnemies())
        {
            var off = thug.Location - feet;
            if (off.X * off.X + off.Y * off.Y > reach * reach || MathF.Abs(off.Z) > BeamHeight)
            {
                continue;
            }

            var away = Teleport.Flat(off);
            var push = Vector3.Normalize((away.LengthSquared() > 1f ? Vector3.Normalize(away) : Vector3.UnitX) + Vector3.UnitZ * 0.6f);
            if (Villains.Knock(player, thug, LandDamage, push, LandForce, fall: true))
            {
                flattened++;
            }
        }

        flattened += Villains.Wreck(player, feet, reach, LandWreck, LandParked);
        Villains.Break(player, feet, reach);
        if (flattened > 0)
        {
            _solved += flattened;
            Speak(SmashVoice, SmashLines);
        }
    }
}
