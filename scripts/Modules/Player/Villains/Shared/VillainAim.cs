#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// A villain's weapon on Batman's gadget buttons. Holding aim raises it, and while it is up fire
// and secondary fire are taken from the game and given straight back on letting go. On the
// ground a quick press of aim is still the game's counter, so aiming there starts after a short
// hold, and only with the hands free of Batman's own moves.
sealed class VillainAim
{
    const float Delay = 0.2f;

    // How fast the villain turns to the crosshair.
    const float TurnRate = 900f;

    const float Ease = 0.12f;

    // A crosshair spot closer than this ahead of a gun can't be aimed at sensibly, so the gun
    // aims down the line of view instead.
    const float NearestAim = 250f;

    // How far above and below the crosshair the ground is looked for.
    const float GroundAbove = 100f;
    const float GroundBelow = 1500f;

    // The game's own names for the fire and secondary fire buttons.
    static readonly string[] FireButtons = { "LeftMouseButton", "MiddleMouseButton" };
    static readonly Pad.Button[] FirePads = { Pad.Button.RT, Pad.Button.RB };

    // The camera eases in over the right shoulder and narrows, like the game's own aim. Back, side
    // and up are from the head, zoom is a share of the field of view.
    public float Back { get; init; } = 330f;

    public float Side { get; init; } = 75f;

    public float Up { get; init; } = 45f;

    public float Zoom { get; init; } = 0.85f;

    // How far the crosshair is looked for. A weapon that reaches further than this needs it
    // raised to match, or past it the crosshair stops in mid air short of what it is on.
    public float Reach { get; init; } = 6000f;

    // A further zoom a villain can change as it goes, like a scope.
    public float Scope { get; set; } = 1f;

    float _since = -1f;
    bool _taken;
    float _shown;

    // The way the villain was turned this tick while aiming on the ground.
    float? _facing;

    public bool On { get; private set; }

    public bool Fire { get; private set; }

    public bool Secondary { get; private set; }

    // Where the crosshair lands, or as far as it reaches.
    public Vector3 Target { get; private set; }

    // The camera's place and the way it looks, this tick.
    Vector3 _eye;
    Vector3 _look;

    // Where the camera was drawn last frame, and the way it looked.
    Vector3 _drawnEye;
    Vector3 _drawnLook;
    bool _drawn;

    public Vector3 Eye => _eye;

    public Vector3 Look => _look;

    // Where a gun at this spot should point. The crosshair's spot, unless that is too close in
    // front of the gun or behind it, like a wall right in front of him.
    public Vector3 PointFor(Vector3 gun) =>
        Vector3.Dot(Target - gun, _look) >= NearestAim ? Target : _eye + _look * Reach;

    // Airborne, aim is up at once and the flight does the turning.
    public void Update(RPawnPlayer player, RPlayerController controller, bool airborne)
    {
        var now = World.Now;
        var held = !Menu.Showing && (Keyboard.IsDown(Keys.RButton) || Pad.IsDown(Pad.Button.LT));
        if (!held)
        {
            _since = -1f;
        }
        else if (_since < 0f)
        {
            _since = now;
        }

        var free = airborne || (player.Physics == Actor.EPhysics.PHYS_Walking
            && player.CurrentSpecialMove is not { IsValid: true } && player.CombatMove is not { IsValid: true });
        On = held && free && (airborne || now - _since >= Delay);
        Take(On);

        Fire = On && (Keyboard.IsDown(Keys.LButton) || Pad.IsDown(Pad.Button.RT));
        Secondary = On && (Keyboard.IsDown(Keys.MButton) || Pad.IsDown(Pad.Button.RB));

        controller.GetPlayerViewPoint(out var eye, out var view);
        var way = view.ToDirection();

        // The game's view point is from before the aim camera moved it over the shoulder, so the
        // aim runs from where the camera was drawn last, as the crosshair does.
        if (_drawn && _shown > 0.001f)
        {
            eye = _drawnEye;
            way = _drawnLook;
        }

        _eye = eye;
        _look = way;
        var end = eye + way * Reach;
        Target = Blink.Aim(player, eye, end, out var spot, out _) ? spot : end;

        _facing = null;
        if (On && !airborne)
        {
            Turn(player, way, World.Delta, TurnRate);
            _facing = player.Rotation.Yaw;
        }
    }

    // The game can still turn the villain after the tick, toward where he walks or toward a
    // thug in a fight. The camera is worked out after it has, so the aim's facing is put back
    // from there, before anything is drawn.
    public void Hold()
    {
        if (_facing is not { } yaw || !On || World.Player is not { IsValid: true } player)
        {
            return;
        }

        var off = yaw - player.Rotation.Yaw;
        off -= 360f * MathF.Round(off / 360f);
        if (MathF.Abs(off) > 0.01f)
        {
            Face(player, new Rotator(0f, yaw, 0f));
        }
    }

    // The ground under the crosshair, brought in to no further than reach from the villain.
    // False when there is no ground below it.
    public bool Ground(RPawnPlayer player, float reach, out Vector3 at)
    {
        var target = Target;
        var off = target - player.Location;
        if (off.Length() > reach)
        {
            target = player.Location + Vector3.Normalize(off) * reach;
        }

        return Blink.Aim(player, target + Vector3.UnitZ * GroundAbove, target - Vector3.UnitZ * GroundBelow, out at, out _);
    }

    // Turns the body flat toward a way, no faster than the rate in degrees a second.
    public static void Turn(RPawnPlayer player, Vector3 way, float delta, float rate)
    {
        if (way.X * way.X + way.Y * way.Y < 1e-6f)
        {
            return;
        }

        var facing = player.Rotation;
        var turn = MathF.Atan2(way.Y, way.X) * (180f / MathF.PI) - facing.Yaw;
        turn -= 360f * MathF.Round(turn / 360f);
        var most = rate * delta;
        Face(player, new Rotator(0f, facing.Yaw + Math.Clamp(turn, -most, most), 0f));
    }

    // The game turns the pawn toward the way it wants each tick, so that is set too.
    static void Face(RPawnPlayer player, Rotator facing)
    {
        player.SetRotation(facing);
        player.DesiredRotation = facing;
    }

    public void View(ref View view, float deltaTime)
    {
        _shown += ((On ? 1f : 0f) - _shown) * (1f - MathF.Exp(-deltaTime / Ease));
        if (_shown < 0.001f || World.Player is not { IsValid: true } player)
        {
            _shown = On ? _shown : 0f;
            return;
        }

        var ahead = view.Rotation.ToDirection();
        var side = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, ahead));
        var head = player.Location + Vector3.UnitZ * (Teleport.Lift(player) * 0.6f);
        var shoulder = head - ahead * Back + side * Side + Vector3.UnitZ * Up;
        view.Location = Vector3.Lerp(view.Location, shoulder, _shown);
        view.FOV += (view.FOV * Zoom * Scope - view.FOV) * _shown;
        _drawnEye = view.Location;
        _drawnLook = ahead;
        _drawn = true;
    }

    public void Release()
    {
        Take(false);
        _facing = null;
        On = false;
        Fire = false;
        Secondary = false;
        _since = -1f;
        _drawn = false;
    }

    void Take(bool take)
    {
        if (take == _taken)
        {
            return;
        }

        _taken = take;
        foreach (var name in FireButtons)
        {
            if (take)
            {
                Swallowed.Take(name);
            }
            else
            {
                Swallowed.Give(name);
            }
        }

        foreach (var button in FirePads)
        {
            if (take)
            {
                Swallowed.Take(button);
            }
            else
            {
                Swallowed.Give(button);
            }
        }
    }
}
