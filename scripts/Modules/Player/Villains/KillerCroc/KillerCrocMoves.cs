#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// His swipes and how he takes a hit, each one of his moves from his fight played once on the
// action copy. While one plays you can't walk, and the ground it covers carries the pawn, so a
// lunge really lunges. A move that spins him round turns the pawn with it.
static partial class KillerCroc
{
    enum Move
    {
        None,
        Swipe,
        Charge,
        Recruit,
        Roar,
        Hurt,
    }

    // His swings at whoever is in front, taken in turn by a run of presses.
    static readonly FName[] Swipes = { new("Melee_Strike"), new("Melee_Strike_02") };
    static readonly FName SmallHit = new("Hit_Small");
    static readonly FName BigHit = new("Hit_Big");

    // His backhands spin him round onto someone behind him, one each way. Their names carry a
    // number, which a name made from text doesn't split off, so they are looked up in his set.
    const string BackhandRightName = "Melee_Stirke_Backhand_180";
    const string BackhandLeftName = "Melee_Stirke_Backhand_L_180";
    static FName _backhandRight;
    static FName _backhandLeft;

    const string ThudPath = "ww_dlc14_croc_chr_killercroc.Play_DLC14_CHR_KillerCroc_Foley_Hit";

    // A swing lands on whoever is in front of him within his arm's reach as it lands, and on
    // anyone close to the claw he swings, which a backhand sweeps wide.
    static readonly FName LeftHand = new("Bip01_L_Hand");
    static readonly FName RightHand = new("Bip01_R_Hand");
    const float SwipeReach = 240f;
    const float SwipeCone = 0.64f;
    const float ClawReach = 120f;
    const float SwipeHeight = 160f;
    const int SwipeDamage = 20;

    // A vehicle his claw meets loses this share of its full health, and a parked car this much.
    const float SwipeWreck = 0.15f;
    const int SwipeParked = 20;

    // The hit his fight gives the thugs he swats: an explosive gel blast that blows them off
    // their feet with its own flying fall, along the swing, and leaves them dazed.
    static readonly FName BlownUp = new("Blown_Up");
    static readonly FName Chest = new("Bip01_Spine2");
    const float ThrowImpulse = 6000f;
    const float DazedSeconds = 3.5f;

    // He swings where the camera looks, at an enemy this close and this near that way if there
    // is one, which is as far as his fight reaches for a close attack. He turns into the swing
    // this fast. Further round than this he backhands instead.
    const float AimReach = 560f;
    const float AimCone = 0.5f;
    const float TurnRate = 720f;
    const float BehindAngle = 110f;

    // A move whose own turn is more than this spins him, and the pawn turns with it.
    const float SpinAngle = 45f;

    // A press this long after a swing lands cuts the rest of it short for the next swing. One
    // before that is let go, so pressing over and over doesn't stack swings up. A press this
    // soon after a swing ends goes on to his next swing, not back to the first.
    const float CancelAfter = 0.25f;
    const float ComboGap = 0.6f;

    // A move ends this long before its last frame, so it blends back into walking.
    const float EndEarly = 0.15f;
    const float ActionBlend = 0.08f;

    // He shrugs off part of every hit. A hit this heavy rocks him, and he reacts to one hit a
    // second at most, as in his fight.
    const float Toughness = 0.6f;
    const int HeavyHit = 15;
    const float HurtGap = 1f;

    static Move _move;
    static int _swipe;
    static float _hitAt;
    static bool _hit;
    static float _endedAt = -10f;
    static float _hurtAt = -10f;
    static float _owed;
    static Vector2 _lastRoot;
    static RPlayerController? _movesHeld;
    static AkEvent? _thud;

    // While a move plays the pawn faces where it is told here. Base is where the move's own
    // ground is laid out from, and it eases to Aim. Spun is how far each action copy's move has
    // turned him so far, which is added to the pawn and taken off that copy's pose.
    static float _base;
    static float _aim;

    // Where the swing is aimed. A backhand lands before most of its spin, so the swing is
    // judged by where it is aimed, not by where he faces as it lands.
    static float _swingAt;

    // The enemy a swing is aimed at. He closes on them as he winds up, as his fight moves him in
    // to attack, so the claw lands this far from them, and no faster than this.
    static RPawnVillain? _aimed;
    const float LandAt = 150f;
    const float LungeSpeed = 1500f;

    // The step he takes over a kerb when the game doesn't give one, and how far down a slope
    // he is let for each unit forward, about 40 degrees.
    const float StepHeight = 35f;
    const float SlopeDrop = 0.85f;

    static bool _spins;
    static float _spin;
    static float _lastYaw;
    static int _slot = Action;
    static readonly float[] Spun = new float[NextAction + 1];

    static bool Acting => _move != Move.None;

    static Outcome Swipe()
    {
        if (!Option.On || _body is not { IsValid: true } || World.Player is not { IsValid: true } player || World.Controller is not { } controller)
        {
            return Outcome.Unchanged("CROC IS OFF");
        }

        // Mid-swing, a press once it has landed starts the next swing now.
        if (_move == Move.Swipe)
        {
            if (!_hit || Played[_slot] is not { IsValid: true } swinging || swinging.CurrentTime < _hitAt + CancelAfter)
            {
                return Outcome.Unchanged("NOT YET");
            }

            _swipe++;
            return StartSwipe(player, controller) ? Outcome.Done("SWIPE") : Outcome.Unchanged("NOT READY");
        }

        if (Acting || player.Physics != Actor.EPhysics.PHYS_Walking || player.CurrentSpecialMove is { IsValid: true } || controller.bCinematicMode)
        {
            return Outcome.Unchanged("NOT NOW");
        }

        _swipe = World.Now - _endedAt <= ComboGap ? _swipe + 1 : 0;
        return StartSwipe(player, controller) ? Outcome.Done("SWIPE") : Outcome.Unchanged("NOT READY");
    }

    // Toward an enemy where the camera looks, or just where it looks. Someone well behind him
    // gets the backhand that spins that way, the rest a swing in turn.
    static bool StartSwipe(RPawnPlayer player, RPlayerController controller)
    {
        var aim = AimFor(player, controller);
        var facing = player.Rotation.Yaw;
        var round = Wrap(aim - facing);
        var name = MathF.Abs(round) < BehindAngle ? Swipes[_swipe % Swipes.Length]
            : round > 0f ? _backhandRight
            : _backhandLeft;
        if (name == FName.None)
        {
            name = Swipes[_swipe % Swipes.Length];
        }

        if (Play(name, facing) is not { } played)
        {
            return false;
        }

        // A spinning move does part of the turn itself. The rest is eased in.
        _aim = aim - (_spins ? _spin : 0f);
        _swingAt = aim;
        _move = Move.Swipe;
        _hit = false;
        _hitAt = HitTime(played);
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

    // Sets the other action copy to a move, from its first frame, with the pawn facing as it is.
    // The one that played last fades out under it.
    static AnimNodeSequence? Play(FName name, float facing, bool loop = false)
    {
        var slot = _slot == Action ? NextAction : Action;
        if (Played[slot] is not { IsValid: true } played || Copies[slot] is not { IsValid: true } copy)
        {
            return null;
        }

        played.SetAnim(name);
        if (played.AnimSeq is not { IsValid: true })
        {
            return null;
        }

        played.PlayAnim(loop, 1f, 0f);
        copy.ForceSkelUpdate();
        _slot = slot;
        _lastRoot = Root(copy);
        _lastTime = played.CurrentTime;
        _lastYaw = RootYaw(copy);
        _stepMove = -1;
        _base = facing;
        _aim = facing;
        Spun[slot] = 0f;
        _spin = Spin(played, copy);
        _spins = MathF.Abs(_spin) > SpinAngle;
        return played;
    }

    // How far a move turns him from its first frame to its last, read from its root.
    static float Spin(AnimNodeSequence played, SkeletalMeshComponent copy)
    {
        var start = played.CurrentTime;
        played.SetPosition(played.GetAnimPlaybackLength(), false);
        copy.ForceSkelUpdate();
        var end = RootYaw(copy);
        played.SetPosition(start, false);
        copy.ForceSkelUpdate();
        return Wrap(end - RootYaw(copy));
    }

    // When the swing lands, read from the mark his fight uses to start its damage.
    static float HitTime(AnimNodeSequence played)
    {
        if (played.AnimSeq is { IsValid: true } sequence)
        {
            var notifies = sequence.Notifies;
            for (var i = 0; i < notifies.Count; i++)
            {
                if (notifies[i].Notify is RAnimNotify_VillainAttack)
                {
                    return notifies[i].Time;
                }
            }
        }

        return played.GetAnimPlaybackLength() * 0.4f;
    }

    // The way to swing, as a yaw.
    static float AimFor(RPawnPlayer player, RPlayerController controller)
    {
        _aimed = null;
        var look = Teleport.Flat(controller.Rotation.ToDirection());
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
            if (distance > 1f && distance < bestDistance && Vector3.Dot(to / distance, look) >= AimCone)
            {
                best = thug;
                bestDistance = distance;
            }
        }

        _aimed = best;
        var way = best is not null ? Teleport.Flat(best.Location - player.Location) : look;
        return MathF.Atan2(way.Y, way.X) * (180f / MathF.PI);
    }

    // Until the swing lands he keeps turned to the enemy he aimed at and closes the gap, spread
    // over the time left, so he reaches striking distance as the claw comes down.
    static void Close(RPawnPlayer player, AnimNodeSequence played)
    {
        if (_move != Move.Swipe || _hit || _aimed is not { IsAlive: true } aimed || !Crowd.Standing(aimed))
        {
            return;
        }

        var to = Level(aimed.Location - player.Location);
        var distance = to.Length();
        if (distance < 1f)
        {
            return;
        }

        var yaw = MathF.Atan2(to.Y, to.X) * (180f / MathF.PI);
        _swingAt = yaw;
        _aim = yaw - (_spins ? _spin : 0f);
        var gap = distance - LandAt;
        if (gap <= 0f)
        {
            return;
        }

        var delta = World.Delta;
        var left = MathF.Max(_hitAt - played.CurrentTime, delta);
        var step = MathF.Min(gap * MathF.Min(1f, delta / left), LungeSpeed * delta);
        Stride(player, to / distance * step);
    }

    // Moves the pawn along the ground the way walking does. A plain move stops dead at a kerb or
    // a lip of rubble, so a blocked move steps up over it, goes on and comes back down. He is
    // then let down onto ground that falls away under him, as far as a slope that steep would.
    // Returns how far along the ground it really went.
    static float Stride(RPawnPlayer player, Vector3 way)
    {
        var want = way.Length();
        if (want < 0.01f)
        {
            return 0f;
        }

        var start = player.Location;
        var from = player.Location;
        player.Move(way);
        var gone = Level(player.Location - from).Length();
        if (want - gone > 1f)
        {
            var lift = Vector3.UnitZ * (player.MaxStepHeight > 0f ? player.MaxStepHeight : StepHeight);
            player.Move(lift);
            player.Move(way * ((want - gone) / want));
            player.Move(-lift);
        }

        player.Move(-Vector3.UnitZ * (want * SlopeDrop));
        return Level(player.Location - start).Length();
    }

    // Every tick while a move plays.
    static void Act(RPawnPlayer player, SkeletalMeshComponent own)
    {
        if (!Acting || Played[_slot] is not { IsValid: true } played || Copies[_slot] is not { IsValid: true } copy)
        {
            return;
        }

        Close(player, played);
        Steer();

        // He turns into the swing, plus whatever the move turns him by itself.
        var turn = Wrap(_aim - _base);
        var most = TurnRateNow * World.Delta;
        _base += Math.Clamp(turn, -most, most);
        if (_spins)
        {
            var yaw = RootYaw(copy);
            Spun[_slot] += Wrap(yaw - _lastYaw);
            _lastYaw = yaw;
        }

        var facing = new Rotator(0f, _base + Spun[_slot], 0f);
        player.SetRotation(facing);
        player.DesiredRotation = facing;

        // The ground the move covers carries the pawn, laid out the way he faced before it spun.
        // A looping move jumps back to its start each time round, which isn't ground covered.
        var root = Root(copy);
        var wrapped = played.CurrentTime < _lastTime;
        _lastTime = played.CurrentTime;
        var step = wrapped ? Vector2.Zero : (root - _lastRoot) * Parts[0].Scale;
        _lastRoot = root;
        if (step.LengthSquared() > 0.01f)
        {
            Stride(player, Turned(new Vector3(step, 0f), _base + own.Rotation.Yaw + _turn));
        }

        if (_move == Move.Swipe && !_hit && played.CurrentTime >= _hitAt)
        {
            _hit = true;
            Strike(player);
        }

        if (_move == Move.Charge && Charging(player, played))
        {
            return;
        }

        if (_move == Move.Recruit && Recruiting(player, played))
        {
            return;
        }

        if (_move == Move.Roar)
        {
            Roaring(player, played);
        }

        if (!played.bPlaying || played.CurrentTime >= played.GetAnimPlaybackLength() - EndEarly)
        {
            Unact();
        }
    }

    // The game can still turn the pawn after the tick, toward where you look or walk. The camera
    // is worked out after that, so a move's facing is put back from there, before anything is
    // drawn.
    public static void KeepFacing()
    {
        if (!Acting || !Option.On || _body is not { IsValid: true } || World.Player is not { IsValid: true } player)
        {
            return;
        }

        var facing = new Rotator(0f, _base + Spun[_slot], 0f);
        if (MathF.Abs(Wrap(facing.Yaw - player.Rotation.Yaw)) > 0.01f)
        {
            player.SetRotation(facing);
            player.DesiredRotation = facing;
        }
    }

    static Vector2 Root(SkeletalMeshComponent copy)
    {
        var atoms = copy.LocalAtoms;
        return atoms.Count > 0 ? new Vector2(atoms[0].Translation.X, atoms[0].Translation.Y) : Vector2.Zero;
    }

    // Which way his root faces in his own mesh, in degrees.
    static float RootYaw(SkeletalMeshComponent copy)
    {
        var atoms = copy.LocalAtoms;
        if (atoms.Count == 0)
        {
            return 0f;
        }

        var q = atoms[0].Rotation;
        return MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z)) * (180f / MathF.PI);
    }

    // Flattened onto the ground, keeping its length. Teleport.Flat gives only the way.
    static Vector3 Level(Vector3 off) => off with { Z = 0f };

    static float Wrap(float degrees)
    {
        degrees %= 360f;
        return degrees > 180f ? degrees - 360f : degrees < -180f ? degrees + 360f : degrees;
    }

    // Whoever his claw lands on is thrown the way his swing throws the thugs around him in his
    // fight. What breaks under it breaks. Someone riding in a vehicle stays in it.
    static void Strike(RPawnPlayer player)
    {
        if (_body is not { IsValid: true } body)
        {
            return;
        }

        var facing = new Rotator(0f, _swingAt, 0f).ToDirection() with { Z = 0f };
        facing = Vector3.Normalize(facing);
        var claw = Claw(player, body, facing);
        var thrown = 0;
        foreach (var thug in Crowd.StandingEnemies())
        {
            var off = thug.Location - player.Location;
            var flat = Level(off);
            var distance = flat.Length();
            if (MathF.Abs(off.Z) > SwipeHeight || thug.DrivenVehicle is { IsValid: true } || thug.Base is Vehicle)
            {
                continue;
            }

            var away = distance > 1f ? flat / distance : facing;
            var inFront = distance <= SwipeReach && Vector3.Dot(away, facing) >= SwipeCone;
            if (!inFront && Level(thug.Location - claw).Length() > ClawReach)
            {
                continue;
            }

            thug.DamagedBy(new RPawnCombat.FDamageInfo
            {
                Attacker = player,
                BoneName = Chest,
                Impulse = away * ThrowImpulse,
                DamageAmount = SwipeDamage,
                StunTime = DazedSeconds,
                DamageType = RDmgType_ExplosiveGel.StaticClass(),
                WritheAnim = BlownUp,
                bHeavyHit = true,
            });
            thrown++;
        }

        Villains.Break(player, claw, ClawReach);
        var wrecked = Villains.Wreck(player, claw, ClawReach, SwipeWreck, SwipeParked);
        if (thrown + wrecked > 0 && _thud is { IsValid: true } thud)
        {
            player.StartAudioEvent(thud);
        }

        if (thrown > 0)
        {
            _thrown += thrown;
            _thrownAt = World.Now;
            Speech.Speak("hit");
        }
    }

    // The claw he swings is the hand furthest out in front of him as the swing lands.
    static Vector3 Claw(RPawnPlayer player, SkeletalMeshComponent body, Vector3 facing)
    {
        var best = player.Location + facing * ClawReach;
        var furthest = float.MinValue;
        foreach (var hand in new[] { LeftHand, RightHand })
        {
            if (body.MatchRefBone(hand) < 0)
            {
                continue;
            }

            var at = body.GetBoneLocation(hand, 0);
            var ahead = Vector3.Dot(Level(at - player.Location), facing);
            if (ahead > furthest)
            {
                furthest = ahead;
                best = at;
            }
        }

        return best;
    }

    // A hit on him, on its way in. He takes part of it and is rocked by it, unless he is
    // mid-swing, which he swings through as in his fight. What comes back is the damage that
    // lands.
    public static int Hit(RPawnPlayer player, int damage)
    {
        if (!Option.On || _body is not { IsValid: true } || damage <= 0)
        {
            return damage;
        }

        _owed += damage * Toughness;
        _damagedAt = World.Now;
        var landed = (int)_owed;
        _owed -= landed;

        var now = World.Now;
        if (_move is not (Move.Swipe or Move.Charge or Move.Recruit or Move.Roar) && now - _hurtAt >= HurtGap && World.Controller is { } controller
            && Play(damage >= HeavyHit ? BigHit : SmallHit, player.Rotation.Yaw) is not null)
        {
            _hurtAt = now;
            Speech.Speak("hurt");
            _move = Move.Hurt;
            Hold(controller);
        }

        return landed;
    }

    // Ends the move, and lets Batman walk again. Only the controller that was held is let go,
    // since the game counts these. A spin already turned the pawn, so the turn it took off his
    // pose stays until the move has blended out.
    static void Unact()
    {
        if (_move == Move.Swipe)
        {
            _endedAt = World.Now;
        }

        if (_move == Move.Charge)
        {
            _nextCharge = World.Now + ChargeWait;
        }

        if (_move == Move.Roar)
        {
            _nextRoar = World.Now + RoarWait;
        }

        // A haul that never started leaves no stand-in to see the thug through.
        _walkingUp = false;
        _walkSpeedNow = 0f;
        if (_standIn is null)
        {
            _hauled = null;
        }

        _move = Move.None;
        if (_movesHeld is { IsValid: true } held)
        {
            held.IgnoreMoveInput(false);
        }

        _movesHeld = null;
    }
}
