#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static Knightfall.Turns;
using Atom = BmSDK.GameObject.FBoneAtom;
using Turn = BmSDK.GameObject.FQuat;

namespace Knightfall;

// How Firefly looks and sounds in the air. His flying moves, jet sound and in-game exhaust only
// ship in his story levels, so one is loaded as a package while FIREFLY is on, the way DISGUISE
// loads a showcase. Nothing in it runs. Only these assets are taken from it.
//
// His moves play on hidden copies of his skeleton, one move each, and their poses are blended
// into the worn body by how he flies, the way the game picks his moves by direction.
static partial class Firefly
{
    const string LookPackage = "Showcase_Characters_Firefly";
    const string MissionPackage = "CityX__ChF478";

    const string BodyMeshPath = "firefly_v2_mesh.Mesh.Firefly_V2_Skin";
    const string FlyingSetPath = "Anim_Firefly.firefly.Firefly_Flamethrower";
    const string JetSoundPath = "Ww_CHR_Firefly.Play_Firefly_Jetpack_Loop";
    const string ExhaustPath = "EFX_SideStories.firefly.Firefly_JetExhaust";
    const string ShowcaseExhaustPath = "VFX_Cinematics.Particles.Marketing.Firefly_JetExhaust_Showcase";
    const string TrailPath = "BFX_FireFly.Particles.LongTrail";

    static readonly FName[] Exhausts = { new("Exhaust_Left"), new("Exhaust_Right") };
    static readonly FName LeftArm = new("Bip01_L_UpperArm");
    static readonly FName RightArm = new("Bip01_R_UpperArm");
    static readonly FName TrailRate = new("Rate");
    static readonly FName TrailBoost = new("Boost");
    static readonly FName TrailFuel = new("OutOfFuel");

    // His trail's own tuning: thinnest smoke, and the speed it is thickest at.
    const float TrailLeast = 0.03f;
    const float TrailMost = 0.4f;
    const float TrailFullSpeed = 1000f;

    // His trail flares as a flight starts, the way his boss's does on a burst of speed, then
    // settles so it doesn't hide him.
    const float TrailFlare = 0.4f;
    const float TrailBoostSeconds = 0.8f;
    static float _boostUntil;
    static bool _wasPushing;

    const int Idle = 0, Forward = 1, Up = 2, Down = 3, Left = 4, Right = 5, Throwing = 6;
    static readonly FName[] Moves =
    {
        new("FF_Idle"), new("FF_Fly_Forward"), new("FF_Fly_Up"), new("FF_Fly_Down"), new("FF_Fly_Left"), new("FF_Fly_Right"), new("FF_Fire_Grenade"),
    };

    // His throw plays once over his flying for this long. Aiming, he holds it at this far
    // through, where the gun is up.
    const float ThrowSeconds = 0.7f;
    const float AimHold = 0.35f;

    // How fast the flight has to turn, in degrees a second, before his turn moves start to show,
    // and where they are full.
    const float TurnStarts = 20f;
    const float TurnFull = 90f;

    // He rolls into a turn the way his boss does, up to this far, this much per degree a second
    // he turns, easing there in the other.
    const float BankMost = 45f;
    const float BankPerTurn = 0.35f;
    const float BankEase = 0.2f;

    // His climb and dive moves start to show at this share of his fastest climb, and are full at
    // the other.
    const float SlopeStarts = 0.1f;
    const float SlopeFull = 0.7f;

    // His whole body noses up or down with his slope in a flight, this much of it, up to this far.
    const float TiltShare = 0.6f;
    const float TiltMost = 25f;

    // Seconds to blend between moves, and to hand the body back to your own moves on landing.
    const float BlendSeconds = 0.2f;
    const float TurnSmoothing = 0.15f;

    static bool _loadTried;
    static AnimSet? _flyingSet;
    static AkEvent? _jetSound;
    static ParticleSystem? _exhaust;
    static ParticleSystem? _jetFlame;
    static ParticleSystem? _trail;

    static SkeletalMeshComponent? _body;
    static readonly SkeletalMeshComponent?[] Flyers = new SkeletalMeshComponent?[7];
    static readonly AnimNodeSequence?[] Played = new AnimNodeSequence?[7];
    static float _throwUntil;
    static bool _holding;
    static RPawnPlayer? _flyersOn;
    static readonly float[] Weights = new float[7];
    static readonly float[] Wanted = new float[7];
    static float _posed;

    // On the ground his arms and the gun keep his own pose, held across his body in both hands,
    // while the rest of him moves as you do. Upper marks those bones on the worn body.
    static float _held;
    static bool[] _upper = Array.Empty<bool>();
    static nint _upperOf;
    const string Shoulder = "Clavicle";
    const string GunBone = "Gundummy";
    const string FanBone = "engine_fan";
    static int[] _fans = Array.Empty<int>();

    // His aim pose was made for a throw in flight, so on the ground his upper body leans from the
    // spine until the flamethrower points at the crosshair.
    static readonly VillainPose AimLean = new(BlendSeconds) { LeanAt = "Spine", Barrel = Barrel };

    // His fans turn this fast in degrees a second in the air, the speed his chase turns them at.
    // On the ground they idle slower, and they take this long to change speed.
    const float FanFlying = 2300f;
    const float FanIdle = 700f;
    const float FanEase = 0.6f;
    static float _fanSpeed;
    static float _fanTurn;

    // His flying moves are single held poses, so nothing on his jetpack moves in them. His chase
    // moves angle the jets into a turn, flutter their flaps and swing them down to hover. The
    // jetpack bones take the chase move that goes with each flying move. He has no chase move for
    // a dive or a throw, so those take the forward and hover ones.
    const string ChaseSetPath = "Anim_Firefly.firefly.FF_BM_Firefly";
    static readonly FName[] ChaseMoves =
    {
        new("FF_Escape_Idle"), new("FF_Escape_Forward"), new("FF_Escape_Up"), new("FF_Escape_Left"), new("FF_Escape_Right"),
    };

    static readonly int[] ChaseFor = { 0, 1, 2, 1, 3, 4, 0 };
    // The arms that hold his wings out from his back stay as his flying pose has them, since the
    // chase holds them out of line. Only the parts on the wings move with the chase.
    static readonly string[] JetParts = { "jet_back_jet_hinge", "jet_flap", "hinge_exhaust", "wing_tip", "airbrake" };
    static AnimSet? _chaseSet;
    static readonly SkeletalMeshComponent?[] Chasers = new SkeletalMeshComponent?[5];
    static readonly float[] ChaseWeights = new float[5];
    static bool[] _jet = Array.Empty<bool>();
    const string FlapPart = "jet_flap";
    const float FlapShare = 0.4f;
    static bool[] _flap = Array.Empty<bool>();
    static Atom[] _mixed = Array.Empty<Atom>();
    static Quaternion? _rootFix;
    static float _lastYaw;
    static float _yawRate;
    static float _bank;
    static float _tilt;

    // An effect made on a pawn's own component never started, so his jets are the game's pooled
    // effects, the way its gadgets show theirs, put on his jet sockets every frame.
    sealed record Jet(Emitter Effect, FName Socket, bool Trail);

    static readonly List<Jet> Jets = new();
    static SkeletalMeshComponent? _mount;

    static RPawnPlayer? _soundOn;

    static void Load()
    {
        if (_loadTried)
        {
            return;
        }

        _loadTried = true;
        if (Game.FindObject<AnimSet>(FlyingSetPath) is not { IsValid: true } && !Packages.TryLoad(MissionPackage, "Firefly's flying moves", "firefly.load"))
        {
            return;
        }

        _flyingSet = Kept(Game.FindObject<AnimSet>(FlyingSetPath));
        _chaseSet = Kept(Game.FindObject<AnimSet>(ChaseSetPath));
        _jetSound = Kept(Game.FindObject<AkEvent>(JetSoundPath));
        // His story level's exhaust is a big flame burst, which makes his flamethrower. His jets
        // carry the showcase's small flame.
        _exhaust = Kept(Game.FindObject<ParticleSystem>(ExhaustPath));
        _jetFlame = Kept(Game.FindObject<ParticleSystem>(ShowcaseExhaustPath));
        _trail = Kept(Game.FindObject<ParticleSystem>(TrailPath));
        if (_flyingSet is null)
        {
            Log.Warn("firefly.moves", "Firefly's flying moves weren't found, so he flies in Batman's glide pose.");
        }
    }

    static T? Kept<T>(T? thing)
        where T : GameObject => Borrowed.KeepFound(thing, Lender);

    static void Show(RPawnPlayer? player, Mode mode)
    {
        var body = player is null ? null : FireflyBody();
        var up = mode != Mode.Grounded;
        Burn(player, body, up);
        Sound(player, up);
        Animate(player, body, mode);
    }

    // The worn body, while the worn look is Firefly.
    static SkeletalMeshComponent? FireflyBody()
    {
        if (Disguise.WornBody is not { } body)
        {
            _body = null;
            return null;
        }

        if (_body is null || _body.Ptr != body.Ptr)
        {
            _body = body.SkeletalMesh is { IsValid: true } mesh && mesh.GetPathName().Equals(BodyMeshPath, StringComparison.OrdinalIgnoreCase) ? body : null;
        }

        return _body;
    }

    static void Animate(RPawnPlayer? player, SkeletalMeshComponent? body, Mode mode)
    {
        var ready = player is not null && body is { SkeletalMesh: { IsValid: true } } && _flyingSet is { IsValid: true };
        var air = ready && mode != Mode.Grounded;

        // Batman's own moves need the arms, so the gun pose gives way to a fight or a special move.
        var holding = ready && !air && player!.Physics == Actor.EPhysics.PHYS_Walking
            && player.CurrentSpecialMove is not { IsValid: true } && player.CombatMove is not { IsValid: true };
        var on = air || holding;

        // A throw swings the gun about by itself, so the lean waits for it to finish.
        AimLean.UpdateLean(player, body, holding && Aiming, PointAt, World.Now >= _throwUntil);
        if (on && (_flyersOn is null || !_flyersOn.IsValid || _flyersOn.Ptr != player!.Ptr))
        {
            Unfly();
            MakeFlyers(player!, body!.SkeletalMesh, _flyingSet!);
        }

        Array.Clear(Wanted);
        if (on)
        {
            Choose(player!, mode);
        }

        var blend = Settle(World.Delta, BlendSeconds);
        for (var i = 0; i < Weights.Length; i++)
        {
            Weights[i] += (Wanted[i] - Weights[i]) * blend;
        }

        _posed += ((air ? 1f : 0f) - _posed) * blend;
        _held += ((holding ? 1f : 0f) - _held) * blend;
        var moving = on || _posed >= 0.01f || _held >= 0.01f;
        if (!moving)
        {
            _posed = 0f;
            _held = 0f;
            Array.Clear(Weights);
        }

        var fanSpeed = !ready ? 0f : air ? FanFlying : FanIdle;
        _fanSpeed += (fanSpeed - _fanSpeed) * Settle(World.Delta, FanEase);
        _fanTurn = (_fanTurn + _fanSpeed * World.Delta) % 360f;

        Animating(moving, _posed >= 0.01f);
        Disguise.Repose = ready ? Repose : null;
    }

    static void Choose(RPawnPlayer player, Mode mode)
    {
        var delta = World.Delta;
        var yaw = player.Rotation.Yaw;
        var turned = yaw - _lastYaw;
        turned -= 360f * MathF.Round(turned / 360f);
        _lastYaw = yaw;
        var rate = delta > 0f ? turned / delta : 0f;
        _yawRate += (rate - _yawRate) * Settle(delta, TurnSmoothing);

        var wantedBank = mode == Mode.Flying ? Math.Clamp(_yawRate * BankPerTurn, -BankMost, BankMost) : 0f;
        _bank += (wantedBank - _bank) * Settle(delta, BankEase);

        var velocity = player.Velocity;
        var flat = new Vector2(velocity.X, velocity.Y).Length();
        var slope = MathF.Atan2(velocity.Z, MathF.Max(flat, 1f)) * (180f / MathF.PI);
        var wantedTilt = mode == Mode.Flying ? Math.Clamp(slope * TiltShare, -TiltMost, TiltMost) : 0f;
        _tilt += (wantedTilt - _tilt) * Settle(delta, BankEase);

        if (World.Now < _throwUntil)
        {
            Wanted[Throwing] = 1f;
            return;
        }

        if (Aiming)
        {
            HoldAim();
            Wanted[Throwing] = 1f;
            return;
        }

        _holding = false;

        if (mode == Mode.Grounded)
        {
            Wanted[Idle] = 1f;
            return;
        }

        if (mode == Mode.Flying)
        {
            var share = velocity.Z / MathF.Max(FlyClimb, 1f);
            var up = Ramp(share, SlopeStarts, SlopeFull);
            var down = Ramp(-share, SlopeStarts, SlopeFull);
            var side = Ramp(MathF.Abs(_yawRate), TurnStarts, TurnFull) * (1f - MathF.Max(up, down));
            Wanted[Up] = up;
            Wanted[Down] = down;
            Wanted[_yawRate > 0f ? Right : Left] = side;
            Wanted[Forward] = MathF.Max(0f, 1f - up - down - side);
            return;
        }

        var rising = Math.Clamp(velocity.Z / RiseSpeed, 0f, 1f);
        var moving = Math.Clamp(flat / HoverSpeed, 0f, 1f) * 0.5f * (1f - rising);
        Wanted[Up] = rising;
        Wanted[Forward] = moving;
        Wanted[Idle] = MathF.Max(0f, 1f - rising - moving);
    }

    // His throw starts over from its first frame each time he throws in the air.
    static void Threw()
    {
        if ((_posed <= 0f && _held <= 0f) || Played[Throwing] is not { IsValid: true } throwing)
        {
            return;
        }

        throwing.PlayAnim(false, 1f, 0f);
        _throwUntil = World.Now + ThrowSeconds;
        _holding = false;
    }

    static void HoldAim()
    {
        if (_holding || Played[Throwing] is not { IsValid: true } throwing)
        {
            return;
        }

        throwing.StopAnim();
        throwing.SetPosition(throwing.GetAnimPlaybackLength() * AimHold, false);
        _holding = true;
    }

    static float Ramp(float value, float from, float to) => Math.Clamp((value - from) / (to - from), 0f, 1f);

    static void MakeFlyers(RPawnPlayer player, SkeletalMesh mesh, AnimSet moves)
    {
        for (var i = 0; i < Moves.Length; i++)
        {
            (Flyers[i], Played[i]) = Hidden(player, mesh, moves, Moves[i], i != Throwing);
        }

        // Started together, his chase moves are all at the same point of the flutter, so they
        // blend without a wobble.
        if (_chaseSet is { IsValid: true } chase)
        {
            for (var i = 0; i < ChaseMoves.Length; i++)
            {
                Chasers[i] = Hidden(player, mesh, chase, ChaseMoves[i], true).Copy;
            }
        }

        _flyersOn = player;
        _lastYaw = player.Rotation.Yaw;
        _yawRate = 0f;
    }

    static (SkeletalMeshComponent Copy, AnimNodeSequence Move) Hidden(RPawnPlayer player, SkeletalMesh mesh, AnimSet moves, FName name, bool loop)
    {
        var copy = Made.Tracked(new SkeletalMeshComponent(player));
        copy.SetActorCollision(false, false);
        copy.SetSkeletalMesh(mesh, false);
        copy.AnimSets.Add(moves);
        var move = Made.Tracked(new AnimNodeSequence(copy));
        copy.Animations = move;
        copy.bUpdateSkelWhenNotRendered = true;
        copy.bCastDynamicShadow = false;
        copy.SetHidden(true);
        copy.SetTickGroup(GameObject.ETickingGroup.TG_DuringAsyncWork);
        player.AttachComponent(copy);
        move.SetAnim(name);
        move.PlayAnim(loop, 1f, 0f);
        return (copy, move);
    }

    // Hidden copies only work out their pose while it is used. The chase moves are only used in
    // the air.
    static void Animating(bool on, bool chasing)
    {
        foreach (var flyer in Flyers)
        {
            if (flyer is { IsValid: true } && flyer.bUpdateSkelWhenNotRendered != on)
            {
                flyer.bUpdateSkelWhenNotRendered = on;
            }
        }

        foreach (var chaser in Chasers)
        {
            if (chaser is { IsValid: true } && chaser.bUpdateSkelWhenNotRendered != chasing)
            {
                chaser.bUpdateSkelWhenNotRendered = chasing;
            }
        }
    }

    static void Unfly()
    {
        var owner = _flyersOn is { IsValid: true } had ? had : null;
        for (var i = 0; i < Flyers.Length; i++)
        {
            if (Flyers[i] is { IsValid: true } flyer)
            {
                owner?.DetachComponent(flyer);
            }

            Flyers[i] = null;
            Played[i] = null;
        }

        for (var i = 0; i < Chasers.Length; i++)
        {
            if (Chasers[i] is { IsValid: true } chaser)
            {
                owner?.DetachComponent(chaser);
            }

            Chasers[i] = null;
        }

        _flyersOn = null;
    }

    static void Repose(SkeletalMeshComponent body, TArray<Atom> saved)
    {
        if (_body is null || body.Ptr != _body.Ptr)
        {
            return;
        }

        var pose = saved.AsSpan();
        if (_upperOf != body.Ptr || _upper.Length != pose.Length)
        {
            MarkBones(body, pose.Length);
        }

        if (_posed > 0f || _held > 0f)
        {
            Blend(pose);
        }

        AimLean.LeanOver(body, saved);
        Spin(pose);
    }

    // Only his chase moves turn his jet fans, and his flying moves leave them still. So they are
    // turned here, about the axis the chase turns them on, over whatever pose he has.
    static void Spin(Span<Atom> pose)
    {
        if (_fans.Length == 0 || _fanSpeed <= 0f)
        {
            return;
        }

        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _fanTurn * (MathF.PI / 180f));
        foreach (var fan in _fans)
        {
            var atom = pose[fan];
            atom.Rotation = ToTurn(Quaternion.Normalize(Numeric(atom.Rotation) * turn));
            pose[fan] = atom;
        }
    }

    // His moves blended by weight, then over the pose from your own moves by how far into the
    // air he is. The turns are summed on one side of the first one, then made whole again.
    static void Blend(Span<Atom> pose)
    {
        Span<int> used = stackalloc int[Flyers.Length];
        var count = 0;
        var total = 0f;
        for (var i = 0; i < Flyers.Length; i++)
        {
            if (Weights[i] > 0.001f && Flyers[i] is { IsValid: true } flyer && flyer.LocalAtoms.Count == pose.Length)
            {
                used[count++] = i;
                total += Weights[i];
            }
        }

        if (count == 0 || total <= 0f)
        {
            return;
        }

        // His moves are made facing a quarter round from the way the pawn faces, which is along
        // X. Which way is read once from his own hover, as the line across his shoulders, and
        // rounded to the quarter so his sway doesn't move it. Your own pose is no guide, since
        // hanging from a ledge or climbing turns it round.
        if (_rootFix is null && Flyers[Idle] is { IsValid: true } idler)
        {
            var whole = idler.SpaceBases;
            var left = idler.MatchRefBone(LeftArm);
            var right = idler.MatchRefBone(RightArm);
            if (left >= 0 && right >= 0 && whole.Count > Math.Max(left, right))
            {
                var across = whole[right].Translation - whole[left].Translation;
                if (across.X * across.X + across.Y * across.Y > 1f)
                {
                    // His right is a quarter round from his front.
                    var facing = MathF.Atan2(across.Y, across.X) - MathF.PI / 2f;
                    var quarters = MathF.Round(-facing / (MathF.PI / 2f));
                    _rootFix = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, quarters * (MathF.PI / 2f));
                }
            }
        }

        // The body is laid out with the pawn, ahead along X and its right along Y. So his roll
        // into a turn is a turn about X on the root, and his nose going up a turn back about Y.
        var rootFix = Quaternion.CreateFromAxisAngle(Vector3.UnitX, _bank * (MathF.PI / 180f))
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -_tilt * (MathF.PI / 180f))
            * (_rootFix ?? Quaternion.Identity);

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
            var atoms = Flyers[i]!.LocalAtoms.AsSpan();
            for (var bone = 0; bone < pose.Length; bone++)
            {
                var turn = Numeric(atoms[bone].Rotation);
                var at = atoms[bone].Translation;
                if (bone == 0)
                {
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

        if (_posed > 0f)
        {
            Chase(mixed, used[..count], total);
        }

        for (var bone = 0; bone < pose.Length; bone++)
        {
            var share = MathF.Max(_posed, _upper[bone] ? _held : 0f);
            if (share <= 0f)
            {
                continue;
            }

            var mine = pose[bone];
            var theirs = mixed[bone];
            var turn = Quaternion.Normalize(Numeric(theirs.Rotation));
            mine.Rotation = ToTurn(Quaternion.Slerp(Numeric(mine.Rotation), turn, share));
            mine.Translation = Vector3.Lerp(mine.Translation, theirs.Translation, share);
            mine.Scale += (theirs.Scale - mine.Scale) * share;
            pose[bone] = mine;
        }
    }

    // His jetpack bones in the blend take his chase moves instead, weighted the way his flying
    // moves are. The fans are left out, since they are turned on their own.
    static void Chase(Span<Atom> mixed, ReadOnlySpan<int> used, float total)
    {
        Array.Clear(ChaseWeights);
        foreach (var i in used)
        {
            ChaseWeights[ChaseFor[i]] += Weights[i] / total;
        }

        Span<int> chasing = stackalloc int[Chasers.Length];
        var count = 0;
        var sum = 0f;
        for (var c = 0; c < Chasers.Length; c++)
        {
            if (ChaseWeights[c] > 0.001f && Chasers[c] is { IsValid: true } chaser && chaser.LocalAtoms.Count == mixed.Length)
            {
                chasing[count++] = c;
                sum += ChaseWeights[c];
            }
        }

        if (count == 0 || sum <= 0f)
        {
            return;
        }

        for (var bone = 0; bone < mixed.Length; bone++)
        {
            if (!_jet[bone])
            {
                continue;
            }

            var blended = new Atom();
            for (var n = 0; n < count; n++)
            {
                var c = chasing[n];
                var share = ChaseWeights[c] / sum;
                var atom = Chasers[c]!.LocalAtoms[bone];
                var turn = Numeric(atom.Rotation);
                if (n > 0 && Quaternion.Dot(Numeric(blended.Rotation), turn) < 0f)
                {
                    turn = -turn;
                }

                blended.Rotation = ToTurn(Numeric(blended.Rotation) + turn * share);
                blended.Translation += atom.Translation * share;
                blended.Scale += atom.Scale * share;
            }

            // The chase flutters the flaps loosely. They keep this much of it, measured from how
            // they sit in his flying pose.
            if (_flap[bone])
            {
                var still = mixed[bone];
                var stillTurn = Quaternion.Normalize(Numeric(still.Rotation));
                var chaseTurn = Quaternion.Normalize(Numeric(blended.Rotation));
                blended.Rotation = ToTurn(Quaternion.Slerp(stillTurn, chaseTurn, FlapShare));
                blended.Translation = Vector3.Lerp(still.Translation, blended.Translation, FlapShare);
                blended.Scale = still.Scale + (blended.Scale - still.Scale) * FlapShare;
            }

            mixed[bone] = blended;
        }
    }

    // His arms from each shoulder down, the bones the gun hangs from, his jetpack and its fans.
    static void MarkBones(SkeletalMeshComponent body, int count)
    {
        _upper = new bool[count];
        _jet = new bool[count];
        _flap = new bool[count];
        _upperOf = body.Ptr;
        var fans = new List<int>();
        for (var bone = 0; bone < count; bone++)
        {
            var name = body.GetBoneName(bone);
            var own = name.ToString() ?? "";
            if (own.Contains(FanBone, StringComparison.OrdinalIgnoreCase))
            {
                fans.Add(bone);
            }
            else
            {
                _jet[bone] = Array.Exists(JetParts, part => own.Contains(part, StringComparison.OrdinalIgnoreCase));
                _flap[bone] = own.Contains(FlapPart, StringComparison.OrdinalIgnoreCase);
            }

            for (var up = 0; up < 64; up++)
            {
                var text = name.ToString() ?? "";
                if (text.Length == 0 || text.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (text.Contains(Shoulder, StringComparison.OrdinalIgnoreCase) || text.StartsWith(GunBone, StringComparison.OrdinalIgnoreCase))
                {
                    _upper[bone] = true;
                    break;
                }

                name = body.GetParentBone(name);
            }
        }

        _fans = fans.ToArray();
    }

    // His exhaust and smoke trail on his jets, as the game puts them on him in the chase.
    static void Burn(RPawnPlayer? player, SkeletalMeshComponent? body, bool on)
    {
        if (!on || player is null || body is null || (_mount is not null && _mount.Ptr != body.Ptr))
        {
            PutOut();
        }

        if (!on || player is null || body is null)
        {
            return;
        }

        if (Jets.Count == 0)
        {
            Light(body);
        }

        var speed = player.Velocity.Length();
        if (Pushing && !_wasPushing)
        {
            _boostUntil = World.Now + TrailBoostSeconds;
        }

        _wasPushing = Pushing;
        var rate = TrailLeast + (TrailMost - TrailLeast) * Math.Clamp(speed / TrailFullSpeed, 0f, 1f);
        var boost = Pushing && World.Now < _boostUntil ? TrailFlare : 0f;
        foreach (var jet in Jets)
        {
            if (jet.Trail && jet.Effect.IsValid && jet.Effect.ParticleSystemComponent is { IsValid: true } trail)
            {
                trail.SetFloatParameter(TrailRate, rate);
                trail.SetFloatParameter(TrailBoost, boost);
            }
        }

        Follow();
    }

    static void Light(SkeletalMeshComponent body)
    {
        if (!Game.TryGetWorldInfo(out var world) || world.GRI?.ObjectPool is not { IsValid: true } pool)
        {
            return;
        }

        foreach (var socket in Exhausts)
        {
            if (!body.GetSocketWorldLocationAndRotation(socket, out var at, out var turn, 0))
            {
                continue;
            }

            if ((_jetFlame ??= Kept(Game.FindObject<ParticleSystem>(ShowcaseExhaustPath))) is { IsValid: true } small
                && pool.SpawnEmitter(small, at, turn, 0, 1f) is { IsValid: true } flame)
            {
                Jets.Add(new Jet(flame, socket, false));
            }

            if (_trail is { IsValid: true } smoke && pool.SpawnEmitter(smoke, at, turn, 0, 1f) is { IsValid: true } trail)
            {
                trail.ParticleSystemComponent?.SetFloatParameter(TrailFuel, 0f);
                Jets.Add(new Jet(trail, socket, true));
            }
        }

        _mount = body;
    }

    // Run from the camera hook too, once every mesh has its pose for the frame, so the jets and
    // the flame sit on him instead of a frame behind.
    public static void Follow()
    {
        if (_mount is { IsValid: true } body)
        {
            foreach (var jet in Jets)
            {
                if (jet.Effect.IsValid && body.GetSocketWorldLocationAndRotation(jet.Socket, out var at, out var turn, 0))
                {
                    jet.Effect.SetLocation(at);
                    jet.Effect.SetRotation(turn);
                }
            }
        }
    }

    static void PutOut()
    {
        foreach (var jet in Jets)
        {
            if (jet.Effect.IsValid && jet.Effect.ParticleSystemComponent is { IsValid: true } effect)
            {
                effect.DeactivateSystem();
            }
        }

        Jets.Clear();
        _mount = null;
    }

    static void Sound(RPawnPlayer? player, bool on)
    {
        var wanted = on && player is { IsValid: true } && _jetSound is { IsValid: true } ? player : null;
        if (ReferenceEquals(wanted, _soundOn))
        {
            return;
        }

        if (_soundOn is { IsValid: true } was && _jetSound is { IsValid: true } loop)
        {
            was.RemoveLoopingAudioEvent(loop);
        }

        _soundOn = wanted;
        if (wanted is not null)
        {
            wanted.AddLoopingAudioEvent(_jetSound!);
        }
    }

    static void Unload()
    {
        Sound(null, false);
        Disguise.Repose = null;
        AimLean.Clear();
        Unfly();
        PutOut();
        Array.Clear(Weights);
        _posed = 0f;
        _held = 0f;
        _upper = Array.Empty<bool>();
        _fans = Array.Empty<int>();
        _jet = Array.Empty<bool>();
        _flap = Array.Empty<bool>();
        _fanSpeed = 0f;
        _upperOf = 0;
        _rootFix = null;
        _bank = 0f;
        _tilt = 0f;
        _body = null;
        _flyingSet = null;
        _chaseSet = null;
        _jetSound = null;
        _exhaust = null;
        _jetFlame = null;
        _trail = null;
        _loadTried = false;
        Borrowed.Let(Lender);
    }
}
