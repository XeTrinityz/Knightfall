#nullable enable

using BmSDK;
using BmSDK.BmGame;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// The one hook on the game's camera, shared by everything that moves the view. The view the game
// worked out is put back before it works out the next one, so its smoothing never builds on ours.
static class CameraHook
{
    static nint _viewOf;
    static View _gameView;
    static View _ourView;

    [Redirect(typeof(R3rdPersonCamera), nameof(R3rdPersonCamera.UpdateCamera))]
    static void UpdateCamera(R3rdPersonCamera self, float deltaTime)
    {
        try
        {
            PutBack(self);
            CameraView.Before(self);
            Villains.BeforeCamera();
        }
        catch (Exception error)
        {
            Log.Error("camera", "Camera hook failed", error);
        }

        Redirects.RunOriginal();
        Disguise.CatchUp();

        try
        {
            Firefly.Follow();
            MrFreeze.Follow();
            Fit(self, deltaTime);
        }
        catch (Exception error)
        {
            Log.Error("camera", "Camera hook failed", error);
        }
    }

    static void PutBack(R3rdPersonCamera camera)
    {
        if (_viewOf != camera.Ptr)
        {
            return;
        }

        ref var cache = ref camera.CameraCache;
        if (Same(cache.POV, _ourView))
        {
            cache.POV = _gameView;
        }

        _viewOf = 0;
    }

    static void Fit(R3rdPersonCamera camera, float deltaTime)
    {
        if (World.Controller is not { } controller || camera.PCOwner is not { } owner || owner.Ptr != controller.Ptr)
        {
            return;
        }

        ref var cache = ref camera.CameraCache;
        var game = cache.POV;
        var view = game;
        DisguiseFit.Fit(ref view, controller, deltaTime);
        CameraView.Fit(ref view, controller, deltaTime, FreeCamera.HasView || FirstPerson.Replaces || Eyes.HasView);
        Villains.Fit(ref view, deltaTime);
        FirstPerson.Fit(ref view, controller, deltaTime);
        Eyes.Fit(ref view, camera, controller);
        FreeCamera.Fit(ref view, controller);
        if (Same(view, game))
        {
            return;
        }

        cache.POV = view;
        _gameView = game;
        _ourView = cache.POV;
        _viewOf = camera.Ptr;
    }

    // Turns the view the game steers Batman by, and not the one drawn, which is made again before
    // it shows. Called in the game tick, before the player moves. The game's own view still goes
    // back before its next update.
    public static void Steer(float yaw)
    {
        if (World.Controller?.PlayerCamera is not R3rdPersonCamera { IsValid: true } camera)
        {
            return;
        }

        ref var cache = ref camera.CameraCache;
        if (_viewOf != camera.Ptr)
        {
            _gameView = cache.POV;
            _viewOf = camera.Ptr;
        }
        else if (!Same(cache.POV, _ourView))
        {
            return;
        }

        var steered = cache.POV;
        steered.Rotation = new Rotator(steered.Rotation.Pitch, yaw, steered.Rotation.Roll);
        cache.POV = steered;
        _ourView = steered;
    }

    // An unload leaves the game its own view.
    public static void Release()
    {
        if (World.Controller?.PlayerCamera is R3rdPersonCamera { IsValid: true } camera)
        {
            PutBack(camera);
        }
    }

    static bool Same(in View a, in View b) =>
        a.Location == b.Location && a.Rotation == b.Rotation && a.FOV == b.FOV;
}
