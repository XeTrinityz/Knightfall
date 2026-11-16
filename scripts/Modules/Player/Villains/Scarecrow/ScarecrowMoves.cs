#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// His moves, each one of his own played once on an action copy. While one plays Batman stands
// still under him, and the ground it covers carries the pawn. His needle is the jab he makes at
// Batman on Stagg's airship, cut where Batman counters it.
static partial class Scarecrow
{
    enum Move
    {
        None,
        Needle,
        Dart,
        Toxin,
    }

    // His jab, from where he steps in to just after the needle is furthest out. The rest is him
    // being thrown. Measured on his body, the needle tip is furthest out at the hit, about this
    // far ahead of his feet.
    static readonly FName Jab = new("Scarecrow_Attack_BM_Strike");
    const float JabFrom = 0.45f;
    const float JabHit = 1.2f;
    const float JabUntil = 1.32f;
    static readonly FName NeedleTip = new("ScrCrw_Injct_Bone_Needle_A_005");
    static readonly FName Head = new("Bip01_Head");
    const float NeedleAhead = 110f;

    // The bone the needles hang from may not be on the part that is posed, so his hand is used,
    // with the needles reaching about this far past it.
    static readonly FName RightHand = new("Bip01_R_Hand");
    const float NeedleLength = 25f;

    // The needle meets a thug whose middle is this close to its tip, or the thug it was aimed at
    // this close in front of him. His body is about this wide.
    const float TipReach = 85f;
    const float AimedReach = 165f;
    const float NeedleHeight = 150f;

    // He jabs where the camera looks, at an enemy this close and this near that way if there is
    // one. He turns into it this fast.
    const float AimReach = 450f;
    const float AimCone = 0.5f;
    const float TurnRate = 720f;

    // He closes on the thug he aimed at as he steps in, so the needle lands this far from his
    // middle, no faster than this.
    const float LandAt = 95f;
    const float LungeSpeed = 1300f;

    // A press this long after a jab lands starts the next one. One before that is let go, so
    // pressing over and over doesn't stack jabs up.
    const float CancelAfter = 0.08f;

    // A move ends this long before its last frame used, so it blends back into standing.
    const float EndEarly = 0.1f;

    static Move _move;
    static float _hitAt;
    static float _until;
    static bool _hit;
    static Vector2 _lastRoot;
    static RPlayerController? _movesHeld;
    static int _slot = Action;

    // While a move plays the pawn faces Base, which eases to Aim.
    static float _base;
    static float _aim;
    static RPawnVillain? _aimed;

    static bool Acting => _move != Move.None;

    static Outcome Needle()
    {
        if (!Ready(out var player, out var controller))
        {
            return Outcome.Unchanged("SCARECROW IS OFF");
        }

        // Mid-jab, a press once it has landed starts the next one now.
        if (_move == Move.Needle)
        {
            if (!_hit || Played[_slot] is not { IsValid: true } jabbing || jabbing.CurrentTime < _hitAt + CancelAfter)
            {
                return Outcome.Unchanged("NOT YET");
            }

            return StartNeedle(player, controller) ? Outcome.Done("NEEDLE") : Outcome.Unchanged("NOT READY");
        }

        if (!Free(player, controller))
        {
            return Outcome.Unchanged("NOT NOW");
        }

        return StartNeedle(player, controller) ? Outcome.Done("NEEDLE") : Outcome.Unchanged("NOT READY");
    }

    static bool Ready(out RPawnPlayer player, out RPlayerController controller)
    {
        player = null!;
        controller = null!;
        if (!Option.On || WornBody() is null || Copies[Action] is not { IsValid: true } || World.Player is not { IsValid: true } pawn
            || World.Controller is not { } brain)
        {
            return false;
        }

        player = pawn;
        controller = brain;
        return true;
    }

    // Not already in a move, on his feet and not in one of Batman's.
    static bool Free(RPawnPlayer player, RPlayerController controller) =>
        !Acting && player.Physics == Actor.EPhysics.PHYS_Walking && player.CurrentSpecialMove is not { IsValid: true }
        && !Shaken(player) && !controller.bCinematicMode;

    static bool StartNeedle(RPawnPlayer player, RPlayerController controller)
    {
        var aim = AimFor(player, controller);
        if (Play(Jab, JabFrom, JabUntil, player.Rotation.Yaw) is null)
        {
            return false;
        }

        _aim = aim;
        _move = Move.Needle;
        _hit = false;
        _hitAt = JabHit;
        Hold(controller);
        return true;
    }

    // Batman stands still under him while he moves.
    static void Hold(RPlayerController controller)
    {
        if (_movesHeld is null)
        {
            controller.IgnoreMoveInput(true);
            _movesHeld = controller;
        }
    }

    // Sets the other action copy to a move, from a point in it, with the pawn facing as it is.
    // The one that played last fades out under it.
    static AnimNodeSequence? Play(FName name, float from, float until, float facing)
    {
        var slot = _slot == Action ? NextAction : Action;
        if (Played[slot] is not { IsValid: true } played || Copies[slot] is not { IsValid: true } copy)
        {
            return null;
        }

        played.SetAnim(name);
        if (played.AnimSeq is not { IsValid: true } sequence)
        {
            return null;
        }

        played.PlayAnim(false, 1f, from);
        copy.ForceSkelUpdate();
        _slot = slot;
        _until = MathF.Min(until, sequence.SequenceLength);
        _lastRoot = Root(copy);
        _base = facing;
        _aim = facing;
        _hit = false;
        return played;
    }

    // The time of the first script mark of a name in a move, like where his dart leaves.
    static float Marked(FName name, string mark, float otherwise)
    {
        var slot = _slot;
        if (Played[slot] is { IsValid: true, AnimSeq: { IsValid: true } sequence } && sequence.SequenceName == name)
        {
            var notifies = sequence.Notifies;
            for (var i = 0; i < notifies.Count; i++)
            {
                if (notifies[i].Notify is AnimNotify_Script script && script.NotifyName.ToString() == mark)
                {
                    return notifies[i].Time;
                }
            }
        }

        return otherwise;
    }

    // Every tick while a move plays.
    static void Act(RPawnPlayer player, SkeletalMeshComponent own, SkeletalMeshComponent? body)
    {
        if (!Acting)
        {
            return;
        }

        if (body is null || Played[_slot] is not { IsValid: true } played || Copies[_slot] is not { IsValid: true } copy
            || player.Physics != Actor.EPhysics.PHYS_Walking || player.CurrentSpecialMove is { IsValid: true } || Shaken(player))
        {
            // A hit, a fall or one of Batman's own moves takes over, and shows.
            Unact();
            return;
        }

        Close(player, played);

        var turn = Wrap(_aim - _base);
        var most = (_move == Move.Dart ? DartTurnRate : TurnRate) * World.Delta;
        _base += Math.Clamp(turn, -most, most);
        var facing = new Rotator(0f, _base, 0f);
        player.SetRotation(facing);
        player.DesiredRotation = facing;

        // The ground the move covers carries the pawn.
        var root = Root(copy);
        var step = root - _lastRoot;
        _lastRoot = root;
        if (step.LengthSquared() > 0.01f)
        {
            Villains.Stride(player, Turned(new Vector3(step, 0f) * body.Scale, _base + own.Rotation.Yaw + _rootTurn));
        }

        var time = played.CurrentTime;
        if (!_hit && time >= _hitAt)
        {
            _hit = true;
            switch (_move)
            {
                case Move.Needle:
                    Jabbed(player, body);
                    break;
                case Move.Dart:
                    Fire(player, body);
                    break;
                case Move.Toxin:
                    Vent(player);
                    break;
            }
        }

        if (!played.bPlaying || time >= _until - EndEarly)
        {
            Unact();
        }
    }

    // The game can still turn the pawn after the tick, toward where you look or walk. The camera
    // is worked out after that, so a move's facing is put back from there.
    public static void KeepFacing()
    {
        if (!Acting || !Option.On || World.Player is not { IsValid: true } player)
        {
            return;
        }

        if (MathF.Abs(Wrap(_base - player.Rotation.Yaw)) > 0.01f)
        {
            var facing = new Rotator(0f, _base, 0f);
            player.SetRotation(facing);
            player.DesiredRotation = facing;
        }
    }

    // The way to jab, as a yaw: at an enemy where the camera looks, or just where it looks.
    static float AimFor(RPawnPlayer player, RPlayerController controller)
    {
        _aimed = null;
        var look = controller.Rotation.ToDirection() with { Z = 0f };
        if (look.LengthSquared() < 0.01f)
        {
            return player.Rotation.Yaw;
        }

        look = Vector3.Normalize(look);
        RPawnVillain? best = null;
        var bestDistance = AimReach;
        foreach (var thug in Crowd.StandingEnemies())
        {
            var to = Level(thug.Location - player.Location);
            var distance = to.Length();
            if (distance > 1f && distance < bestDistance && MathF.Abs(thug.Location.Z - player.Location.Z) < NeedleHeight
                && Vector3.Dot(to / distance, look) >= AimCone)
            {
                best = thug;
                bestDistance = distance;
            }
        }

        _aimed = best;
        var way = best is not null ? best.Location - player.Location : look;
        return MathF.Atan2(way.Y, way.X) * (180f / MathF.PI);
    }

    // Until the jab lands he keeps turned to the thug he aimed at and closes the gap, spread over
    // the time left, so the needle lands on him.
    static void Close(RPawnPlayer player, AnimNodeSequence played)
    {
        if (_move != Move.Needle || _hit || _aimed is not { IsAlive: true } aimed || !Crowd.Standing(aimed))
        {
            return;
        }

        var to = Level(aimed.Location - player.Location);
        var distance = to.Length();
        if (distance < 1f)
        {
            return;
        }

        _aim = MathF.Atan2(to.Y, to.X) * (180f / MathF.PI);
        var gap = distance - LandAt;
        if (gap <= 0f)
        {
            return;
        }

        var delta = World.Delta;
        var left = MathF.Max(_hitAt - played.CurrentTime, delta);
        Villains.Stride(player, to / distance * MathF.Min(gap * MathF.Min(1f, delta / left), LungeSpeed * delta));
    }

    // The needle meets whoever is at its tip, or the thug it was aimed at if he is right in
    // front. Calm, he panics. Already panicking, the dose is too much and he drops.
    static void Jabbed(RPawnPlayer player, SkeletalMeshComponent body)
    {
        var facing = Vector3.Normalize(new Rotator(0f, _base, 0f).ToDirection() with { Z = 0f });
        var tip = body.MatchRefBone(NeedleTip) >= 0 ? body.GetBoneLocation(NeedleTip, 0)
            : body.MatchRefBone(RightHand) >= 0 ? body.GetBoneLocation(RightHand, 0) + facing * NeedleLength
            : player.Location + facing * NeedleAhead;

        RPawnVillain? met = null;
        if (_aimed is { IsAlive: true } aimed && Crowd.Standing(aimed) && MathF.Abs(aimed.Location.Z - player.Location.Z) < NeedleHeight)
        {
            var off = Level(aimed.Location - player.Location);
            if (off.Length() <= AimedReach && Vector3.Dot(Vector3.Normalize(off), facing) > 0.3f)
            {
                met = aimed;
            }
        }

        if (met is null)
        {
            var nearest = TipReach;
            foreach (var thug in Crowd.StandingEnemies())
            {
                var reach = Level(thug.Location - tip).Length();
                if (reach < nearest && MathF.Abs(thug.Location.Z - player.Location.Z) < NeedleHeight)
                {
                    nearest = reach;
                    met = thug;
                }
            }
        }

        if (met is null || met.DrivenVehicle is { IsValid: true } || met.Base is Vehicle)
        {
            return;
        }

        if (_stab is { IsValid: true } stab)
        {
            player.StartAudioEvent(stab);
        }

        Burst(_splash, tip, NeedleSplash, BurstSeconds);
        if (Panicked(met))
        {
            Overdose(player, met);
        }
        else
        {
            Panic(player, met);
        }
    }

    // Ends the move, and lets Batman walk again. Only the controller that was held is let go,
    // since the game counts these.
    static void Unact()
    {
        if (_move == Move.Toxin)
        {
            _nextToxin = World.Now + Villains.Wait(ToxinWait);
        }

        _move = Move.None;
        _aimed = null;
        if (_movesHeld is { IsValid: true } held)
        {
            held.IgnoreMoveInput(false);
        }

        _movesHeld = null;
    }

    static Vector2 Root(SkeletalMeshComponent copy)
    {
        var atoms = copy.LocalAtoms;
        return atoms.Count > 0 ? new Vector2(atoms[0].Translation.X, atoms[0].Translation.Y) : Vector2.Zero;
    }

    // Flattened onto the ground, keeping its length.
    static Vector3 Level(Vector3 off) => off with { Z = 0f };

    static float Wrap(float degrees)
    {
        degrees %= 360f;
        return degrees > 180f ? degrees - 360f : degrees < -180f ? degrees + 360f : degrees;
    }
}
