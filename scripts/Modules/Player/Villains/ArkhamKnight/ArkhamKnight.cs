#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// PLAYER > VILLAINS, ARKHAM KNIGHT. His look goes on through DISGUISE. He has no cape, so he can't
// glide, but he still grapples, climbs and fights. Holding aim shoulders his sniper rifle.
static partial class ArkhamKnight
{
    const string Lender = "arkham knight";

    // How often a look that isn't ready yet is tried again.
    const float DressRetry = 1f;

    // A glide that starts anyway is ended.
    static readonly FName FallingMode = new("FallingMode");

    static readonly Held<RPlayerController, bool> NoGlide = new("arkham knight glide", controller => controller.bCapeGlideDisabled, (controller, off) => controller.bCapeGlideDisabled = off);
    static readonly Held<RPawnPlayer, bool> NoPawnGlide = new("arkham knight pawn glide", pawn => pawn.bCanCapeGlide, (pawn, can) => pawn.bCanCapeGlide = can);

    // A long rifle at the shoulder, so the camera sits close over his right shoulder and a little
    // further in than for a shotgun.
    static readonly VillainAim Aim = new() { Back = 140f, Side = 60f, Up = 25f, Zoom = 0.75f, Reach = Range };

    // The rifle aims through a scope's tighter view and the pistols with a wider one, eased
    // between when he switches.
    const float RifleScope = 0.6f;
    const float PistolScope = 1.15f;
    const float ScopeEase = 0.15f;

    static bool _dressed;
    static float _nextDress;

    public static readonly Toggle Option = new(
        "player.villains.arkham_knight", "ARKHAM KNIGHT",
        "Become the Arkham Knight. He can't glide. 1 and 2 pick his sniper rifle or twin pistols. With the rifle, hold secondary fire to see where a drone strike lands, and let go to call it.")
    {
        Tick = Keep,
        Restore = Stop,
    };

    public static readonly Command RifleKey = new(
        "player.villains.arkham_knight_rifle", "SNIPER RIFLE",
        "Aim and fire use his sniper rifle. Its laser marks where a round or a drone strike lands.",
        UseRifle)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "ARKHAM KNIGHT IS OFF",
    };

    public static readonly Command PistolsKey = new(
        "player.villains.arkham_knight_pistols", "TWIN PISTOLS",
        "Aim and fire use his twin pistols. Hold fire to shoot them in turn. A few hits put a thug down.",
        UsePistols)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "ARKHAM KNIGHT IS OFF",
    };

    public static readonly Command MilitiaKey = new(
        "player.villains.arkham_knight_militia", "CALL MILITIA",
        "A Humvee brings four of his militia with rifles. They get out by him and follow him as bodyguards. Every 30 seconds.",
        CallMilitia)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "ARKHAM KNIGHT IS OFF",
    };

    public static readonly Toggle Hud = new(
        "player.villains.arkham_knight_hud", "HUD",
        "Shows his visor in the bottom left: his weapons, the range to his mark, his drone strike and his militia.",
        on: true)
    {
        Preference = true,
    };

    // The keys he comes with. Gadgets are off while he is on, so their keys are free.
    public static void DefaultKeys()
    {
        Hotkeys.Add(RifleKey, key: "D1", pad: "DPadLeft");
        Hotkeys.Add(PistolsKey, key: "D2", pad: "DPadRight");
        Hotkeys.Add(MilitiaKey, key: "D3", pad: "DPadUp");
    }

    // Everything a section lists is declared in this file. Static fields in another part of the
    // class may not be set up yet when this is.
    public static readonly Catalog.Section[] Sections =
    {
        new("ARKHAM KNIGHT", Option, RifleKey, PistolsKey, MilitiaKey, Hud),
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

        // A squad on its way keeps coming while he drives or a scene plays.
        Squads();

        if (World.Player is not { IsValid: true } player || World.Controller is not { } controller
            || World.Driving is not null || controller.bCinematicMode)
        {
            Away();
            return;
        }

        // His helmet's voice. He has no lines of his own here, but under it a hit doesn't bring
        // Batman's grunt out of him.
        Villains.Keep(player, controller, "DLG_Voices.Redhood");
        NoGlide.Hold(controller, true);
        NoPawnGlide.Hold(player, false);

        // A grapple boost or a fall off a ledge can still go into a glide before the switches
        // above are read.
        if (player.bGliding)
        {
            controller.GotoState(FallingMode);
        }

        Aim.Update(player, controller, false);
        var scope = _pistols ? PistolScope : RifleScope;
        Aim.Scope += (scope - Aim.Scope) * (1f - MathF.Exp(-World.Delta / ScopeEase));
        Pose(player);
        Shoot(player, controller);
        Strike(player);
        Watch(player);
        ShowHud();
    }

    // His look goes on once each time ARKHAM KNIGHT is turned on. Changing DISGUISE after that is
    // left alone.
    static void Dress()
    {
        if (_dressed || World.Now < _nextDress)
        {
            return;
        }

        _nextDress = World.Now + DressRetry;
        _dressed = WearsSuit ? Disguise.PutSuit(Suit, SuitLabel) : Disguise.Put(ShowcaseLook);
    }

    public static void AimView(ref View view, float deltaTime) => Aim.View(ref view, deltaTime);

    public static void KeepAimFacing() => Aim.Hold();

    // In the car or a cutscene Batman has all his own moves back.
    static void Away()
    {
        StopRifle();
        StopStrike();
        VillainHud.Show(null);
        Aim.Release();
        Pose(null);
        Villains.Release();
        NoGlide.Release();
        NoPawnGlide.Release();
    }

    static void Stop()
    {
        Away();
        SendSquadsHome();
        Unload();
        _nextDress = 0f;
        if (_dressed)
        {
            _dressed = false;
            if (WearsSuit)
            {
                Disguise.TakeOffSuit(Suit);
            }
            else
            {
                Disguise.TakeOffLook(ShowcaseLook);
            }
        }
    }
}
