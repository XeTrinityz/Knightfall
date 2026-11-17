#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// PLAYER > GADGETS > GRAPNEL and LINE LAUNCHER. Every slider scales the game's own values, so 1X is
// always the normal game. The first four save under player.movement, where existing settings files
// already have them.
static class Grapnel
{
    static readonly Held<RGrappleGun, int> StackLimit = new("grapple boost stacks", gun => gun.GrappleBoostLvl, (gun, stacks) => gun.GrappleBoostLvl = stacks);
    static readonly Held<RLineLauncher, bool> TightropeFlag = new("line launcher tightrope", launcher => launcher.AlwaysAllowLineLauncherTightrope, (launcher, on) => launcher.AlwaysAllowLineLauncherTightrope = on);

    static readonly Func<int, int> AtLeastStacks = static was => Math.Max(was, (int)BoostStacks!.Value);

    static readonly Scale<RPawnPlayer> Speed = new("grapple speed",
        (tick, restore) => new Slider(
            "player.movement.grapple_speed", "GRAPPLE SPEED",
            "How fast the grapple gun pulls you up to a ledge.",
            min: 0.5, max: 3, step: 0.25, value: 1, unit: "X")
        {
            Tick = tick,
            Restore = restore,
        },
        () => World.Player,
        Fields.Of<RPawnPlayer>(pawn => pawn.MinGrappleSpeed, (pawn, value) => pawn.MinGrappleSpeed = value),
        Fields.Of<RPawnPlayer>(pawn => pawn.MaxGrappleSpeed, (pawn, value) => pawn.MaxGrappleSpeed = value));

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
        Fields.Of<RGrappleGun>(gun => gun.MaxGrappleBoostExtraSpeed, (gun, value) => gun.MaxGrappleBoostExtraSpeed = value),
        Fields.Of<RGrappleGun>(gun => gun.MaxGrappleBoostExtraSpeedMk3, (gun, value) => gun.MaxGrappleBoostExtraSpeedMk3 = value),
        Fields.Of<RGrappleGun>(gun => gun.MaxGrappleBoostExtraSpeedMk4, (gun, value) => gun.MaxGrappleBoostExtraSpeedMk4 = value),
        Fields.Of<RGrappleGun>(gun => gun.MaxGrappleBoostExtraHeight, (gun, value) => gun.MaxGrappleBoostExtraHeight = value),
        Fields.Of<RGrappleGun>(gun => gun.MaxGrappleBoostExtraHeightMk3, (gun, value) => gun.MaxGrappleBoostExtraHeightMk3 = value),
        Fields.Of<RGrappleGun>(gun => gun.MaxGrappleBoostExtraHeightMk4, (gun, value) => gun.MaxGrappleBoostExtraHeightMk4 = value));

    static readonly Scale<RGrappleGun> Height = new("grapple height",
        (tick, restore) => new Slider(
            "player.movement.grapple_height", "GRAPPLE HEIGHT",
            "How high a ledge the grapple gun can reach.",
            min: 0.5, max: 3, step: 0.25, value: 1, unit: "X")
        {
            Tick = tick,
            Restore = restore,
        },
        () => World.Player?.GrappleGun,
        Fields.Of<RGrappleGun>(gun => gun.MaxGrappleHeight, (gun, value) => gun.MaxGrappleHeight = value));

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
        Fields.Of<RLineLauncher>(launcher => launcher.ZipForwardMaxSpeed, (launcher, value) => launcher.ZipForwardMaxSpeed = value),
        Fields.Of<RLineLauncher>(launcher => launcher.ZipForwardAccel, (launcher, value) => launcher.ZipForwardAccel = value));

    public static readonly Slider BoostStacks = new(
        "player.gadgets.boost_stacks", "GRAPPLE BOOST STACKS",
        "How many grapple boosts in a row stack up, each throwing you further. OFF is what your upgrades allow. Needs the V8.04 batsuit.",
        min: 0, max: 4, step: 1, value: 0, zero: "OFF")
    {
        Block = static () => Equipment.Missing(static pawn => pawn.GrappleGun, "NO GRAPPLE GUN") ?? (PlayerLocks.NewBatsuit ? null : "NEEDS BATSUIT V8.04"),
        Tick = KeepStacks,
        Restore = StackLimit.Release,
    };

    public static readonly Toggle Tightrope = new(
        "player.gadgets.tightrope", "LINE LAUNCHER TIGHTROPE",
        "Walk a line launcher line like a tightrope without the upgrade.")
    {
        Block = static () => Equipment.Missing(static pawn => pawn.LineLauncher, "NO LINE LAUNCHER"),
        Tick = KeepTightrope,
        Restore = TightropeFlag.Release,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("GRAPNEL", Speed.Slider, Boost.Slider, Height.Slider, BoostStacks, GadgetUpgrades.BoostTakedown),
        new("LINE LAUNCHER", Zip.Slider, Tightrope),
    };

    static void KeepStacks()
    {
        if (World.Player?.GrappleGun is { IsValid: true } gun)
        {
            StackLimit.Hold(gun, AtLeastStacks);
        }
    }

    static void KeepTightrope()
    {
        if (World.Player?.LineLauncher is { IsValid: true } launcher)
        {
            TightropeFlag.Hold(launcher, true);
        }
    }
}
