#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// PLAYER > MOVEMENT. Every slider scales the game's own values, so 1X is always the normal game.
// If the game changes one of them itself, like a scripted slow walk, ours is worked out from that.
static class Movement
{
    static readonly Held<RPawnPlayer, float> FallGravityHeld = new("fall gravity", pawn => pawn.OverrideFallingGravity, (pawn, gravity) => pawn.OverrideFallingGravity = gravity);
    static readonly Held<RPersistentDebugData, bool> FlightFlag = new("free flight", data => data.Debug_PowerGlide, (data, on) => data.Debug_PowerGlide = on);

    static float _wantedGravity;
    static float _pace = 1f;

    public static readonly Toggle FreeFlight = new(
        "player.movement.free_flight", "FREE FLIGHT",
        "Turns a glide into flight for as long as you like. Nothing pulls you down and you keep full glide speed. Look straight up to climb or straight down to dive. GLIDE SPEED still scales it.")
    {
        Tick = KeepFlying,
        Restore = FlightFlag.Release,
    };

    public static readonly Slider FallGravity = new(
        "player.movement.fall_gravity", "FALL GRAVITY",
        "How hard you fall after a jump or a drop. Lower is slower and floatier. Gliding isn't changed.",
        min: 0.1, max: 2, step: 0.05, value: 1, unit: "X")
    {
        Tick = KeepGravity,
        Restore = FallGravityHeld.Release,
    };

    static readonly Scale<RPawnPlayer> Walk = new("move speed",
        (tick, restore) => new Slider(
            "player.movement.move_speed", "MOVE SPEED",
            "How fast you walk and run. Scripted slow walks still slow you down.",
            min: 0.5, max: 3, step: 0.05, value: 1, unit: "X")
        {
            Tick = tick,
            Restore = restore,
        },
        () => World.Player,
        Fields.Of<RPawnPlayer>(pawn => pawn.MovementSpeedModifier, (pawn, value) => pawn.MovementSpeedModifier = value))
    {
        Extra = static () => _pace,
    };

    static readonly Scale<RPawnPlayer> Tunnel = new("tunnel speed",
        (tick, restore) => new Slider(
            "player.movement.tunnel_speed", "LOW TUNNEL SPEED",
            "How fast you get through low passages where you have to crouch.",
            min: 0.5, max: 3, step: 0.25, value: 1, unit: "X")
        {
            Tick = tick,
            Restore = restore,
        },
        () => World.Player,
        Fields.Of<RPawnPlayer>(pawn => pawn.TunnelSpeedScale, (pawn, value) => pawn.TunnelSpeedScale = value));

    static readonly Scale<RPlayerController> Glide = new("glide speed",
        (tick, restore) => new Slider(
            "player.movement.glide_speed", "GLIDE SPEED",
            "How fast you glide. Cruising, diving, dive boosts and how quickly you reach them all scale.",
            min: 0.5, max: 4, step: 0.25, value: 1, unit: "X")
        {
            Tick = tick,
            Restore = restore,
        },
        () => World.Controller,
        Fields.Of<RPlayerController>(controller => controller.CapeGlideMinSpeed, (controller, value) => controller.CapeGlideMinSpeed = value),
        Fields.Of<RPlayerController>(controller => controller.CapeGlideMaxSpeed, (controller, value) => controller.CapeGlideMaxSpeed = value),
        Fields.Of<RPlayerController>(controller => controller.CapeGlideOverworldMinSpeed, (controller, value) => controller.CapeGlideOverworldMinSpeed = value),
        Fields.Of<RPlayerController>(controller => controller.CapeSoaringMinSpeed, (controller, value) => controller.CapeSoaringMinSpeed = value),
        Fields.Of<RPlayerController>(controller => controller.CapeGlideAccel, (controller, value) => controller.CapeGlideAccel = value),
        Fields.Of<RPlayerController>(controller => controller.CapeGlideDecel, (controller, value) => controller.CapeGlideDecel = value));

    public static readonly Catalog.Section[] Sections =
    {
        new("ON FOOT", Walk.Slider, Tunnel.Slider),
        new("GLIDING", Glide.Slider, FreeFlight),
        new("FALLING", FallGravity),
        new("NOCLIP", Noclip.Clip, Noclip.Speed),
        new("CLUMSY", Clumsy.Trips, Clumsy.Odds),
    };

    // A villain that walks at a share of the player's pace. It goes through the same hold as
    // MOVE SPEED, since two holds on one value each take the other's for the game's own.
    public static void Pace(float share)
    {
        if (share == 1f && _pace == 1f)
        {
            return;
        }

        _pace = share;
        if (share != 1f)
        {
            Walk.Apply();
            return;
        }

        // While MOVE SPEED is on its own tick holds the value. Holding it from here could take it
        // again after MOVE SPEED has put it back on unload.
        if (Walk.Slider.IsDefault)
        {
            Walk.Release();
        }
    }

    // The game's own debug glide. Its glide code reads this flag every tick. With it set the glide
    // has no gravity, no ceiling that slows it and no limit on how steeply it climbs or dives.
    static void KeepFlying()
    {
        if (Combat.DebugData() is { } data)
        {
            FlightFlag.Hold(data, true);
        }
    }

    // The game's own override wins. Catwoman's whip rappel and rescue falls set one, and while
    // either is in place it is left alone.
    static void KeepGravity()
    {
        if (World.Player is not { } pawn)
        {
            return;
        }

        var world = WorldGravity(pawn);
        if (world >= 0f)
        {
            return;
        }

        _wantedGravity = world * (float)FallGravity.Value;
        FallGravityHeld.Hold(pawn, static was => was != 0f ? was : _wantedGravity);
    }

    static float WorldGravity(RPawnPlayer pawn)
    {
        if (pawn.PhysicsVolume is { IsValid: true } volume)
        {
            var gravity = volume.GetGravityZ();
            if (gravity < 0f)
            {
                return gravity;
            }
        }

        if (!Game.TryGetWorldInfo(out var world))
        {
            return 0f;
        }

        return world.WorldGravityZ < 0f ? world.WorldGravityZ : world.DefaultGravityZ;
    }
}
