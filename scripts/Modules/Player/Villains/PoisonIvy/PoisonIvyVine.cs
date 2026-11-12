#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Poison Ivy's vine, on fire while she aims. It is the vine that tears the militia's tanks apart
// at the Botanical Gardens, from that chapter's level, which is loaded as a package while she is
// on. Nothing in the level runs. Only the vine and its moves are taken from it.
//
// Aimed at one of the small tanks, the vine grabs it the way the game does there: the tank is
// pinned and plays its half of the vine's move for its kind, and it is destroyed at the moment the
// game's own move destroys it. Aimed anywhere else, the vine bursts out of the ground there, and
// anyone it bursts under is thrown and every vehicle and breakable around it is wrecked.
static partial class PoisonIvy
{
    const string Lender = "poison ivy";
    const string MissionPackage = "CityX__Ch7_LOD2";
    const string VinePath = "City_Ivy_Plant.Mesh.Ivy_Vine_Tank_A";

    // The vine's moves for bursting through a road. The level also has a set for soft ground.
    const string VineMovesPath = "Anim_Ivy.Plants.Plants_Attack_Tarmac";

    // The move for any spot without a tank, and the notify the game's moves destroy the tank on.
    static readonly FName StrikeMove = new("Ivy_Plant_Attack_01");
    const string DestroyNotify = "IvyDestroyTarget";

    // The tank's own anim node and the blend that shows it, which the game's grab plays through.
    static readonly FName TankMoveNode = new("CustomAnimation");
    static readonly FName TankMoveBlend = new("CustomAnimationBlend");

    // Each kind of small tank has its own grab, the same move on the vine and on the tank.
    static readonly (string Kind, string Move, string TankMovesPath)[] Grips =
    {
        ("FV_DRONETYPE_Diamondback", "Ivy_Plant_Attack_Armoured", "Anim_Ivy.Plants.Tanks_Armoured_Plants_Attack"),
        ("FV_DRONETYPE_Mamba", "Ivy_Plant_Attack_Missile", "Anim_Ivy.Plants.Tanks_Missile_Plants_Attack"),
        ("FV_DRONETYPE_TwinRattler", "Ivy_Plant_Attack_Double", "Anim_Ivy.Plants.Tanks_Double_Plants_Attack"),
        ("FV_DRONETYPE_Rattler", "Ivy_Plant_Attack_01", "Anim_Ivy.Plants.Tanks_Plants_Attack"),
    };

    // How far away she can strike, how close to the crosshair a vehicle must be to be grabbed,
    // and how long until the next vine.
    const float VineReach = 4000f;
    const float GrabNear = 900f;
    const float VineWait = 1.5f;

    // What the vine's burst reaches. Anyone in it takes this much and is thrown this hard.
    // Vehicles take many times their full health, past any armour, and parked cars blow up.
    const float StrikeReach = 350f;
    const float StrikeHeight = 300f;
    const int StrikeDamage = 1000;
    const float StrikeForce = 1500f;
    const float StrikeWreck = 10f;
    const int StrikeParked = 1000;

    // The damage the game's grab gives a tank.
    const int TankDamage = 10000;
    static readonly Vector3 TankPush = new(0f, 0f, 5000f);

    // Without a destroy notify in a move, the strike lands this far through it.
    const float StrikeShare = 0.5f;

    // A thug this close to the crosshair is seized. The vine spears him on its tip when the
    // game's move spears a tank, carries him through the move, and flings him off when it lets go.
    const float SeizeNear = 350f;
    const string PierceNotify = "IvyPiercedTank";
    const string ReleaseNotify = "IvyUnpiercedTank";
    static readonly FName HeldBone = new("Bip01_Spine2");
    const float ReleaseShare = 0.8f;

    // How hard the tip pulls the thug along, and how hard he is flung off, out and up.
    const float HoldFastest = 5000f;
    const float FlingForce = 1600f;
    const float FlingLift = 900f;

    // A carried car is thrown once destroyed. A car's own physics don't take a push, so it is
    // flown along the arc by hand for a moment, out, up and tumbling, then its physics are put
    // back with the arc's speed and kept going against the game pinning its wreck as it dies.
    const float CarThrow = 1400f;
    const float CarLift = 900f;
    const float CarTumble = 360f;
    static readonly Vector3 ArcGravity = new(0f, 0f, -1900f);
    const int ThrowTicks = 3;

    // The longest a throw is flown before it is set down where it is, how far off a wall it
    // drops, and how far below that the ground is looked for.
    const float ArcLongest = 3f;
    const float WallGap = 150f;
    const float DropDeepest = 3000f;

    // How long her hand stays thrown forward after a cast.
    const float CastSeconds = 0.6f;
    static readonly FName CastMove = new("Drone_Disruptor_Fire");

    sealed class Vine
    {
        public required SkeletalMeshActorSpawnable Host;
        public RVehicleNPC? Tank;
        public Class? TankBehaviour;
        public RAbandonedVehicle? Parked;
        public (bool Collide, bool Block) ParkedCollision;
        public (bool Collide, bool Block) TankCollision;
        public bool Unpinned;
        public float ThrowUntil;
        public int Flung;
        public float ArcStart = -1f;
        public float RideHeight = 60f;
        public Vector3 ArcFrom;
        public Vector3 ArcSpeed;
        public Vector3 ArcLast;
        public Rotator ArcTurn;

        // A car has no half of the vine's move to play. A hidden small tank plays it at the car's
        // spot instead. The car turns as the tank's chassis does and rides the vine's tip, then is
        // slammed down to where the chassis lands as it is destroyed. Its place and facing are
        // kept in the chassis's own frame, measured once the tank has its first real pose.
        public bool Lifts;
        public SkeletalMeshComponent? Ghost;
        public int GhostTicks;
        public bool Measured;
        public bool Lifting;
        public Vector3 ChassisOffset;
        public Vector3 LiftForward;
        public Vector3 LiftUp;
        public Vector3 StartAt;
        public Rotator StartTurn;
        public float LiftedAt;
        public (bool Occupied, bool Escorted, bool Barge)? Spared;
        public RPawnVillain? Victim;
        public Vector3 At;
        public float Strike;
        public float Pierce;
        public float Release;
        public float Ends;
        public bool Struck;
        public bool Pierced;
        public bool Released;
    }

    static readonly List<Vine> Vines = new();

    // The rubble and sound of a plant breaking up through a road, from the same chapter. Her
    // flytrap comes up through the ground with them.
    const string BurstLookPath = "EFX_City.CityY_10_Graveyard.Particles.VineFloorBurst_Tarmac";
    const string BurstSoundPath = "ww_seq_cityy_10_ivy_plants_tank_attack.Play_SEQ_IvyPlantAttack_Plant_In";
    const string SinkSoundPath = "ww_seq_cityy_10_ivy_plants_tank_attack.Play_SEQ_IvyPlantAttack_Plant_Out";

    // The smallest tank, whose half of the grab a car is moved by. Its mesh ships in Startup.
    const string GhostMeshPath = "Red_Hood_Lite_Tank.Mesh.TANK_Lite_Tank_GUN_V3";
    const string GhostKind = "FV_DRONETYPE_Rattler";
    static readonly FName Chassis = new("Chassis");
    const int GhostSettle = 3;

    // How long a car takes to be caught up onto the tip, and how long before it is destroyed it
    // is brought down off it.
    const float CatchSeconds = 0.3f;
    const float SlamSeconds = 0.35f;
    static SkeletalMesh? _ghostMesh;

    static bool _vineTried;
    static SkeletalMesh? _vineMesh;
    static AnimSet? _vineMoves;
    static ParticleSystem? _burstLook;
    static AkEvent? _burstSound;
    static AkEvent? _sinkSound;
    static readonly Dictionary<string, AnimSet> TankMoves = new();

    static bool _fireWas;
    static float _nextVine;
    static float _castUntil;

    static void LoadVine()
    {
        if (_vineTried)
        {
            return;
        }

        _vineTried = true;

        // Only what was kept outlives the level's load, so anything missing loads it again.
        var missing = Game.FindObject<SkeletalMesh>(VinePath) is not { IsValid: true } || Game.FindObject<AnimSet>(VineMovesPath) is not { IsValid: true }
            || Game.FindObject<AkBank>(VoiceBankPath) is not { IsValid: true } || Game.FindObject<AkDialogueLineRandom>(AttackLinePath) is not { IsValid: true }
            || Game.FindObject<ParticleSystem>(BurstLookPath) is not { IsValid: true } || Game.FindObject<AkEvent>(BurstSoundPath) is not { IsValid: true }
            || Game.FindObject<AkEvent>(SinkSoundPath) is not { IsValid: true };
        foreach (var grip in Grips)
        {
            missing |= Game.FindObject<AnimSet>(grip.TankMovesPath) is not { IsValid: true };
        }

        if (missing && !Packages.TryLoad(MissionPackage, "Poison Ivy's vine", "poison_ivy.load"))
        {
            return;
        }

        _vineMesh = Kept(Game.FindObject<SkeletalMesh>(VinePath));
        _vineMoves = Kept(Game.FindObject<AnimSet>(VineMovesPath));
        _ghostMesh = Kept(Game.FindObject<SkeletalMesh>(GhostMeshPath));
        _burstLook = Kept(Game.FindObject<ParticleSystem>(BurstLookPath));
        _burstSound = Kept(Game.FindObject<AkEvent>(BurstSoundPath));
        _sinkSound = Kept(Game.FindObject<AkEvent>(SinkSoundPath));
        foreach (var grip in Grips)
        {
            if (Kept(Game.FindObject<AnimSet>(grip.TankMovesPath)) is { } moves)
            {
                TankMoves[grip.Kind] = moves;
            }
        }

        if (_vineMesh is null || _vineMoves is null)
        {
            Log.Warn("poison_ivy.vine", "Poison Ivy's vine wasn't found, so her vine doesn't show.");
        }
    }

    static T? Kept<T>(T? thing)
        where T : GameObject => Borrowed.KeepFound(thing, Lender);

    static void Lash(RPawnPlayer player)
    {
        var fire = Aim.Fire;
        var now = World.Now;
        if (fire && !_fireWas && now >= _nextVine && _vineMesh is not null && _vineMoves is not null)
        {
            if (Grabbable(player) is { } grab)
            {
                _nextVine = now + VineWait;
                _castUntil = now + CastSeconds;
                Grab(player, grab.Tank, grab.Move, grab.Moves);
            }
            else if (Wreckable(player) is { } car)
            {
                _nextVine = now + VineWait;
                _castUntil = now + CastSeconds;
                Wrap(player, car);
            }
            else if (Seizable(player) is { } thug)
            {
                _nextVine = now + VineWait;
                _castUntil = now + CastSeconds;
                Seize(player, thug);
                Say(player, "vine");
            }
            else if (Aim.Ground(player, VineReach, out var at))
            {
                _nextVine = now + VineWait;
                _castUntil = now + CastSeconds;
                Burst(player, at, Rotator.FromDirection(Teleport.Flat(at - player.Location)));
                Say(player, "vine");
            }
        }

        _fireWas = fire;
        Grow(player);
    }

    // The small tank nearest the crosshair, if one is close enough to it and to her, with the
    // grab for its kind.
    static (RVehicleNPC Tank, FName Move, AnimSet Moves)? Grabbable(RPawnPlayer player)
    {
        var aimed = Aim.Target;
        (RVehicleNPC Tank, FName Move, AnimSet Moves)? best = null;
        var bestOff = GrabNear * GrabNear;
        foreach (var tank in Game.FindObjects<RVehicleTank>())
        {
            if (tank is not { IsValid: true, IsClassDefaultObject: false, bDeleteMe: false, bHidden: false, DroneHacked: false, IsFriendly: false, IsHackedFriendly: false, IsHeavyTank: false, bGrabbedByIvyVine: false, Health: > 0 }
                || Vector3.DistanceSquared(tank.Location, player.Location) > VineReach * VineReach)
            {
                continue;
            }

            var off = Vector3.DistanceSquared(tank.Location, aimed);
            if (off >= bestOff)
            {
                continue;
            }

            var kind = tank.DroneCombatEvTag.ToString() ?? "";
            foreach (var grip in Grips)
            {
                if (grip.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && TankMoves.TryGetValue(grip.Kind, out var moves))
                {
                    best = (tank, new FName(grip.Move), moves);
                    bestOff = off;
                    break;
                }
            }
        }

        return best;
    }

    // The car or other ground vehicle nearest the crosshair, driven or parked, if one is close
    // enough to it and to her. Flying drones and heavy tanks are left alone, as the game's vine
    // leaves them.
    static Actor? Wreckable(RPawnPlayer player)
    {
        var aimed = Aim.Target;
        Actor? best = null;
        var bestOff = GrabNear * GrabNear;
        foreach (var car in Game.FindObjects<RVehicleNPC>())
        {
            if (car is RVehicleFlying or RVehicleNPCBatmobile
                || car is not { IsValid: true, IsClassDefaultObject: false, bDeleteMe: false, bHidden: false, DroneHacked: false, IsFriendly: false, IsHackedFriendly: false, IsHeavyTank: false, bGrabbedByIvyVine: false, Health: > 0 })
            {
                continue;
            }

            Closer(car, ref best, ref bestOff);
        }

        foreach (var car in Game.FindObjects<RAbandonedVehicle>())
        {
            if (car is { IsValid: true, IsClassDefaultObject: false, bDeleteMe: false, bHidden: false, Health: > 0 })
            {
                Closer(car, ref best, ref bestOff);
            }
        }

        return best;

        void Closer(Actor car, ref Actor? best, ref float bestOff)
        {
            var off = Vector3.DistanceSquared(car.Location, aimed);
            if (off < bestOff && Vector3.DistanceSquared(car.Location, player.Location) <= VineReach * VineReach && !Held(car))
            {
                best = car;
                bestOff = off;
            }
        }
    }

    static bool Held(Actor car)
    {
        foreach (var vine in Vines)
        {
            if (vine.Tank is { } tank && tank.Ptr == car.Ptr || vine.Parked is { } parked && parked.Ptr == car.Ptr)
            {
                return true;
            }
        }

        return false;
    }

    // A car gets the vine's grab for the smallest tank, which is near a car's size. A parked car
    // already stands still. A driven one is pinned the way a tank is.
    static void Wrap(RPawnPlayer player, Actor car)
    {
        if (car is RVehicleNPC driven)
        {
            if (Grab(player, driven, StrikeMove, null) is { } held)
            {
                Carries(held);
            }

            return;
        }

        if (car is RAbandonedVehicle parked
            && Sprout(player, parked.Location, new Rotator(0f, parked.Rotation.Yaw, 0f), StrikeMove, grabbing: true) is { } vine)
        {
            vine.Parked = parked;
            vine.ParkedCollision = (parked.bCollideActors, parked.bBlockActors);
            parked.SetCollision(false, false, default);
            Carries(vine);
        }
    }

    // The hidden tank stands where the car is, facing its way, and plays the smallest tank's half
    // of the grab in step with the vine. It always ships with the game.
    static void Carries(Vine vine)
    {
        if (_ghostMesh is null || !TankMoves.TryGetValue(GhostKind, out var moves))
        {
            return;
        }

        var ghost = Made.Tracked(new SkeletalMeshComponent(vine.Host));
        ghost.SetActorCollision(false, false);
        ghost.AnimSets.Add(moves);
        var playing = Made.Tracked(new AnimNodeSequence(ghost));
        ghost.Animations = playing;
        ghost.bUpdateSkelWhenNotRendered = true;
        ghost.SetSkeletalMesh(_ghostMesh, false);
        ghost.SetHidden(true);
        vine.Host.AttachComponent(ghost);
        playing.SetAnim(StrikeMove);
        playing.PlayAnim(false, 1f, 0f);
        vine.Ghost = ghost;
        vine.Lifts = true;
    }

    // The game's grab: the vehicle is let go of by its driving and fights, pinned where it is with
    // nothing to bump into, and a tank plays its half of the vine's move.
    static Vine? Grab(RPawnPlayer player, RVehicleNPC tank, FName move, AnimSet? moves)
    {
        AnimNodeSequence? played = null;
        AnimNodeBlend? shown = null;
        if (moves is not null && tank.Mesh is { IsValid: true } body
            && body.FindAnimNode(TankMoveNode) is AnimNodeSequence { IsValid: true } node
            && body.FindAnimNode(TankMoveBlend) is AnimNodeBlend { IsValid: true } blend)
        {
            played = node;
            shown = blend;
            var sets = body.AnimSets;
            var known = false;
            for (var i = 0; i < sets.Count && !known; i++)
            {
                known = sets[i] is { IsValid: true } set && set.Ptr == moves.Ptr;
            }

            if (!known)
            {
                sets.Add(moves);
            }

            body.bForceDiscardRootMotion = false;
        }

        var vine = Sprout(player, tank.Location, new Rotator(0f, tank.Rotation.Yaw, 0f), move, grabbing: true);
        if (vine is null)
        {
            return null;
        }

        vine.Tank = tank;
        vine.TankBehaviour = tank.DefaultCombatBehaviour;
        vine.TankCollision = (tank.bCollideActors, tank.bBlockActors);
        tank.SetRigidBodyFixed(true);
        tank.SetCollision(false, false, default);
        tank.SetRigidBodyCollisionEnabled(false);
        tank.DetachInteractionsForDead();
        tank.GotoBehaviour(null!, default);
        tank.Velocity = Vector3.Zero;
        tank.DefaultCombatBehaviour = null!;
        tank.bGrabbedByIvyVine = true;

        if (played is not null && shown is not null)
        {
            played.SetAnim(move);
            played.PlayAnim(false, 1f, 0f);
            shown.SetBlendTarget(1f, 0f);
        }

        return vine;
    }

    // Moves a carried car as the hidden tank's chassis moves, until the car is destroyed.
    static void Lift(Vine vine)
    {
        Actor? car = vine.Parked is { IsValid: true, bDeleteMe: false } parked ? parked
            : vine.Tank is { IsValid: true, bDeleteMe: false } driven ? driven : null;
        if (!vine.Lifts || car is null || vine.Struck || vine.Ghost is not { IsValid: true } ghost)
        {
            return;
        }

        // A hidden mesh's first pose after it starts is stale.
        if (++vine.GhostTicks < GhostSettle)
        {
            return;
        }

        var chassis = ghost.GetBoneLocation(Chassis, 0);
        var x = ghost.GetBoneAxis(Chassis, GameObject.EAxis.AXIS_X);
        var y = ghost.GetBoneAxis(Chassis, GameObject.EAxis.AXIS_Y);
        var z = ghost.GetBoneAxis(Chassis, GameObject.EAxis.AXIS_Z);
        if (!vine.Measured)
        {
            vine.Measured = true;
            vine.StartAt = car.Location;
            vine.StartTurn = car.Rotation;
            if (Blink.Aim(vine.Host, car.Location + Vector3.UnitZ * 50f, car.Location - Vector3.UnitZ * 500f, out var floor, out _))
            {
                vine.RideHeight = MathF.Max(20f, car.Location.Z - floor.Z);
            }
            var off = car.Location - chassis;
            var forward = car.Rotation.ToDirection();
            var up = Up(car.Rotation);
            vine.ChassisOffset = new Vector3(Vector3.Dot(off, x), Vector3.Dot(off, y), Vector3.Dot(off, z));
            vine.LiftForward = new Vector3(Vector3.Dot(forward, x), Vector3.Dot(forward, y), Vector3.Dot(forward, z));
            vine.LiftUp = new Vector3(Vector3.Dot(up, x), Vector3.Dot(up, y), Vector3.Dot(up, z));
        }

        // The car waits until the tip has burst up past it, then rides on the tip.
        if (!TipAt(vine, out var tip) || !vine.Lifting && tip.Z < car.Location.Z)
        {
            return;
        }

        if (!vine.Lifting)
        {
            vine.Lifting = true;
            vine.LiftedAt = World.Now;
        }

        // Caught off the ground over a moment, carried, then slammed down where the chassis lands.
        var caught = Math.Clamp((World.Now - vine.LiftedAt) / CatchSeconds, 0f, 1f);
        caught = caught * caught * (3f - 2f * caught);
        var carried = Vector3.Lerp(vine.StartAt, tip, caught);
        var landed = chassis + x * vine.ChassisOffset.X + y * vine.ChassisOffset.Y + z * vine.ChassisOffset.Z;
        var slam = Math.Clamp((vine.Strike - World.Now) / SlamSeconds, 0f, 1f);
        var at = Vector3.Lerp(landed, carried, slam);
        var facing = Vector3.Normalize(x * vine.LiftForward.X + y * vine.LiftForward.Y + z * vine.LiftForward.Z);
        var top = Vector3.Normalize(x * vine.LiftUp.X + y * vine.LiftUp.Y + z * vine.LiftUp.Z);
        var turn = Facing(facing, top);
        car.SetLocation(at);
        car.SetRotation(turn);
        PrimitiveComponent? body = car is RAbandonedVehicle shell ? shell.SkeletalMeshComponent : car is RVehicle ride ? ride.Mesh : null;
        if (body is { IsValid: true })
        {
            body.SetRBPosition(at, FName.None);
            body.SetRBRotation(turn, FName.None);
        }
    }

    // The way up from a turn: straight up turned by its pitch and roll.
    static Vector3 Up(Rotator turn)
    {
        var forward = turn.ToDirection();
        var flatRight = Vector3.Normalize(new Vector3(-MathF.Sin(turn.Yaw * MathF.PI / 180f), MathF.Cos(turn.Yaw * MathF.PI / 180f), 0f));
        var up = Vector3.Normalize(Vector3.Cross(forward, flatRight));
        if (up.Z < 0f)
        {
            up = -up;
        }

        var roll = turn.Roll * MathF.PI / 180f;
        var right = Vector3.Normalize(Vector3.Cross(up, forward));
        return Vector3.Normalize(up * MathF.Cos(roll) - right * MathF.Sin(roll));
    }

    // A turn that faces a way, with its top toward another.
    static Rotator Facing(Vector3 forward, Vector3 top)
    {
        var pointed = Rotator.FromDirection(forward);
        var level = Up(new Rotator(pointed.Pitch, pointed.Yaw, 0f));
        var right = Vector3.Normalize(Vector3.Cross(level, forward));
        var roll = MathF.Atan2(-Vector3.Dot(top, right), Vector3.Dot(top, level)) * 180f / MathF.PI;
        return new Rotator(pointed.Pitch, pointed.Yaw, roll);
    }

    static void Burst(RPawnPlayer player, Vector3 at, Rotator turn) => Sprout(player, at, turn, StrikeMove);

    // The standing enemy nearest the crosshair, if one is close enough to it and to her.
    static RPawnVillain? Seizable(RPawnPlayer player)
    {
        var aimed = Aim.Target;
        RPawnVillain? best = null;
        var bestOff = SeizeNear * SeizeNear;
        foreach (var thug in Crowd.StandingEnemies())
        {
            if (thug.DrivenVehicle is { IsValid: true } || thug.Base is Vehicle || Vector3.DistanceSquared(thug.Location, player.Location) > VineReach * VineReach)
            {
                continue;
            }

            var off = Vector3.DistanceSquared(thug.Location, aimed);
            if (off < bestOff)
            {
                best = thug;
                bestOff = off;
            }
        }

        return best;
    }

    // The vine bursts up under his feet, facing him away from her.
    static void Seize(RPawnPlayer player, RPawnVillain thug)
    {
        var feet = thug.Location;
        if (thug.CylinderComponent is { IsValid: true } body)
        {
            feet.Z -= body.CollisionHeight;
        }

        if (Sprout(player, feet, Rotator.FromDirection(Teleport.Flat(feet - player.Location)), StrikeMove) is { } vine)
        {
            vine.Victim = thug;
        }
    }

    // The vine's very tip. Its pierce point sits partway along, where it spears a tank's hull.
    // The vine is one chain of rig bones, and a mesh lists every bone after the one it hangs
    // from, so the last rig bone listed is the tip. Found once from the mesh.
    const string RigBone = "Rig_Skin";
    static FName? _tipBone;

    static bool TipAt(Vine vine, out Vector3 tip)
    {
        tip = default;
        if (vine.Host.SkeletalMeshComponent is not { IsValid: true } mesh)
        {
            return false;
        }

        if (_tipBone is null)
        {
            var count = mesh.SkeletalMesh?.RefSkeleton.Count ?? 0;
            for (var i = count - 1; i >= 0 && _tipBone is null; i--)
            {
                var bone = mesh.GetBoneName(i);
                if ((bone.ToString() ?? "").Contains(RigBone, StringComparison.OrdinalIgnoreCase))
                {
                    _tipBone = bone;
                }
            }

            if (_tipBone is null && count > 0)
            {
                _tipBone = mesh.GetBoneName(count - 1);
            }
        }

        if (_tipBone is not { } tipBone)
        {
            return false;
        }

        tip = mesh.GetBoneLocation(tipBone, 0);
        return true;
    }

    // While speared he is a ragdoll pulled onto the tip each tick, so he hangs off it and swings
    // as it moves. Letting go throws him on away from her.
    static void Hold(RPawnPlayer player, Vine vine)
    {
        if (vine.Victim is not { IsValid: true, bDeleteMe: false } thug)
        {
            return;
        }

        var now = World.Now;
        if (!vine.Pierced && now >= vine.Pierce)
        {
            vine.Pierced = true;
            Villains.Knock(player, thug, StrikeDamage, Vector3.UnitZ, 0f, fall: true);
        }

        if (!vine.Pierced || vine.Flung >= ThrowTicks || !thug.IsRagdoll() || thug.Mesh is not { IsValid: true } body)
        {
            return;
        }

        // Thrown on away from her, given over a few ticks so a ragdoll settling on the tip
        // doesn't swallow it.
        if (now >= vine.Release)
        {
            vine.Released = true;
            vine.Flung++;
            body.WakeRigidBody(FName.None);
            body.SetRBLinearVelocity(Thrown(player, thug.Location, FlingForce, FlingLift), false);
            return;
        }

        if (!TipAt(vine, out var tip))
        {
            return;
        }

        var pull = (tip - body.GetBoneLocation(HeldBone, 0)) / MathF.Max(World.Delta, 1f / 120f);
        if (pull.LengthSquared() > HoldFastest * HoldFastest)
        {
            pull = Vector3.Normalize(pull) * HoldFastest;
        }

        body.WakeRigidBody(FName.None);
        body.SetRBLinearVelocity(pull, false);
    }

    // The vine stands in the world as an actor of its own. Its moves put their rubble and dust
    // where the actor that owns them stands, so held by the player they burst at her feet.
    static Vine? Sprout(RPawnPlayer player, Vector3 at, Rotator turn, FName move, bool grabbing = false)
    {
        if (_vineMoves?.FindAnimSequence(move) is not { IsValid: true } sequence
            || player.Spawn(SkeletalMeshActorSpawnable.StaticClass(), player, FName.None, at, turn, null!, true) is not SkeletalMeshActorSpawnable { IsValid: true } host
            || host.SkeletalMeshComponent is not { IsValid: true } mesh)
        {
            return null;
        }

        host.SetCollision(false, false, false);
        mesh.SetActorCollision(false, false);
        mesh.AnimSets.Add(_vineMoves!);
        var playing = Made.Tracked(new AnimNodeSequence(mesh));
        mesh.Animations = playing;
        mesh.bUpdateSkelWhenNotRendered = true;
        mesh.SetSkeletalMesh(_vineMesh!, false);
        playing.SetAnim(move);
        playing.PlayAnim(false, 1f, 0f);

        // A move can play faster than its length says, so its times are turned into seconds.
        var now = World.Now;
        var speed = sequence.RateScale > 0f ? sequence.RateScale : 1f;
        var length = MathF.Max(sequence.SequenceLength, 0.1f);
        var strike = StrikeAt(sequence, length, grabbing);
        var vine = new Vine
        {
            Host = host,
            At = at,
            Strike = now + strike / speed,
            Pierce = now + (NotifyAt(sequence, PierceNotify) ?? strike) / speed,
            Release = now + (NotifyAt(sequence, ReleaseNotify) ?? length * ReleaseShare) / speed,
            Ends = now + length / speed,
        };

        Vines.Add(vine);
        return vine;
    }

    // When a script notify of the game's move fires, in the move's own time.
    static float? NotifyAt(AnimSequence sequence, string name)
    {
        foreach (var notify in sequence.Notifies)
        {
            if (notify.Notify is AnimNotify_Script { IsValid: true } script
                && (script.NotifyName.ToString() ?? "").Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return notify.Time;
            }
        }

        return null;
    }

    // When the hit lands, read from the move. A grabbed tank is destroyed when the game's own
    // move destroys it. A burst hits when the vine first breaks out of the ground, which is the
    // move's first effect.
    static float StrikeAt(AnimSequence sequence, float length, bool grabbing)
    {
        var burst = float.MaxValue;
        if (grabbing && NotifyAt(sequence, DestroyNotify) is { } destroy)
        {
            return Math.Clamp(destroy, 0f, length);
        }

        foreach (var notify in sequence.Notifies)
        {
            if (notify.Notify is RAnimNotify_SpawnParticleFX { IsValid: true })
            {
                burst = MathF.Min(burst, notify.Time);
            }
        }

        return !grabbing && burst <= length ? burst : length * StrikeShare;
    }

    static void Grow(RPawnPlayer? player)
    {
        var now = World.Now;
        for (var i = Vines.Count - 1; i >= 0; i--)
        {
            var vine = Vines[i];
            if (!vine.Struck && now >= vine.Strike && player is not null)
            {
                vine.Struck = true;
                Strike(player, vine);
            }

            if (player is not null)
            {
                Hold(player, vine);
                Lift(vine);
                Throw(player, vine);
            }

            if (now >= MathF.Max(vine.Ends, vine.ThrowUntil) || player is null)
            {
                Wither(vine);
                Vines.RemoveAt(i);
            }
        }
    }

    static void Strike(RPawnPlayer player, Vine vine)
    {
        var hit = new HashSet<nint>();
        if (vine.Tank is { IsValid: true, bDeleteMe: false } tank)
        {
            // A car with people in it, or one under escort, is spared explosions by the game.
            // The vine isn't, so that is switched off for its blow, and put back if it lives.
            vine.Spared = (tank.bInvincibleWhenOccupied, tank.bInvincibleWhenEscorted, tank.bOnlyVulnerableToBarge);
            tank.bInvincibleWhenOccupied = false;
            tank.bInvincibleWhenEscorted = false;
            tank.bOnlyVulnerableToBarge = false;
            hit.Add(tank.Ptr);
            tank.PlayDeathBarks();
            tank.TakeDamage(TankDamage, player.Controller, tank.Location, TankPush, RDmgType_Explosion.StaticClass(), default, player);
            Say(player, "wreck", always: true);
            _wrecked++;

            // A carried car is thrown as it blows up, while the vine pulls back.
            if (vine.Lifts && tank.IsValid && !tank.bDeleteMe)
            {
                var now = World.Now;
                vine.ArcStart = now;
                vine.ArcFrom = tank.Location;
                vine.ArcLast = tank.Location;
                vine.ArcSpeed = Thrown(player, tank.Location, CarThrow, CarLift);
                vine.ArcTurn = tank.Rotation;
                vine.ThrowUntil = now + ArcLongest;
            }
        }

        // A parked car is set alight the way the riots do it, then blown up.
        if (vine.Parked is { IsValid: true, bDeleteMe: false } parked)
        {
            hit.Add(parked.Ptr);
            Say(player, "wreck", always: true);
            _wrecked++;
            if (!parked.bOnFire)
            {
                parked.MolotovExplode();
            }

            parked.TakeDamage(Math.Max(StrikeParked, parked.Health + 1), player.Controller, parked.Location, TankPush, RDmgType_Explosion.StaticClass(), default, player);
        }

        var middle = vine.At + Vector3.UnitZ * 100f;
        foreach (var thug in Crowd.StandingEnemies())
        {
            if (vine.Victim is { } held && held.Ptr == thug.Ptr)
            {
                continue;
            }

            var off = thug.Location - vine.At;
            if (off.X * off.X + off.Y * off.Y > StrikeReach * StrikeReach || MathF.Abs(off.Z) > StrikeHeight
                || !player.FastTrace(thug.Location, middle, Vector3.Zero, false))
            {
                continue;
            }

            Villains.Knock(player, thug, StrikeDamage, Vector3.Normalize(Teleport.Flat(off) * 0.5f + Vector3.UnitZ), StrikeForce, fall: true);
        }

        Villains.Wreck(player, vine.At, StrikeReach, StrikeWreck, StrikeParked, hit);
        Villains.Break(player, vine.At, StrikeReach);
    }

    // The vine goes back into the ground. A tank that lived through it is given back everything
    // the grab took from it.
    static void Wither(Vine vine)
    {
        if (vine.Host is { IsValid: true, bDeleteMe: false } host)
        {
            host.Destroy();
        }

        Release(vine);
        if (vine.Tank is { IsValid: true, bDeleteMe: false, Health: > 0 } tank)
        {
            tank.bGrabbedByIvyVine = false;
            tank.DefaultCombatBehaviour = vine.TankBehaviour!;
            if (vine.Spared is { } spared)
            {
                tank.bInvincibleWhenOccupied = spared.Occupied;
                tank.bInvincibleWhenEscorted = spared.Escorted;
                tank.bOnlyVulnerableToBarge = spared.Barge;
            }

            if (tank.Mesh?.FindAnimNode(TankMoveBlend) is AnimNodeBlend { IsValid: true } shown)
            {
                shown.SetBlendTarget(0f, 0.2f);
            }
        }

        // A parked car can't fall, so one that lived through it goes back where it stood.
        if (vine.Parked is { IsValid: true, bDeleteMe: false, Health: > 0 } parked)
        {
            if (vine.Measured)
            {
                parked.SetLocation(vine.StartAt);
                parked.SetRotation(vine.StartTurn);
            }

            parked.SetCollision(vine.ParkedCollision.Collide, vine.ParkedCollision.Block, default);
        }
    }

    // The grab's pin comes off a vehicle that is still alive. A wreck's physics are torn down a
    // moment after it dies, and touching them after that took the game down, so a wreck keeps
    // its pin where it was set down.
    static void Release(Vine vine)
    {
        if (vine.Unpinned || vine.Tank is not { IsValid: true, bDeleteMe: false, Health: > 0 } tank)
        {
            return;
        }

        vine.Unpinned = true;
        tank.SetRigidBodyFixed(false);
        tank.SetRigidBodyCollisionEnabled(true);
        tank.SetCollision(vine.TankCollision.Collide, vine.TankCollision.Block, default);
        if (tank.Mesh is { IsValid: true } body)
        {
            body.WakeRigidBody(FName.None);
        }
    }

    // Out and away from her, and up.
    static Vector3 Thrown(RPawnPlayer player, Vector3 at, float outward, float up)
    {
        var away = Teleport.Flat(at - player.Location);
        return (away.LengthSquared() > 1e-4f ? Vector3.Normalize(away) * outward : Vector3.Zero) + Vector3.UnitZ * up;
    }

    // Flies a thrown car along its arc until it comes down. A wreck's physics are torn down a
    // moment after it dies, and switching them back on then took the game down, so a wreck is
    // only ever placed: it lands on the ground it comes down on, and stays put there. One that
    // somehow lived gets its own physics back as it lands.
    static void Throw(RPawnPlayer player, Vine vine)
    {
        var now = World.Now;
        if (vine.ArcStart < 0f || vine.Tank is not { IsValid: true, bDeleteMe: false } car)
        {
            return;
        }

        var t = now - vine.ArcStart;
        var at = vine.ArcFrom + vine.ArcSpeed * t + ArcGravity * (0.5f * t * t);

        // On the way up it is still leaving the ground it was slammed into, so only what it comes
        // down onto stops it. Against a wall it drops to the ground below.
        var landed = false;
        var hit = vine.ArcLast;
        var normal = Vector3.Zero;
        if (at.Z < vine.ArcLast.Z && Blink.Aim(player, vine.ArcLast, at, out var struck, out var facing))
        {
            landed = true;
            hit = struck;
            normal = facing;
        }
        else if (t >= ArcLongest)
        {
            landed = true;
        }

        if (landed)
        {
            if (normal.Z < 0.5f)
            {
                var from = hit + Teleport.Flat(normal) * WallGap;
                if (Blink.Aim(player, from + Vector3.UnitZ * 50f, from - Vector3.UnitZ * DropDeepest, out var ground, out _))
                {
                    hit = ground;
                }
            }

            Land(car, hit + Vector3.UnitZ * vine.RideHeight, vine.ArcTurn.Yaw, (vine.ArcTurn.Pitch - CarTumble * t) % 360f);
            vine.ArcStart = -1f;
            vine.ThrowUntil = now;
            if (car.Health > 0)
            {
                Release(vine);
            }

            return;
        }

        var turn = vine.ArcTurn with { Pitch = vine.ArcTurn.Pitch - CarTumble * t };
        vine.ArcLast = at;
        Place(car, at, turn);
    }

    // Set down upright or on its roof, whichever it was nearer as it tumbled.
    static void Land(RVehicleNPC car, Vector3 at, float yaw, float pitch)
    {
        var tipped = MathF.Abs(MathF.Abs(pitch) - 180f) < 90f;
        Place(car, at, new Rotator(0f, yaw, tipped ? 180f : 0f));
    }

    static void Place(RVehicleNPC car, Vector3 at, Rotator turn)
    {
        car.SetLocation(at);
        car.SetRotation(turn);
        if (car.Mesh is { IsValid: true } flown)
        {
            flown.SetRBPosition(at, FName.None);
            flown.SetRBRotation(turn, FName.None);
        }
    }

    static void StopVines()
    {
        Grow(null);
        _fireWas = false;
        _castUntil = 0f;
    }

    static void UnloadVine()
    {
        StopVines();
        _vineMesh = null;
        _vineMoves = null;
        _burstLook = null;
        _burstSound = null;
        _sinkSound = null;
        _ghostMesh = null;
        TankMoves.Clear();
        _vineTried = false;
    }
}
