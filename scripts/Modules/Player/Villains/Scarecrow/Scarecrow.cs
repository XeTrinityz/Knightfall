#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > VILLAINS, SCARECROW. His look goes on through DISGUISE and moves with Batman's walk,
// run, jumps and climbs. Standing, creeping and in his own moves he plays his own animations from
// his fight on Stagg's airship over the top. He has no cape or grapnel and doesn't fight like
// Batman. Fear is how he fights.
static partial class Scarecrow
{
    const string Lender = "scarecrow";

    // How often a look that isn't ready yet is tried again.
    const float DressRetry = 1f;

    // A glide that starts anyway is ended.
    static readonly FName FallingMode = new("FallingMode");

    static readonly Held<RPlayerController, bool> NoGlide = new("scarecrow glide", c => c.bCapeGlideDisabled, (c, off) => c.bCapeGlideDisabled = off);
    static readonly Held<RPawnPlayer, bool> NoPawnGlide = new("scarecrow pawn glide", pawn => pawn.bCanCapeGlide, (pawn, can) => pawn.bCanCapeGlide = can);
    static readonly Held<RGameInfo, bool> NoGrapple = new("scarecrow grapple", game => game.bNoGrapple, (game, off) => game.bNoGrapple = off);
    static readonly Held<RPlayerControllerCombat, bool> NoFists = new("scarecrow fists", c => c.bDisallowCombat, (c, off) => c.bDisallowCombat = off);
    static readonly Held<RPlayerControllerCombat, bool> NoCapeStun = new("scarecrow cape stun", c => c.bCapeStunDisabled, (c, off) => c.bCapeStunDisabled = off);
    static readonly Held<RPlayerController, bool> NoTakedowns = new("scarecrow takedowns", c => c.bDisableTakedowns, (c, off) => c.bDisableTakedowns = off);
    static readonly Held<RPawnPlayer, bool> NoPawnTakedowns = new("scarecrow pawn takedowns", pawn => pawn.bDisableAllTakedowns, (pawn, off) => pawn.bDisableAllTakedowns = off);

    // Without Batman's fists there is no breaking a thug's grab, so thugs don't try one.
    static readonly Held<RBMCombatManager, bool> NoGrabs = new("scarecrow grabs", manager => manager.bDisableGrabAttack, (manager, off) => manager.bDisableGrabAttack = off);

    static bool _dressed;
    static float _nextDress;

    public static readonly Toggle Option = new(
        "player.villains.scarecrow", "SCARECROW",
        "Become the Scarecrow, with his own creep, needle and fear toxin. He can't glide, grapple or fight like Batman. Fear is how he fights.")
    {
        Tick = Keep,
        Restore = Stop,
        Block = Locked,
    };

    // Everything he needs ships with the game. One already on is let run, so it can still be
    // turned off.
    static string? Locked() => Installed || Option.On ? null : "GAME FILES MISSING";

    public static readonly Command NeedleKey = new(
        "player.villains.scarecrow_needle", "NEEDLE",
        "Lunges at a thug near where the camera looks and jabs his injector in. A calm thug panics. One already in a panic collapses. Press again once it lands to jab again.",
        Needle)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "SCARECROW IS OFF",
    };

    public static readonly Command DartKey = new(
        "player.villains.scarecrow_dart", "FEAR DART",
        "Flicks a fear toxin dart from his wrist at the thug nearest the crosshair. An unarmed thug panics for a while and a gunman cowers. He carries three, and they fill back up.",
        Dart)
    {
        Quiet = true,
        Block = static () => !Option.On ? "SCARECROW IS OFF" : _loadTried && _wrist is null ? "DART NOT FOUND" : null,
    };

    public static readonly Command ToxinKey = new(
        "player.villains.scarecrow_toxin", "FEAR TOXIN",
        "Raises his injector and vents fear toxin that rolls out round him. Every thug it reaches panics. It takes a while to fill back up.",
        Toxin)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "SCARECROW IS OFF",
    };

    public static readonly Command BroadcastKey = new(
        "player.villains.scarecrow_broadcast", "BROADCAST",
        "Goes on air the way he took over every screen in the city. Screens around him cut to his face and his voice plays from them. Thugs who hear it run for their lives, or cower where they can't.",
        Broadcast)
    {
        Quiet = true,
        Block = static () => Option.On ? null : "SCARECROW IS OFF",
    };

    public static readonly Toggle Hud = new(
        "player.villains.scarecrow_hud", "HUD",
        "Shows his fear study in the bottom left: his toxin, a trace of the fear in the thugs around him, his moves and what he says. On air it shows his broadcast.",
        on: true)
    {
        Preference = true,
    };

    // The keys he comes with. The player's own are read after these.
    public static void DefaultKeys()
    {
        Hotkeys.Add(NeedleKey, key: "LButton", pad: "X");
        Hotkeys.Add(DartKey, key: "RButton", pad: "Y");
        Hotkeys.Add(ToxinKey, key: "Q", pad: "RB");
        Hotkeys.Add(BroadcastKey, key: "E", pad: "B");
    }

    // Everything a section lists is declared in this file. Static fields in another part of the
    // class may not be set up yet when this is.
    public static readonly Catalog.Section[] Sections =
    {
        new("SCARECROW", Option, NeedleKey, DartKey, ToxinKey, BroadcastKey, Hud),
    };

    static void Keep()
    {
        if (!Villains.Claim(Option))
        {
            Option.SetTo(false);
            return;
        }

        Dress();
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

        Villains.Keep(player, controller, Voice);
        Limit(player, controller);
        if (player.bGliding)
        {
            controller.GotoState(FallingMode);
        }

        if (controller.IsInCombat(false) && !Acting)
        {
            Speech.Quiet(Taunts[Random.Shared.Next(Taunts.Length)], TauntSeconds);
        }

        var body = WornBody();
        Act(player, own, body);
        Animate(player, body);
        Refill();
        Fly(player);
        KeepCloud(player);
        KeepBroadcast();
        Watch(player);
        ShowHud();
    }

    // His look goes on once each time SCARECROW is turned on. Changing DISGUISE after that is
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

    static void Limit(RPawnPlayer player, RPlayerController controller)
    {
        NoGlide.Hold(controller, true);
        NoPawnGlide.Hold(player, false);
        NoTakedowns.Hold(controller, true);
        NoPawnTakedowns.Hold(player, true);
        if (controller is RPlayerControllerCombat combat)
        {
            NoFists.Hold(combat, true);
            NoCapeStun.Hold(combat, true);
        }

        if (World.Info is { IsValid: true } game)
        {
            NoGrapple.Hold(game, true);
            if (game.CombatManager is { IsValid: true } manager)
            {
                NoGrabs.Hold(manager, true);
            }
        }
    }

    static void Unlimit()
    {
        NoGlide.Release();
        NoPawnGlide.Release();
        NoTakedowns.Release();
        NoPawnTakedowns.Release();
        NoFists.Release();
        NoCapeStun.Release();
        NoGrapple.Release();
        NoGrabs.Release();
    }

    // In the car or a cutscene Batman has all his own moves back.
    static void Away()
    {
        Unact();
        Animate(null, null);
        PutOutCloud();
        OffAir();
        VillainHud.Show(null);
        Villains.Release();
        Unlimit();
        Speech.Release();
    }

    static void Stop()
    {
        Away();
        Unload();
        Speech.Unload();
        _nextDress = 0f;
        if (_dressed)
        {
            _dressed = false;
            Disguise.TakeOffLook(LookPackage);
        }
    }
}
