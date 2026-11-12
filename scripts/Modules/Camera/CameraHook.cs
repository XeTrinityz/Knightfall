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
        CameraView.Fit(ref view, controller, deltaTime);
        Villains.Fit(ref view, deltaTime);
        FirstPerson.Fit(ref view, controller, deltaTime);
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
