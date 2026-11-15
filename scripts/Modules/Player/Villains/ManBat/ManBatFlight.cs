#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// His flight. The hidden Batman is put in the game's flying physics, the way the game's own ghost
// cheat does it, and moved here each tick toward where you steer, sliding along whatever he
// meets. Batman's own move and button input are held off, so nothing of his starts mid-air.
static partial class ManBat
{
    // How fast he flies, flat out and with the fast key held, and climbs, in units a second. How
    // quickly he gets up to speed, and slows when let go.
    const float FlySpeed = 1100f;
    const float FastSpeed = 2400f;
    const float ClimbSpeed = 700f;
    const float SpeedUp = 0.45f;
    const float SlowDown = 0.3f;

    // How fast he turns to face where the camera looks, in degrees a second.
    const float TurnRate = 400f;
    const float LongestStep = 0.1f;

    // How far below him the ground is looked for when he stops flying.
    const float GroundReach = 60f;

    static readonly Held<RPawnPlayer, Actor.EPhysics> Flying = new("man-bat flying", pawn => pawn.Physics, (pawn, physics) => pawn.SetPhysics(physics));

    // Without this the game's movement driver puts the body back on walking or falling every
    // frame, and walking holds him to the floor.
    static readonly Held<RAnimNode_Pose, bool> Driver = new("man-bat movement", pose => pose.MovementOutput.CheatsEnabled, (pose, on) => pose.MovementOutput.CheatsEnabled = on);

    static readonly Keys[] FlyKeys = { Keys.Space, Keys.ControlKey, Keys.ShiftKey };
    static readonly Pad.Button[] FlyButtons = { Pad.Button.A, Pad.Button.LT, Pad.Button.RT, Pad.Button.LeftStick };

    static bool _flying;
    static RPlayerController? _inputOffFor;
    static Vector3 _velocity;
    static float _speed;
    static float _yaw;

    // How much of his speed goes forward, sideways and up, for his lean and bank.
    static float _ahead;
    static float _sideways;
    static float _climb;

    static void Fly(RPawnPlayer player, RPlayerController controller)
    {
        if (!_flying)
        {
            _flying = true;
            _velocity = Vector3.Zero;
            _yaw = player.Rotation.Yaw;
            foreach (var key in FlyKeys)
            {
                Swallowed.Take(key);
            }

            foreach (var button in FlyButtons)
            {
                Swallowed.Take(button);
            }
        }

        Flying.Hold(player, Actor.EPhysics.PHYS_Flying);
        if (player.Anim is { IsValid: true } pose)
        {
            Driver.Hold(pose, true);
        }

        // Flying physics would add its own drift on top of the moves made here.
        player.Velocity = Vector3.Zero;
        if (!ReferenceEquals(_inputOffFor, controller))
        {
            GiveBackInput();
            controller.IgnoreMoveInput(true);
            controller.IgnoreButtonInput(true);
            _inputOffFor = controller;
        }

        // His moves steer him themselves.
        if (_act != Act.None)
        {
            return;
        }

        var seconds = MathF.Min(World.Delta, LongestStep);
        var look = controller.Rotation;
        var forward = new Rotator(look.Pitch, look.Yaw, 0f).ToDirection();
        var flat = new Rotator(0f, look.Yaw, 0f).ToDirection();
        var right = new Vector3(-flat.Y, flat.X, 0f);

        var menu = Menu.Showing;
        var input = controller.PlayerInput;
        var ahead = Math.Clamp((input?.RawJoyUp ?? 0f) + Axis(Keys.W, Keys.S), -1f, 1f);
        var side = Math.Clamp((input?.RawJoyRight ?? 0f) + Axis(Keys.D, Keys.A), -1f, 1f);
        var rise = Math.Clamp(Axis(Keys.Space, Keys.ControlKey) + PadAxis(Pad.Button.RT, Pad.Button.LT), -1f, 1f);
        var fast = Keyboard.IsDown(Keys.ShiftKey) || Pad.IsDown(Pad.Button.LeftStick);
        if (menu)
        {
            ahead = side = rise = 0f;
        }

        var steer = forward * ahead + right * side;
        if (steer.LengthSquared() > 1f)
        {
            steer = Vector3.Normalize(steer);
        }

        var wanted = steer * (fast ? FastSpeed : FlySpeed) + Vector3.UnitZ * (rise * ClimbSpeed);
        var easing = wanted.LengthSquared() > _velocity.LengthSquared() ? SpeedUp : SlowDown;
        _velocity += (wanted - _velocity) * Settle(seconds, easing);

        if (seconds > 0f)
        {
            var went = Glide(player, _velocity * seconds);

            // Flying into a wall takes the speed out of him that way.
            _velocity = Vector3.Lerp(_velocity, went / seconds, 0.5f);
        }

        // He turns to face where the camera looks.
        var turn = Wrap(look.Yaw - _yaw);
        var most = TurnRate * seconds;
        _yaw += Math.Clamp(turn, -most, most);
        Face(player);
        Gauge();
    }

    // How fast he goes, and how much of it is ahead of him and to his side, for his wing beats,
    // how far he is tipped and his bank.
    static void Gauge()
    {
        var flat = new Rotator(0f, _yaw, 0f).ToDirection();
        var right = new Vector3(-flat.Y, flat.X, 0f);
        _speed = _velocity.Length();
        var level = _velocity with { Z = 0f };
        _ahead = Math.Clamp(Vector3.Dot(level, flat) / FastSpeed, -0.5f, 1f);
        _sideways = Math.Clamp(Vector3.Dot(level, right) / FlySpeed, -1f, 1f);
        _climb = Math.Clamp(_velocity.Z / FlySpeed, -1f, 1f);
    }

    // Moves him the whole way if nothing is in it. Otherwise what is left is tried along each
    // axis on its own, so he slides along a wall or a roof rather than sticking. Returns how far
    // he really went.
    static Vector3 Glide(RPawnPlayer player, Vector3 way)
    {
        var from = player.Location;
        player.Move(way);
        var left = way - (player.Location - from);
        if (left.LengthSquared() > 1f)
        {
            player.Move(new Vector3(left.X, 0f, 0f));
            player.Move(new Vector3(0f, left.Y, 0f));
            player.Move(new Vector3(0f, 0f, left.Z));
        }

        return player.Location - from;
    }

    static void Face(RPawnPlayer player)
    {
        var facing = new Rotator(0f, _yaw, 0f);
        if (MathF.Abs(Wrap(facing.Yaw - player.Rotation.Yaw)) > 0.01f || player.Rotation.Pitch != 0f || player.Rotation.Roll != 0f)
        {
            player.SetRotation(facing);
        }

        player.DesiredRotation = facing;
    }

    // The game can still turn the pawn after the tick. The camera is worked out after that, so
    // his facing is put back from there, before anything is drawn.
    public static void KeepFacing()
    {
        if (_flying && Option.On && _body is { IsValid: true } && World.Player is { IsValid: true } player)
        {
            Face(player);
        }
    }

    // Lets Batman have his own physics and input back. Dropped in the air with nothing just
    // below, he falls, and can glide from there.
    static void Land(bool drop)
    {
        if (!_flying)
        {
            return;
        }

        _flying = false;
        _velocity = Vector3.Zero;
        _speed = 0f;
        _ahead = 0f;
        _sideways = 0f;
        _climb = 0f;
        Flying.Release();
        Driver.Release();
        GiveBackInput();
        foreach (var key in FlyKeys)
        {
            Swallowed.Give(key);
        }

        foreach (var button in FlyButtons)
        {
            Swallowed.Give(button);
        }

        if (drop && World.Player is { IsValid: true } player && player.Physics != Actor.EPhysics.PHYS_Falling
            && !Teleport.Floor(player, player.Location, player.Location.Z, player.Location.Z - player.CylinderComponent.CollisionHeight - GroundReach, out _))
        {
            player.SetPhysics(Actor.EPhysics.PHYS_Falling);
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
