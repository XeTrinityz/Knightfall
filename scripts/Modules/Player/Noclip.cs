#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > MOVEMENT > NOCLIP. Batman floats where the view points and goes through walls, and
// keeps his pose. His own move and button input are held the way FREE CAMERA holds them, so he
// can't start a move while he flies.
static class Noclip
{
    const float Metre = 100f;
    const float FastFactor = 4f;
    const float Glide = 8f;
    const float LongestStep = 0.1f;

    // How far Batman is looked for below and above him when the switch goes off.
    const float FloorDown = 3000f;
    const float FloorUp = 50f;

    static readonly Held<RPawnPlayer, bool> CollideWorld = new("noclip world", pawn => pawn.bCollideWorld, (pawn, on) => pawn.bCollideWorld = on);
    // Actor collision stays on, so he still touches the level volumes the city streams by. Only
    // being blocked by other actors is turned off.
    static readonly Held<RPawnPlayer, bool> BlockActors = new("noclip blocking", pawn => pawn.bBlockActors, (pawn, on) => pawn.bBlockActors = on);
    static readonly Held<RPawnPlayer, Actor.EPhysics> Physics = new("noclip physics", pawn => pawn.Physics, (pawn, physics) => pawn.SetPhysics(physics));

    // The game's own ghost cheat sets this on the movement driver. Without it the driver puts the
    // body back on walking or falling every frame, and walking holds him to the floor.
    static readonly Held<RAnimNode_Pose, bool> Cheats = new("noclip movement", pose => pose.MovementOutput.CheatsEnabled, (pose, on) => pose.MovementOutput.CheatsEnabled = on);

    static readonly Keys[] FlyKeys = { Keys.Space, Keys.ControlKey, Keys.ShiftKey };
    static readonly Pad.Button[] FlyButtons =
    {
        Pad.Button.A, Pad.Button.B, Pad.Button.X, Pad.Button.Y, Pad.Button.LB, Pad.Button.RB, Pad.Button.LT, Pad.Button.RT,
        Pad.Button.LeftStick, Pad.Button.RightStick, Pad.Button.DPadUp, Pad.Button.DPadDown, Pad.Button.DPadLeft, Pad.Button.DPadRight,
    };

    static bool _active;
    static bool _keysTaken;
    static bool _buttonsTaken;
    static RPlayerController? _inputOffFor;
    static Vector3 _velocity;

    public static readonly Toggle Clip = new(
        "player.movement.noclip", "NOCLIP",
        "Batman floats where you look and goes through walls and floors. He keeps his pose. WASD or the left stick move, Space and Ctrl or RT and LT rise and drop, Shift or a left stick click goes faster. Turning it off drops him to the nearest floor below.",
        saved: false)
    {
        Block = Blocked,
        Tick = Fly,
        Restore = Stop,
    };

    public static readonly Slider Speed = new(
        "player.movement.noclip_speed", "NOCLIP SPEED",
        "How fast Batman flies with NOCLIP on.",
        min: 1, max: 60, step: 1, value: 10, unit: " M/S")
    {
        Preference = true,
    };

    static string? Blocked()
    {
        if (Car.InCar() is { } inCar)
        {
            return inCar;
        }

        return World.Controller is { } controller && InScene(controller) ? "NOT DURING SCENES" : null;
    }

    // Only the scene signs. Switcher.InScene also counts ignored move input, and this switch sets
    // that itself, so it would lock the switch on the tick after it turns on.
    static bool InScene(RPlayerController controller) =>
        controller.bCinematicMode || controller.bInBink || controller.ControllingDirTrackInst is not null
        || controller.ActiveCinematicMode is { IsValid: true, bHidePlayer: true } or { IsValid: true, bDisableMoveInput: true };

    static void Fly()
    {
        if (Blocked() is not null || World.Player is not { IsValid: true } pawn || World.Controller is not { IsValid: true } controller)
        {
            Let();
            return;
        }

        _active = true;
        CollideWorld.Hold(pawn, false);
        BlockActors.Hold(pawn, false);
        Physics.Hold(pawn, Actor.EPhysics.PHYS_Flying);
        if (pawn.Anim is { IsValid: true } pose)
        {
            Cheats.Hold(pose, true);
        }

        // Flying physics would add its own drift on top of the moves made here.
        pawn.Velocity = Vector3.Zero;

        if (!ReferenceEquals(_inputOffFor, controller))
        {
            GiveBackInput();
            controller.IgnoreMoveInput(true);
            controller.IgnoreButtonInput(true);
            _inputOffFor = controller;
        }

        if (!_keysTaken)
        {
            foreach (var key in FlyKeys)
            {
                Swallowed.Take(key);
            }

            _keysTaken = true;
        }

        if (!_buttonsTaken)
        {
            foreach (var button in FlyButtons)
            {
                Swallowed.Take(button);
            }

            _buttonsTaken = true;
        }

        var seconds = MathF.Min(World.Delta, LongestStep);
        var menu = Menu.Showing;

        var look = controller.Rotation;
        var forward = new Rotator(look.Pitch, look.Yaw, 0f).ToDirection();
        var flat = new Rotator(0f, look.Yaw, 0f).ToDirection();
        var right = new Vector3(-flat.Y, flat.X, 0f);

        var input = controller.PlayerInput;
        var ahead = (input?.RawJoyUp ?? 0f) + Axis(Keys.W, Keys.S);
        var side = (input?.RawJoyRight ?? 0f) + Axis(Keys.D, Keys.A);
        var rise = Axis(Keys.Space, Keys.ControlKey) + PadAxis(Pad.Button.RT, Pad.Button.LT);

        var move = forward * Math.Clamp(ahead, -1f, 1f) + right * Math.Clamp(side, -1f, 1f) + Vector3.UnitZ * Math.Clamp(rise, -1f, 1f);
        if (move.LengthSquared() > 1f)
        {
            move = Vector3.Normalize(move);
        }

        if (menu)
        {
            move = Vector3.Zero;
        }

        var fast = Keyboard.IsDown(Keys.ShiftKey) || (!menu && Pad.IsDown(Pad.Button.LeftStick)) ? FastFactor : 1f;
        var wanted = move * (float)Speed.Value * Metre * fast;
        _velocity += (wanted - _velocity) * (1f - MathF.Exp(-Glide * seconds));

        // Read from the pawn each tick, so a teleport or a respawn is followed rather than undone.
        pawn.SetLocation(pawn.Location + _velocity * seconds);
    }

    // Puts everything back and drops Batman to the floor below. With none in reach he falls.
    static void Stop()
    {
        Let();
        _velocity = Vector3.Zero;

        if (World.Player is not { IsValid: true } pawn)
        {
            return;
        }

        var at = pawn.Location;
        if (Teleport.Floor(pawn, at, at.Z + FloorUp, at.Z - FloorDown, out var floor))
        {
            Teleport.Hop(pawn, floor, pawn.Rotation, true);
        }
        else
        {
            pawn.SetPhysics(Actor.EPhysics.PHYS_Falling);
        }
    }

    // Gives back the holds and input. Runs when the switch is off, and when a lock stops it while
    // it is on, so it has to be safe to run again.
    static void Let()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        CollideWorld.Release();
        BlockActors.Release();
        Physics.Release();
        Cheats.Release();

        GiveBackInput();

        if (_keysTaken)
        {
            foreach (var key in FlyKeys)
            {
                Swallowed.Give(key);
            }

            _keysTaken = false;
        }

        if (_buttonsTaken)
        {
            foreach (var button in FlyButtons)
            {
                Swallowed.Give(button);
            }

            _buttonsTaken = false;
        }
    }

    // Both switches are counts, so each is given back once.
    static void GiveBackInput()
    {
        if (_inputOffFor is { IsValid: true } controller)
        {
            controller.IgnoreMoveInput(false);
            controller.IgnoreButtonInput(false);
        }

        _inputOffFor = null;
    }

    static float Axis(Keys plus, Keys minus) => (Keyboard.IsDown(plus) ? 1f : 0f) - (Keyboard.IsDown(minus) ? 1f : 0f);

    static float PadAxis(Pad.Button plus, Pad.Button minus) => (Pad.IsDown(plus) ? 1f : 0f) - (Pad.IsDown(minus) ? 1f : 0f);
}
