#nullable enable

using System.Numerics;
using System.Runtime.CompilerServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// In the Batmobile the game shows its hologram over the dashboard, and a call moves the camera
// into the cockpit and holds the car still. This does the same for the menu. The game's version
// lives in its radio state, which needs a mission script behind it, so the pieces are done here.
static class Cockpit
{
    static readonly FName ChassisBone = new("Batmobile_Chassis_Mesh");
    static readonly FName Hold = new("InCar_GauntletConversationCam");
    const string CameraAnims = "Anim_Batman_Batmobile.Code.Camera";

    // The game's own figures for a call in the car, except the way in. A call swoops in with a
    // set move that can't be steered. The menu blends straight to the shot, so the view can be
    // turned at once.
    const float FieldOfView = 50f;
    const float BlendInSeconds = 1.25f;
    const float BlendOutSeconds = 0.5f;

    // How hard the car is slowed, in units a second each second, and when it counts as stopped.
    const float Braking = 3000f;
    const float StoppedSpeed = 60f;

    static RVehicleBatmobileBase? _car;
    static RInGameCinematicCam? _camera;
    static AnimSet? _anims;
    static bool _mirrored;
    static bool _holding;
    static float _startYaw;
    static bool _closeBefore;
    static bool _inputHeld;

    // Moves the camera into the cockpit and takes the car's controls from the player.
    public static void Enter(RPlayerController controller, RVehicleBatmobileBase car)
    {
        _car = car;

        // The menu keys are already the player's. This stops the car being driven blind.
        controller.IgnoreMoveInput(true);
        _inputHeld = true;
        car.EmergencyBrake();

        if (controller.CinematicCamera is not { IsValid: true } camera || controller.PlayerCamera is not R3rdPersonCamera { IsValid: true } view)
        {
            return;
        }

        _anims ??= Game.FindObject<AnimSet>(CameraAnims);
        if (_anims is not { IsValid: true })
        {
            Log.Warn("cockpit.anims", "The Batmobile's call camera wasn't found, so the camera stays where it is.");
            return;
        }

        Follow(camera, car);
        camera.FOVAngle = FieldOfView;
        camera.bUseAspectRatioCorretion = false;
        camera.bExitPlayingWhenFinished = false;

        // The camera comes in from whichever side of the car it was on.
        controller.GetPlayerViewPoint(out var eye, out _);
        var place = Unsafe.As<GameObject.FMatrix, Matrix4x4>(ref car.Mesh.LocalToWorld);
        var right = new Vector3(place.M21, place.M22, place.M23);
        _mirrored = Vector3.Dot(right, eye - car.Location) < 0f;

        HoldShot(camera, controller);

        // The camera hides whatever comes close to the lens. On the way in the car itself is that
        // close, and it would vanish, so nothing is hidden in the cockpit.
        _closeBefore = view.DontDrawClosePawns;
        view.DontDrawClosePawns = false;
        view.BlendToCameraTarget(camera, BlendInSeconds);
        controller.SetRainClippingDistForCockpitView(true);
        _camera = camera;
    }

    // The held shot is one turn round the seat. Turning the view moves the camera along it, from
    // where the view pointed when the shot began.
    static void HoldShot(RInGameCinematicCam camera, RPlayerController controller)
    {
        camera.StaticAnim(Hold, _mirrored, _anims!);
        _startYaw = controller.Rotation.Yaw;
        _holding = true;
    }

    static void Orbit(RInGameCinematicCam camera, RPlayerController controller)
    {
        var turns = (controller.Rotation.Yaw - _startYaw) / 360f;
        var along = turns - MathF.Floor(turns);

        // Unmirrored the shot runs the other way round.
        if (!_mirrored && along != 0f)
        {
            along = 1f - along;
        }

        camera.SetNormalizedTime(along);
    }

    public static void Tick(RPlayerController controller)
    {
        if (_car is not { IsValid: true } car)
        {
            return;
        }

        Brake(car);
        if (_camera is not { IsValid: true } camera)
        {
            return;
        }

        Follow(camera, car);

        if (_holding)
        {
            Orbit(camera, controller);
        }
    }

    // Gives the camera and the controls back.
    public static void Leave(RPlayerController? controller)
    {
        var camera = _camera;
        _camera = null;
        _car = null;
        _holding = false;

        if (controller is not { IsValid: true })
        {
            _inputHeld = false;
            return;
        }

        if (_inputHeld)
        {
            controller.IgnoreMoveInput(false);
            _inputHeld = false;
        }

        if (camera is { IsValid: true })
        {
            camera.Finished();
            if (controller.PlayerCamera is R3rdPersonCamera { IsValid: true } view)
            {
                if (view.GetTargetActor() is { } target && target.Ptr == camera.Ptr)
                {
                    view.BlendToCameraTarget(controller, BlendOutSeconds, true);
                }

                view.DontDrawClosePawns = _closeBefore;
            }

            controller.SetRainClippingDistForCockpitView(false);
        }
    }

    // The shot is authored round the car's chassis and moves with it.
    static void Follow(RInGameCinematicCam camera, RVehicleBatmobileBase car)
    {
        camera.RootLocation = car.Mesh.GetBoneLocation(ChassisBone, 0);
        camera.RootRotation = car.Rotation;
    }

    // Slows the car to a stop along the ground.
    static void Brake(RVehicleBatmobileBase car)
    {
        if (car.CollisionComponent is not { IsValid: true } body)
        {
            return;
        }

        var flat = new Vector3(car.Velocity.X, car.Velocity.Y, 0f);
        var speed = flat.Length();
        if (speed < StoppedSpeed)
        {
            return;
        }

        body.SetRBLinearVelocity(-flat / speed * MathF.Min(Braking * World.Delta, speed), true);
    }
}
