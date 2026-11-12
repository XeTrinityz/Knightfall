#nullable enable

using System.Numerics;
using System.Runtime.CompilerServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static Knightfall.Turns;
using Atom = BmSDK.GameObject.FBoneAtom;
using Turn = BmSDK.GameObject.FQuat;

namespace Knightfall;

// A villain's own pose for part of his body, laid over the worn look, like his arms while he aims.
// The pose plays on a hidden copy of his body. The bones picked out take it over the worn body's
// pose, and the rest of him moves as you do.
sealed class VillainPose
{
    // How much of the barrel's miss is taken up each tick, the most the body leans to aim, and how
    // long a miss this wide may last before the lean is started over.
    const float LeanGain = 0.35f;
    const float MostTurn = 60f * MathF.PI / 180f;
    const float MostBend = 40f * MathF.PI / 180f;

    // The fastest the lean turns, in degrees a second, so a crosshair that jumps is eased after.
    const float LeanRate = 360f;
    const float ToRadians = MathF.PI / 180f;

    // A shot throws the barrel up, and it settles back over about this long. The aim isn't
    // steered until it has, or the steering would fight the kick.
    const float KickSeconds = 0.18f;
    const float KickSettled = 0.5f * ToRadians;

    readonly Func<string, bool> _takes;
    readonly float _raiseSeconds;
    readonly Action<SkeletalMeshComponent, TArray<Atom>> _reposer;

    SkeletalMeshComponent? _body;
    RPawnPlayer? _player;
    SkeletalMeshComponent? _copy;
    AnimNodeSequence? _playing;
    RPawnPlayer? _copyOn;
    FName _move;
    AnimSet? _moves;
    bool _loop = true;
    bool[] _taken = Array.Empty<bool>();
    nint _takenOf;

    // The lean that brings the barrel onto the crosshair, kept in the villain's own facing so it
    // holds while he turns. Applied is the whole turn in the world the last pose was given.
    Quaternion _lean = Quaternion.Identity;
    Quaternion _applied = Quaternion.Identity;
    bool[] _leans = Array.Empty<bool>();
    int[] _parents = Array.Empty<int>();
    Quaternion[] _turns = Array.Empty<Quaternion>();
    int _leanCount;
    int _firstLean = -1;
    int _lastLean = -1;
    float _kick;

    // Ticks the copy has been working out its pose for. Its first pose after it starts is stale.
    int _posedTicks;
    const int PosedAfter = 3;
    bool _conjugate;

    // Takes says by a bone's name whether it and every bone under it take the pose.
    public VillainPose(float raiseSeconds, Func<string, bool> takes)
    {
        _raiseSeconds = raiseSeconds;
        _takes = takes;
        _reposer = Repose;
    }

    // Only the lean, for a villain whose feature lays his pose on itself and calls LeanOver.
    public VillainPose(float raiseSeconds)
        : this(raiseSeconds, _ => false)
    {
    }

    // How far the pose is laid over, from 0 to 1.
    public float Raised { get; private set; }

    // A pose made for another body or camera seldom points a gun where you look. With these set,
    // the upper body leans until the barrel is on the aim, spread over every bone named LeanAt
    // with or without a number after it, like a spine's. Barrel gives where the barrel is and the
    // way it points, or nothing while there is no gun to read.
    public string? LeanAt { get; init; }

    // Throws the barrel up by this many degrees, as a shot does.
    public void Kick(float degrees) => _kick = MathF.Max(_kick, degrees * ToRadians);

    public Func<(Vector3 From, Vector3 Way)?>? Barrel { get; init; }

    // Holds the move still at this share of its length, for a pose that only exists partway
    // through a move. Nothing lets it play.
    public float? Hold { get; set; }

    // AimAt gives where a gun at a spot should point. Without it the lean is left alone. A move
    // that doesn't loop plays once and holds its last frame.
    public void Update(RPawnPlayer? player, SkeletalMeshComponent? body, AnimSet? moves, FName move, bool up, Func<Vector3, Vector3>? aimAt = null, bool loop = true)
    {
        _body = player is null ? null : body;
        _player = player;
        var ready = player is not null && body is { SkeletalMesh: { IsValid: true } } && moves is { IsValid: true };
        up &= ready;
        if (up && (_copy is not { IsValid: true } || _copyOn is not { IsValid: true } || _copyOn.Ptr != player!.Ptr
            || _copy.SkeletalMesh is not { IsValid: true } shown || shown.Ptr != body!.SkeletalMesh.Ptr))
        {
            Unmake();
            Make(player!, body!.SkeletalMesh, moves!, move, loop);
        }
        else if (up && _playing is { IsValid: true } playing && _copy is { IsValid: true } shown2
            && (_move.Index != move.Index || _move.Number != move.Number || _loop != loop || _moves is null || _moves.Ptr != moves!.Ptr))
        {
            Use(shown2, moves!);
            Play(playing, move, loop);
        }

        if (Hold is { } at && _playing is { IsValid: true } held && held.AnimSeq is { IsValid: true } sequence)
        {
            if (held.bPlaying)
            {
                held.StopAnim();
            }

            var time = Math.Clamp(at, 0f, 1f) * sequence.SequenceLength;
            if (MathF.Abs(held.CurrentTime - time) > 0.001f)
            {
                held.SetPosition(time, false);
            }
        }
        else if (Hold is null && _loop && _playing is { IsValid: true, bPlaying: false } stopped)
        {
            stopped.PlayAnim(true, 1f, stopped.CurrentTime);
        }

        if (player is not null && _copy is { IsValid: true } placed && player.Mesh is { IsValid: true } own)
        {
            Place(placed, own);
        }

        Track(player, up, aimAt, true);

        // A hidden copy only works out its pose while it is used.
        if (_copy is { IsValid: true } copy && copy.bUpdateSkelWhenNotRendered != Raised > 0f)
        {
            copy.bUpdateSkelWhenNotRendered = Raised > 0f;
        }

        _posedTicks = _copy is { IsValid: true, bUpdateSkelWhenNotRendered: true } ? _posedTicks + 1 : 0;

        if (ready)
        {
            Disguise.Repose = _reposer;
        }
        else if (Disguise.Repose == _reposer)
        {
            Disguise.Repose = null;
        }
    }

    // The lean alone, over a pose the villain's own feature makes. Steering can be held off while
    // that pose swings the gun about by itself, like a throw.
    public void UpdateLean(RPawnPlayer? player, SkeletalMeshComponent? body, bool up, Func<Vector3, Vector3> aimAt, bool steer = true)
    {
        _body = player is null ? null : body;
        _player = player;
        Track(player, up && _body is not null, aimAt, steer);
    }

    // Lays the lean over a pose, from the repose of the feature that made it.
    public void LeanOver(SkeletalMeshComponent body, TArray<Atom> saved)
    {
        if (Raised <= 0f || _body is null || body.Ptr != _body.Ptr)
        {
            _applied = Quaternion.Identity;
            return;
        }

        var pose = saved.AsSpan();
        if (_takenOf != body.Ptr || _taken.Length != pose.Length)
        {
            Mark(body, pose.Length);
        }

        Lean(body, pose);
    }

    void Track(RPawnPlayer? player, bool up, Func<Vector3, Vector3>? aimAt, bool steer)
    {
        Raised += ((up ? 1f : 0f) - Raised) * (1f - MathF.Exp(-World.Delta / _raiseSeconds));
        if (!up && Raised < 0.01f)
        {
            Raised = 0f;
        }

        _kick *= MathF.Exp(-World.Delta / KickSeconds);
        if (_kick < 0.01f * ToRadians)
        {
            _kick = 0f;
        }

        if (player is not null && aimAt is not null && steer && Raised > 0.5f && _kick < KickSettled)
        {
            Steer(player, aimAt);
        }
    }

    public void Clear()
    {
        if (Disguise.Repose == _reposer)
        {
            Disguise.Repose = null;
        }

        Unmake();
        _player = null;
        Raised = 0f;
        _taken = Array.Empty<bool>();
        _takenOf = 0;
        _leans = Array.Empty<bool>();
        _parents = Array.Empty<int>();
        _leanCount = 0;
        _firstLean = -1;
        _lastLean = -1;
        _lean = Quaternion.Identity;
        _applied = Quaternion.Identity;
        _kick = 0f;
        _body = null;
    }

    // The barrel was read with last tick's lean on it. Taking that lean back off gives where the
    // pose alone points, and the whole lean wanted is worked out from there each tick. Nothing is
    // added up over time, so a lean held back by its limits can't wind up and stay bent.
    void Steer(RPawnPlayer player, Func<Vector3, Vector3> aimAt)
    {
        if (Barrel?.Invoke() is not { } barrel)
        {
            return;
        }

        var want = aimAt(barrel.From) - barrel.From;
        if (want.LengthSquared() < 1f || barrel.Way.LengthSquared() < 1e-6f)
        {
            return;
        }

        var bare = Vector3.Transform(Vector3.Normalize(barrel.Way), Quaternion.Inverse(_applied));
        var facing = Facing(player);
        var wanted = Limit(Quaternion.Normalize(Quaternion.Inverse(facing) * Between(bare, Vector3.Normalize(want)) * facing));
        var step = Cap(Quaternion.Slerp(Quaternion.Identity, Quaternion.Normalize(wanted * Quaternion.Inverse(_lean)), LeanGain), LeanRate * ToRadians * World.Delta);
        _lean = Limit(Quaternion.Normalize(step * _lean));
    }

    // The lean is split into its turn about the vertical and the bend left over, and each is held
    // within what a body can do without folding up.
    static Quaternion Limit(Quaternion lean)
    {
        var turn = new Quaternion(0f, 0f, lean.Z, lean.W);
        turn = turn.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(turn);
        var bend = Quaternion.Normalize(lean * Quaternion.Inverse(turn));
        return Quaternion.Normalize(Cap(bend, MostBend) * Cap(turn, MostTurn));
    }

    static Quaternion Cap(Quaternion turn, float most)
    {
        var angle = Angle(turn);
        return angle > most ? Quaternion.Slerp(Quaternion.Identity, turn, most / angle) : turn;
    }

    static Quaternion Facing(RPawnPlayer player) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, player.Rotation.Yaw * ToRadians);

    static float Angle(Quaternion turn) => 2f * MathF.Acos(Math.Clamp(MathF.Abs(turn.W), 0f, 1f));

    // The shortest turn that takes one way onto another.
    static Quaternion Between(Vector3 from, Vector3 to)
    {
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        var axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-8f)
        {
            return Quaternion.Identity;
        }

        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
    }

    void Make(RPawnPlayer player, SkeletalMesh mesh, AnimSet moves, FName move, bool loop)
    {
        var copy = Made.Tracked(new SkeletalMeshComponent(player));
        copy.SetActorCollision(false, false);
        copy.SetSkeletalMesh(mesh, false);
        Use(copy, moves);
        var playing = Made.Tracked(new AnimNodeSequence(copy));
        copy.Animations = playing;
        copy.bUpdateSkelWhenNotRendered = true;
        copy.bCastDynamicShadow = false;
        copy.SetHidden(true);
        copy.SetTickGroup(GameObject.ETickingGroup.TG_DuringAsyncWork);
        player.AttachComponent(copy);
        if (player.Mesh is { IsValid: true } own)
        {
            Place(copy, own);
        }

        Play(playing, move, loop);
        _copy = copy;
        _playing = playing;
        _copyOn = player;
    }

    void Play(AnimNodeSequence playing, FName move, bool loop)
    {
        playing.SetAnim(move);
        playing.PlayAnim(loop, 1f, 0f);
        _move = move;
        _loop = loop;
    }

    // A move is found in any set the copy has, so each set used is added once.
    void Use(SkeletalMeshComponent copy, AnimSet moves)
    {
        var sets = copy.AnimSets;
        var known = false;
        for (var i = 0; i < sets.Count && !known; i++)
        {
            known = sets[i] is { IsValid: true } set && set.Ptr == moves.Ptr;
        }

        if (!known)
        {
            sets.Add(moves);
        }

        _moves = moves;
    }

    // The copy sits on the player as the player's own mesh does, so a pose made for a player's
    // mesh faces the way the player faces.
    static void Place(SkeletalMeshComponent copy, SkeletalMeshComponent own)
    {
        if (copy.Translation != own.Translation)
        {
            copy.SetTranslation(own.Translation);
        }

        var turned = copy.Rotation;
        var turn = own.Rotation;
        if (turned.Pitch != turn.Pitch || turned.Yaw != turn.Yaw || turned.Roll != turn.Roll)
        {
            copy.SetRotation(turn);
        }
    }

    void Unmake()
    {
        if (_copy is { IsValid: true } copy && _copyOn is { IsValid: true } owner)
        {
            owner.DetachComponent(copy);
        }

        _copy = null;
        _playing = null;
        _copyOn = null;
        _moves = null;
    }

    void Repose(SkeletalMeshComponent body, TArray<Atom> saved)
    {
        if (Raised <= 0f || _body is null || body.Ptr != _body.Ptr || _copy is not { IsValid: true } copy)
        {
            _applied = Quaternion.Identity;
            return;
        }

        var pose = saved.AsSpan();
        var posed = copy.LocalAtoms.AsSpan();
        if (posed.Length != pose.Length)
        {
            return;
        }

        if (_takenOf != body.Ptr || _taken.Length != pose.Length)
        {
            Mark(body, pose.Length);
        }

        for (var bone = 0; bone < pose.Length; bone++)
        {
            if (!_taken[bone])
            {
                continue;
            }

            var mine = pose[bone];
            var theirs = posed[bone];
            mine.Rotation = ToTurn(Quaternion.Slerp(Numeric(mine.Rotation), Quaternion.Normalize(Numeric(theirs.Rotation)), Raised));
            mine.Translation = Vector3.Lerp(mine.Translation, theirs.Translation, Raised);
            mine.Scale += (theirs.Scale - mine.Scale) * Raised;
            pose[bone] = mine;
        }

        Lean(body, pose);
    }

    // With a copy, the lowest lean bone is held turned as it is on the copy's root, measured from
    // the body's root, so the upper body ignores how the hips below it swing about. Roots are compared, not
    // the meshes, because a villain's moves can be made with the root a quarter turn round from
    // the player's. The lean is a turn in the
    // world. It is carried into the body's own space through the body's place, then shared out
    // over the lean bones from the lowest up, each through its parent's turn in the pose being
    // made.
    void Lean(SkeletalMeshComponent body, Span<Atom> pose)
    {
        _applied = Quaternion.Identity;
        if (_leanCount == 0 || _parents.Length != pose.Length || _player is not { IsValid: true } player)
        {
            return;
        }

        if (Frame(body) is not { } frame)
        {
            return;
        }

        // Without a copy of his own there is no pose to hold the spine to, so it keeps yours.
        Quaternion? held = null;
        if (_copy is { IsValid: true } copy && _firstLean >= 0 && _posedTicks >= PosedAfter && copy.SpaceBases.Count == pose.Length)
        {
            var copied = copy.SpaceBases.AsSpan();
            held = Quaternion.Normalize(Quaternion.Inverse(Read(copied[0].Rotation)) * Read(copied[_firstLean].Rotation));
        }

        var facing = Facing(player);
        // The kick lifts the barrel about the villain's own right, which turns forward into up.
        var kick = Quaternion.CreateFromAxisAngle(-Vector3.UnitY, _kick);
        var world = facing * Quaternion.Slerp(Quaternion.Identity, kick * _lean, Raised) * Quaternion.Inverse(facing);
        _applied = world;
        var share = Quaternion.Slerp(Quaternion.Identity, Into(frame, world), 1f / _leanCount);

        if (_turns.Length != pose.Length)
        {
            _turns = new Quaternion[pose.Length];
        }

        for (var bone = 0; bone <= _lastLean; bone++)
        {
            var parent = _parents[bone] >= 0 ? _turns[_parents[bone]] : Quaternion.Identity;
            var local = Read(pose[bone].Rotation);
            if (bone == _firstLean && held is { } anchor)
            {
                var onRoot = _turns[0] * anchor;
                local = Quaternion.Normalize(Quaternion.Slerp(local, Quaternion.Inverse(parent) * onRoot, Raised));
            }

            if (_leans[bone])
            {
                local = Quaternion.Normalize(Into(parent, share) * local);
            }

            if (bone == _firstLean || _leans[bone])
            {
                var atom = pose[bone];
                atom.Rotation = Write(local);
                pose[bone] = atom;
            }

            _turns[bone] = parent * local;
        }
    }

    // The body's turn in the world, from its place with any size taken out.
    static Quaternion? Frame(SkeletalMeshComponent body)
    {
        var place = Unsafe.As<GameObject.FMatrix, Matrix4x4>(ref body.LocalToWorld);
        var x = new Vector3(place.M11, place.M12, place.M13);
        var y = new Vector3(place.M21, place.M22, place.M23);
        var z = new Vector3(place.M31, place.M32, place.M33);
        if (x.LengthSquared() < 1e-8f || y.LengthSquared() < 1e-8f || z.LengthSquared() < 1e-8f)
        {
            return null;
        }

        x = Vector3.Normalize(x);
        y = Vector3.Normalize(y);
        z = Vector3.Normalize(z);
        var turn = new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(turn));
    }

    // A turn given in an outer space, as seen from inside a frame.
    static Quaternion Into(Quaternion frame, Quaternion turn) => Quaternion.Inverse(frame) * turn * frame;

    // The game's bone turns can rotate a point the other way round from System.Numerics. Those
    // are read and written as their conjugate, so the sums here stay the usual ones.
    Quaternion Read(Turn turn)
    {
        var plain = Quaternion.Normalize(Numeric(turn));
        return _conjugate ? Quaternion.Conjugate(plain) : plain;
    }

    Turn Write(Quaternion turn) => ToTurn(_conjugate ? Quaternion.Conjugate(turn) : turn);

    // The body as last drawn tells which way its turns go. Each bone sits at its parent plus its
    // own offset turned by the parent, so whichever reading puts the bones where they are wins.
    void LearnTurns(SkeletalMeshComponent body)
    {
        var spaces = body.SpaceBases.AsSpan();
        var locals = body.LocalAtoms.AsSpan();
        if (spaces.Length != _parents.Length || locals.Length != _parents.Length)
        {
            return;
        }

        var plain = 0f;
        var flipped = 0f;
        for (var bone = 1; bone < spaces.Length; bone++)
        {
            var parent = _parents[bone];
            var offset = locals[bone].Translation * spaces[Math.Max(parent, 0)].Scale;
            if (parent < 0 || offset.LengthSquared() < 1f)
            {
                continue;
            }

            var turn = Quaternion.Normalize(Numeric(spaces[parent].Rotation));
            var want = spaces[bone].Translation - spaces[parent].Translation;
            plain += (Vector3.Transform(offset, turn) - want).Length();
            flipped += (Vector3.Transform(offset, Quaternion.Conjugate(turn)) - want).Length();
        }

        _conjugate = flipped < plain;
    }

    bool IsLean(string name)
    {
        if (LeanAt is not { } lean)
        {
            return false;
        }

        var at = name.LastIndexOf(lean, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return false;
        }

        for (var i = at + lean.Length; i < name.Length; i++)
        {
            if (!char.IsDigit(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    // A bone takes the pose when it or one it hangs from is picked out by name.
    void Mark(SkeletalMeshComponent body, int count)
    {
        _taken = new bool[count];
        _takenOf = body.Ptr;
        _leans = new bool[count];
        _parents = new int[count];
        _leanCount = 0;
        _firstLean = -1;
        _lastLean = -1;
        for (var bone = 0; bone < count; bone++)
        {
            var name = body.GetBoneName(bone);
            _parents[bone] = bone == 0 ? -1 : body.MatchRefBone(body.GetParentBone(name));
            if (IsLean(name.ToString() ?? "") && _parents[bone] < bone)
            {
                _leans[bone] = true;
                _leanCount++;
                _lastLean = bone;
                if (_firstLean < 0)
                {
                    _firstLean = bone;
                }
            }

            for (var up = 0; up < 64; up++)
            {
                var text = name.ToString() ?? "";
                if (text.Length == 0 || text.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (_takes(text))
                {
                    _taken[bone] = true;
                    break;
                }

                name = body.GetParentBone(name);
            }
        }

        LearnTurns(body);
    }
}
