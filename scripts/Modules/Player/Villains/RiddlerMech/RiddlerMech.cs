#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// PLAYER > VILLAINS, RIDDLER MECH. The mech Riddler fights in at his orphanage, with him at the
// controls. Batman is hidden and goes on moving underneath it, so the game still walks, runs
// and collides for it. The mech is shown in his place and plays its own moves to match.
static partial class RiddlerMech
{
    static readonly Cover Hidden = new("riddler mech");

    // A glide that starts anyway is ended.
    static readonly FName FallingMode = new("FallingMode");

    // The camera is lifted this much while you are the mech, since its cockpit sits far above
    // Batman's head. How it eases back and up, and the room it keeps from a wall behind.
    const float CameraLift = 150f;
    const float ViewEase = 0.4f;
    const float WallRoom = 25f;
    static float _back;
    static float _lift;

    public static readonly Toggle Option = new(
        "player.villains.riddler_mech", "RIDDLER MECH",
        "Become the mech Riddler fights in, with its own moves and sounds. Batman's fighting, gadgets, grapple, glide and climbing are off while it is on.")
    {
        Tick = Keep,
        Restore = Stop,
    };

    public static readonly Slider CameraBack = new(
        "player.villains.riddler_mech_camera_back", "CAMERA DISTANCE",
        "How much further back the camera sits while you are the mech, to fit it in.",
        min: 0, max: 8, step: 0.25, value: 3, unit: " M")
    {
        Preference = true,
    };

    public static readonly Command SmashKey = new(
        "player.villains.riddler_mech_smash", "SMASH",
        "Slams its claws down. Enemies under them are knocked flat. It turns to face an enemy close to where you look.",
        () => Start(Move.Smash))
    {
        Quiet = true,
        Block = static () => Option.On ? null : "THE MECH IS OFF",
    };

    public static readonly Command HopKey = new(
        "player.villains.riddler_mech_hop", "HOP BACK",
        "Tap twice to hop back out of trouble. It shares the run key, so a single tap still runs.",
        Hop)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "THE MECH IS OFF",
    };

    public static readonly Command BeamKey = new(
        "player.villains.riddler_mech_beam", "BEAM",
        "Hold to charge its beam toward where you aim, then let go to fire. It runs along the ground and knocks down everyone it crosses. Let go too soon and it is lost.",
        () => Start(Move.Charge))
    {
        Quiet = true,
        Block = static () => Option.On ? null : "THE MECH IS OFF",
    };

    public static readonly Command SurgeKey = new(
        "player.villains.riddler_mech_surge", "POWER SURGE",
        "Slams power into the ground. The shockwave knocks down everyone close, hits vehicles and breaks what is around. It takes a while to build up again.",
        () => Start(Move.Surge))
    {
        Quiet = true,
        Block = static () => Option.On ? null : "THE MECH IS OFF",
    };

    public static readonly Toggle Hud = new(
        "player.villains.riddler_mech_hud", "HUD",
        "Shows what the mech is doing, its speed, the enemies close by and its controls in the bottom left.",
        on: true)
    {
        Preference = true,
    };

    // The keys it comes with. The player's own are read after these.
    public static void DefaultKeys()
    {
        Hotkeys.Add(SmashKey, key: "LButton", pad: "X");
        Hotkeys.Add(HopKey, key: "Space", pad: "A");
        Hotkeys.Add(BeamKey, key: "RButton", pad: "LT");
        Hotkeys.Add(SurgeKey, key: "Q", pad: "B");
    }

    // Everything a section lists is declared in this file. Static fields in another part of the
    // class may not be set up yet when this is.
    public static readonly Catalog.Section[] Sections =
    {
        new("RIDDLER MECH", Option, SmashKey, BeamKey, HopKey, SurgeKey, CameraBack, Hud),
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
            Option.Reset();
            return;
        }

        if (World.Player is not { IsValid: true } player || player.Mesh is not { IsValid: true } own || World.Controller is not { } controller
            || World.Driving is not null || controller.bCinematicMode)
        {
            Away();
            return;
        }

        if (_on is not { IsValid: true } || _on.Ptr != player.Ptr || _mech is not { IsValid: true })
        {
            Build(player, own);
        }

        Hidden.Hide(player, own, Ours.Contains, keepCape: false);
        Villains.Keep(player, controller);
        Limit(player, controller);
        if (player.bGliding)
        {
            controller.GotoState(FallingMode);
        }

        Land(player);
        Act(player, own);
        Choose(player);
        PlaceAll(own);
        Pose();
        WaitForPick();
        ShowHud(player);
    }

    // Back and up, to fit the mech in. A wall behind stops the camera short of it.
    public static void Fit(ref View view, float deltaTime)
    {
        var shown = Option.On && World.Driving is null && _mech is { IsValid: true } && World.Player is { IsValid: true };
        var blend = Settle(deltaTime, ViewEase);
        _back += ((shown ? (float)CameraBack.Value * Teleport.UnitsPerMetre : 0f) - _back) * blend;
        _lift += ((shown ? CameraLift : 0f) - _lift) * blend;
        if ((_back < 1f && _lift < 1f) || World.Player is not { IsValid: true } player)
        {
            return;
        }

        var wanted = view.Location + Vector3.UnitZ * _lift - view.Rotation.ToDirection() * _back;
        view.Location = Blink.Aim(player, view.Location, wanted, out var wall, out _)
            ? wall + Vector3.Normalize(view.Location - wanted) * WallRoom
            : wanted;
    }

    // In the car or a cutscene Batman is shown as himself, with all his moves.
    static void Away()
    {
        Unact();
        _falling = false;
        VillainHud.Show(null);
        Unbuild();
        Hidden.Show();
        Villains.Release();
        Unlimit();
        Unvoice();
    }

    static void Stop()
    {
        Away();
        Unload();
    }
}
