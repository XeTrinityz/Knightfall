#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SNATCH. He dives to just over the thug nearest the crosshair and carries him off by the scruff
// in his talons, the thug clinging to Man-Bat's ankles and dangling under him as he flies. A ragdoll can't be
// carried, the game lets it fall, so while held the thug is taken out of the game's hands: his
// brain stops, he stops colliding and falling, and he plays the game's own dangle from a ledge
// takedown, placed under Man-Bat each tick. Pressed again, he lets go, and the thug falls limp.
// A long enough fall puts him down for good.
static partial class ManBat
{
    // He comes in over the thug's head with this much room between them, then grabs him.
    const float GrabAbove = 40f;

    // The thug's hands are held at Man-Bat's feet, eased there from where he was grabbed over this
    // long. He faces the way Man-Bat flies and swings back as he speeds up, up to this far at his
    // fastest, lagging a little behind. His feet are kept this far off the ground under him, and
    // Man-Bat climbs to keep them there.
    const float LiftSeconds = 0.3f;
    const float MostSwing = 40f;
    const float SwingLag = 0.35f;
    const float FeetClear = 20f;
    const float FloorLook = 400f;
    static readonly FName ManBatLeftFoot = new("Bip01_L_Foot");
    static readonly FName ManBatRightFoot = new("Bip01_R_Foot");

    // The thug's side of the ledge takedown: dragged over the edge, then hanging by his collar.
    const string DangleIn = "Ledge_In";
    const string Dangle = "Ledge_Idle";
    static AnimSequence? _dangleIn;
    static AnimSequence? _dangle;
    static bool _dangleLooked;

    // He drops a thug held this long. A drop from this high puts him down for good once he lands,
    // which is when he has stopped falling or after this long at most.
    const float CarryLongest = 12f;
    const float FatalDrop = 500f;
    const float LandingSlow = 150f;
    const float LandingLongest = 3f;
    const float LandingSoonest = 0.4f;
    const float GroundSearch = 5000f;
    const int DropDamage = 5;
    static readonly FName Middle = new("Bip01_Spine2");

    static bool _snatching;
    static RPawnVillain? _carried;
    static float _carriedAt;
    static Vector3 _carriedFrom;
    static Vector3 _carrierAt;
    static Vector3 _carrierVelocity;
    static FName _leftHand;
    static FName _rightHand;
    static float _swing;

    // What the thug had before he was taken, put back as he is let go.
    static AnimNode? _ownTree;
    static int _ownSets = -1;
    static AnimNodeSequence? _dangling;
    static bool _ownCollide;
    static bool _ownBlock;
    static bool _ownIgnore;
    static Controller? _brain;
    static bool _brainWasOff;

    static RPawnVillain? _dropped;
    static float _droppedAt;
    static float _dropHeight;

    static Outcome Snatch()
    {
        if (!Option.On || _body is not { IsValid: true } || World.Player is not { IsValid: true } player)
        {
            return Outcome.Unchanged("MAN-BAT IS OFF");
        }

        if (_carried is not null)
        {
            Drop();
            return Outcome.Done("DROP");
        }

        if (_act != Act.None || _aiming)
        {
            return Outcome.Unchanged("NOT YET");
        }

        // He dives once the key is let go, on whoever is marked then.
        _snatching = true;
        _prey = Prey(player);
        _lookedAt = World.Now;
        _aiming = true;
        return Outcome.Done("SNATCH");
    }

    // Straight at a spot over his head.
    static void DiveToGrab(RPawnPlayer player, RPawnVillain prey)
    {
        var flat = (prey.Location - player.Location) with { Z = 0f };
        if (flat.LengthSquared() > 1f)
        {
            _yaw = MathF.Atan2(flat.Y, flat.X) * (180f / MathF.PI);
        }

        Face(player);
        var spot = prey.Location + Vector3.UnitZ * (prey.CylinderComponent.CollisionHeight + player.CylinderComponent.CollisionHeight + GrabAbove);
        var to = spot - player.Location;
        var gap = to.Length();
        var seconds = MathF.Min(World.Delta, LongestStep);
        if (gap <= Arrived)
        {
            Grab(player, prey);
            return;
        }

        var step = MathF.Min(gap, DiveSpeed * seconds);
        var went = Glide(player, to / gap * step);
        _velocity = seconds > 0f ? went / seconds : Vector3.Zero;
        Gauge();

        // Something in the way. Close enough, he reaches down from where he is.
        _stuck = went.Length() < step * 0.2f ? _stuck + seconds : 0f;
        if (_stuck > StuckLongest)
        {
            if (gap <= CloseEnough)
            {
                Grab(player, prey);
            }
            else
            {
                Log.Info("Man-Bat's snatch missed: something was in the way.");
                Unact();
            }
        }
    }

    static void Grab(RPawnPlayer player, RPawnVillain prey)
    {
        Unact();
        if (prey.Mesh is not { IsValid: true } mesh)
        {
            return;
        }

        // Whatever he was doing ends the way the game ends a move.
        if (prey.CombatMove is { IsValid: true } move)
        {
            move.ExitMoveForPawn(prey);
        }

        _ownCollide = prey.bCollideActors;
        _ownBlock = prey.bBlockActors;
        _ownIgnore = prey.bIgnoreEncroachers;
        prey.SetCollision(false, false, true);
        _brain = prey.Controller is { IsValid: true } brain ? brain : null;
        if (_brain is not null)
        {
            _brainWasOff = _brain.bTickIsDisabled;
            _brain.SetTickIsDisabled(true);
        }

        prey.SetPhysics(Actor.EPhysics.PHYS_None);
        prey.Velocity = Vector3.Zero;
        prey.Acceleration = Vector3.Zero;
        TakeTree(mesh);
        _leftHand = BoneOf(mesh, "Bip01_L_Hand", "L_Hand");
        _rightHand = BoneOf(mesh, "Bip01_R_Hand", "R_Hand");
        _swing = 0f;

        _carried = prey;
        _carriedAt = World.Now;
        _carriedFrom = prey.Location;
        _carrierAt = player.Location;
        _carrierVelocity = Vector3.Zero;
        if (_squeal is { IsValid: true } squeal)
        {
            player.StartAudioEvent(squeal);
        }
    }

    // His anim tree is swapped for one node playing the dangle, with its set added for the time.
    static void TakeTree(SkeletalMeshComponent mesh)
    {
        LookForDangle();
        if (_dangle is null && _dangleIn is null)
        {
            return;
        }

        _ownTree = mesh.Animations;
        _ownSets = mesh.AnimSets.Count;
        foreach (var sequence in new[] { _dangleIn, _dangle })
        {
            if (sequence?.Outer is AnimSet { IsValid: true } set && !Has(mesh.AnimSets, set))
            {
                mesh.AnimSets.Push(set);
            }
        }

        _dangling = Made.Tracked(new AnimNodeSequence(mesh));
        mesh.SetAnimations(_dangling);
    }

    static bool Has(TArray<AnimSet> sets, AnimSet set)
    {
        for (var i = 0; i < sets.Count; i++)
        {
            if (sets[i] is { } each && each.Ptr == set.Ptr)
            {
                return true;
            }
        }

        return false;
    }

    // The thug's side of the ledge takedown, found once. Batman's own sets are skipped, and a set
    // made for a thug opposite Batman is the likeliest to be his half.
    static void LookForDangle()
    {
        if (_dangleLooked && (_dangle is null || _dangle.IsValid))
        {
            return;
        }

        _dangleLooked = true;
        _dangleIn = null;
        _dangle = null;
        var bestIn = -1;
        var best = -1;
        foreach (var sequence in Game.FindObjects<AnimSequence>())
        {
            if (!sequence.IsValid)
            {
                continue;
            }

            var name = sequence.SequenceName.ToString();
            var dangleIn = name.Equals(DangleIn, StringComparison.OrdinalIgnoreCase);
            if (!dangleIn && !name.Equals(Dangle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var set = sequence.Outer is AnimSet { IsValid: true } outer ? outer.Name.ToString() ?? "" : "";
            if (set.StartsWith("BM_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = (set.Contains("_BM", StringComparison.OrdinalIgnoreCase) ? 2 : 0) + (set.StartsWith("TG", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            if (dangleIn && score > bestIn)
            {
                bestIn = score;
                _dangleIn = Kept(sequence);
            }
            else if (!dangleIn && score > best)
            {
                best = score;
                _dangle = Kept(sequence);
            }
        }

        if (_dangle is null)
        {
            Log.Warn("man_bat.dangle", "The thug's dangle from the ledge takedown wasn't found, so a snatched thug hangs stiff.");
        }
    }

    // A bone by its usual name, or else the first bone with a part of it in its name. Thug
    // skeletons don't all name their bones alike.
    static FName BoneOf(SkeletalMeshComponent mesh, string usual, string part)
    {
        var known = new FName(usual);
        if (mesh.MatchRefBone(known) >= 0)
        {
            return known;
        }

        for (var i = 0; i < 400; i++)
        {
            var bone = mesh.GetBoneName(i);
            if (bone == FName.None)
            {
                break;
            }

            var text = bone.ToString() ?? "";
            if (text.Contains(part, StringComparison.OrdinalIgnoreCase) && !text.Contains("Twist", StringComparison.OrdinalIgnoreCase))
            {
                return bone;
            }
        }

        return FName.None;
    }

    // Every tick. The dangle plays on, and the thug is put so his hands sit in Man-Bat's feet.
    static void Carry(RPawnPlayer player)
    {
        Landing();
        if (_carried is null)
        {
            return;
        }

        if (_carried is not { IsAlive: true } held || held.Mesh is not { IsValid: true } mesh || held.bGoodAsDead || World.Now - _carriedAt > CarryLongest)
        {
            Drop();
            return;
        }

        var seconds = MathF.Min(World.Delta, LongestStep);
        if (seconds > 0f)
        {
            _carrierVelocity = (player.Location - _carrierAt) / seconds;
            _carrierAt = player.Location;
        }

        var since = World.Now - _carriedAt;
        if (held.Physics != Actor.EPhysics.PHYS_None)
        {
            held.SetPhysics(Actor.EPhysics.PHYS_None);
        }

        held.Velocity = Vector3.Zero;
        held.Acceleration = Vector3.Zero;

        // Flying ahead, his legs trail behind him, which tips him forward from his hands. The
        // swing lags behind Man-Bat's speed, so he sways when Man-Bat stops or turns.
        var ahead = new Rotator(0f, _yaw, 0f).ToDirection();
        var trail = Math.Clamp(Vector3.Dot(_carrierVelocity with { Z = 0f }, ahead) / FastSpeed, -0.5f, 1f) * MostSwing;
        _swing += (trail - _swing) * Settle(seconds, SwingLag);
        held.SetRotation(new Rotator(-_swing, _yaw, 0f));
        held.ForceUpdateComponents(false, false);
        PlayDangle(mesh, since);

        // His hands are measured from where the dangle holds them now, so his body hangs below.
        var hold = Talons(player);
        var hands = Hands(held, mesh) - held.Location;
        var spot = hold - hands;
        var lift = Math.Clamp(since / LiftSeconds, 0f, 1f);
        spot = Vector3.Lerp(_carriedFrom, spot, lift * (2f - lift));
        if (!held.SetLocation(spot))
        {
            held.Move(spot - held.Location);
        }

        // Too low, his feet would drag along the ground, so Man-Bat climbs.
        var feet = held.Location.Z - held.CylinderComponent.CollisionHeight;
        if (lift >= 1f && Teleport.Floor(held, held.Location, held.Location.Z, feet - FloorLook, out var floor) && feet - floor.Z < FeetClear)
        {
            Glide(player, Vector3.UnitZ * (FeetClear - (feet - floor.Z)));
        }
    }

    // Between Man-Bat's feet as his body shows them, so the hold follows his wing beats.
    static Vector3 Talons(RPawnPlayer player)
    {
        if (_body is { IsValid: true } body && body.MatchRefBone(ManBatLeftFoot) >= 0 && body.MatchRefBone(ManBatRightFoot) >= 0)
        {
            return (body.GetBoneLocation(ManBatLeftFoot, 0) + body.GetBoneLocation(ManBatRightFoot, 0)) * 0.5f;
        }

        return player.Location - Vector3.UnitZ * player.CylinderComponent.CollisionHeight;
    }

    // Between the thug's hands, or over his head when his hands can't be found.
    static Vector3 Hands(RPawnVillain held, SkeletalMeshComponent mesh)
    {
        if (_leftHand != FName.None && _rightHand != FName.None && mesh.MatchRefBone(_leftHand) >= 0 && mesh.MatchRefBone(_rightHand) >= 0)
        {
            return (mesh.GetBoneLocation(_leftHand, 0) + mesh.GetBoneLocation(_rightHand, 0)) * 0.5f;
        }

        return held.Location + Vector3.UnitZ * held.CylinderComponent.CollisionHeight;
    }

    // His way in, then the hang over and over, set by time since he was grabbed.
    static void PlayDangle(SkeletalMeshComponent mesh, float since)
    {
        if (_dangling is not { IsValid: true } node)
        {
            return;
        }

        AnimSequence? sequence;
        float at;
        if (_dangleIn is { IsValid: true } lead && since < lead.SequenceLength)
        {
            sequence = lead;
            at = since;
        }
        else if (_dangle is { IsValid: true } hang)
        {
            sequence = hang;
            var into = _dangleIn is { IsValid: true } first ? since - first.SequenceLength : since;
            at = into % MathF.Max(hang.SequenceLength, 0.01f);
        }
        else
        {
            return;
        }

        if (node.AnimSeq is not { IsValid: true } playing || playing.Ptr != sequence.Ptr)
        {
            node.SetAnim(sequence.SequenceName);
        }

        node.SetPosition(at, false);
        mesh.ForceSkelUpdate();
    }

    // Lets go of the thug he holds. Everything taken from him goes back, and he falls limp with
    // the speed he was carried at.
    static void Drop()
    {
        if (_carried is not { } held)
        {
            return;
        }

        _carried = null;
        if (held.Mesh is { IsValid: true } mesh)
        {
            if (_ownTree is { IsValid: true } tree)
            {
                mesh.SetAnimations(tree);
            }

            if (_ownSets >= 0 && mesh.AnimSets.Count > _ownSets)
            {
                mesh.AnimSets.Pop(mesh.AnimSets.Count - _ownSets);
            }
        }

        _ownTree = null;
        _ownSets = -1;
        _dangling = null;
        if (_brain is { IsValid: true } brain)
        {
            brain.SetTickIsDisabled(_brainWasOff);
        }

        _brain = null;
        if (!held.IsAlive)
        {
            return;
        }

        held.SetCollision(_ownCollide, _ownBlock, _ownIgnore);
        held.SetPhysics(Actor.EPhysics.PHYS_Falling);
        held.Velocity = _carrierVelocity;
        if (World.Player is { IsValid: true } player)
        {
            var speed = _carrierVelocity.Length();
            Villains.Knock(player, held, DropDamage, speed > 1f ? _carrierVelocity / speed : -Vector3.UnitZ, MathF.Max(speed, 100f), fall: true);
        }

        var at = held.Location;
        _dropHeight = Teleport.Floor(held, at, at.Z, at.Z - GroundSearch, out var floor) ? at.Z - floor.Z - held.CylinderComponent.CollisionHeight : GroundSearch;
        _dropped = held;
        _droppedAt = World.Now;
    }

    // Once a dropped thug has landed, a long fall puts him down for good.
    static void Landing()
    {
        if (_dropped is not { IsAlive: true } dropped)
        {
            _dropped = null;
            return;
        }

        var since = World.Now - _droppedAt;
        var speed = dropped.Mesh is { IsValid: true } mesh && mesh.FindBodyInstanceNamed(Middle) is { IsValid: true } body
            ? body.GetUnrealWorldVelocity().Length()
            : dropped.Velocity.Length();
        if (since < LandingSoonest || (speed > LandingSlow && since < LandingLongest))
        {
            return;
        }

        if (_dropHeight >= FatalDrop && !dropped.bGoodAsDead && dropped.Health > 0 && World.Controller is { } controller)
        {
            dropped.TakeDamage(10000, controller, dropped.Location, Vector3.UnitZ, RDmgType_CombatTakedown.StaticClass(), default, World.Player);
            _downed++;
            _downedAt = World.Now;
        }

        _dropped = null;
    }
}
