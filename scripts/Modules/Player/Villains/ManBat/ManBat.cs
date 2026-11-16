#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// PLAYER > VILLAINS, MAN-BAT. Kirk Langstrom as he hunts the city's rooftops at night. He never
// walks. He flies wherever you steer him, and Batman is hidden underneath and carried along, so
// the game still collides and streams the city for him.
static partial class ManBat
{
    static readonly Cover Hidden = new("man-bat");

    // He is taller than Batman with his wings out, so the camera is lifted. How it eases back and
    // up, and the room it keeps from a wall behind.
    const float CameraLift = 40f;
    const float ViewEase = 0.4f;
    const float WallRoom = 25f;
    static float _back;
    static float _lift;

    public static readonly Toggle Option = new(
        "player.villains.man_bat", "MAN-BAT",
        "Become Man-Bat. He flies wherever you steer him and never lands. WASD or the left stick fly, Space and Ctrl or RT and LT climb and drop, Shift or a left stick click flies faster. Batman's fighting, gadgets and grapple are off while he is on.")
    {
        Tick = Keep,
        Restore = Stop,
        Block = Locked,
    };

    public static readonly Slider CameraBack = new(
        "player.villains.man_bat_camera_back", "CAMERA DISTANCE",
        "How much further back the camera sits while you are Man-Bat, to fit his wings in.",
        min: 0, max: 6, step: 0.25, value: 2.5, unit: " M")
    {
        Preference = true,
    };

    // His body and flight ship with the game.
    static string? Locked() => Villains.Lock(Option, Installed, "GAME FILES MISSING", Needs);

    static readonly string[] Needs = { LookPackage, FlightPackage };

    public static readonly Command SwoopKey = new(
        "player.villains.man_bat_swoop", "SWOOP",
        "Hold to mark the enemy nearest the crosshair, up to 30 m away, and let go to dive on him. He pins him, slashes him down for good and launches back into the air. A tap dives at once.",
        StartSwoop)
    {
        Quiet = true,
        Block = static () => !Option.On ? "MAN-BAT IS OFF" : _body is { IsValid: true } && Copies[Action] is null ? "GAME FILES MISSING" : null,
    };

    public static readonly Command ScreechKey = new(
        "player.villains.man_bat_screech", "SCREECH",
        "Rears up and shrieks. Thugs around him cower, any right under him are rocked off their feet, and screens and glass nearby crack. It takes a few seconds before he can screech again.",
        Screech)
    {
        Quiet = true,
        Block = static () => !Option.On ? "MAN-BAT IS OFF" : _body is { IsValid: true } && Copies[Action] is null ? "GAME FILES MISSING" : null,
    };

    public static readonly Command SnatchKey = new(
        "player.villains.man_bat_snatch", "SNATCH",
        "Hold to mark the enemy nearest the crosshair, up to 30 m away, and let go to swoop down and carry him off by the neck. Press again to drop him. A drop from 5 m or more puts him down for good.",
        Snatch)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "MAN-BAT IS OFF",
    };

    public static readonly Toggle Hud = new(
        "player.villains.man_bat_hud", "HUD",
        "Shows his sonar in the bottom left: thugs ahead light up as each echo passes them, with his health on its rim, his moves and the prey he has taken.",
        on: true)
    {
        Preference = true,
    };

    // The keys he comes with. The player's own are read after these.
    public static void DefaultKeys()
    {
        Hotkeys.Add(SwoopKey, key: "LButton", pad: "X");
        Hotkeys.Add(SnatchKey, key: "RButton", pad: "Y");
        Hotkeys.Add(ScreechKey, key: "Q", pad: "RB");
    }

    // Everything a section lists is declared in this file. Static fields in another part of the
    // class may not be set up yet when this is.
    public static readonly Catalog.Section[] Sections =
    {
        new("MAN-BAT", Option, SwoopKey, SnatchKey, ScreechKey, CameraBack, Hud),
    };

    static void Keep()
    {
        if (!Villains.Claim(Option))
        {
            Option.SetTo(false);
            return;
        }

        // DISGUISE hides Batman too. Whichever was turned on last stays on.
        if (Disguise.Option.On)
        {
            if (Disguise.Option.TouchedAt > Option.TouchedAt)
            {
                Option.SetTo(false);
                return;
            }

            Disguise.Option.SetTo(false);
        }

        if (!Load())
        {
            Villains.LoadFailed(Option, Needs);
            return;
        }

        if (World.Player is not { IsValid: true } player || player.Mesh is not { IsValid: true } own || World.Controller is not { } controller
            || World.Driving is not null || controller.bCinematicMode)
        {
            Away();
            return;
        }

        if (_on is not { IsValid: true } || _on.Ptr != player.Ptr || _body is not { IsValid: true })
        {
            Build(player, own);
        }

        Hidden.Hide(player, own, Ours.Contains, keepCape: false);
        Villains.Keep(player, controller, Voice);
        Heavy.Limit(player, controller);
        Fly(player, controller);
        Aiming(player);
        Acting(player, own, controller);
        Carry(player);
        Choose();
        PlaceAll(own);
        Pose();
        Squeal(player);
        ShowHud();
    }

    // Back and up, to fit him in. A wall behind stops the camera short of it.
    public static void Fit(ref View view, float deltaTime)
    {
        var shown = Option.On && World.Driving is null && _body is { IsValid: true } && World.Player is { IsValid: true };
        var blend = Settle(deltaTime, ViewEase);
        _back += ((shown ? (float)CameraBack.Value * Teleport.UnitsPerMetre : 0f) - _back) * blend;
        _lift += ((shown ? CameraLift : 0f) - _lift) * blend;
        if ((_back < 1f && _lift < 1f) || World.Player is not { IsValid: true } player)
        {
            return;
        }

        var wanted = view.Location + Vector3.UnitZ * _lift - view.Rotation.ToDirection() * _back;
        view.Location = Crosshair.Line(player, view.Location, wanted, Crosshair.Meets.Level, out var wall)
            ? wall.Spot + Vector3.Normalize(view.Location - wanted) * WallRoom
            : wanted;
    }

    // Each footstep the game plays for the player asks for its sound. He never touches the ground
    // with Batman's feet, so it gets none.
    public static bool Steps(RPawnPlayer pawn, RAnimNotify_Footstep.EFoot foot, out AkEvent? sound)
    {
        sound = null;
        return _on is { IsValid: true } on && on.Ptr == pawn.Ptr && _body is { IsValid: true };
    }

    // A hit on him, on its way in. Batman's own reaction would knock the hidden body out of the
    // air, so it never plays and the whole hit lands. He shrieks at it, not every time.
    public static int Hit(RPawnPlayer player, int damage)
    {
        if (!Option.On || _body is not { IsValid: true } || damage <= 0)
        {
            return damage;
        }

        var now = World.Now;
        _damagedAt = now;
        if (now - _hurtAt >= HurtGap && _hurt is { IsValid: true } hurt)
        {
            _hurtAt = now;
            player.StartAudioEvent(hurt);
        }

        return damage;
    }

    // In the car or a cutscene Batman is shown as himself, with all his moves. Switched off in
    // the air, he drops from where he was.
    static void Away(bool drop = false)
    {
        VillainHud.Show(null);
        Unact();
        Drop();
        _dropped = null;
        Shrieks.Release();
        Land(drop);
        Unbuild();
        Hidden.Show();
        Villains.Release();
        Heavy.Unlimit();
    }

    static void Stop()
    {
        Away(drop: true);
        Unload();
    }
}
