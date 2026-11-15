#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// PLAYER > VILLAINS, KILLER CROC. Croc as he fights in Blackgate in the Season of Infamy. Batman
// is hidden and goes on moving underneath him, so the game still walks, runs and collides for
// him. Croc is shown in his place and plays his own moves to match.
static partial class KillerCroc
{
    static readonly Cover Hidden = new("killer croc");

    // A glide that starts anyway is ended.
    static readonly FName FallingMode = new("FallingMode");

    // Croc stands a head taller than Batman, so the camera is lifted. How it eases back and up,
    // and the room it keeps from a wall behind.
    const float CameraLift = 45f;
    const float ViewEase = 0.4f;
    const float WallRoom = 25f;
    static float _back;
    static float _lift;

    public static readonly Toggle Option = new(
        "player.villains.killer_croc", "KILLER CROC",
        "Become Killer Croc, with his own moves, steps and growls. Batman's fighting, gadgets, grapple, glide and climbing are off while he is on.")
    {
        Tick = Keep,
        Restore = Stop,
        Block = Locked,
    };

    public static readonly Slider CameraBack = new(
        "player.villains.killer_croc_camera_back", "CAMERA DISTANCE",
        "How much further back the camera sits while you are Croc, to fit him in.",
        min: 0, max: 6, step: 0.25, value: 3, unit: " M")
    {
        Preference = true,
    };

    // His body and moves are in the Season of Infamy pack. One already on is let run, so it can
    // still be turned off.
    static string? Locked() => Installed || Option.On ? null : "NEEDS SEASON OF INFAMY";

    public static readonly Command SwipeKey = new(
        "player.villains.killer_croc_swipe", "SWIPE",
        "Lunges at a thug near where the camera looks and swings his claws, throwing whoever they meet. Someone behind him gets a spinning backhand. Press again once a swing lands to chain the next.",
        Swipe)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "CROC IS OFF",
    };

    public static readonly Command ChargeKey = new(
        "player.villains.killer_croc_charge", "CHARGE",
        "Charges head down where the camera looks, bowling over everyone in his path and smashing through what breaks. Steer with the camera. He stops at a wall, and it takes a few seconds to build up again.",
        Charge)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "CROC IS OFF",
    };

    public static readonly Command RecruitKey = new(
        "player.villains.killer_croc_recruit", "RECRUIT",
        "Hauls the nearest knocked out thug back onto his feet, and he gets up fighting for you as a bodyguard. DISMISS in SPAWNER > BODYGUARDS lets him go.",
        Recruit)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "CROC IS OFF",
    };

    public static readonly Command RoarKey = new(
        "player.villains.killer_croc_roar", "ROAR",
        "Roars and shakes the ground. Thugs around him cower. It takes a few seconds before he can roar again.",
        Roar)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "CROC IS OFF",
    };

    public static readonly Toggle Hud = new(
        "player.villains.killer_croc_hud", "HUD",
        "Shows his eye in the bottom left: his health in its scales, his charge and roar, his crew and the thugs he has thrown.",
        on: true)
    {
        Preference = true,
    };

    // The keys he comes with. The player's own are read after these.
    public static void DefaultKeys()
    {
        Hotkeys.Add(SwipeKey, key: "LButton", pad: "X");
        Hotkeys.Add(ChargeKey, key: "RButton", pad: "Y");
        Hotkeys.Add(RecruitKey, key: "E", pad: "B");
        Hotkeys.Add(RoarKey, key: "Q", pad: "RB");
    }

    // Everything a section lists is declared in this file. Static fields in another part of the
    // class may not be set up yet when this is.
    public static readonly Catalog.Section[] Sections =
    {
        new("KILLER CROC", Option, SwipeKey, ChargeKey, RecruitKey, RoarKey, CameraBack, Hud),
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

        if (_on is not { IsValid: true } || _on.Ptr != player.Ptr || _body is not { IsValid: true })
        {
            Build(player, own);
        }

        Hidden.Hide(player, own, Ours.Contains, keepCape: false);
        Villains.Keep(player, controller, Voice);
        if (controller.IsInCombat(false) && !Acting)
        {
            Speech.Quiet("taunt", TauntSeconds);
        }
        Heavy.Limit(player, controller);
        if (player.bGliding)
        {
            controller.GotoState(FallingMode);
        }

        Act(player, own);
        KeepHauled(player);
        Choose(player);
        PlaceAll(own);
        Pose();
        Step(player);
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

    // In the car or a cutscene Batman is shown as himself, with all his moves.
    static void Away()
    {
        Unact();
        DropHaul();
        VillainHud.Show(null);
        Unbuild();
        Hidden.Show();
        Villains.Release();
        Heavy.Unlimit();
        Speech.Release();
    }

    // Switched off, his crew go with him. A new world only starts him over, and they stay.
    static void Stop()
    {
        Away();
        Unload();
        if (!Option.On)
        {
            LetCrewGo();
        }
    }

    static void LetCrewGo()
    {
        var bodies = Bodyguards.All.Select(guard => guard.Body).ToArray();
        foreach (var body in bodies)
        {
            if (body.IsAlive)
            {
                Spawned.Take(body);
            }
        }

        if (bodies.Length > 0)
        {
            Log.Info($"Killer Croc's crew of {bodies.Length} left with him.");
        }
    }
}
