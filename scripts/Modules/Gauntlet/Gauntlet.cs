#nullable enable

using System.Numerics;
using System.Runtime.CompilerServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Batman raises his gauntlet the way he does for a call, and the menu is drawn on the game's own
// hologram. In the Batmobile the hologram shows over the dashboard instead, as calls do there. The
// game does this in its radio state, which needs a mission script behind it. Here only the pieces
// are used: the call stance or the cockpit, the call camera and the projector with no video.
static class Gauntlet
{
    enum Where
    {
        Nowhere,
        Arm,
        Cockpit,
    }

    static readonly FName CallStance = new("RadioHologram");
    static readonly FName Standing = new("Standing");
    static readonly FName Outgoing = new("OutgoingCall");
    static readonly FName CallCamera = new("GauntletConversation");
    static readonly FName Walking = new("WalkingMode");
    static readonly FName Driving = new("DrivingMode");

    // Short moves that end with Batman back on his feet, like climbing out of the car.
    static readonly FName[] Passing = { new("SpecialMove"), new("PairedAnimation"), new("ControlledMoveTo") };

    // Batman has to be back on his feet this long before the arm goes up again, so a hop or a
    // stumble doesn't throw it up and down.
    const float SettleSeconds = 0.25f;

    // Leaving the car counts as passing for this long, as it can end in a short fall. Passing
    // holds the menu back for at most the second figure before it opens on the screen.
    const float LeftCarSeconds = 1.5f;
    const float MostWaitSeconds = 1.5f;

    static Where _where;
    static RPawnPlayer? _pawn;
    static RVehicleBatmobileBase? _car;
    static RPlayerController? _controller;
    static FName _cameraBefore;
    static float _settled;
    static bool _hadCar;
    static float _leftCarAt = float.MinValue;
    static float _waited;
    static bool _passing;

    // Batman could raise the gauntlet as of the last game tick.
    static bool _able;

    // The menu belongs on the gauntlet right now. It may still be on its way up. It is read in
    // the draw callback, which sees the menu open before the next game tick does, so it can't
    // wait for the tick to say the menu is wanted.
    public static bool Hosting => _able && Menu.Showing && Look.OnGauntlet;

    // Batman can't raise it yet but soon will, so the menu shows nowhere for that moment rather
    // than opening on the screen and jumping to the gauntlet.
    public static bool Waiting => _passing && Menu.Showing && Look.OnGauntlet;

    static bool Up => _where != Where.Nowhere;

    public static void Tick()
    {
        var want = Menu.Showing && Look.OnGauntlet;
        var controller = World.Controller;
        var pawn = controller?.CombatPawn;
        var car = World.Driving;

        // On foot anything that takes Batman out of walking ends it, as it ends a call in the
        // game. In the car it works while driving it yourself, not by remote or in battle mode.
        // A call the game is showing keeps the screen, and the menu opens flat until it is over.
        // Our own screen still fading out after the menu closed is taken straight back.
        var projector = controller?.GauntletProjector;
        var where = WhereNow(controller, projector, pawn, car);
        var able = where != Where.Nowhere;

        // Our texture stays on the screen while it fades out, and comes off once it is off or a
        // game call has it.
        if (GauntletScreen.Active && (projector is not { IsValid: true } || !projector.ScreenOn || !Ours(projector)))
        {
            GauntletScreen.Give();
        }

        GauntletScreen.Refresh();

        var moved = (where == Where.Arm && !ReferenceEquals(pawn, _pawn)) || (where == Where.Cockpit && !ReferenceEquals(car, _car));
        if (Up && (!want || where != _where || moved))
        {
            Lower();
        }

        _settled = able ? _settled + World.Delta : 0f;
        if (want && able && !Up && _settled >= SettleSeconds)
        {
            Raise(controller!, where, pawn, car);
        }

        if (_where == Where.Cockpit)
        {
            Cockpit.Tick(controller!);
        }

        _able = able;

        if (_hadCar && car is null)
        {
            _leftCarAt = World.Now;
        }

        _hadCar = car is not null;
        var passing = !able && car is null && controller is not null &&
            (World.Now - _leftCarAt < LeftCarSeconds || InPassing(controller));
        _waited = passing ? _waited + World.Delta : 0f;
        _passing = passing && _waited < MostWaitSeconds;
    }

    static Where WhereNow(RPlayerController? controller, RGauntletProjector? projector, RPawnPlayer? pawn, RVehicleBatmobileBase? car)
    {
        if (controller is null || projector is not { IsValid: true } || (projector.ScreenOn && !Ours(projector)))
        {
            return Where.Nowhere;
        }

        var state = controller.GetStateName().Index;
        if (car is null)
        {
            return pawn is { IsValid: true } && state == Walking.Index ? Where.Arm : Where.Nowhere;
        }

        return car is { IsValid: true, bRemoteDrive: false } && state == Driving.Index && !car.IsInBattleMode() ? Where.Cockpit : Where.Nowhere;
    }

    static bool InPassing(RPlayerController controller)
    {
        var state = controller.GetStateName().Index;
        foreach (var name in Passing)
        {
            if (name.Index == state)
            {
                return true;
            }
        }

        return false;
    }

    public static void Release()
    {
        Lower();
        GauntletScreen.Release();
        _able = false;
        _passing = false;
    }

    static void Raise(RPlayerController controller, Where where, RPawnPlayer? pawn, RVehicleBatmobileBase? car)
    {
        _where = where;
        _pawn = pawn;
        _car = car;
        _controller = controller;

        // Tick only raises it while the screen is free. In the car the game puts the screen on
        // the dashboard and trims the HUD by itself.
        var projector = controller.GauntletProjector;
        projector.StartVideoProjection(GameText.Once(""), GameText.Once(""), bNoMovie: true);

        if (where == Where.Cockpit)
        {
            Cockpit.Enter(controller, car!);
        }
        else
        {
            RaiseArm(controller, pawn!);
        }

        // The menu is drawn onto the screen. If that can't be done, the screen would be a black
        // pane with no video, so it is hidden and the menu is drawn over it. The game shows it
        // again the next time it starts a call.
        if (!GauntletScreen.Take(projector))
        {
            projector.ScreenHologramMovieMesh?.SetHidden(true);
        }
    }

    static void RaiseArm(RPlayerController controller, RPawnPlayer pawn)
    {
        // A gadget in hand would hold its own pose over the call stance.
        (pawn.InvManager as RInventoryManager)?.UnequipAllGadgets();

        pawn.BeginChangePose();
        pawn.Pose.TransitionName = Outgoing;
        pawn.Pose.MovementStance = CallStance;
        pawn.Pose.Mirrored = false;
        pawn.Pose.TransitionMirrored = RAnimUtil.EMirrorChoice.MC_No;
        pawn.EndChangePose();
        pawn.SetWalkSpeed();

        _cameraBefore = controller.OverrideWalkCamera;
        controller.OverrideWalkCamera = CallCamera;
    }

    static void Lower()
    {
        var where = _where;
        var pawn = _pawn;
        var controller = _controller;
        _where = Where.Nowhere;
        _pawn = null;
        _car = null;
        _controller = null;

        if (where == Where.Cockpit)
        {
            Cockpit.Leave(controller);
        }

        if (pawn is { IsValid: true } && pawn.GetMovementStance().Index == CallStance.Index)
        {
            pawn.ChangeMovementStance(Standing);
            pawn.SetWalkSpeed();
        }

        if (controller is not { IsValid: true })
        {
            return;
        }

        if (where == Where.Arm && controller.OverrideWalkCamera.Index == CallCamera.Index)
        {
            controller.OverrideWalkCamera = _cameraBefore;
        }

        // Only our projection is stopped. A real call that took over the screen plays on.
        var projector = controller.GauntletProjector;
        if (projector is { IsValid: true } && projector.ScreenOn && Ours(projector))
        {
            projector.StopProjection(null!, GameText.Once(""), -1f);
        }
    }

    // A game call always names its video. Ours has none, which the game turns into ".usm".
    static bool Ours(RGauntletProjector projector) => projector.lastPlayedVideoResource.ToString() is null or "" or ".usm";

    // While walking the game asks this every tick and changes the stance to the answer.
    [Redirect(typeof(RPawnPlayer), nameof(RPawnPlayer.GetWalkingModeMovementStance))]
    static FName WalkingStance(RPawnPlayer self)
    {
        try
        {
            return _where == Where.Arm && ReferenceEquals(self, _pawn) ? CallStance : self.GetWalkingModeMovementStance();
        }
        catch (Exception error)
        {
            Log.Error("gauntlet.stance", "The walking stance check failed", error);
            return Standing;
        }
    }

    // Lays a layout this many units wide onto the gauntlet's screen. Height is how tall the
    // layout is at that width, and shown is how far the game's screen has come on. False when
    // there is nothing to draw on, and nothing is pushed then. End the hologram after drawing.
    public static bool Place(Canvas canvas, float width, out float height, out float shown)
    {
        height = 0f;
        shown = 0f;

        // When the menu is drawn on the screen itself there is nothing to draw over it.
        if (GauntletScreen.Active || !Up || World.Player is not { IsValid: true } pawn || _controller is not { IsValid: true } controller ||
            controller.GauntletProjector is not { IsValid: true, ScreenOn: true } projector || !Ours(projector) ||
            projector.ScreenHologramMovieMesh is not { IsValid: true } screen)
        {
            return false;
        }

        controller.GetPlayerViewPoint(out var eye, out var turn);
        var fieldOfView = controller.PlayerCamera is { IsValid: true } camera ? camera.GetFOVAngle() : controller.GetFOVAngle();
        Hologram.Frame(canvas, eye, turn, fieldOfView);

        if (!Surface(screen, pawn, width, out var panel, out height) || !Hologram.Place(panel, width, height))
        {
            return false;
        }

        // It comes on with the game's own screen, which eases in over two seconds.
        shown = projector.transState == RGauntletProjector.transitionState.eTransIn ? projector.transProportion : 1f;
        return true;
    }

    // The game's video screen is a flat model on the wrist or the dashboard. Its two long sides give the panel the
    // layout is laid onto, so it covers the screen exactly.
    static bool Surface(StaticMeshComponent screen, RPawnPlayer pawn, float width, out Hologram.Panel panel, out float height)
    {
        panel = default;
        height = 0f;
        if (screen.StaticMesh is not { IsValid: true } model)
        {
            return false;
        }

        var bounds = pawn.GetStaticMeshBounds(model);
        var place = Unsafe.As<GameObject.FMatrix, Matrix4x4>(ref screen.LocalToWorld);
        var middle = Vector3.Transform(bounds.Origin, place);
        Span<Vector3> sides = stackalloc Vector3[]
        {
            new Vector3(place.M11, place.M12, place.M13) * bounds.BoxExtent.X,
            new Vector3(place.M21, place.M22, place.M23) * bounds.BoxExtent.Y,
            new Vector3(place.M31, place.M32, place.M33) * bounds.BoxExtent.Z,
        };

        // The thinnest side is the screen's depth. Of the other two the longer runs across.
        var thin = 0;
        for (var i = 1; i < 3; i++)
        {
            if (sides[i].LengthSquared() < sides[thin].LengthSquared())
            {
                thin = i;
            }
        }

        var across = sides[(thin + 1) % 3];
        var down = sides[(thin + 2) % 3];
        if (down.LengthSquared() > across.LengthSquared())
        {
            (across, down) = (down, across);
        }

        if (down.Z > 0f)
        {
            down = -down;
        }

        if (across.Length() < 1f || down.Length() < 0.5f)
        {
            return false;
        }

        height = width * down.Length() / across.Length();
        panel = new Hologram.Panel(middle - across, across * 2f / width, down * 2f / height);
        return true;
    }
}
