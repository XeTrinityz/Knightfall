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
        Field<RPawnPlayer>(pawn => pawn.MovementSpeedModifier, (pawn, value) => pawn.MovementSpeedModifier = value))
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
        Field<RPawnPlayer>(pawn => pawn.TunnelSpeedScale, (pawn, value) => pawn.TunnelSpeedScale = value));

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
        Field<RPlayerController>(controller => controller.CapeGlideMinSpeed, (controller, value) => controller.CapeGlideMinSpeed = value),
        Field<RPlayerController>(controller => controller.CapeGlideMaxSpeed, (controller, value) => controller.CapeGlideMaxSpeed = value),
        Field<RPlayerController>(controller => controller.CapeGlideOverworldMinSpeed, (controller, value) => controller.CapeGlideOverworldMinSpeed = value),
        Field<RPlayerController>(controller => controller.CapeSoaringMinSpeed, (controller, value) => controller.CapeSoaringMinSpeed = value),
        Field<RPlayerController>(controller => controller.CapeGlideAccel, (controller, value) => controller.CapeGlideAccel = value),
        Field<RPlayerController>(controller => controller.CapeGlideDecel, (controller, value) => controller.CapeGlideDecel = value));

    static readonly Scale<RPawnPlayer> Grapple = new("grapple speed",
        (tick, restore) => new Slider(
            "player.movement.grapple_speed", "GRAPPLE SPEED",
            "How fast the grapple gun pulls you up to a ledge.",
            min: 0.5, max: 3, step: 0.25, value: 1, unit: "X")
        {
            Tick = tick,
            Restore = restore,
        },
        () => World.Player,
        Field<RPawnPlayer>(pawn => pawn.MinGrappleSpeed, (pawn, value) => pawn.MinGrappleSpeed = value),
        Field<RPawnPlayer>(pawn => pawn.MaxGrappleSpeed, (pawn, value) => pawn.MaxGrappleSpeed = value));

    static readonly Scale<RGrappleGun> Boost = new("grapple boost",
        (tick, restore) => new Slider(
            "player.movement.grapple_boost", "GRAPPLE BOOST POWER",
            "How far and how high a grapple boost throws you off the ledge, at every upgrade level.",
            min: 0.5, max: 3, step: 0.25, value: 1, unit: "X")
        {
            Tick = tick,
            Restore = restore,
        },
        () => World.Player?.GrappleGun,
        Field<RGrappleGun>(gun => gun.MaxGrappleBoostExtraSpeed, (gun, value) => gun.MaxGrappleBoostExtraSpeed = value),
        Field<RGrappleGun>(gun => gun.MaxGrappleBoostExtraSpeedMk3, (gun, value) => gun.MaxGrappleBoostExtraSpeedMk3 = value),
        Field<RGrappleGun>(gun => gun.MaxGrappleBoostExtraSpeedMk4, (gun, value) => gun.MaxGrappleBoostExtraSpeedMk4 = value),
        Field<RGrappleGun>(gun => gun.MaxGrappleBoostExtraHeight, (gun, value) => gun.MaxGrappleBoostExtraHeight = value),
        Field<RGrappleGun>(gun => gun.MaxGrappleBoostExtraHeightMk3, (gun, value) => gun.MaxGrappleBoostExtraHeightMk3 = value),
        Field<RGrappleGun>(gun => gun.MaxGrappleBoostExtraHeightMk4, (gun, value) => gun.MaxGrappleBoostExtraHeightMk4 = value));

    static readonly Scale<RGrappleGun> Reach = new("grapple height",
        (tick, restore) => new Slider(
            "player.movement.grapple_height", "GRAPPLE HEIGHT",
            "How high a ledge the grapple gun can reach.",
            min: 0.5, max: 3, step: 0.25, value: 1, unit: "X")
        {
            Tick = tick,
            Restore = restore,
        },
        () => World.Player?.GrappleGun,
        Field<RGrappleGun>(gun => gun.MaxGrappleHeight, (gun, value) => gun.MaxGrappleHeight = value));

    static readonly Scale<RLineLauncher> Zip = new("line launcher",
        (tick, restore) => new Slider(
            "player.movement.line_launcher_speed", "LINE LAUNCHER SPEED",
            "How fast the line launcher carries you along the line, and how quickly it gets there.",
            min: 0.5, max: 3, step: 0.25, value: 1, unit: "X")
        {
            Tick = tick,
            Restore = restore,
        },
        () => World.Player?.LineLauncher,
        Field<RLineLauncher>(launcher => launcher.ZipForwardMaxSpeed, (launcher, value) => launcher.ZipForwardMaxSpeed = value),
        Field<RLineLauncher>(launcher => launcher.ZipForwardAccel, (launcher, value) => launcher.ZipForwardAccel = value));

    public static readonly Catalog.Section[] Sections =
    {
        new("ON FOOT", Walk.Slider, Tunnel.Slider),
        new("GLIDING", Glide.Slider, FreeFlight),
        new("GRAPPLE", Grapple.Slider, Boost.Slider, Reach.Slider, Zip.Slider),
        new("FALLING", FallGravity),
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

    static (Func<T, float> Read, Action<T, float> Write) Field<T>(Func<T, float> read, Action<T, float> write)
        where T : GameObject => (read, write);

    // A slider that multiplies a set of the game's values on one object.
    sealed class Scale<T>
        where T : GameObject
    {
        readonly Held<T, float>[] _fields;
        readonly Func<T?> _find;
        readonly Func<float, float> _scaled;

        // The scale makes its own slider, so the slider's tick and restore can point back at it.
        public Scale(string name, Func<Action, Action, Slider> slider, Func<T?> find, params (Func<T, float> Read, Action<T, float> Write)[] fields)
        {
            _fields = Array.ConvertAll(fields, field => new Held<T, float>(name, field.Read, field.Write));
            _find = find;
            Slider = slider(Apply, Release);
            _scaled = was => was * (float)Slider.Value * (Extra?.Invoke() ?? 1f);
        }

        public Slider Slider { get; }

        public Func<float>? Extra { get; init; }

        public void Apply()
        {
            if (_find() is not { IsValid: true } target)
            {
                return;
            }

            foreach (var field in _fields)
            {
                field.Hold(target, _scaled);
            }
        }

        public void Release()
        {
            foreach (var field in _fields)
            {
                field.Release();
            }
        }
    }
}
