#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SNATCH. He dives to just over the thug nearest the crosshair and carries him off in his talons.
// The thug goes limp and hangs by his hands from Man-Bat's feet on two physics handles, so he
// swings as Man-Bat flies. The game would stand a ragdoll back up after a second, so he is kept
// down while held. Pressed again, he lets go, and the thug falls. A long enough fall puts him
// down for good.
static partial class ManBat
{
    // He comes in over the thug's head with this much room between them, then grabs him.
    const float GrabAbove = 40f;

    // A thug that hasn't gone limp this long after the grab is left alone.
    const float GoLimpLongest = 0.5f;

    // The thug's hands are drawn up to Man-Bat's feet over this long. His feet are kept this far
    // off the ground under him, and Man-Bat climbs to keep them there.
    const float LiftSeconds = 0.3f;
    const float FeetClear = 20f;
    const float FloorLook = 400f;
    const float LegLength = 100f;
    static readonly FName LeftFoot = new("Bip01_L_Foot");
    static readonly FName RightFoot = new("Bip01_R_Foot");
    static readonly FName LeftHand = new("Bip01_L_Hand");
    static readonly FName RightHand = new("Bip01_R_Hand");
    static readonly FName Hips = new("Bip01_Pelvis");

    // He drops a thug held this long. A drop from this high puts him down for good once he lands,
    // which is when he has stopped falling or after this long at most.
    const float CarryLongest = 12f;
    const float FatalDrop = 500f;
    const float LandingSlow = 150f;
    const float LandingLongest = 3f;
    const float LandingSoonest = 0.4f;
    const float GroundSearch = 5000f;
    static readonly FName Middle = new("Bip01_Spine2");

    static readonly Grip LeftTalon = new();
    static readonly Grip RightTalon = new();
    static readonly Limp Prone = new();

    static bool _snatching;
    static RPawnVillain? _carried;
    static float _carriedAt;

    // Held once he has gone limp. By both hands when his ragdoll has them, else by one bone.
    static bool _gripped;
    static float _grippedAt;
    static bool _byHands;
    static bool _crossed;
    static bool _retried;
    static FName _oneBone = FName.None;
    static Vector3 _leftFrom;
    static Vector3 _rightFrom;

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

    // Knocks him limp. He is taken hold of once his ragdoll is running, a tick or two on.
    static void Grab(RPawnPlayer player, RPawnVillain prey)
    {
        Unact();
        if (prey.Mesh is not { IsValid: true })
        {
            return;
        }

        // Whatever he was doing ends the way the game ends a move.
        if (prey.CombatMove is { IsValid: true } move)
        {
            move.ExitMoveForPawn(prey);
        }

        if (!prey.IsRagdoll())
        {
            prey.GenericGoRagdoll();
        }

        _carried = prey;
        _carriedAt = World.Now;
        _gripped = false;
        _retried = false;
        if (_squeal is { IsValid: true } squeal)
        {
            player.StartAudioEvent(squeal);
        }
    }

    // His hands, each to the nearer of Man-Bat's feet so his arms don't cross. A ragdoll with no
    // hands of its own is held by the chest, or the hips.
    static bool Take(RPawnPlayer player, SkeletalMeshComponent mesh)
    {
        _byHands = mesh.FindBodyInstanceNamed(LeftHand) is { IsValid: true } && mesh.FindBodyInstanceNamed(RightHand) is { IsValid: true };
        if (_byHands)
        {
            _leftFrom = mesh.GetBoneLocation(LeftHand, 0);
            _rightFrom = mesh.GetBoneLocation(RightHand, 0);
            Talons(player, out var left, out var right);
            _crossed = Vector3.Distance(_leftFrom, left) + Vector3.Distance(_rightFrom, right)
                > Vector3.Distance(_leftFrom, right) + Vector3.Distance(_rightFrom, left);
            _gripped = LeftTalon.Take(player, mesh, LeftHand, _leftFrom) && RightTalon.Take(player, mesh, RightHand, _rightFrom);
        }
        else
        {
            _oneBone = mesh.FindBodyInstanceNamed(Middle) is { IsValid: true } ? Middle : Hips;
            _leftFrom = mesh.GetBoneLocation(_oneBone, 0);
            _gripped = LeftTalon.Take(player, mesh, _oneBone, _leftFrom);
        }

        _grippedAt = World.Now;
        return _gripped;
    }

    // Every tick. He is kept limp, and his hands are pulled to Man-Bat's feet.
    static void Carry(RPawnPlayer player)
    {
        Landing();
        if (_carried is null)
        {
            return;
        }

        if (_carried is not { IsAlive: true } held || held.Mesh is not { IsValid: true } mesh || held.bGoodAsDead || held.Health <= 0 || World.Now - _carriedAt > CarryLongest)
        {
            Drop();
            return;
        }

        if (!_gripped)
        {
            var waited = World.Now - _carriedAt;
            if (!held.IsRagdoll())
            {
                if (waited > GoLimpLongest)
                {
                    Log.Info("Man-Bat's snatch missed: the thug didn't go limp.");
                    Drop();
                }
                else if (!_retried && waited > GoLimpLongest * 0.5f)
                {
                    _retried = true;
                    held.GenericGoRagdoll();
                }

                return;
            }

            if (!Take(player, mesh))
            {
                Log.Info("Man-Bat's snatch missed: his talons found nothing to hold.");
                Drop();
                return;
            }
        }
        else if (!held.IsRagdoll() || !LeftTalon.Holding)
        {
            Drop();
            return;
        }

        Prone.Keep(held);
        var lift = Math.Clamp((World.Now - _grippedAt) / LiftSeconds, 0f, 1f);
        var ease = lift * (2f - lift);
        Talons(player, out var leftFoot, out var rightFoot);
        if (_byHands)
        {
            LeftTalon.Pull(Vector3.Lerp(_leftFrom, _crossed ? rightFoot : leftFoot, ease));
            RightTalon.Pull(Vector3.Lerp(_rightFrom, _crossed ? leftFoot : rightFoot, ease));
            mesh.WakeRigidBody(LeftHand);
        }
        else
        {
            LeftTalon.Pull(Vector3.Lerp(_leftFrom, (leftFoot + rightFoot) * 0.5f, ease));
            mesh.WakeRigidBody(_oneBone);
        }

        // Too low, his feet would drag along the ground, so Man-Bat climbs.
        var feet = Lowest(mesh);
        if (lift >= 1f && Teleport.Floor(held, feet, feet.Z + LegLength, feet.Z - FloorLook, out var floor) && feet.Z - floor.Z < FeetClear)
        {
            Glide(player, Vector3.UnitZ * (FeetClear - (feet.Z - floor.Z)));
        }
    }

    // Man-Bat's feet as his body shows them, so the hold follows his wing beats.
    static void Talons(RPawnPlayer player, out Vector3 left, out Vector3 right)
    {
        if (_body is { IsValid: true } body && body.MatchRefBone(LeftFoot) >= 0 && body.MatchRefBone(RightFoot) >= 0)
        {
            left = body.GetBoneLocation(LeftFoot, 0);
            right = body.GetBoneLocation(RightFoot, 0);
            return;
        }

        left = right = player.Location - Vector3.UnitZ * player.CylinderComponent.CollisionHeight;
    }

    // The lower of the thug's feet, or a leg's length under his hips when they can't be found.
    static Vector3 Lowest(SkeletalMeshComponent mesh)
    {
        if (mesh.MatchRefBone(LeftFoot) >= 0 && mesh.MatchRefBone(RightFoot) >= 0)
        {
            var left = mesh.GetBoneLocation(LeftFoot, 0);
            var right = mesh.GetBoneLocation(RightFoot, 0);
            return left.Z < right.Z ? left : right;
        }

        return mesh.GetBoneLocation(Hips, 0) - Vector3.UnitZ * LegLength;
    }

    // Lets go of the thug he holds. The handles come off, and the thug falls limp with the speed
    // he was carried at, left enough stun time to land before the game stands him up.
    static void Drop()
    {
        if (_carried is not { } held)
        {
            return;
        }

        _carried = null;
        var gripped = _gripped;
        _gripped = false;
        LeftTalon.Remove();
        RightTalon.Remove();
        Prone.Free(LandingLongest);
        if (!gripped || !held.IsAlive || held.Mesh is not { IsValid: true } mesh)
        {
            return;
        }

        var feet = Lowest(mesh);
        _dropHeight = Teleport.Floor(held, feet, feet.Z, feet.Z - GroundSearch, out var floor) ? feet.Z - floor.Z : GroundSearch;
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
