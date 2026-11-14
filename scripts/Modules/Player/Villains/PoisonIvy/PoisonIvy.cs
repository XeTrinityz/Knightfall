#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// PLAYER > VILLAINS, POISON IVY. Her look goes on through DISGUISE. She has no cape, so she can't
// glide, but she still grapples, climbs and fights. Holding aim raises her hand toward the spot
// her plants will strike.
static partial class PoisonIvy
{
    // How often a look that isn't ready yet is tried again.
    const float DressRetry = 1f;

    // A glide that starts anyway is ended.
    static readonly FName FallingMode = new("FallingMode");

    static readonly Held<RPlayerController, bool> NoGlide = new("poison ivy glide", controller => controller.bCapeGlideDisabled, (controller, off) => controller.bCapeGlideDisabled = off);
    static readonly Held<RPawnPlayer, bool> NoPawnGlide = new("poison ivy pawn glide", pawn => pawn.bCanCapeGlide, (pawn, can) => pawn.bCanCapeGlide = can);

    // She points with an open hand, so the camera sits a little further back than for a gun.
    static readonly VillainAim Aim = new() { Back = 220f, Side = 70f, Up = 30f, Zoom = 0.85f };

    static bool _dressed;
    static float _nextDress;

    public static readonly Toggle Option = new(
        "player.villains.poison_ivy", "POISON IVY",
        "Become Poison Ivy. She can't glide. Hold aim, then hold fire to see where her vine will strike and let go to cast it. It seizes thugs and tears small tanks apart. Hold and let go of secondary fire the same way to plant a flytrap that eats the next enemy to come close.")
    {
        Tick = Keep,
        Restore = Stop,
    };

    public static readonly Slider TrapLife = new(
        "player.villains.poison_ivy_trap_life", "FLYTRAP LIFE",
        "How long a flytrap stays before it sinks back into the ground. Its clock stops while it eats.",
        min: 10, max: 120, step: 5, value: 30, unit: " S")
    {
        Preference = true,
    };

    public static readonly Toggle Hud = new(
        "player.villains.poison_ivy_hud", "HUD",
        "Shows her herbarium sheet in the bottom left: her vine, her flytraps, how many she has eaten and wrecked, and what she says.",
        on: true)
    {
        Preference = true,
    };

    // Everything a section lists is declared in this file. Static fields in another part of the
    // class may not be set up yet when this is.
    public static readonly Catalog.Section[] Sections =
    {
        new("POISON IVY", Option, TrapLife, Hud),
    };

    static void Keep()
    {
        if (!Villains.Claim(Option))
        {
            Option.SetTo(false);
            return;
        }

        Dress();
        LoadVine();
        LoadTrap();
        LoadVoice();
        if (IvyBody() is not null)
        {
            Disguise.StillCloth();
        }

        if (World.Player is not { IsValid: true } player || World.Controller is not { } controller
            || World.Driving is not null || controller.bCinematicMode)
        {
            Away();
            return;
        }

        Villains.Keep(player, controller, "DLG_Voices.PoisonIvy", Villains.Known.Woman);
        NoGlide.Hold(controller, true);
        NoPawnGlide.Hold(player, false);

        // A grapple boost or a fall off a ledge can still go into a glide before the switches
        // above are read.
        if (player.bGliding)
        {
            controller.GotoState(FallingMode);
        }

        Aim.Update(player, controller, false);
        Lash(player);
        Plant(player);
        KeepVoice();
        Pose(player);
        ShowHud();
    }

    // Her look goes on once each time POISON IVY is turned on. Changing DISGUISE after that is
    // left alone.
    static void Dress()
    {
        if (_dressed || World.Now < _nextDress)
        {
            return;
        }

        _nextDress = World.Now + DressRetry;

        // Her own stance holds her hands inside her body once she moves like Batman.
        Disguise.PreferYourStance(LookPackage);
        _dressed = Disguise.Put(LookPackage);
    }

    public static void AimView(ref View view, float deltaTime) => Aim.View(ref view, deltaTime);

    public static void KeepAimFacing() => Aim.Hold();

    // In the car or a cutscene Batman has all his own moves back.
    static void Away()
    {
        VillainHud.Show(null);
        Aim.Release();
        StopVines();
        StopTraps();
        QuietVoice();
        Pose(null);
        Villains.Release();
        NoGlide.Release();
        NoPawnGlide.Release();
    }

    static void Stop()
    {
        Away();
        Disguise.LetCloth();
        ResetHud();
        Unload();
        _nextDress = 0f;
        if (_dressed)
        {
            _dressed = false;
            Disguise.TakeOffLook(LookPackage);
        }
    }
}
