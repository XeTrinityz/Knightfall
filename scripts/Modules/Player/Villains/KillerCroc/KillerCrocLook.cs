#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using static Knightfall.Turns;
using Atom = BmSDK.GameObject.FBoneAtom;
using ParentMode = BmSDK.Engine.RSkeletalMeshComponent_Export.EParentAnimComponentMode;

namespace Knightfall;

// How Croc looks and moves. His body, chains and rags ship in his showcase, which is loaded the
// way DISGUISE loads one. His moves only ship in the Blackgate level of his fight, so that is
// loaded as a package too. Nothing in it runs.
//
// His moves are made for his own skeleton, so he doesn't follow the player's. He is a model of
// his own on the player, posed every frame from hidden copies of him that play one move each,
// blended by how fast you go. What he wears follows his body.
static partial class KillerCroc
{
    const string Lender = "killer croc";
    const string LookPackage = "Showcase_Characters_DLCKillerCroc";
    const string FightPackage = "VPKillerCroc_B2";
    const string MovesPath = "dlc_anim_croc.Code.Croc_Combat";
    const string ThugMovesPath = "dlc_anim_croc.Code.TG_Croc_Combat";
    const string BodyPath = "DLC_KillerCroc_BM3_Mesh.Mesh.Killer_Croc_Skin";
    const string StepPath = "ww_dlc14_croc_chr_killercroc.Play_DLC14_CHR_KillerCroc_Footstep";
    const string LightStepPath = "ww_dlc14_croc_chr_killercroc.Play_DLC14_CHR_KillerCroc_Footstep_Light";
    const string Voice = "DLG_Voices.KillerCroc";

    // The two action copies take turns playing one move at a time, like a swipe, so a move cut
    // short by the next one can fade out under it.
    const int Idle = 0, Walk = 1, Run = 2, Action = 3, NextAction = 4;
    static readonly FName[] Moves = { new("Stand_Idle_Combat"), new("Walk_Forward_Combat"), new("Run_Forward_Combat"), new("Melee_Strike"), new("Melee_Strike") };

    // The shoulders show which way a skeleton's moves face.
    static readonly FName LeftArm = new("Bip01_L_UpperArm");
    static readonly FName RightArm = new("Bip01_R_UpperArm");

    // How fast his walk and run look right at their own pace, judged by eye.
    const float WalkSpeed = 200f;
    const float RunSpeed = 450f;

    // His moves are sped up or slowed down this far at most to keep his feet planted.
    const float SlowestRate = 0.6f;
    const float FastestRate = 1.6f;

    // Under this speed he stands.
    const float StandSpeed = 40f;
    const float BlendSeconds = 0.2f;

    // How quickly his moves follow a change of speed, so they don't jitter with it.
    const float SpeedEase = 0.15f;

    // A step only sounds from a walk or run this much on show.
    const float StepWeight = 0.3f;

    // The player's own mesh's tick group. Parts shown in its place are worked out with it.
    const GameObject.ETickingGroup Shown = GameObject.ETickingGroup.TG_PreAsyncWork2;
    const GameObject.ETickingGroup Unseen = GameObject.ETickingGroup.TG_DuringAsyncWork;

    sealed record Part(SkeletalMesh Mesh, MaterialInterface?[] Materials, PhysicsAsset? Physics, ParentMode Follows, bool Flaps, bool Controls, float Scale, Vector3 Scale3D);

    static bool _loadTried;
    static readonly List<Part> Parts = new();
    static AnimSet? _moves;

    // The thug's side of Croc's moves that hold one, like being hauled up off the floor.
    static AnimSet? _thugMoves;
    static AkEvent? _step;
    static AkEvent? _lightStep;

    static RPawnPlayer? _on;
    static SkeletalMeshComponent? _body;
    static RAnimNode_AnimCapture? _pose;
    static readonly List<SkeletalMeshComponent> Worn = new();
    static readonly SkeletalMeshComponent?[] Copies = new SkeletalMeshComponent?[Moves.Length];
    static readonly AnimNodeSequence?[] Played = new AnimNodeSequence?[Moves.Length];
    static readonly HashSet<nint> Ours = new();

    static readonly float[] Weights = new float[Moves.Length];
    static readonly float[] Wanted = new float[Moves.Length];

    // How far each move has carried his root from where he stands, taken back off so he stays
    // on the spot. The pawn does the walking.
    static readonly Vector2[] Shifts = new Vector2[Moves.Length];

    // How far his model is turned from the pawn's way, in degrees, so he faces where you go.
    static float _turn;

    static Atom[] _mixed = Array.Empty<Atom>();
    static float _speed;
    static bool _inStep;

    // The move whose steps are being listened to, and how far into it the last tick was.
    static int _stepMove = -1;
    static float _stepFrom;

    static bool? _installed;

    static bool Installed => _installed ??= GameFiles.HasPackage(LookPackage) && GameFiles.HasPackage(FightPackage);

    // False once loading has failed, so it isn't tried every tick.
    static bool Load()
    {
        if (Parts.Count > 0 && _moves is { IsValid: true })
        {
            return true;
        }

        if (_loadTried)
        {
            return false;
        }

        _loadTried = true;
        var actors = Find();
        if (actors.Count == 0)
        {
            LoadPackage(LookPackage, Packages.World(LookPackage));
            actors = Find();
        }

        // The showcase may hold more than him. He is the one wearing his body, and the rest of
        // what he wears comes after it.
        foreach (var actor in actors)
        {
            var sources = Disguise.Sources(actor);
            var body = sources.FindIndex(IsBody);
            if (body < 0)
            {
                continue;
            }

            Borrowed.Keep(actor, Lender);
            Parts.Add(PartOf(sources[body]));
            for (var i = 0; i < sources.Count; i++)
            {
                if (i != body)
                {
                    Parts.Add(PartOf(sources[i]));
                }
            }

            break;
        }

        if (Parts.Count == 0)
        {
            Log.Warn("killer_croc.missing", "Killer Croc's body wasn't found in his showcase.");
            return false;
        }

        // Anything not kept from it before is gone again, so the level is loaded whenever any
        // part of it is missing.
        LoadPackage(FightPackage, MovesPath, ThugMovesPath, LightStepPath);

        _moves = Kept(Game.FindObject<AnimSet>(MovesPath));
        _thugMoves = Kept(Game.FindObject<AnimSet>(ThugMovesPath));
        _backhandRight = Named(BackhandRightName);
        _backhandLeft = Named(BackhandLeftName);
        _step = Kept(Game.FindObject<AkEvent>(StepPath));
        _lightStep = Kept(Game.FindObject<AkEvent>(LightStepPath)) ?? _step;
        _thud = Kept(Game.FindObject<AkEvent>(ThudPath));
        if (_moves is null)
        {
            Log.Warn("killer_croc.moves", "Killer Croc's moves weren't found in his Blackgate level.");
            Parts.Clear();
            return false;
        }

        return true;
    }

    // A move in his set by its full name, number and all.
    static FName Named(string text)
    {
        if (_moves is not { IsValid: true } moves)
        {
            return FName.None;
        }

        var sequences = moves.Sequences;
        for (var i = 0; i < sequences.Count; i++)
        {
            if (sequences[i] is { IsValid: true } sequence && sequence.SequenceName.ToString() == text)
            {
                return sequence.SequenceName;
            }
        }

        return FName.None;
    }

    static bool LoadPackage(string package, params string[] wanted) =>
        Packages.TryLoad(package, $"{package} for Killer Croc", "killer_croc.load", wanted);

    static bool IsBody(SkeletalMeshComponent source) =>
        source.SkeletalMesh is { IsValid: true } mesh && mesh.GetPathName().Equals(BodyPath, StringComparison.OrdinalIgnoreCase);

    static List<RCinematicActor> Find()
    {
        var prefix = LookPackage + ".";
        var found = new List<RCinematicActor>();
        foreach (var actor in Game.FindObjects<RCinematicActor>())
        {
            if (actor.IsValid && actor.GetPathName().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(actor);
            }
        }

        return found;
    }

    static Part PartOf(SkeletalMeshComponent source)
    {
        var painted = source.Materials;
        var materials = new MaterialInterface?[painted.Count];
        for (var i = 0; i < materials.Length; i++)
        {
            materials[i] = painted[i];
        }

        return new Part(source.SkeletalMesh!, materials, source.PhysicsAsset, source.ParentAnimComponentMode, source.bAllowRFlapsAssetInstance,
            source.bEnableSkeletalMeshSkelControls, source.Scale, source.Scale3D);
    }

    static T? Kept<T>(T? thing)
        where T : GameObject => Borrowed.KeepFound(thing, Lender);

    static void Build(RPawnPlayer player, SkeletalMeshComponent own)
    {
        Unbuild();
        var bodyPart = Parts[0];
        MakeCopies(player, bodyPart.Mesh);
        Played[Action]!.StopAnim();
        Played[NextAction]!.StopAnim();
        _slot = Action;

        // His moves may face another way than Batman's. Both are read from the line across the
        // shoulders, standing, and he is turned by the difference, rounded to a quarter.
        Copies[Idle]!.ForceSkelUpdate();
        _turn = TurnFor(own, Copies[Idle]!);

        // The game's own node for playing back a pose it is handed. It has to be on the mesh
        // before the mesh is attached.
        var body = Show(player, own, bodyPart, out var physics);
        _pose = Made.Tracked(new RAnimNode_AnimCapture(body));
        body.Animations = _pose;
        player.AttachComponent(body);

        // Asked for before it is attached, the swinging parts stay still. So they are asked for
        // again once it is part of the world.
        if (physics && !body.bAllowRFlapsAssetInstance)
        {
            body.SetAllowRFlapsAssetInstance(true);
        }

        _body = body;
        Worn.Add(body);
        Ours.Add(body.Ptr);

        // A piece with bones of its own that his body hasn't got follows in the fill-in mode,
        // which works those bones out from the piece's own rest pose. The showcase's mode leaves
        // them unset, so the cloth on them drifts off him as he moves.
        for (var i = 1; i < Parts.Count; i++)
        {
            var part = Parts[i];
            var piece = Show(player, own, part, out _);
            piece.ParentAnimComponentMode = Disguise.Unshared(piece, body) ? ParentMode.PACM_FillIn : part.Follows;
            piece.bTransformFromAnimParent = 0;
            piece.SetParentAnimComponent(body);
            piece.SetAllowRFlapsAssetInstance(part.Flaps);
            player.AttachComponent(piece);
            Worn.Add(piece);
            Ours.Add(piece.Ptr);
        }

        _on = player;
        _speed = 0f;
        _inStep = false;
        _stepMove = -1;
        Array.Clear(Weights);
        Weights[Idle] = 1f;
    }

    // Hidden copies of his body, one move each.
    static void MakeCopies(RPawnPlayer player, SkeletalMesh mesh)
    {
        for (var i = 0; i < Moves.Length; i++)
        {
            var copy = Made.Tracked(new SkeletalMeshComponent(player));
            copy.SetActorCollision(false, false);
            copy.SetSkeletalMesh(mesh, false);
            copy.AnimSets.Add(_moves!);
            var move = Made.Tracked(new AnimNodeSequence(copy));
            copy.Animations = move;
            copy.bUpdateSkelWhenNotRendered = true;
            copy.bCastDynamicShadow = false;
            copy.SetHidden(true);
            copy.SetTickGroup(Unseen);
            player.AttachComponent(copy);
            move.SetAnim(Moves[i]);
            move.PlayAnim(true, 1f, 0f);
            Copies[i] = copy;
            Played[i] = move;
            Ours.Add(copy.Ptr);
        }
    }

    // A shown part with the look's paint, in the player's place and light.
    static SkeletalMeshComponent Show(RPawnPlayer player, SkeletalMeshComponent own, Part part, out bool physics)
    {
        var piece = Made.Tracked(new SkeletalMeshComponent(player));
        piece.SetActorCollision(false, false);
        piece.SetSkeletalMesh(part.Mesh, false);
        physics = part.Physics is { IsValid: true };
        if (physics)
        {
            piece.SetPhysicsAsset(part.Physics!, false);
        }

        var slots = Math.Min(part.Materials.Length, piece.GetNumElements());
        for (var i = 0; i < slots; i++)
        {
            if (part.Materials[i] is { IsValid: true } material)
            {
                piece.SetMaterial(i, material);
            }
        }

        piece.SetTickGroup(Shown);
        piece.bEnableSkeletalMeshSkelControls = part.Controls;
        piece.SetAllowRFlapsAssetInstance(physics);
        piece.bCastDynamicShadow = own.bCastDynamicShadow;
        piece.SetLightingChannels(own.LightingChannels);

        // The player's own depth group. In the world's group, cinematic skin renders black.
        piece.SetDepthPriorityGroup(own.DepthPriorityGroup);
        piece.SetOwnerNoSee(own.bOwnerNoSee);
        Place(piece, own, part);
        return piece;
    }

    // Every part takes the player's mesh's place, turned to face where you go. The hidden
    // copies stand there too, so effects their moves spawn on their bones land on him.
    static void PlaceAll(SkeletalMeshComponent own)
    {
        for (var i = 0; i < Worn.Count && i < Parts.Count; i++)
        {
            if (Worn[i].IsValid)
            {
                Place(Worn[i], own, Parts[i]);
            }
        }

        for (var i = 0; i < Copies.Length; i++)
        {
            if (Copies[i] is { IsValid: true } copy)
            {
                Place(copy, own, Parts[0], Shifts[i]);
            }
        }
    }

    // A shift is in the part's own mesh space, so it is scaled and turned with the part.
    static void Place(SkeletalMeshComponent piece, SkeletalMeshComponent own, Part part, Vector2 shift = default)
    {
        var wanted = own.Rotation with { Yaw = own.Rotation.Yaw + _turn };
        var at = own.Translation + Turned(new Vector3(shift, 0f) * part.Scale, wanted.Yaw);
        if (piece.Translation != at)
        {
            piece.SetTranslation(at);
        }

        if (piece.Rotation != wanted)
        {
            piece.SetRotation(wanted);
        }

        if (piece.Scale != part.Scale)
        {
            piece.SetScale(part.Scale);
        }

        if (piece.Scale3D != part.Scale3D)
        {
            piece.SetScale3D(part.Scale3D);
        }
    }

    // Which moves show, by how fast you go.
    static void Choose(RPawnPlayer player)
    {
        var delta = World.Delta;
        var velocity = player.Velocity;
        var moving = _walkingUp ? _walkSpeedNow : player.Physics == Actor.EPhysics.PHYS_Walking ? new Vector2(velocity.X, velocity.Y).Length() : 0f;
        _speed += (moving - _speed) * Settle(delta, SpeedEase);
        var speed = _speed;

        Array.Clear(Wanted);
        if (Posing)
        {
            Wanted[_slot] = 1f;
        }
        else if (speed < StandSpeed)
        {
            Wanted[Idle] = 1f;
        }
        else
        {
            var running = Ramp(speed, WalkSpeed, RunSpeed);
            Wanted[Run] = running;
            Wanted[Walk] = 1f - running;
        }

        var blend = Settle(delta, Posing || Weights[Action] > 0.01f || Weights[NextAction] > 0.01f ? ActionBlend : BlendSeconds);
        var heaviest = 0;
        for (var i = 0; i < Weights.Length; i++)
        {
            Weights[i] += (Wanted[i] - Weights[i]) * blend;
            if (Weights[i] > Weights[heaviest])
            {
                heaviest = i;
            }
        }

        // A move's own sounds start on its first frame, before it is most on show.
        if (Posing)
        {
            heaviest = _slot;
        }

        // Every move carries its own sounds, like his chains. Only the one most on show sounds.
        for (var i = 0; i < Played.Length; i++)
        {
            if (Played[i] is { IsValid: true } move && move.bNoNotifies != (i != heaviest))
            {
                move.bNoNotifies = i != heaviest;
            }
        }

        if (Played[Walk] is not { IsValid: true } walk || Played[Run] is not { IsValid: true } run)
        {
            return;
        }

        // Walk and run go round their steps together, at a pace between their own by how much
        // each is on show, so blending them doesn't cross his legs. They are put in step once as
        // the run comes in.
        var walkLength = walk.GetAnimPlaybackLength();
        var runLength = run.GetAnimPlaybackLength();
        if (walkLength <= 0f || runLength <= 0f)
        {
            return;
        }

        var stepping = MathF.Max(speed, StandSpeed);
        var both = Weights[Walk] + Weights[Run];
        var share = both > 0f ? Weights[Run] / both : 0f;
        var steps = (1f - share) * Math.Clamp(stepping / WalkSpeed, SlowestRate, FastestRate) / walkLength
            + share * Math.Clamp(stepping / RunSpeed, SlowestRate, FastestRate) / runLength;
        walk.Rate = steps * walkLength;
        run.Rate = steps * runLength;

        var blending = Weights[Walk] > 0.01f && Weights[Run] > 0.01f;
        if (blending && !_inStep)
        {
            run.SetPosition(walk.GetNormalizedPosition() * runLength, false);
        }

        _inStep = blending;
    }

    // The moves blended by weight and handed to his body, once a tick before the game works out
    // its meshes. Posed twice in a frame, his swinging chains are stepped twice and jitter.
    static void Pose()
    {
        if (_body is not { IsValid: true } || _pose is not { IsValid: true } pose || Copies[Idle] is not { IsValid: true } idler)
        {
            return;
        }

        // His moves walk his root away from where he stands. The pawn does the walking, so each
        // move's root is put back over his standing place.
        var still = idler.LocalAtoms;
        if (still.Count == 0)
        {
            return;
        }

        var stand = still[0].Translation;
        for (var i = 0; i < Copies.Length; i++)
        {
            Shifts[i] = Copies[i] is { IsValid: true } copy && copy.LocalAtoms.Count > 0
                ? new Vector2(stand.X - copy.LocalAtoms[0].Translation.X, stand.Y - copy.LocalAtoms[0].Translation.Y)
                : Vector2.Zero;
        }

        pose.Capture(idler);
        Mix(pose.SavedAtoms.AsSpan());
    }

    // The copies' poses blended by weight into the pose on show, with each move's root put back
    // by its shift, and the turn a spinning move gave the pawn taken off its root. The turns are
    // summed on one side of the first one, then made whole again.
    static void Mix(Span<Atom> into)
    {
        var count = into.Length;
        if (count == 0)
        {
            return;
        }

        Span<int> used = stackalloc int[Copies.Length];
        var n = 0;
        var total = 0f;
        for (var i = 0; i < Copies.Length; i++)
        {
            if (Weights[i] > 0.001f && Copies[i] is { IsValid: true } copy && copy.LocalAtoms.Count == count)
            {
                used[n++] = i;
                total += Weights[i];
            }
        }

        if (n == 0 || total <= 0f)
        {
            return;
        }

        if (_mixed.Length < count)
        {
            _mixed = new Atom[count];
        }

        var mixed = _mixed.AsSpan(0, count);
        mixed.Clear();
        for (var k = 0; k < n; k++)
        {
            var i = used[k];
            var share = Weights[i] / total;
            var atoms = Copies[i]!.LocalAtoms.AsSpan();
            var unspin = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -Spun[i] * (MathF.PI / 180f));
            for (var bone = 0; bone < count; bone++)
            {
                var turn = Numeric(atoms[bone].Rotation);
                var at = atoms[bone].Translation;
                if (bone == 0)
                {
                    at += new Vector3(Shifts[i], 0f);
                    if (Spun[i] != 0f)
                    {
                        turn = unspin * turn;
                    }
                }

                var sum = mixed[bone];
                if (k > 0 && Quaternion.Dot(Numeric(sum.Rotation), turn) < 0f)
                {
                    turn = -turn;
                }

                sum.Rotation = ToTurn(Numeric(sum.Rotation) + turn * share);
                sum.Translation += at * share;
                sum.Scale += atoms[bone].Scale * share;
                mixed[bone] = sum;
            }
        }

        for (var bone = 0; bone < count; bone++)
        {
            var atom = mixed[bone];
            atom.Rotation = ToTurn(Quaternion.Normalize(Numeric(atom.Rotation)));
            into[bone] = atom;
        }
    }

    // His steps are his own. The game's footsteps for the player come from Batman's hidden walk
    // as well as Croc's, so the game's are kept quiet below and his are played here, as his walk,
    // run or move passes each step marked in it.
    static void Step(RPawnPlayer player)
    {
        var move = Posing ? _slot : Weights[Run] > Weights[Walk] ? Run : Walk;
        if ((!Posing && Weights[move] < StepWeight) || Played[move] is not { IsValid: true } played || played.AnimSeq is not { IsValid: true } sequence)
        {
            _stepMove = -1;
            return;
        }

        var now = played.CurrentTime;
        if (move != _stepMove)
        {
            _stepMove = move;
            _stepFrom = now;
            return;
        }

        var from = _stepFrom;
        _stepFrom = now;
        var notifies = sequence.Notifies;
        for (var i = 0; i < notifies.Count; i++)
        {
            if (notifies[i].Notify is not RAnimNotify_Footstep { IsValid: true } step)
            {
                continue;
            }

            // A walk that went round its end since the last tick passes the steps either side.
            var at = notifies[i].Time;
            var passed = now >= from ? at > from && at <= now : at > from || at <= now;
            if (!passed)
            {
                continue;
            }

            // As in his fight, a hand on the ground is a lighter step than a foot.
            var sound = step.Foot is RAnimNotify_Footstep.EFoot.Hand_Left or RAnimNotify_Footstep.EFoot.Hand_Right or RAnimNotify_Footstep.EFoot.Hand_Both
                ? _lightStep
                : _step;
            if (sound is { IsValid: true })
            {
                player.StartAudioEvent(sound);
            }
        }
    }

    // Each footstep the game plays for the player asks for its sound. While he is Croc it gets
    // none.
    public static bool Steps(RPawnPlayer pawn, RAnimNotify_Footstep.EFoot foot, out AkEvent? sound)
    {
        sound = null;
        return _on is { IsValid: true } on && on.Ptr == pawn.Ptr && _body is { IsValid: true };
    }

    static float TurnFor(SkeletalMeshComponent own, SkeletalMeshComponent idler)
    {
        if (Facing(own) is not { } mine || Facing(idler) is not { } his)
        {
            return 0f;
        }

        var turn = mine - his;
        return 90f * MathF.Round(turn / 90f);
    }

    // Which way a skeleton faces in its own mesh, in degrees. Its right shoulder is a quarter
    // round from its front.
    static float? Facing(SkeletalMeshComponent body)
    {
        var whole = body.SpaceBases;
        var l = body.MatchRefBone(LeftArm);
        var r = body.MatchRefBone(RightArm);
        if (l < 0 || r < 0 || whole.Count <= Math.Max(l, r))
        {
            return null;
        }

        var across = whole[r].Translation - whole[l].Translation;
        if (across.X * across.X + across.Y * across.Y <= 1f)
        {
            return null;
        }

        return MathF.Atan2(across.Y, across.X) * (180f / MathF.PI) - 90f;
    }

    static void Unbuild()
    {
        if (_on is { IsAlive: true } owner)
        {
            foreach (var piece in Worn)
            {
                Detach(owner, piece);
            }

            foreach (var copy in Copies)
            {
                Detach(owner, copy);
            }
        }

        Worn.Clear();
        Ours.Clear();
        Array.Clear(Copies);
        Array.Clear(Played);
        Array.Clear(Weights);
        Array.Clear(Shifts);
        Array.Clear(Spun);
        _body = null;
        _pose = null;
        _on = null;
        _turn = 0f;
        _stepMove = -1;
    }

    static void Detach(RPawnPlayer owner, SkeletalMeshComponent? piece)
    {
        if (piece is { IsValid: true } && piece.Owner is { } holder && holder.Ptr == owner.Ptr)
        {
            piece.SetHidden(true);
            owner.DetachComponent(piece);
        }
    }

    static void Unload()
    {
        Unbuild();
        Parts.Clear();
        _moves = null;
        _thugMoves = null;
        _step = null;
        _lightStep = null;
        _thud = null;
        _thrown = 0;
        Speech.Unload();
        _backhandRight = FName.None;
        _backhandLeft = FName.None;
        _loadTried = false;
        Borrowed.Let(Lender);
    }

    // Turned about the up axis by a yaw in degrees.
    static Vector3 Turned(Vector3 along, float yaw)
    {
        var (sin, cos) = MathF.SinCos(yaw * (MathF.PI / 180f));
        return new Vector3(along.X * cos - along.Y * sin, along.X * sin + along.Y * cos, along.Z);
    }

    static float Settle(float delta, float seconds) => 1f - MathF.Exp(-delta / seconds);

    static float Ramp(float value, float from, float to) => Math.Clamp((value - from) / (to - from), 0f, 1f);
}
