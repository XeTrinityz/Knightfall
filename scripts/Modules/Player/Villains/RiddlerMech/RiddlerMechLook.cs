#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static Knightfall.Turns;
using Atom = BmSDK.GameObject.FBoneAtom;
using Turn = BmSDK.GameObject.FQuat;
using ParentMode = BmSDK.Engine.RSkeletalMeshComponent_Export.EParentAnimComponentMode;

namespace Knightfall;

// How the mech looks and moves. Its model, its moves and its sounds all ship in its showcase,
// which is loaded the way DISGUISE loads one. Riddler's moves at the controls only ship in the
// orphanage level the fight is in, so that is loaded as a package too. Nothing in it runs.
//
// The mech isn't shaped like a person, so it can't follow the player's skeleton. It is a model of
// its own on the player, posed every frame from hidden copies of it that play one move each,
// blended by how you move. Riddler is posed the same way from his own copies. Each of his moves
// is made to play with the mech's move of the same name, so his are held at the same frame.
static partial class RiddlerMech
{
    const string Lender = "riddler mech";
    const string LookPackage = "Showcase_Characters_RiddlerMech";
    const string FightPackage = "Orphan_A1";
    const string MovesPath = "Anim_Riddler_Mech.Code.RMech_DualPlay";
    const string PilotMovesPath = "Anim_Riddler_Mech.Code.RD_DualPlay";
    const string MechMeshPath = "Riddler_Mech_Mesh.Mesh.Riddler_Mech_Skin";

    // Without the orphanage level, Riddler holds the one seated pose baked into the showcase.
    const string SeatedSetName = "Baked_RD_DualPlay_0";

    // Action is the copy that plays one move at a time, like the smash, set to it as it starts.
    const int Idle = 0, Walk = 1, Run = 2, Action = 3;
    static readonly FName[] Moves = { new("Stand_Idle"), new("Walk_Forward"), new("Run_Forward"), new("Melee_Attack") };

    // The shoulders show which way a skeleton's moves face.
    static readonly FName LeftArm = new("R_Mech_Skin_L_UpArm");
    static readonly FName RightArm = new("R_Mech_Skin_R_UpArm");
    static readonly FName PersonLeftArm = new("Bip01_L_UpperArm");
    static readonly FName PersonRightArm = new("Bip01_R_UpperArm");

    // How fast its walk and run look right at their own pace, judged by eye.
    const float WalkSpeed = 250f;
    const float RunSpeed = 600f;

    // Its moves are sped up or slowed down this far at most to keep its feet planted.
    const float SlowestRate = 0.6f;
    const float FastestRate = 1.6f;

    // Under this speed it stands.
    const float StandSpeed = 40f;
    const float BlendSeconds = 0.2f;
    const float ActionBlend = 0.08f;

    // How quickly its moves follow a change of speed, so they don't jitter with it.
    const float SpeedEase = 0.15f;

    // The player's own mesh's tick group. Parts shown in its place are worked out with it.
    const GameObject.ETickingGroup Shown = GameObject.ETickingGroup.TG_PreAsyncWork2;
    const GameObject.ETickingGroup Unseen = GameObject.ETickingGroup.TG_DuringAsyncWork;

    sealed record Part(SkeletalMesh Mesh, MaterialInterface?[] Materials, PhysicsAsset? Physics, ParentMode Follows, bool Flaps, bool Controls, float Scale, Vector3 Scale3D);

    static bool _loadTried;
    static Part? _mechPart;
    static readonly List<Part> PilotParts = new();
    static AnimSet? _moves;
    static AnimSet? _pilotMoves;
    static AnimSet? _seatedSet;

    static RPawnPlayer? _on;
    static SkeletalMeshComponent? _mech;
    static RAnimNode_AnimCapture? _pose;
    static readonly SkeletalMeshComponent?[] Copies = new SkeletalMeshComponent?[Moves.Length];
    static readonly AnimNodeSequence?[] Played = new AnimNodeSequence?[Moves.Length];
    static readonly SkeletalMeshComponent?[] PilotCopies = new SkeletalMeshComponent?[Moves.Length];
    static readonly AnimNodeSequence?[] PilotPlayed = new AnimNodeSequence?[Moves.Length];
    static RAnimNode_AnimCapture? _pilotPose;
    static readonly List<SkeletalMeshComponent> Pilot = new();
    static readonly HashSet<nint> Ours = new();

    static readonly float[] Weights = new float[Moves.Length];
    static readonly float[] Wanted = new float[Moves.Length];

    // How far each move has carried the mech's root from where it stands, taken back off both it
    // and Riddler so they stay on the spot together.
    static readonly Vector2[] Shifts = new Vector2[Moves.Length];

    // How far its model is turned from the pawn's way, in degrees, so it faces where you go.
    static float _turn;

    // Riddler's moves carry him along with the mech's. His body and clothes are moved back by
    // this much together, in his own mesh's space, so nothing he wears is left behind.
    static Vector2 _pilotShift;

    static Atom[] _mixed = Array.Empty<Atom>();
    static float _speed;
    static bool _inStep;

    // False once loading has failed, so it isn't tried every tick.
    static bool Load()
    {
        if (_mechPart is not null && _moves is { IsValid: true })
        {
            return true;
        }

        if (_loadTried)
        {
            return false;
        }

        _loadTried = true;
        var actors = Find();
        if (actors.Count == 0 && LoadPackage(LookPackage))
        {
            actors = Find();
        }

        // The showcase has the mech and Riddler. The mech is known by its model. Riddler is the
        // part shaped like a person, and the rest of what he wears comes after his body.
        foreach (var actor in actors)
        {
            Borrowed.Keep(actor, Lender);
            var sources = Disguise.Sources(actor);
            foreach (var source in sources)
            {
                if (_mechPart is null && IsMech(source))
                {
                    _mechPart = PartOf(source);
                }
            }

            var body = sources.FindIndex(Disguise.Person);
            if (body < 0 || PilotParts.Count > 0)
            {
                continue;
            }

            PilotParts.Add(PartOf(sources[body]));
            for (var i = 0; i < sources.Count; i++)
            {
                if (i != body && !IsMech(sources[i]))
                {
                    PilotParts.Add(PartOf(sources[i]));
                }
            }

            _seatedSet = SeatedSet(sources[body]);
        }

        _moves = Kept(Game.FindObject<AnimSet>(MovesPath));
        if (_mechPart is null || _moves is null)
        {
            Log.Warn("riddler_mech.missing", "The Riddler mech's model or moves weren't found in its showcase.");
            return false;
        }

        // Anything not kept from it before is gone again, so the level is loaded whenever any
        // part of it is missing.
        if (Game.FindObject<AnimSet>(PilotMovesPath) is not { IsValid: true } || !BeamLoaded())
        {
            LoadPackage(FightPackage);
        }

        _pilotMoves = Kept(Game.FindObject<AnimSet>(PilotMovesPath));
        LoadBeam();
        LoadVoice();
        if (_pilotMoves is null)
        {
            Log.Info("Riddler's moves in the mech weren't found, so he holds one seated pose.");
        }

        return true;
    }

    static bool LoadPackage(string package) =>
        Packages.TryLoad(package, $"{package} for the Riddler mech", "riddler_mech.load");

    static bool IsMech(SkeletalMeshComponent source) =>
        source.SkeletalMesh is { IsValid: true } mesh && mesh.GetPathName().Equals(MechMeshPath, StringComparison.OrdinalIgnoreCase);

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

    // Riddler's seated pose is baked into the showcase level as a set of its own.
    static AnimSet? SeatedSet(SkeletalMeshComponent body)
    {
        var sets = body.AnimSets;
        for (var i = 0; i < sets.Count; i++)
        {
            if (sets[i] is { IsValid: true } set && set.Name.ToString() == SeatedSetName)
            {
                return Kept(set);
            }
        }

        var prefix = LookPackage + ".";
        foreach (var set in Game.FindObjects<AnimSet>())
        {
            if (set.IsValid && set.Name.ToString() == SeatedSetName && set.GetPathName().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Kept(set);
            }
        }

        return null;
    }

    static T? Kept<T>(T? thing)
        where T : GameObject => Borrowed.KeepFound(thing, Lender);

    static void Build(RPawnPlayer player, SkeletalMeshComponent own)
    {
        Unbuild();
        var part = _mechPart!;
        MakeCopies(player, part.Mesh, _moves!, Copies, Played, play: true);
        Played[Action]!.StopAnim();

        // Its moves may face another way than Batman's. Both are read from the line across the
        // shoulders, standing, and the mech is turned by the difference, rounded to a quarter.
        Copies[Idle]!.ForceSkelUpdate();
        _turn = TurnFor(own, Copies[Idle]!);

        var mech = Show(player, own, part, out var physics);

        // The game's own node for playing back a pose it is handed. It has to be on the mesh
        // before the mesh is attached.
        _pose = Made.Tracked(new RAnimNode_AnimCapture(mech));
        mech.Animations = _pose;
        player.AttachComponent(mech);

        // Asked for before it is attached, the swinging chains on its arms stay still. So they
        // are asked for again once it is part of the world.
        if (physics && !mech.bAllowRFlapsAssetInstance)
        {
            mech.SetAllowRFlapsAssetInstance(true);
        }

        _mech = mech;
        Ours.Add(mech.Ptr);
        SeatPilot(player, own);

        _on = player;
        _speed = 0f;
        _inStep = false;
        Array.Clear(Weights);
        Weights[Idle] = 1f;
        Unact();
    }

    // Hidden copies of a skeleton, one move each. Riddler's aren't played, only held at the
    // mech's frame.
    static void MakeCopies(RPawnPlayer player, SkeletalMesh mesh, AnimSet moves, SkeletalMeshComponent?[] copies, AnimNodeSequence?[] played, bool play)
    {
        for (var i = 0; i < Moves.Length; i++)
        {
            var copy = Made.Tracked(new SkeletalMeshComponent(player));
            copy.SetActorCollision(false, false);
            copy.SetSkeletalMesh(mesh, false);
            copy.AnimSets.Add(moves);
            var move = Made.Tracked(new AnimNodeSequence(copy));
            move.bNoNotifies = !play;
            copy.Animations = move;
            copy.bUpdateSkelWhenNotRendered = true;
            copy.bCastDynamicShadow = false;
            copy.SetHidden(true);
            copy.SetTickGroup(Unseen);
            player.AttachComponent(copy);
            move.SetAnim(Moves[i]);
            if (play)
            {
                move.PlayAnim(true, 1f, 0f);
            }

            copies[i] = copy;
            played[i] = move;
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

    // Riddler shares the mech's place, since his moves are made in the same space as its. His
    // body is handed his pose, or plays his seated one, and his clothes follow his body the way
    // they did in the showcase.
    static void SeatPilot(RPawnPlayer player, SkeletalMeshComponent own)
    {
        if (PilotParts.Count == 0 || (_pilotMoves is not { IsValid: true } && _seatedSet is not { IsValid: true }))
        {
            return;
        }

        SkeletalMeshComponent? body = null;
        foreach (var part in PilotParts)
        {
            var piece = Show(player, own, part, out var physics);
            if (body is null)
            {
                if (_pilotMoves is { IsValid: true } moves)
                {
                    MakeCopies(player, part.Mesh, moves, PilotCopies, PilotPlayed, play: false);
                    _pilotPose = Made.Tracked(new RAnimNode_AnimCapture(piece));
                    piece.Animations = _pilotPose;
                    player.AttachComponent(piece);
                }
                else
                {
                    piece.AnimSets.Add(_seatedSet!);
                    var seated = Made.Tracked(new AnimNodeSequence(piece));
                    seated.bNoNotifies = true;
                    piece.Animations = seated;
                    player.AttachComponent(piece);
                    seated.SetAnim(Moves[Idle]);
                    seated.PlayAnim(true, 1f, 0f);
                }

                if (physics && !piece.bAllowRFlapsAssetInstance)
                {
                    piece.SetAllowRFlapsAssetInstance(true);
                }

                body = piece;
            }
            else
            {
                // A piece with bones of its own that his body hasn't got, like a collar, follows in
                // the fill-in mode, which works those bones out from the piece's own rest pose. The
                // showcase's mode leaves them unset, so the cloth on them drifts off him as he moves.
                piece.ParentAnimComponentMode = Disguise.Unshared(piece, body) ? ParentMode.PACM_FillIn : part.Follows;
                piece.bTransformFromAnimParent = 0;
                piece.SetParentAnimComponent(body);
                piece.SetAllowRFlapsAssetInstance(part.Flaps);
                player.AttachComponent(piece);
            }

            Pilot.Add(piece);
            Ours.Add(piece.Ptr);
        }
    }

    // Every part on show takes the player's mesh's place, turned to face where you go.
    static void PlaceAll(SkeletalMeshComponent own)
    {
        if (_mech is { IsValid: true } mech)
        {
            Place(mech, own, _mechPart!);
        }

        // The hidden copies stand where the mech is shown. Their moves spawn the game's own
        // effects, like the smash's dust, on their bones, so the effects land on the mech.
        for (var i = 0; i < Copies.Length; i++)
        {
            if (Copies[i] is { IsValid: true } copy)
            {
                Place(copy, own, _mechPart!, Shifts[i]);
            }
        }

        PlacePilot(own);
    }

    static void PlacePilot(SkeletalMeshComponent own)
    {
        for (var i = 0; i < Pilot.Count && i < PilotParts.Count; i++)
        {
            if (Pilot[i].IsValid)
            {
                Place(Pilot[i], own, PilotParts[i], _pilotShift);
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
        var moving = player.Physics == Actor.EPhysics.PHYS_Walking ? new Vector2(velocity.X, velocity.Y).Length() : 0f;
        _speed += (moving - _speed) * Settle(delta, SpeedEase);
        var speed = _speed;

        Array.Clear(Wanted);
        if (Acting)
        {
            Wanted[Action] = 1f;
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

        var blend = Settle(delta, Acting || Weights[Action] > 0.01f ? ActionBlend : BlendSeconds);
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
        if (Acting)
        {
            heaviest = Action;
        }

        if (Played[Walk] is not { IsValid: true } walk || Played[Run] is not { IsValid: true } run)
        {
            return;
        }

        // Walk and run go round their steps together, at a pace between their own by how much
        // each is on show, so blending them doesn't cross its legs. They are put in step once as
        // the run comes in. The run's copy is posed again at once, since Riddler is held at its
        // frame and its pose has to match.
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
        if (blending && !_inStep && Copies[Run] is { IsValid: true } runner)
        {
            run.SetPosition(walk.GetNormalizedPosition() * runLength, false);
            runner.ForceSkelUpdate();
        }

        _inStep = blending;

        // Every move carries its own footsteps and servos. Only the one most on show sounds.
        for (var i = 0; i < Played.Length; i++)
        {
            if (Played[i] is { IsValid: true } move && move.bNoNotifies != (i != heaviest))
            {
                move.bNoNotifies = i != heaviest;
            }
        }
    }

    // The moves blended by weight and handed to the mech and Riddler on show, once a tick before
    // the game works out its meshes. Posed twice in a frame, the swinging chains on the mech are
    // stepped twice and jitter. The pose is from the copies' last frame, which can't be seen.
    static void Pose()
    {
        if (_mech is not { IsValid: true } mech || _pose is not { IsValid: true } pose || Copies[Idle] is not { IsValid: true } idler)
        {
            return;
        }

        // Its moves walk its root away from where it stands. The pawn does the walking, so each
        // move's root is put back over its standing place, and Riddler by the same amount.
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
        if (!Mix(Copies, pose.SavedAtoms.AsSpan(), shiftRoot: true))
        {
            return;
        }

        PoseRiddler();
    }

    static void PoseRiddler()
    {
        if (_pilotPose is not { IsValid: true } pose || PilotCopies[Idle] is not { IsValid: true } idler || Pilot.Count == 0)
        {
            return;
        }

        for (var i = 0; i < PilotCopies.Length; i++)
        {
            if (Weights[i] > 0.001f && PilotCopies[i] is { IsValid: true } copy && PilotPlayed[i] is { IsValid: true } held && Played[i] is { IsValid: true } move)
            {
                held.SetPosition(move.CurrentTime, false);
                copy.ForceSkelUpdate();
            }
        }

        pose.Capture(idler);
        if (!Mix(PilotCopies, pose.SavedAtoms.AsSpan(), shiftRoot: false))
        {
            return;
        }

        var shift = Vector2.Zero;
        var total = 0f;
        for (var i = 0; i < PilotCopies.Length; i++)
        {
            if (Weights[i] > 0.001f && PilotCopies[i] is { IsValid: true })
            {
                shift += Shifts[i] * Weights[i];
                total += Weights[i];
            }
        }

        _pilotShift = total > 0f ? shift / total : Vector2.Zero;
        if (World.Player?.Mesh is { IsValid: true } own)
        {
            PlacePilot(own);
        }
    }

    // The copies' poses blended by weight into the pose on show, with each move's root put back
    // by its shift if asked. The turns are summed on one side of the first one, then made whole
    // again.
    static bool Mix(SkeletalMeshComponent?[] copies, Span<Atom> into, bool shiftRoot)
    {
        var count = into.Length;
        if (count == 0)
        {
            return false;
        }

        Span<int> used = stackalloc int[copies.Length];
        var n = 0;
        var total = 0f;
        for (var i = 0; i < copies.Length; i++)
        {
            if (Weights[i] > 0.001f && copies[i] is { IsValid: true } copy && copy.LocalAtoms.Count == count)
            {
                used[n++] = i;
                total += Weights[i];
            }
        }

        if (n == 0 || total <= 0f)
        {
            return false;
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
            var atoms = copies[i]!.LocalAtoms.AsSpan();
            for (var bone = 0; bone < count; bone++)
            {
                var turn = Numeric(atoms[bone].Rotation);
                var at = atoms[bone].Translation;
                if (bone == 0 && shiftRoot)
                {
                    at += new Vector3(Shifts[i], 0f);
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

        return true;
    }

    static float TurnFor(SkeletalMeshComponent own, SkeletalMeshComponent idler)
    {
        if (Facing(own, PersonLeftArm, PersonRightArm) is not { } mine || Facing(idler, LeftArm, RightArm) is not { } its)
        {
            return 0f;
        }

        var turn = mine - its;
        return 90f * MathF.Round(turn / 90f);
    }

    // Which way a skeleton faces in its own mesh, in degrees. Its right shoulder is a quarter
    // round from its front.
    static float? Facing(SkeletalMeshComponent body, FName left, FName right)
    {
        var whole = body.SpaceBases;
        var l = body.MatchRefBone(left);
        var r = body.MatchRefBone(right);
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
        if (_on is { IsValid: true, bDeleteMe: false } owner)
        {
            foreach (var piece in Pilot)
            {
                Detach(owner, piece);
            }

            Detach(owner, _mech);
            foreach (var copy in Copies)
            {
                Detach(owner, copy);
            }

            foreach (var copy in PilotCopies)
            {
                Detach(owner, copy);
            }
        }

        Pilot.Clear();
        Ours.Clear();
        Array.Clear(Copies);
        Array.Clear(Played);
        Array.Clear(PilotCopies);
        Array.Clear(PilotPlayed);
        Array.Clear(Weights);
        _mech = null;
        _pose = null;
        _pilotPose = null;
        _on = null;
        _turn = 0f;
        _pilotShift = Vector2.Zero;
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
        _mechPart = null;
        PilotParts.Clear();
        _moves = null;
        _pilotMoves = null;
        _seatedSet = null;
        _beamLook = null;
        _impactLook = null;
        _glowLook = null;
        _dustLook = null;
        _thump = null;
        UnloadVoice();
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
