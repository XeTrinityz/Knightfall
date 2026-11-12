#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace Knightfall;

// BATTLE MODE ON ANY SKIN. The game refuses battle mode on a skin for one reason: the car has no
// transform node, because the skin's animation tree has no node called Turret_Up. Every route
// into battle mode, and the winch and the tutorials too, test that one field.
//
// So nothing of the game's is overridden here. The skin is given the parts it lacks and the game
// runs its own battle mode: its own input, camera, HUD and way out.
//
//   A transform node with no animation. The game treats that as an instant transform.
//   A Batmobile wheel control on every wheel. Once a car has a transform node its physics reads
//   one from each wheel without checking it is there, so these go on first and come off last.
//   The two winch sockets, which these skins also lack.
static class BattleAnySkin
{
    // A wheel control with no wheel bone is skipped wherever the physics would follow the bone.
    // That is the safe setting. It also leaves the wheels without drive in battle mode, so the
    // car is moved along by Strafe below.
    const int NoBone = -1;
    const int RootBone = 0;

    // Measured on the Arkham Knight car in battle mode: it closes on its top speed like a curve
    // flattening out, about 86% of the way in 0.22 seconds. That is a response time of 0.1 s.
    const float ResponseSeconds = 0.1f;
    const float FallbackSpeed = 1200f;

    // The fitted wheels get no drive in battle mode, so their grip, and the hard grip of a parked
    // wheel when no throttle is applied, fights every push and the car bounces. While strafing,
    // the grip is turned down to this share and the push does the driving.
    const float StrafeGrip = 0.1f;

    // Roughly where the Arkham Knight car's winch door is, from the front axle.
    const float WinchAhead = 140f;
    const float WinchUp = 60f;

    static readonly FName BattleState = new("BattleMode");
    static readonly FName PursuitState = new("PursuitMode");
    static readonly FName[] WinchSockets = { new("WinchOrigin"), new("WinchAttach") };

    // Battle mode wheels turn toward the way the car is moving, at this many degrees a second,
    // and hold their angle below this speed so they don't flick about at a standstill.
    const float WheelTurnRate = 300f;
    const float WheelHoldBelow = 60f;
    const float ToDegrees = 180f / MathF.PI;

    // Learning how the game's wheel numbers look on screen. A sample only counts while the car is
    // clearly moving and turning, and the count is capped so a few odd samples can't flip it.
    const float LearnSpeed = 400f;
    const float LearnSteer = 4f;
    const float LearnYawRate = 8f;
    const int MostVotes = 60;

    sealed class Fitted
    {
        public required RVehicleBatmobileBase Car;
        public required AnimNodeSequence Node;
        public required List<(SVehicleWheel Wheel, SkelControlWheel? Original, RSkelControlBatmobileWheel Ours)> Wheels;
        public SkeletalMesh? Mesh;
        public List<SkeletalMeshSocket> Sockets = new();

        // What each wheel shows, in the game's own units. Driven by the game outside battle mode
        // and by us inside it, so the hand-over is smooth both ways.
        public float[] Steer = Array.Empty<float>();
        public float[] Roll = Array.Empty<float>();
        public float LastYaw;

        // When the game last started turning the car to face the camera after a mode change.
        public float TurningSince = -1f;
    }

    // Changing mode, the game takes over steering to turn the car toward where the camera faces,
    // for as long as the transform takes. It lets go once the car lines up and the steering is
    // near the middle, so holding a drift kept it turning the car, and the camera with it, round
    // and round. Ours has no transform to wait for, so it gets about as long as a real one.
    const float TurnTakeoverSeconds = 1f;

    static Fitted? _fitted;

    // Going into battle mode, the game blends each car's suspension from its normal values to its
    // battle values, by how far the transform has got. With no transform node the blend stays at
    // normal. With ours the transform counts as done, so the battle values apply in full, and
    // those are made for the Arkham Knight car changing shape. On these skins they lift the rear.
    // So a fitted car's battle values are held at its own normal values.
    // Only the ride height and wheel droop values lift the car. The battle stiffness and damping
    // stay, as on the real car, which keeps it steady.
    static readonly (string Name, Func<RVehicleSimBatmobile, float> Normal, Held<RVehicleSimBatmobile, float> Battle)[] Suspension =
    {
        ("suspension bias", sim => sim.PMWheelSuspensionBias, new("battle suspension bias", sim => sim.BMWheelSuspensionBias, (sim, value) => sim.BMWheelSuspensionBias = value)),
        ("front suspension bias", sim => sim.PMFrontWheelSuspensionBias, new("battle front suspension bias", sim => sim.BMFrontWheelSuspensionBias, (sim, value) => sim.BMFrontWheelSuspensionBias = value)),
        ("front wheel droop", sim => sim.PMFrontWheelMaxDroop, new("battle front wheel droop", sim => sim.BMFrontWheelMaxDroop, (sim, value) => sim.BMFrontWheelMaxDroop = value)),
        ("rear wheel droop", sim => sim.PMRearWheelMaxDroop, new("battle rear wheel droop", sim => sim.BMRearWheelMaxDroop, (sim, value) => sim.BMRearWheelMaxDroop = value)),
    };

    static readonly Func<float, float> Loosened = was => was * StrafeGrip;

    static readonly Held<SVehicleWheel, float>[] Grip =
    {
        new("wheel side grip", wheel => wheel.LatSlipFactor, (wheel, value) => wheel.LatSlipFactor = value),
        new("wheel forward grip", wheel => wheel.LongSlipFactor, (wheel, value) => wheel.LongSlipFactor = value),
        new("parked wheel grip", wheel => wheel.ParkedSlipFactor, (wheel, value) => wheel.ParkedSlipFactor = value),
    };

    static void ReleaseGrip()
    {
        foreach (var held in Grip)
        {
            held.Release();
        }
    }

    // Whether a positive steering number turns a wheel right, and a positive change in roll turns
    // it forward. Learnt by watching the game drive the car normally, and kept over a hot reload.
    static int SteerVotes
    {
        get => Survive.Get(Survive.WheelSteerVotes, 0);
        set => Survive.Set(Survive.WheelSteerVotes, Math.Clamp(value, -MostVotes, MostVotes));
    }

    static int RollVotes
    {
        get => Survive.Get(Survive.WheelRollVotes, 0);
        set => Survive.Set(Survive.WheelRollVotes, Math.Clamp(value, -MostVotes, MostVotes));
    }

    static float SteerSign => SteerVotes < 0 ? -1f : 1f;

    static float RollSign => RollVotes < 0 ? -1f : 1f;

    public static readonly Toggle Option = new(
        "batmobile.driving.battle_any_skin", "BATTLE MODE ON ANY SKIN",
        "Battle mode, with its weapons, winch and camera, on the skins the game won't let transform. The car keeps its shape.")
    {
        Tick = Keep,
        Restore = Remove,
    };

    static bool InBattle(RVehicleBatmobileBase car) => car.GetStateName().Index == BattleState.Index;

    // An object made here and the same object handed back by the game may not be the same C#
    // wrapper, so they are compared by what they point at.
    static bool Same(GameObject? one, GameObject? other) => (one?.Ptr ?? 0) == (other?.Ptr ?? 0);

    static void Keep()
    {
        var car = Car.Find();
        if (_fitted is { } old && !Same(old.Car, car))
        {
            // A new car, from a skin change or a new area. If the old one is still about it is
            // put back the way it was. If not, it took its parts with it.
            if (old.Car.IsValid)
            {
                Remove();
            }
            else
            {
                Forget(old);
            }
        }

        if (car is null)
        {
            return;
        }

        if (_fitted is null && car.TransformationAnimNode is null)
        {
            Fit(car);
        }

        if (_fitted is { } fitted)
        {
            Maintain(fitted);
        }
    }

    static void Fit(RVehicleBatmobileBase car)
    {
        if (car.Mesh is not { IsValid: true } body || car.Wheels.Count == 0)
        {
            return;
        }

        // New objects live inside the car's animation tree, so they go when the car does.
        GameObject home = body.Animations is { IsValid: true } tree ? tree : body;

        var wheels = new List<(SVehicleWheel, SkelControlWheel?, RSkelControlBatmobileWheel)>();
        foreach (var wheel in car.Wheels)
        {
            if (wheel is not { IsValid: true })
            {
                return;
            }

            // Every wheel gets one of ours, even on a skin that already has Batmobile wheel
            // controls, like Batman v Superman. With a transform node the game would start
            // posing the physics wheels from those controls' bones, which it never does for that
            // skin, and its own controls are then free to show our battle wheel turning.
            var control = Made.Tracked(new RSkelControlBatmobileWheel(home));
            control.WheelBoneIndex = NoBone;
            control.DiskBrakeBoneIndex = NoBone;
            control.CoolingDiskBoneIndex = NoBone;

            // One path reads this bone without checking the number, so it has to be a real one.
            control.SuspensionBoneIndex = RootBone;

            // The physics divides by this.
            control.SquashMaxDist = 1f;
            wheels.Add((wheel, wheel.WheelControl, control));
        }

        var node = Made.Tracked(new AnimNodeSequence(home));

        // Either of these would make the game reach for the mesh a loose node doesn't have.
        node.bCauseActorAnimPlay = false;
        node.bCauseActorAnimEnd = false;
        node.bForceRefposeWhenNotPlaying = false;

        // Wheels first. The node is what makes the physics start reading them.
        foreach (var (wheel, _, control) in wheels)
        {
            wheel.WheelControl = control;
        }

        // Only the car gets the node. The game gives the car's physics its own copy, and that copy
        // is what lets a car turn on the spot in normal driving, with the camera following it
        // round. These skins never could, and a slow U-turn would tip into it half way.
        car.TransformationAnimNode = node;

        var fitted = new Fitted { Car = car, Node = node, Wheels = wheels };
        _fitted = fitted;

        try
        {
            AddWinchSockets(fitted, body);
        }
        catch (Exception error)
        {
            Log.Error("battle.sockets", "The winch sockets could not be added, so the winch may not find targets on this skin", error);
        }
    }

    static void Maintain(Fitted fitted)
    {
        var car = fitted.Car;
        if (!Same(car.TransformationAnimNode, fitted.Node))
        {
            // The game put its own node in. The car is the game's again.
            Forget(fitted);
            return;
        }

        // The game starts a transform when the mode changes and waits for the animation to end.
        // Ours has none and nothing ever plays it, so the transform is finished here.
        if (fitted.Node.bPlaying)
        {
            fitted.Node.bPlaying = false;
        }

        if (car.bTransforming)
        {
            car.bTransforming = false;
        }

        if (Car.Sim(car) is { } sim)
        {
            foreach (var (_, normal, held) in Suspension)
            {
                held.Hold(sim, normal(sim));
            }

            if (!sim.bTakenControlDuringTransformation)
            {
                fitted.TurningSince = -1f;
            }
            else if (fitted.TurningSince < 0f)
            {
                fitted.TurningSince = World.Now;
            }
            else if (World.Now - fitted.TurningSince > TurnTakeoverSeconds)
            {
                sim.bTakenControlDuringTransformation = false;
                fitted.TurningSince = -1f;
            }
        }

        var driven = Same(World.Driving, car);
        var battle = driven && InBattle(car);
        if (driven && !battle)
        {
            Learn(fitted);
        }

        fitted.LastYaw = car.Rotation.Yaw;
        if (fitted.Steer.Length != fitted.Wheels.Count)
        {
            fitted.Steer = new float[fitted.Wheels.Count];
            fitted.Roll = new float[fitted.Wheels.Count];
        }

        for (var i = 0; i < fitted.Wheels.Count; i++)
        {
            var (wheel, original, ours) = fitted.Wheels[i];
            if (!wheel.IsValid)
            {
                continue;
            }

            // If the game links a wheel back to its old control, ours goes straight back on. A
            // transform node without one crashes the game.
            if (!Same(wheel.WheelControl, ours))
            {
                wheel.WheelControl = ours;
            }

            if (battle)
            {
                BattleWheel(fitted, i, wheel);
                foreach (var held in Grip)
                {
                    held.Hold(wheel, Loosened);
                }
            }
            else
            {
                fitted.Steer[i] = ours.WheelSteering;
                fitted.Roll[i] = ours.WheelRoll;
            }

            // The physics writes the wheel's motion into the control it knows about. The skin's
            // own control is the one that turns the wheel on screen, so it is handed on.
            if (original is { IsValid: true })
            {
                original.WheelDisplacement = ours.WheelDisplacement;
                original.WheelRoll = fitted.Roll[i];
                original.WheelSteering = fitted.Steer[i];
            }
        }

        if (battle)
        {
            Strafe(car);
        }
        else
        {
            // Back to full grip the moment battle mode ends.
            ReleaseGrip();
        }
    }

    // In battle mode the Arkham Knight car swings its wheels to face the way it is moving. Here
    // every wheel turns toward the car's direction of travel and rolls at the speed it covers the
    // ground. Past 90 degrees the car is backing up, so the wheels face the other way and roll
    // backwards, as real wheels would.
    static void BattleWheel(Fitted fitted, int index, SVehicleWheel wheel)
    {
        var car = fitted.Car;
        var (forward, _) = Axes(car.Rotation);
        forward = forward with { Z = 0f };
        var right = new Vector3(-forward.Y, forward.X, 0f);
        var flat = car.Velocity with { Z = 0f };
        var speed = flat.Length();

        var backwards = 1f;
        if (speed >= WheelHoldBelow)
        {
            var heading = MathF.Atan2(Vector3.Dot(flat, right), Vector3.Dot(flat, forward)) * ToDegrees;
            if (MathF.Abs(heading) > 90f)
            {
                heading -= 180f * MathF.Sign(heading);
                backwards = -1f;
            }

            var target = heading * SteerSign;
            var step = WheelTurnRate * World.Delta;
            fitted.Steer[index] += Math.Clamp(target - fitted.Steer[index], -step, step);
        }
        else if (Vector3.Dot(flat, forward) < 0f)
        {
            backwards = -1f;
        }

        // A roll of the wheel's radius covers its radius of ground, in the game's degrees.
        var radius = MathF.Max(wheel.WheelRadius, 1f);
        fitted.Roll[index] += RollSign * backwards * speed * World.Delta / radius * ToDegrees;
    }

    // While the game drives the car normally, its own steering and rolling are compared with how
    // the car actually turns and moves. That settles which way round the numbers go.
    static void Learn(Fitted fitted)
    {
        var car = fitted.Car;
        if (World.Delta <= 0f || fitted.Wheels.Count == 0)
        {
            return;
        }

        var (forward, _) = Axes(car.Rotation);
        var along = Vector3.Dot(car.Velocity with { Z = 0f }, forward with { Z = 0f });
        if (MathF.Abs(along) < LearnSpeed)
        {
            return;
        }

        var turn = car.Rotation.Yaw - fitted.LastYaw;
        turn = turn > 180f ? turn - 360f : turn < -180f ? turn + 360f : turn;
        var yawRate = turn / World.Delta;

        var steer = 0f;
        var rollChange = 0f;
        for (var i = 0; i < fitted.Wheels.Count && i < fitted.Steer.Length; i++)
        {
            var ours = fitted.Wheels[i].Ours;
            if (MathF.Abs(ours.WheelSteering) > MathF.Abs(steer))
            {
                steer = ours.WheelSteering;
            }

            var change = ours.WheelRoll - fitted.Roll[i];
            change = change > 180f ? change - 360f : change < -180f ? change + 360f : change;
            rollChange += change;
        }

        // Going forward, a right turn turns the car's yaw up. Backing up swaps it round.
        if (along > 0f && MathF.Abs(steer) > LearnSteer && MathF.Abs(yawRate) > LearnYawRate)
        {
            SteerVotes += MathF.Sign(steer * yawRate);
        }

        if (MathF.Abs(rollChange) > 0.01f)
        {
            RollVotes += MathF.Sign(rollChange * along);
        }
    }

    // On a radio call in the car the game cuts to a camera inside the cockpit, placed on a chassis
    // bone. It skips that for a skin with no transform node, because those skins have neither the
    // bone nor a cockpit. A fitted skin has a node now, so it is skipped here the same way.
    // Nothing about battle mode itself is redirected.
    [Redirect("BmGame.RPlayerController:RadioMode:PlayBatmobileConversationCam", typeof(RPlayerController))]
    static void PlayBatmobileConversationCam(RPlayerController self)
    {
        try
        {
            if (_fitted is { } fitted && Same(self.BatmobilePawn, fitted.Car))
            {
                self.bRadioModePlayingBatmobileCameraIn = false;
                return;
            }
        }
        catch (Exception error)
        {
            Log.Error("battle.radio_camera", "The radio call camera check failed", error);
        }

        Redirects.RunOriginal();
    }

    // Battle mode's stick input arrives as a direction to move in. The car's speed along the
    // ground is steered toward it.
    static void Strafe(RVehicleBatmobileBase car)
    {
        if (World.Delta <= 0f || Car.Sim(car) is not { } sim || car.CollisionComponent is not { IsValid: true } body || !car.HasWheelsOnGround())
        {
            return;
        }

        var move = sim.AbsoluteMoveDirection with { Z = 0f };
        var amount = MathF.Min(move.Length(), 1f);
        var pushing = amount > 0.05f;

        // BATTLE MODE SPEED already scales the game's own top speed, so it is used as it is.
        var top = sim.BattleModeMaxSpeed is > 100f and < 20000f ? sim.BattleModeMaxSpeed : FallbackSpeed;
        var flat = car.Velocity with { Z = 0f };
        var wanted = pushing ? Vector3.Normalize(move) * top * amount : Vector3.Zero;
        var change = (wanted - flat) * (1f - MathF.Exp(-World.Delta / ResponseSeconds));
        if (change.LengthSquared() < 1f)
        {
            return;
        }

        body.WakeRigidBody(FName.None);
        body.SetRBLinearVelocity(change, true);
    }

    // The winch aims from a socket called WinchOrigin and fires from WinchAttach. Without them
    // every target is measured from the middle of the world. Both go just ahead of the front
    // wheels, on the root bone.
    static void AddWinchSockets(Fitted fitted, SkeletalMeshComponent body)
    {
        if (body.SkeletalMesh is not { IsValid: true } mesh)
        {
            return;
        }

        var missing = WinchSockets.Where(name => body.GetSocketByName(name) is not { IsValid: true }).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var car = fitted.Car;
        var (forward, up) = Axes(car.Rotation);

        // The two wheels furthest forward give the front axle.
        var wheels = new List<Vector3>();
        foreach (var wheel in car.Wheels)
        {
            if (wheel is { IsValid: true } && wheel.BoneName.Index != 0 && body.MatchRefBone(wheel.BoneName) >= 0)
            {
                wheels.Add(body.GetBoneLocation(wheel.BoneName, 0));
            }
        }

        Vector3 front;
        if (wheels.Count >= 2)
        {
            wheels.Sort((a, b) => Vector3.Dot(b, forward).CompareTo(Vector3.Dot(a, forward)));
            front = (wheels[0] + wheels[1]) / 2f;
        }
        else
        {
            front = car.Location + forward * MathF.Max(car.PursuitModeLength / 2f, 200f);
        }

        // A socket's place is measured along its bone's own axes.
        var root = body.GetBoneName(RootBone);
        var offset = front + forward * WinchAhead + up * WinchUp - body.GetBoneLocation(root, 0);
        var local = new Vector3(
            Vector3.Dot(offset, body.GetBoneAxis(root, GameObject.EAxis.AXIS_X)),
            Vector3.Dot(offset, body.GetBoneAxis(root, GameObject.EAxis.AXIS_Y)),
            Vector3.Dot(offset, body.GetBoneAxis(root, GameObject.EAxis.AXIS_Z)));

        fitted.Mesh = mesh;
        foreach (var name in missing)
        {
            var socket = Made.Tracked(new SkeletalMeshSocket(mesh));
            socket.SocketName = name;
            socket.BoneName = root;
            socket.RelativeLocation = local;
            socket.RelativeRotation = default;
            socket.RelativeScale = Vector3.One;
            mesh.Sockets.Add(socket);
            fitted.Sockets.Add(socket);
        }
    }

    // Which way the car's nose and roof point in the world.
    static (Vector3 Forward, Vector3 Up) Axes(Rotator turn)
    {
        const float toRadians = MathF.PI / 180f;
        var (sinPitch, cosPitch) = MathF.SinCos(turn.Pitch * toRadians);
        var (sinYaw, cosYaw) = MathF.SinCos(turn.Yaw * toRadians);
        var (sinRoll, cosRoll) = MathF.SinCos(turn.Roll * toRadians);

        var forward = new Vector3(cosPitch * cosYaw, cosPitch * sinYaw, sinPitch);
        var up = new Vector3(
            -(cosRoll * sinPitch * cosYaw + sinRoll * sinYaw),
            cosYaw * sinRoll - cosRoll * sinPitch * sinYaw,
            cosRoll * cosPitch);
        return (forward, up);
    }

    // Everything the car was given comes off again, in the opposite order to how it went on.
    static void Remove()
    {
        if (_fitted is not { } fitted)
        {
            return;
        }

        var car = fitted.Car;
        if (car.IsValid && Same(car.TransformationAnimNode, fitted.Node))
        {
            // Out of battle mode first, while the car still has what battle mode needs. The game
            // is asked, and if it won't, the state is switched the way its winch puzzles do it.
            if (InBattle(car))
            {
                car.bTransforming = false;
                if (!car.TryToChangeMode(PursuitState))
                {
                    car.GotoState(PursuitState, FName.None, false, false);
                }
            }

            car.TransformationAnimNode = null!;
            if (Car.Sim(car) is { } sim && Same(sim.TransformationAnimNode, fitted.Node))
            {
                sim.TransformationAnimNode = null!;
            }

            foreach (var (wheel, original, ours) in fitted.Wheels)
            {
                if (wheel.IsValid && Same(wheel.WheelControl, ours))
                {
                    wheel.WheelControl = original!;
                }
            }
        }

        Forget(fitted);
    }

    // The sockets sit on the skin's mesh, which outlives the car, so they are taken off whenever
    // a fitted car is let go.
    static void Forget(Fitted fitted)
    {
        _fitted = null;

        foreach (var (_, _, held) in Suspension)
        {
            held.Release();
        }

        ReleaseGrip();
        if (fitted.Mesh is not { IsValid: true } mesh)
        {
            return;
        }

        // TArray.Remove compares wrappers, and a socket made here isn't the wrapper the array
        // hands back, so it would find nothing. Matched by pointer instead.
        var sockets = mesh.Sockets;
        for (var i = sockets.Count - 1; i >= 0; i--)
        {
            var entry = sockets[i];
            if (fitted.Sockets.Exists(socket => Same(socket, entry)))
            {
                sockets.RemoveAt(i);
            }
        }
    }
}
