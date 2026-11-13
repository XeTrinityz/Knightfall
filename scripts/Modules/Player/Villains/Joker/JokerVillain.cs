#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// PLAYER > VILLAINS, JOKER. His look goes on through DISGUISE. He has no cape, so he can't glide,
// but he still grapples, climbs and fights. Holding aim draws his shotgun in his own aiming pose.
static partial class JokerVillain
{
    const string Lender = "joker";

    // How often a look that isn't ready yet is tried again.
    const float DressRetry = 1f;

    // A glide that starts anyway is ended.
    static readonly FName FallingMode = new("FallingMode");

    static readonly Held<RPlayerController, bool> NoGlide = new("joker glide", controller => controller.bCapeGlideDisabled, (controller, off) => controller.bCapeGlideDisabled = off);
    static readonly Held<RPawnPlayer, bool> NoPawnGlide = new("joker pawn glide", pawn => pawn.bCanCapeGlide, (pawn, can) => pawn.bCanCapeGlide = can);

    // He shoulders his gun, so the camera sits close in over his right shoulder.
    static readonly VillainAim Aim = new() { Back = 150f, Side = 60f, Up = 25f, Zoom = 0.8f };

    static bool _dressed;
    static float _nextDress;

    public static readonly Toggle Option = new(
        "player.villains.joker", "JOKER",
        "Become the Joker. He can't glide. Hold aim and fire for his shotgun. Secondary fire launches laughing gas where you aim.")
    {
        Tick = Keep,
        Restore = Stop,
    };

    public static readonly Command GoonsKey = new(
        "player.villains.joker_goons", "CALL GOONS",
        "Calls three thugs dressed as Harley's gang to fight for him. Every 45 seconds.",
        CallGoons)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "JOKER IS OFF",
    };

    public static readonly Toggle Brutal = new(
        "player.villains.joker_brutal", "BRUTAL FIGHTING",
        "Fights like Batman does when the Joker takes over his mind. Counters throw thugs and strikes pick downed ones back up.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Toggle BackGun = new(
        "player.villains.joker_back_gun", "GUN ON BACK",
        "Slings his shotgun across his back while he isn't aiming it.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Toggle JokerfyCity = new(
        "player.villains.joker_city", "JOKERFY THE CITY",
        "While he is on, every billboard around him that has a Joker version turns into it.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Toggle Hud = new(
        "player.villains.joker_hud", "HUD",
        "Shows his card in the bottom left: shells, laughing gas, how many he has put in stitches, and a log of what he says.",
        on: true)
    {
        Preference = true,
    };

    // Everything a section lists is declared in this file. Static fields in another part of the
    // class may not be set up yet when this is.
    public static readonly Catalog.Section[] Sections =
    {
        new("JOKER", Option, GoonsKey, Brutal, BackGun, JokerfyCity, Hud),
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
        City();

        if (World.Player is not { IsValid: true } player || World.Controller is not { } controller
            || World.Driving is not null || controller.bCinematicMode)
        {
            Away();
            return;
        }

        Villains.Keep(player, controller, "DLG_Voices.Joker", Villains.Known.Joker);
        NoGlide.Hold(controller, true);
        NoPawnGlide.Hold(player, false);

        // A grapple boost or a fall off a ledge can still go into a glide before the switches
        // above are read.
        if (player.bGliding)
        {
            controller.GotoState(FallingMode);
        }

        Aim.Update(player, controller, false);
        Pose(player);
        Shoot(player, controller);
        Gas(player);
        Talk(player, controller);
        Fight(player, controller);
        KeepGoons();
        Tint(controller);
        Crosshair(controller);
        XRay(controller);
        ShowHud();
    }

    // His look goes on once each time JOKER is turned on. Changing DISGUISE after that is left
    // alone.
    static void Dress()
    {
        if (_dressed || World.Now < _nextDress)
        {
            return;
        }

        _nextDress = World.Now + DressRetry;
        _dressed = Disguise.PutSuit(Suit, SuitLabel);
    }

    public static void AimView(ref View view, float deltaTime) => Aim.View(ref view, deltaTime);

    public static void KeepAimFacing() => Aim.Hold();

    // In the car or a cutscene Batman has all his own moves back.
    static void Away()
    {
        VillainHud.Show(null);
        ViewLook.Release();
        StopFighting();
        StopThrow();
        Voice.Release();
        Aim.Release();
        Pose(null);
        Villains.Release();
        NoGlide.Release();
        NoPawnGlide.Release();
    }

    static void Stop()
    {
        Away();
        Untint();
        Joker.PutBackUnlessKept();
        DismissGoons();
        Unsling();
        Unload();
        ResetGun();
        ResetGas();
        Voice.Unload();
        ResetVoice();
        ResetHud();
        _nextDress = 0f;
        if (_dressed)
        {
            _dressed = false;
            Disguise.TakeOffSuit(Suit);
        }
    }
}
