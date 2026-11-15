#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static Knightfall.Turns;
using Atom = BmSDK.GameObject.FBoneAtom;

namespace Knightfall;

// How Man-Bat looks and moves. His body ships in his showcase, loaded the way DISGUISE loads one.
// His flight ships in the city's level for his chase, and his attack in the level of the rooftop
// where he first jumps out at Batman. Each is loaded as a package when the city hasn't already.
// Nothing in them runs.
//
// His moves are made for his own skeleton, so he is a model of his own on the player, posed every
// frame from hidden copies of him that play one move each, blended by weight. His wing beats are
// made lying flat, the way he flies flat out, so slowing down he is tipped up until he hangs
// nearly upright, and he banks into his turns. That is done on each move's root, so his attack,
// made on the ground and facing the other way, blends in and out of his flight cleanly.
static partial class ManBat
{
    const string Lender = "man-bat";
    const string LookPackage = "Showcase_Characters_Manbat";
    const string FlightPackage = "CityX__ChN2";
    const string AttackPackage = "BatEntry__City_ChN1_LOD2";
    const string MovesPath = "anim_manbat.Code.MB_Movement";
    const string AttackPath = "Anim_Matinee_ManBat.ManBat.MB_BM_Attack";
    const string BodyPath = "Manbat_Mesh.Mesh.Manbat_Skin";
    const string SquealPath = "ww_chr_manbat_vocal.Play_ManBat_Idle_Squeal";
    const string HurtPath = "Ww_SEQ_Manbat_BM_Takedown_MB_Chase01.Play_SEQ_Manbat_Takedown_Rdm_ScreamShort_2D";
    const string Voice = "DLG_Voices.ManBat";

    // He shrieks at a hit at most this often.
    const float HurtGap = 1.5f;
    static AkEvent? _hurt;
    static float _hurtAt = -10f;

    // Hovering he beats his wings slowly. Flying hard he beats them faster. His attack plays on a
    // copy of its own, and his fast wing beat is held still on a copy of its own where his wings
    // are spread widest.
    const int Hover = 0, Cruise = 1, Action = 2, Spread = 3;
    static readonly FName[] Moves = { new("Flying_Cycle_01"), new("Flying_Cycle_02"), new("Ch_03_Teaser"), new("Flying_Cycle_02") };
    const float SpreadAt = 0.83f;

    static readonly FName LeftArm = new("Bip01_L_UpperArm");
    static readonly FName RightArm = new("Bip01_R_UpperArm");

    // His flight's root is his middle, with his feet hanging this far below. He is lifted so his
    // feet hang at Batman's. His attack is made standing on the ground and needs no lift.
    const float Hang = 110f;

    // His attack is made facing the other way from his flight.
    static readonly Quaternion AttackTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

    // His wing beats speed up with him, up to this much faster than their own pace.
    const float FastestBeat = 1.5f;

    // How far he is tipped up from lying flat while he hangs in the air, in degrees, and the
    // speed ahead by which he is flat. Climbing tips him further up and dropping tips him down,
    // most of all when he flies flat out. How far he banks into a turn, by how fast he turns in
    // degrees a second. How quickly both follow.
    const float Upright = 72f;
    const float FlatAt = 900f;
    const float ClimbTilt = 35f;
    const float HoverClimbShare = 0.4f;
    const float MostTip = 85f;
    const float LeastTip = -45f;
    const float MostBank = 45f;
    const float BankPerTurn = 0.25f;
    const float LeanEase = 0.25f;
    const float BlendSeconds = 0.35f;
    const float ActionBlend = 0.1f;

    // A squeal now and then while he flies, this many seconds apart at least, and at most.
    const float SquealSoonest = 9f;
    const float SquealLatest = 20f;

    // The player's own mesh's tick group. Parts shown in its place are worked out with it.
    const GameObject.ETickingGroup Shown = GameObject.ETickingGroup.TG_PreAsyncWork2;
    const GameObject.ETickingGroup Unseen = GameObject.ETickingGroup.TG_DuringAsyncWork;

    static bool _loadTried;
    static SkeletalMesh? _mesh;
    static MaterialInterface?[] _materials = Array.Empty<MaterialInterface?>();
    static PhysicsAsset? _physics;
    static AnimSet? _moves;
    static AnimSet? _attack;
    static AkEvent? _squeal;

    static RPawnPlayer? _on;
    static SkeletalMeshComponent? _body;
    static RAnimNode_AnimCapture? _pose;
    static readonly SkeletalMeshComponent?[] Copies = new SkeletalMeshComponent?[Moves.Length];
    static readonly AnimNodeSequence?[] Played = new AnimNodeSequence?[Moves.Length];
    static readonly HashSet<nint> Ours = new();
    static readonly float[] Weights = new float[Moves.Length];

    // How far his model is turned from the pawn's way, in degrees, so he faces where he flies.
    static float _turn;
    static float _lean;
    static float _bank;
    static float _lastYaw;
    static float _nextSqueal;
    static Atom[] _mixed = Array.Empty<Atom>();

    // Where his attack's root is held while the pawn carries the ground it covers.
    static Vector3 _pin;

    static bool? _installed;

    static bool Installed => _installed ??= GameFiles.HasPackage(LookPackage) && GameFiles.HasPackage(FlightPackage);

    // False once loading has failed, so it isn't tried every tick.
    static bool Load()
    {
        if (_mesh is { IsValid: true } && _moves is { IsValid: true })
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

        foreach (var actor in actors)
        {
            foreach (var source in Disguise.Sources(actor))
            {
                if (source.SkeletalMesh is not { IsValid: true } mesh || !mesh.GetPathName().Equals(BodyPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Borrowed.Keep(actor, Lender);
                _mesh = mesh;
                _physics = source.PhysicsAsset;
                var painted = source.Materials;
                _materials = new MaterialInterface?[painted.Count];
                for (var i = 0; i < _materials.Length; i++)
                {
                    _materials[i] = painted[i];
                }

                break;
            }

            if (_mesh is not null)
            {
                break;
            }
        }

        if (_mesh is null)
        {
            Log.Warn("man_bat.missing", "Man-Bat's body wasn't found in his showcase.");
            return false;
        }

        // The city may have these levels in already. Anything not kept from them before is gone
        // again, so each is loaded whenever what it holds is missing.
        if (Game.FindObject<AnimSet>(MovesPath) is not { IsValid: true } || Game.FindObject<AkEvent>(SquealPath) is not { IsValid: true }
            || Game.FindObject<AkEvent>(ImpactPath) is not { IsValid: true })
        {
            LoadPackage(FlightPackage);
        }

        if (Game.FindObject<AnimSet>(AttackPath) is not { IsValid: true } && GameFiles.HasPackage(AttackPackage))
        {
            LoadPackage(AttackPackage);
        }

        _moves = Kept(Game.FindObject<AnimSet>(MovesPath));
        _attack = Kept(Game.FindObject<AnimSet>(AttackPath));
        _squeal = Kept(Game.FindObject<AkEvent>(SquealPath));
        _impact = Kept(Game.FindObject<AkEvent>(ImpactPath));
        _scream = Kept(Game.FindObject<AkEvent>(ScreamPath));
        _hurt = Kept(Game.FindObject<AkEvent>(HurtPath));
        _impactLook = Kept(Game.FindObject<ParticleSystem>(ImpactLookPath));
        if (_moves is null)
        {
            Log.Warn("man_bat.moves", "Man-Bat's flight wasn't found in his chase level.");
            _mesh = null;
            return false;
        }

        if (_attack is null)
        {
            Log.Warn("man_bat.attack", "Man-Bat's attack wasn't found in his rooftop level, so he can't swoop.");
        }

        return true;
    }

    static bool LoadPackage(string package) =>
        Packages.TryLoad(package, $"{package} for Man-Bat", "man_bat.load");

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

    static T? Kept<T>(T? thing)
        where T : GameObject => Borrowed.KeepFound(thing, Lender);

    static void Build(RPawnPlayer player, SkeletalMeshComponent own)
    {
        Unbuild();
        for (var i = 0; i < Moves.Length; i++)
        {
            var set = i == Action ? _attack : _moves;
            if (set is not { IsValid: true })
            {
                continue;
            }

            var copy = Made.Tracked(new SkeletalMeshComponent(player));
            copy.SetActorCollision(false, false);
            copy.SetSkeletalMesh(_mesh!, false);
            copy.AnimSets.Add(set);
            var move = Made.Tracked(new AnimNodeSequence(copy));
            copy.Animations = move;
            copy.bUpdateSkelWhenNotRendered = true;
            copy.bCastDynamicShadow = false;
            copy.SetHidden(true);
            copy.SetTickGroup(Unseen);
            player.AttachComponent(copy);
            move.SetAnim(Moves[i]);
            if (i == Action)
            {
                move.StopAnim();
            }
            else if (i == Spread)
            {
                move.SetPosition(SpreadAt, false);
                move.StopAnim();
            }
            else
            {
                move.PlayAnim(true, 1f, 0f);
            }

            Copies[i] = copy;
            Played[i] = move;
            Ours.Add(copy.Ptr);
        }

        // His moves may face another way than Batman's. Both are read from the line across the
        // shoulders and he is turned by the difference, rounded to a quarter.
        Copies[Hover]!.ForceSkelUpdate();
        _turn = TurnFor(own, Copies[Hover]!);

        // The game's own node for playing back a pose it is handed. It has to be on the mesh
        // before the mesh is attached.
        var body = Made.Tracked(new SkeletalMeshComponent(player));
        body.SetActorCollision(false, false);
        body.SetSkeletalMesh(_mesh!, false);
        var physics = _physics is { IsValid: true };
        if (physics)
        {
            body.SetPhysicsAsset(_physics!, false);
        }

        var slots = Math.Min(_materials.Length, body.GetNumElements());
        for (var i = 0; i < slots; i++)
        {
            if (_materials[i] is { IsValid: true } material)
            {
                body.SetMaterial(i, material);
            }
        }

        body.SetTickGroup(Shown);
        body.bCastDynamicShadow = own.bCastDynamicShadow;
        body.SetLightingChannels(own.LightingChannels);

        // The player's own depth group. In the world's group, cinematic skin renders black.
        body.SetDepthPriorityGroup(own.DepthPriorityGroup);
        body.SetOwnerNoSee(own.bOwnerNoSee);
        _pose = Made.Tracked(new RAnimNode_AnimCapture(body));
        body.Animations = _pose;
        body.SetAllowRFlapsAssetInstance(physics);
        player.AttachComponent(body);

        // Asked for before it is attached, his pipes and ears hang still. So they are asked for
        // again once he is part of the world.
        if (physics && !body.bAllowRFlapsAssetInstance)
        {
            body.SetAllowRFlapsAssetInstance(true);
        }

        _body = body;
        Ours.Add(body.Ptr);
        _on = player;
        _lean = Upright;
        _bank = 0f;
        _lastYaw = player.Rotation.Yaw;
        _nextSqueal = World.Now + SquealSoonest;
        Array.Clear(Weights);
        Weights[Hover] = 1f;
        PlaceAll(own);
    }

    // Which move shows: his attack while it plays, otherwise the wing beat for how fast he flies.
    // Both wing beats quicken as he speeds up.
    static void Choose()
    {
        var delta = World.Delta;
        var share = Math.Clamp(_speed / FastSpeed, 0f, 1f);
        var striking = _act == Act.Strike;
        var screeching = _act == Act.Screech;
        var flying = !striking && !screeching;
        var blend = Settle(delta, !flying || Weights[Action] > 0.01f || Weights[Spread] > 0.01f ? ActionBlend : BlendSeconds);
        Weights[Hover] += ((flying ? 1f - share : 0f) - Weights[Hover]) * blend;
        Weights[Cruise] += ((flying ? share : 0f) - Weights[Cruise]) * blend;
        Weights[Action] += ((striking ? 1f : 0f) - Weights[Action]) * blend;
        Weights[Spread] += ((screeching ? 1f : 0f) - Weights[Spread]) * blend;

        // A move's own sounds start on its first frame, before it is most on show.
        var heaviest = striking ? Action : screeching ? Spread : Weights[Cruise] > Weights[Hover] ? Cruise : Hover;
        for (var i = 0; i < Played.Length; i++)
        {
            if (Played[i] is not { IsValid: true } move)
            {
                continue;
            }

            // Every move carries its own sounds. Only the one most on show sounds.
            if (move.bNoNotifies != (i != heaviest))
            {
                move.bNoNotifies = i != heaviest;
            }

            if (i is Hover or Cruise)
            {
                move.Rate = 1f + (FastestBeat - 1f) * share;
            }
        }

        // He tips up as he slows and banks the way he is turning, eased so he doesn't twitch.
        var yaw = _on is { IsValid: true } on ? on.Rotation.Yaw : _lastYaw;
        var turning = delta > 0f ? Wrap(yaw - _lastYaw) / delta : 0f;
        _lastYaw = yaw;
        var ease = Settle(delta, LeanEase);
        var flat = Math.Clamp(_ahead * FastSpeed / FlatAt, 0f, 1f);
        var climb = ClimbTilt * _climb * (HoverClimbShare + (1f - HoverClimbShare) * flat);
        var tip = screeching ? ScreechTip : Math.Clamp(Upright * (1f - flat * (2f - flat)) + climb, LeastTip, MostTip);
        _lean += (tip - _lean) * (screeching ? Settle(delta, HeadIn) : ease);
        ShriekPose();
        _bank += (Math.Clamp(turning * BankPerTurn + MostBank * 0.5f * _sideways, -MostBank, MostBank) - _bank) * ease;
    }

    // His model takes the player's mesh's place, turned to face where he flies. The hidden copies
    // stand there too, so effects their moves spawn on their bones land on him.
    static void PlaceAll(SkeletalMeshComponent own)
    {
        var turn = own.Rotation with { Yaw = own.Rotation.Yaw + _turn };
        Place(_body, own.Translation, turn);
        foreach (var copy in Copies)
        {
            Place(copy, own.Translation, turn);
        }
    }

    static void Place(SkeletalMeshComponent? piece, Vector3 at, Rotator turn)
    {
        if (piece is not { IsValid: true })
        {
            return;
        }

        if (piece.Translation != at)
        {
            piece.SetTranslation(at);
        }

        if (piece.Rotation != turn)
        {
            piece.SetRotation(turn);
        }
    }

    // The moves blended by weight and handed to his body, once a tick before the game works out
    // its meshes.
    static void Pose()
    {
        if (_body is not { IsValid: true } || _pose is not { IsValid: true } pose || Copies[Hover] is not { IsValid: true } hover)
        {
            return;
        }

        pose.Capture(hover);
        var atoms = pose.SavedAtoms.AsSpan();
        Mix(atoms);
        Throw(atoms);
    }

    // How each move's root is laid in his model. His front runs along his mesh's Y, so tipping
    // him up turns him about X and a bank about Y. A bank to his right lowers his right, which
    // lies along -X.
    static void RootFix(int move, ref Quaternion turn, ref Vector3 at)
    {
        if (move == Action)
        {
            turn = AttackTurn * turn;
            at = Vector3.Transform(_pin, AttackTurn);
            return;
        }

        var fix = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -_bank * (MathF.PI / 180f))
            * Quaternion.CreateFromAxisAngle(Vector3.UnitX, _lean * (MathF.PI / 180f));
        turn = fix * turn;
        at = Vector3.Transform(at, fix) + Vector3.UnitZ * Hang;
    }

    // The copies' poses blended by weight. The turns are summed on one side of the first one,
    // then made whole again.
    static void Mix(Span<Atom> into)
    {
        var count = into.Length;
        if (count == 0)
        {
            return;
        }

        if (_mixed.Length < count)
        {
            _mixed = new Atom[count];
        }

        var mixed = _mixed.AsSpan(0, count);
        mixed.Clear();
        var total = 0f;
        var first = true;
        for (var i = 0; i < Copies.Length; i++)
        {
            if (Weights[i] <= 0.001f || Copies[i] is not { IsValid: true } copy || copy.LocalAtoms.Count != count)
            {
                continue;
            }

            total += Weights[i];
            var atoms = copy.LocalAtoms.AsSpan();
            for (var bone = 0; bone < count; bone++)
            {
                var turn = Numeric(atoms[bone].Rotation);
                var at = atoms[bone].Translation;
                if (bone == 0)
                {
                    RootFix(i, ref turn, ref at);
                }

                var sum = mixed[bone];
                if (!first && Quaternion.Dot(Numeric(sum.Rotation), turn) < 0f)
                {
                    turn = -turn;
                }

                sum.Rotation = ToTurn(Numeric(sum.Rotation) + turn * Weights[i]);
                sum.Translation += at * Weights[i];
                sum.Scale += atoms[bone].Scale * Weights[i];
                mixed[bone] = sum;
            }

            first = false;
        }

        if (total <= 0f)
        {
            return;
        }

        for (var bone = 0; bone < count; bone++)
        {
            var atom = mixed[bone];
            atom.Rotation = ToTurn(Quaternion.Normalize(Numeric(atom.Rotation)));
            atom.Translation /= total;
            atom.Scale /= total;
            into[bone] = atom;
        }
    }

    // Now and then, while he is about, he squeals.
    static void Squeal(RPawnPlayer player)
    {
        var now = World.Now;
        if (now < _nextSqueal || _act != Act.None)
        {
            return;
        }

        _nextSqueal = now + SquealSoonest + Random.Shared.NextSingle() * (SquealLatest - SquealSoonest);
        if (_squeal is { IsValid: true } squeal)
        {
            player.StartAudioEvent(squeal);
        }
    }

    static float TurnFor(SkeletalMeshComponent own, SkeletalMeshComponent hover)
    {
        if (Facing(own) is not { } mine || Facing(hover) is not { } his)
        {
            return 0f;
        }

        return 90f * MathF.Round((mine - his) / 90f);
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
            Detach(owner, _body);
            foreach (var copy in Copies)
            {
                Detach(owner, copy);
            }
        }

        Ours.Clear();
        Array.Clear(Copies);
        Array.Clear(Played);
        Array.Clear(Weights);
        _body = null;
        _pose = null;
        _on = null;
        _turn = 0f;
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
        _mesh = null;
        _physics = null;
        _materials = Array.Empty<MaterialInterface?>();
        _moves = null;
        _attack = null;
        _squeal = null;
        _impact = null;
        _scream = null;
        _hurt = null;
        _downed = 0;
        Shrieks.Unload();
        _impactLook = null;
        _dangleIn = null;
        _dangle = null;
        _dangleLooked = false;
        _loadTried = false;
        Borrowed.Let(Lender);
    }

    static float Settle(float delta, float seconds) => 1f - MathF.Exp(-delta / seconds);

    static float Wrap(float degrees)
    {
        degrees %= 360f;
        return degrees > 180f ? degrees - 360f : degrees < -180f ? degrees + 360f : degrees;
    }
}
