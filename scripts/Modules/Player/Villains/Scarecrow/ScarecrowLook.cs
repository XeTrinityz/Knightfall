#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static Knightfall.Turns;
using Atom = BmSDK.GameObject.FBoneAtom;

namespace Knightfall;

// How the Scarecrow looks and moves. His body is his showcase, worn the way DISGUISE wears it,
// so Batman's run, jumps and climbs move him. His own moves only ship with the airship scene
// where he faces Batman, and his fear dart with the level it was cut from. Both are loaded as
// packages while he is on. Nothing in them runs.
//
// His moves play on hidden copies of his body, one move each. Standing and creeping, and in
// each of his moves, their pose is laid over the one Batman's moves give him.
static partial class Scarecrow
{
    const string LookPackage = "Showcase_Characters_Scarecrow";
    const string MovesPackage = "Anim_Scarecrow_SF";
    const string DartPackage = "CityX_06_Ch4";
    // Every part of him. The one DISGUISE poses the rest from is his head, which has his whole
    // skeleton.
    const string MeshPrefix = "Scarecrow_Mesh.Mesh.Scarecrow_";
    const string MovesPath = "Anim_Scarecrow.Code.Stagg_B3_Anim_SC_BM";
    const string WristPath = "Anim_Nightwing_Gadget.Code.NW_Wrist_Dart";
    const string DartHitsPath = "Anim_Thug.Code.TG_Wrist_Dart_HitReaction";
    const string StepPath = "ww_chr_scarecrow.Play_Scarecrow_Footstep";
    const string StabPath = "Ww_SEQ_Stagg_B3_Scarecrow.Play_Stagg_B3_Scarecrow_Stab";
    const string DartFirePath = "Ww_CHR_Nightwing_WristDart.Play_NW_Wrist_Dart_FIRE";
    const string PuffPath = "VFX_Gadgets.Particles.Dart_puff";
    const string GasPath = "vfx_feargas.Particles.FearGas_KickUp";
    const string SplashPath = "PFX_Batman.Particles.FearGas.fx_SplashInFearGas_p";
    const string Voice = "DLG_Voices.Scarecrow";

    // The sound banks his steps, his stab and the dart's shot are in.
    static readonly string[] BankNames = { "CHR_Scarecrow", "SEQ_Stagg_B3_Scarecrow", "CHR_Nightwing_WristDart" };

    // The two action copies take turns playing one move at a time, so a move cut short by the
    // next one can fade out under it.
    const int Idle = 0, Creep = 1, Action = 2, NextAction = 3;
    static readonly FName[] Moves = { new("Scarecrow_Cautious_Idle"), new("Scarecrow_Walk_Forwards"), new("Scarecrow_Cautious_Idle"), new("Scarecrow_Cautious_Idle") };

    // The shoulders show which way a skeleton's moves face.
    static readonly FName LeftArm = new("Bip01_L_UpperArm");
    static readonly FName RightArm = new("Bip01_R_UpperArm");

    // His creep covers about this much ground a second at its own pace, judged from how far it
    // carries his root over a loop. It is sped up or slowed down this far at most.
    const float CreepSpeed = 57f;
    const float SlowestRate = 0.7f;
    const float FastestRate = 2.2f;

    // Under the first speed he stands, by the second he creeps, and his own pose gives way to
    // Batman's walk and run between the last two.
    const float StillSpeed = 15f;
    const float CreepFull = 60f;
    const float OwnFades = 110f;
    const float OwnGone = 200f;

    const float BlendSeconds = 0.25f;
    const float ActionBlend = 0.08f;
    const float SpeedEase = 0.15f;

    static bool _loadTried;
    static AnimSet? _moves;
    static AnimSet? _wrist;
    static AnimSet? _dartHits;
    static AkEvent? _step;
    static AkEvent? _stab;
    static AkEvent? _dartFire;
    static ParticleSystem? _puff;
    static ParticleSystem? _gas;
    static ParticleSystem? _splash;
    static readonly List<AkBank> Banks = new();

    static SkeletalMeshComponent? _body;
    static RPawnPlayer? _copiesOn;
    static nint _copiesOf;
    static readonly SkeletalMeshComponent?[] Copies = new SkeletalMeshComponent?[Moves.Length];
    static readonly AnimNodeSequence?[] Played = new AnimNodeSequence?[Moves.Length];
    static readonly float[] Weights = new float[Moves.Length];
    static readonly float[] Wanted = new float[Moves.Length];

    // How far his own pose is laid over Batman's, from 0 to 1.
    static float _cover;
    static float _speed;
    static Atom[] _mixed = Array.Empty<Atom>();

    // His moves face a quarter round from Batman's, which is along X. Read once from his idle.
    static Quaternion? _rootFix;
    static float _rootTurn = -90f;

    static bool? _installed;

    static bool Installed => _installed ??= GameFiles.HasPackage(LookPackage) && GameFiles.HasPackage(MovesPackage);

    // False once loading has failed, so it isn't tried every tick.
    static bool Load()
    {
        if (_moves is { IsValid: true })
        {
            return true;
        }

        if (_loadTried)
        {
            return false;
        }

        _loadTried = true;
        if (Game.FindObject<AnimSet>(MovesPath) is not { IsValid: true } || Game.FindObject<AkEvent>(StabPath) is not { IsValid: true })
        {
            LoadPackage(MovesPackage);
        }

        // The dart is a plus. Without it the rest of him still works and the dart locks.
        if (Game.FindObject<AnimSet>(WristPath) is not { IsValid: true } || Game.FindObject<ParticleSystem>(PuffPath) is not { IsValid: true }
            || Game.FindObject<AkEvent>(DartFirePath) is not { IsValid: true })
        {
            LoadPackage(DartPackage);
        }

        _moves = Kept(Game.FindObject<AnimSet>(MovesPath));
        _wrist = Kept(Game.FindObject<AnimSet>(WristPath));
        _dartHits = Kept(Game.FindObject<AnimSet>(DartHitsPath));
        _step = Kept(Game.FindObject<AkEvent>(StepPath));
        _stab = Kept(Game.FindObject<AkEvent>(StabPath));
        _dartFire = Kept(Game.FindObject<AkEvent>(DartFirePath));
        _puff = Kept(Game.FindObject<ParticleSystem>(PuffPath));
        _gas = Kept(Game.FindObject<ParticleSystem>(GasPath));
        _splash = Kept(Game.FindObject<ParticleSystem>(SplashPath));
        if (_moves is null)
        {
            Log.Warn("scarecrow.moves", "The Scarecrow's own moves weren't found.");
            return false;
        }

        if (_wrist is null)
        {
            Log.Warn("scarecrow.dart", "The Scarecrow's fear dart wasn't found, so FEAR DART is locked.");
        }

        foreach (var bank in Game.FindObjects<AkBank>())
        {
            if (bank.IsValid && Array.Exists(BankNames, name => name.Equals(bank.Name.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                Borrowed.Keep(bank, Lender);
                bank.LoadBank(false);
                Banks.Add(bank);
            }
        }

        return true;
    }

    static bool LoadPackage(string package) =>
        Packages.TryLoad(package, $"{package} for the Scarecrow", "scarecrow.load");

    static T? Kept<T>(T? thing)
        where T : GameObject => Borrowed.KeepFound(thing, Lender);

    // The worn body, while the worn look is his.
    static SkeletalMeshComponent? WornBody()
    {
        if (Disguise.WornBody is not { } body)
        {
            _body = null;
            return null;
        }

        if (_body is null || _body.Ptr != body.Ptr)
        {
            _body = body.SkeletalMesh is { IsValid: true } mesh && mesh.GetPathName().StartsWith(MeshPrefix, StringComparison.OrdinalIgnoreCase) ? body : null;
        }

        return _body;
    }

    static void Animate(RPawnPlayer? player, SkeletalMeshComponent? body)
    {
        var ready = player is not null && body is { SkeletalMesh: { IsValid: true } } && _moves is { IsValid: true };
        if (ready && (_copiesOn is not { IsValid: true } on || on.Ptr != player!.Ptr || _copiesOf != body!.SkeletalMesh.Ptr))
        {
            Uncopy();
            MakeCopies(player!, body!.SkeletalMesh);
        }

        if (!ready)
        {
            _cover = 0f;
            Array.Clear(Weights);
            Updating(false);
            if (Disguise.Repose == (Action<SkeletalMeshComponent, TArray<Atom>>)Repose)
            {
                Disguise.Repose = null;
            }

            return;
        }

        Updating(true);
        Choose(player!);
        Disguise.Repose = Repose;
    }

    // Standing or creeping on his own feet his own pose shows. Anything else is Batman's move
    // under him: running, jumping, climbing, crouching, or a hit.
    static bool OnFoot(RPawnPlayer player) =>
        player.Physics == Actor.EPhysics.PHYS_Walking && player.CurrentSpecialMove is not { IsValid: true } && !Shaken(player)
        && !player.bIsCrouched;

    // Batman taking a hit or rolling out of the way under him. Those show as his.
    static bool Shaken(RPawnPlayer player) => player.CombatMove is (RCombatMove_BatmanHit or RCombatMove_BatmanEvade) and { IsValid: true };

    static void Choose(RPawnPlayer player)
    {
        var delta = World.Delta;
        var velocity = player.Velocity;
        var moving = player.Physics == Actor.EPhysics.PHYS_Walking ? new Vector2(velocity.X, velocity.Y).Length() : 0f;
        _speed += (moving - _speed) * Settle(delta, SpeedEase);

        Array.Clear(Wanted);
        var cover = 0f;
        if (Acting)
        {
            Wanted[_slot] = 1f;
            cover = 1f;
        }
        else if (OnFoot(player))
        {
            var creep = Ramp(_speed, StillSpeed, CreepFull);
            Wanted[Idle] = 1f - creep;
            Wanted[Creep] = creep;
            cover = 1f - Ramp(_speed, OwnFades, OwnGone);
            if (Played[Creep] is { IsValid: true } creeping)
            {
                creeping.Rate = Math.Clamp(_speed / CreepSpeed, SlowestRate, FastestRate);
            }
        }

        var blend = Settle(delta, Acting || Weights[Action] > 0.01f || Weights[NextAction] > 0.01f ? ActionBlend : BlendSeconds);
        var heaviest = 0;
        for (var i = 0; i < Weights.Length; i++)
        {
            Weights[i] += (Wanted[i] - Weights[i]) * blend;
            if (Weights[i] > Weights[heaviest])
            {
                heaviest = i;
            }
        }

        _cover += (cover - _cover) * blend;
        if (cover <= 0f && _cover < 0.01f)
        {
            _cover = 0f;
        }

        // A move's own sounds start on its first frame, before it is most on show.
        if (Acting)
        {
            heaviest = _slot;
        }

        // Each move carries its own sounds, like his gear rattling. Only the one most on show
        // sounds, and none while Batman's moves are what shows.
        for (var i = 0; i < Played.Length; i++)
        {
            var quiet = i != heaviest || _cover < 0.5f;
            if (Played[i] is { IsValid: true } move && move.bNoNotifies != quiet)
            {
                move.bNoNotifies = quiet;
            }
        }
    }

    static void MakeCopies(RPawnPlayer player, SkeletalMesh mesh)
    {
        for (var i = 0; i < Moves.Length; i++)
        {
            var copy = Made.Tracked(new SkeletalMeshComponent(player));
            copy.SetActorCollision(false, false);
            copy.SetSkeletalMesh(mesh, false);
            copy.AnimSets.Add(_moves!);
            if (i >= Action && _wrist is { IsValid: true } wrist)
            {
                copy.AnimSets.Add(wrist);
            }

            var move = Made.Tracked(new AnimNodeSequence(copy));
            copy.Animations = move;
            copy.bUpdateSkelWhenNotRendered = true;
            copy.bCastDynamicShadow = false;
            copy.SetHidden(true);
            copy.SetTickGroup(GameObject.ETickingGroup.TG_DuringAsyncWork);
            player.AttachComponent(copy);
            move.SetAnim(Moves[i]);
            move.PlayAnim(i < Action, 1f, 0f);
            if (i >= Action)
            {
                move.StopAnim();
            }

            Copies[i] = copy;
            Played[i] = move;
        }

        _copiesOn = player;
        _copiesOf = mesh.Ptr;
        _slot = Action;
        Array.Clear(Weights);
        Weights[Idle] = 1f;
    }

    // Hidden copies only work out their pose while it is used.
    static void Updating(bool on)
    {
        foreach (var copy in Copies)
        {
            if (copy is { IsValid: true } && copy.bUpdateSkelWhenNotRendered != on)
            {
                copy.bUpdateSkelWhenNotRendered = on;
            }
        }
    }

    static void Uncopy()
    {
        var owner = _copiesOn is { IsValid: true } had ? had : null;
        for (var i = 0; i < Copies.Length; i++)
        {
            if (Copies[i] is { IsValid: true } copy)
            {
                copy.SetHidden(true);
                owner?.DetachComponent(copy);
            }

            Copies[i] = null;
            Played[i] = null;
        }

        _copiesOn = null;
        _copiesOf = 0;
    }

    static void Repose(SkeletalMeshComponent body, TArray<Atom> saved)
    {
        if (_cover <= 0f || _body is null || body.Ptr != _body.Ptr)
        {
            return;
        }

        Mix(saved.AsSpan());
    }

    // His moves blended by weight, each with its root held over where he stands, since the pawn
    // does the moving. Then laid over the pose from Batman's moves by how far he is covered. The
    // turns are summed on one side of the first one, then made whole again.
    static void Mix(Span<Atom> pose)
    {
        if (Copies[Idle] is not { IsValid: true } idler || idler.LocalAtoms.Count != pose.Length || pose.Length == 0)
        {
            return;
        }

        Span<int> used = stackalloc int[Copies.Length];
        var count = 0;
        var total = 0f;
        for (var i = 0; i < Copies.Length; i++)
        {
            if (Weights[i] > 0.001f && Copies[i] is { IsValid: true } copy && copy.LocalAtoms.Count == pose.Length)
            {
                used[count++] = i;
                total += Weights[i];
            }
        }

        if (count == 0 || total <= 0f)
        {
            return;
        }

        var rootFix = RootFix(idler);
        var stand = idler.LocalAtoms[0].Translation;
        if (_mixed.Length != pose.Length)
        {
            _mixed = new Atom[pose.Length];
        }

        var mixed = _mixed.AsSpan();
        mixed.Clear();
        for (var n = 0; n < count; n++)
        {
            var i = used[n];
            var share = Weights[i] / total;
            var atoms = Copies[i]!.LocalAtoms.AsSpan();
            for (var bone = 0; bone < pose.Length; bone++)
            {
                var turn = Numeric(atoms[bone].Rotation);
                var at = atoms[bone].Translation;
                if (bone == 0)
                {
                    at = new Vector3(stand.X, stand.Y, at.Z);
                    turn = rootFix * turn;
                    at = Vector3.Transform(at, rootFix);
                }

                var sum = mixed[bone];
                if (n > 0 && Quaternion.Dot(Numeric(sum.Rotation), turn) < 0f)
                {
                    turn = -turn;
                }

                sum.Rotation = ToTurn(Numeric(sum.Rotation) + turn * share);
                sum.Translation += at * share;
                sum.Scale += atoms[bone].Scale * share;
                mixed[bone] = sum;
            }
        }

        for (var bone = 0; bone < pose.Length; bone++)
        {
            var mine = pose[bone];
            var theirs = mixed[bone];
            mine.Rotation = ToTurn(Quaternion.Slerp(Numeric(mine.Rotation), Quaternion.Normalize(Numeric(theirs.Rotation)), _cover));
            mine.Translation = Vector3.Lerp(mine.Translation, theirs.Translation, _cover);
            mine.Scale += (theirs.Scale - mine.Scale) * _cover;
            pose[bone] = mine;
        }
    }

    // Which way his moves face, read once from his idle as the line across his shoulders, and
    // rounded to the quarter so his sway doesn't move it.
    static Quaternion RootFix(SkeletalMeshComponent idler)
    {
        if (_rootFix is { } known)
        {
            return known;
        }

        var whole = idler.SpaceBases;
        var left = idler.MatchRefBone(LeftArm);
        var right = idler.MatchRefBone(RightArm);
        if (left < 0 || right < 0 || whole.Count <= Math.Max(left, right))
        {
            return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _rootTurn * (MathF.PI / 180f));
        }

        var across = whole[right].Translation - whole[left].Translation;
        if (across.X * across.X + across.Y * across.Y <= 1f)
        {
            return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _rootTurn * (MathF.PI / 180f));
        }

        // His right is a quarter round from his front.
        var facing = MathF.Atan2(across.Y, across.X) * (180f / MathF.PI) - 90f;
        _rootTurn = -90f * MathF.Round(facing / 90f);
        _rootFix = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _rootTurn * (MathF.PI / 180f));
        return _rootFix.Value;
    }

    // Each footstep the game plays for the player asks for its sound. Batman's feet under him
    // sound as his boots. His hands planting on a vault keep Batman's.
    public static bool Steps(RPawnPlayer pawn, RAnimNotify_Footstep.EFoot foot, out AkEvent? sound)
    {
        sound = null;
        if (_body is not { IsValid: true } || World.Player is not { } player || player.Ptr != pawn.Ptr || _step is not { IsValid: true } step
            || foot is RAnimNotify_Footstep.EFoot.Hand_Left or RAnimNotify_Footstep.EFoot.Hand_Right or RAnimNotify_Footstep.EFoot.Hand_Both)
        {
            return false;
        }

        sound = step;
        return true;
    }

    static void Unload()
    {
        Unact();
        Uncopy();
        Animate(null, null);
        PutOutCloud();
        foreach (var bank in Banks)
        {
            if (bank.IsValid)
            {
                bank.UnloadBank(false);
            }
        }

        Banks.Clear();
        Dosed.Clear();
        Flying.Clear();
        _body = null;
        _moves = null;
        _wrist = null;
        _dartHits = null;
        _step = null;
        _stab = null;
        _dartFire = null;
        _puff = null;
        _gas = null;
        _splash = null;
        _burst = null;
        _cloudTried = false;
        _rootFix = null;
        _rootTurn = -90f;
        _loadTried = false;
        _darts = DartsMost;
        _nextToxin = 0f;
        _nextBroadcast = 0f;
        _collapsed = 0;
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
