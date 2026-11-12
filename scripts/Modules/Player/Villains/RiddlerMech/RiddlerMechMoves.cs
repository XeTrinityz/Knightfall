#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// The mech's own moves, each one of its moves from the fight played once on the action copy.
// While one plays you can't walk, and the ground it covers in the move carries the pawn, so a
// hop back really hops back.
static partial class RiddlerMech
{
    enum Move
    {
        None,
        Smash,
        Hop,
        Charge,
        Fire,
        Surge,
    }

    static readonly FName SmashMove = new("Melee_Attack");
    static readonly FName HopMove = new("Evade_Backwards");

    // The smash lands on whatever is under either claw.
    static readonly FName[] Claws = { new("R_Mech_Skin_L_Claw_Btm_001"), new("R_Mech_Skin_R_Claw_Btm_001") };

    // The game's own smash hits robots within 2 m of the claw. Its claws are big, so a little more.
    const float SmashReach = 250f;
    const float SmashHeight = 250f;
    const int SmashDamage = 25;
    const float SmashForce = 1400f;

    // Vehicles lose this share of their full health to a smash, and a parked car this much.
    const float SmashWreck = 0.34f;
    const int SmashParked = 40;

    // It turns to face an enemy this close and this near where the camera looks as it smashes.
    const float AimReach = 900f;
    const float AimCone = 0.5f;

    // Two taps of the hop key this close together hop back.
    const float TapGap = 0.35f;

    // A move ends this long before its last frame, so it blends back into walking.
    const float EndEarly = 0.15f;

    static Move _move;
    static float _hitAt;

    // Where the smash lands, the socket or bone its own dust is spawned on. None means the claws.
    static FName _hitOn;
    static bool _hit;
    static Vector2 _lastRoot;
    static RPlayerController? _movesHeld;
    static float _lastTap = -10f;

    static bool Acting => _move != Move.None;

    static Outcome Hop()
    {
        var now = World.Now;
        var second = now - _lastTap <= TapGap;
        _lastTap = second ? -10f : now;
        return second ? Start(Move.Hop) : Outcome.Unchanged("TAP AGAIN");
    }

    static Outcome Start(Move move)
    {
        if (!Option.On || _mech is not { IsValid: true } || World.Player is not { IsValid: true } player || World.Controller is not { } controller)
        {
            return Outcome.Unchanged("THE MECH IS OFF");
        }

        if (Acting || player.Physics != Actor.EPhysics.PHYS_Walking || player.CurrentSpecialMove is { IsValid: true } || controller.bCinematicMode)
        {
            return Outcome.Unchanged("NOT NOW");
        }

        if (move == Move.Charge && World.Now < _nextBeam)
        {
            return Outcome.Unchanged("BEAM COOLING");
        }

        if (move == Move.Surge && World.Now < _nextSurge)
        {
            return Outcome.Unchanged("SURGE COOLING");
        }

        var name = move switch
        {
            Move.Smash => SmashMove,
            Move.Hop => HopMove,
            Move.Surge => SurgeMove,
            _ => ChargeMove,
        };

        if (Play(name, loop: false) is not { } played)
        {
            return Outcome.Unchanged("NOT READY");
        }

        _move = move;
        _hit = false;
        _hitAt = move is Move.Smash or Move.Surge ? HitTime(played, out _hitOn) : -1f;
        _surgeOut = false;

        switch (move)
        {
            case Move.Smash:
                AimAt(player, controller);
                break;
            case Move.Hop:
                Speak(TauntVoice, HopLines);
                break;
            case Move.Charge:
                Charging();
                break;
        }

        // Batman stands still under it while it moves.
        if (_movesHeld is null)
        {
            controller.IgnoreMoveInput(true);
            _movesHeld = controller;
        }

        return Outcome.Done(move switch
        {
            Move.Smash => "SMASH",
            Move.Hop => "HOP BACK",
            Move.Surge => "SURGE",
            _ => "BEAM",
        });
    }

    // Sets the action copy and Riddler's to a move, from its first frame.
    static AnimNodeSequence? Play(FName name, bool loop)
    {
        if (Played[Action] is not { IsValid: true } played || Copies[Action] is not { IsValid: true } copy)
        {
            return null;
        }

        played.SetAnim(name);
        played.PlayAnim(loop, 1f, 0f);
        copy.ForceSkelUpdate();
        if (PilotPlayed[Action] is { IsValid: true } held)
        {
            held.SetAnim(name);
        }

        _lastRoot = Root(copy);
        return played;
    }

    // When and where the move lands, read from the dust it spawns, which the game times and
    // places at the hit. Its rumble gives the time when there is no dust.
    static float HitTime(AnimNodeSequence played, out FName on)
    {
        on = FName.None;
        var rumble = -1f;
        if (played.AnimSeq is { IsValid: true } sequence)
        {
            var notifies = sequence.Notifies;
            for (var i = 0; i < notifies.Count; i++)
            {
                if (notifies[i].Notify is RAnimNotify_SpawnParticleFX { IsValid: true } dust)
                {
                    on = dust.SocketName;
                    return notifies[i].Time;
                }

                if (rumble < 0f && notifies[i].Notify is RAnimNotify_Rumble)
                {
                    rumble = notifies[i].Time;
                }
            }
        }

        return rumble >= 0f ? rumble : played.GetAnimPlaybackLength() * 0.45f;
    }

    static void AimAt(RPawnPlayer player, RPlayerController controller)
    {
        var look = Teleport.Flat(controller.Rotation.ToDirection());
        RPawnVillain? best = null;
        var bestDistance = AimReach;
        foreach (var thug in Crowd.StandingEnemies())
        {
            var to = Teleport.Flat(thug.Location - player.Location);
            var distance = to.Length();
            if (distance < bestDistance && distance > 1f && Vector3.Dot(to / distance, look) >= AimCone)
            {
                best = thug;
                bestDistance = distance;
            }
        }

        var way = best is not null ? Teleport.Flat(best.Location - player.Location) : look;
        if (way.LengthSquared() > 1f)
        {
            player.SetRotation(new Rotator(0f, MathF.Atan2(way.Y, way.X) * (180f / MathF.PI), 0f));
        }
    }

    // Every tick while a move plays.
    static void Act(RPawnPlayer player, SkeletalMeshComponent own)
    {
        if (!Acting || Played[Action] is not { IsValid: true } played || Copies[Action] is not { IsValid: true } copy)
        {
            return;
        }

        // The ground the move covers carries the pawn, turned the way the mech faces.
        var root = Root(copy);
        var step = (root - _lastRoot) * (_mechPart?.Scale ?? 1f);
        _lastRoot = root;
        if (step.LengthSquared() > 0.01f)
        {
            player.Move(Turned(new Vector3(step, 0f), player.Rotation.Yaw + own.Rotation.Yaw + _turn));
        }

        if (_move == Move.Smash && !_hit && played.CurrentTime >= _hitAt)
        {
            _hit = true;
            Smash(player);
        }

        if (_move is Move.Charge or Move.Fire)
        {
            Beam(player, played);
            return;
        }

        if (_move == Move.Surge)
        {
            Surging(player, played);
            return;
        }

        if (!played.bPlaying || played.CurrentTime >= played.GetAnimPlaybackLength() - EndEarly)
        {
            Unact();
        }
    }

    static Vector2 Root(SkeletalMeshComponent copy)
    {
        var atoms = copy.LocalAtoms;
        return atoms.Count > 0 ? new Vector2(atoms[0].Translation.X, atoms[0].Translation.Y) : Vector2.Zero;
    }

    static void Smash(RPawnPlayer player)
    {
        if (_mech is not { IsValid: true } mech)
        {
            return;
        }

        Span<Vector3> claws = stackalloc Vector3[Claws.Length];
        var spots = 0;
        if (_hitOn != FName.None && mech.GetSocketWorldLocationAndRotation(_hitOn, out var dust, out _, 0))
        {
            claws[spots++] = dust;
        }
        else if (_hitOn != FName.None && mech.MatchRefBone(_hitOn) >= 0)
        {
            claws[spots++] = mech.GetBoneLocation(_hitOn, 0);
        }
        else
        {
            foreach (var claw in Claws)
            {
                claws[spots++] = mech.GetBoneLocation(claw, 0);
            }
        }

        claws = claws[..spots];

        var flattened = 0;
        foreach (var thug in Crowd.StandingEnemies())
        {
            foreach (var claw in claws)
            {
                var off = thug.Location - claw;
                if (off.X * off.X + off.Y * off.Y > SmashReach * SmashReach || MathF.Abs(off.Z) > SmashHeight)
                {
                    continue;
                }

                var away = Teleport.Flat(thug.Location - player.Location);
                var push = Vector3.Normalize((away.LengthSquared() > 1f ? Vector3.Normalize(away) : Vector3.UnitX) + Vector3.UnitZ * 0.5f);
                if (Villains.Knock(player, thug, SmashDamage, push, SmashForce, fall: true))
                {
                    flattened++;
                }

                break;
            }
        }

        var wrecked = new HashSet<nint>();
        foreach (var claw in claws)
        {
            flattened += Villains.Wreck(player, claw, SmashReach, SmashWreck, SmashParked, wrecked);
            Villains.Break(player, claw, SmashReach, wrecked);
        }

        if (flattened > 0)
        {
            _solved += flattened;
            Speak(SmashVoice, SmashLines);
        }
    }

    // Ends the move, and lets Batman walk again. Only the controller that was held is let go,
    // since the game counts these.
    static void Unact()
    {
        if (_move == Move.Fire)
        {
            _nextBeam = World.Now + BeamWait;
        }

        if (_move == Move.Surge)
        {
            _nextSurge = World.Now + SurgeWait;
        }

        PutOutBeam();
        PutOutSurge();
        _move = Move.None;
        if (_movesHeld is { IsValid: true } held)
        {
            held.IgnoreMoveInput(false);
        }

        _movesHeld = null;
    }
}
