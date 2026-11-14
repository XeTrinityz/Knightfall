#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// PLAYER > VILLAINS, MR. FREEZE. His look goes on through DISGUISE. His suit is heavy and has no
// cape, so he can't glide and moves slower than Batman, but he still grapples and climbs. His
// blaster is part of the suit, and holding aim raises it in his own aiming pose.
static partial class MrFreeze
{
    const string Lender = "mr freeze";

    // How often a look that isn't ready yet is tried again.
    const float DressRetry = 1f;

    // A glide that starts anyway is ended.
    static readonly FName FallingMode = new("FallingMode");

    static readonly Held<RPlayerController, bool> NoGlide = new("mr freeze glide", controller => controller.bCapeGlideDisabled, (controller, off) => controller.bCapeGlideDisabled = off);
    static readonly Held<RPawnPlayer, bool> NoPawnGlide = new("mr freeze pawn glide", pawn => pawn.bCanCapeGlide, (pawn, can) => pawn.bCanCapeGlide = can);

    static readonly VillainAim Aim = new();

    static bool _dressed;
    static float _nextDress;

    public static readonly Toggle Option = new(
        "player.villains.mr_freeze", "MR. FREEZE",
        "Become Mr. Freeze. He is slow and can't glide. Hold aim and fire for his cryo beam, which runs on coolant. Hold secondary fire to see where an ice spire will burst, and let go to burst it.")
    {
        Tick = Keep,
        Restore = Stop,
    };

    public static readonly Slider Speed = new(
        "player.villains.mr_freeze_speed", "SPEED",
        "How fast he walks and runs, as a share of Batman's speed.",
        min: 40, max: 100, step: 5, value: 75, unit: "%")
    {
        Preference = true,
    };

    public static readonly Toggle Hud = new(
        "player.villains.mr_freeze_hud", "HUD",
        "Shows his suit in the bottom left: coolant, suit temperature, Nora's vitals, his beam and ice spire, and a log of what he says.",
        on: true)
    {
        Preference = true,
    };

    // Everything a section lists is declared in this file. Static fields in another part of the
    // class may not be set up yet when this is.
    public static readonly Catalog.Section[] Sections =
    {
        new("MR. FREEZE", Option, Speed, Hud),
    };

    static void Keep()
    {
        if (!Villains.Claim(Option))
        {
            Option.SetTo(false);
            return;
        }

        Dress();
        Load();

        if (World.Player is not { IsValid: true } player || World.Controller is not { } controller
            || World.Driving is not null || controller.bCinematicMode)
        {
            Away();
            return;
        }

        Villains.Keep(player, controller, "DLG_Voices.MrFreeze");
        NoGlide.Hold(controller, true);
        NoPawnGlide.Hold(player, false);
        Movement.Pace((float)Speed.Value / 100f);

        // A grapple boost or a fall off a ledge can still go into a glide before the switches
        // above are read.
        if (player.bGliding)
        {
            controller.GotoState(FallingMode);
        }

        Aim.Update(player, controller, false);
        Pose(player);
        Beam(player);
        Blast(player);
        Watch(player);
        if (!_firing && Voice.Quiet("quiet", QuietEvery))
        {
            Logged();
        }

        ShowHud(player);
    }

    // His look goes on once each time MR. FREEZE is turned on. Changing DISGUISE after that is
    // left alone.
    static void Dress()
    {
        if (_dressed || World.Now < _nextDress)
        {
            return;
        }

        _nextDress = World.Now + DressRetry;
        _dressed = Disguise.Put(LookPackage);
    }

    public static void AimView(ref View view, float deltaTime) => Aim.View(ref view, deltaTime);

    public static void KeepAimFacing() => Aim.Hold();

    // In the car or a cutscene Batman has all his own moves back.
    static void Away()
    {
        VillainHud.Show(null);
        Aim.Release();
        StopBeam();
        StopBlast();
        Pose(null);
        Voice.Release();
        Villains.Release();
        NoGlide.Release();
        NoPawnGlide.Release();
        Movement.Pace(1f);
    }

    static void Stop()
    {
        Away();
        Voice.Unload();
        Unload();
        ResetSuit();
        _nextDress = 0f;
        if (_dressed)
        {
            _dressed = false;
            Disguise.TakeOffLook(LookPackage);
        }
    }
}
