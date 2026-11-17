#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// PLAYER > GADGETS rows that cover every gadget the player carries, and the lock reasons the
// single gadget rows share.
static class Equipment
{
    // A cooldown that jumps up by more than this has just started.
    const float StartedAtLeast = 0.5f;

    static readonly List<RInventoryGadget> Gadgets = new();
    static readonly Dictionary<nint, float> LastWait = new();
    static int _epoch;

    public static readonly Slider RechargeTime = new(
        "player.gadgets.recharge_time", "GADGET RECHARGE TIME",
        "How long a gadget takes to come back after you use it. 1X is the game's. NONE brings it back at once. Batarangs, gel, freeze blasts, smoke pellets, the batclaw, the voice synthesizer and the remote charge all count.",
        min: 0, max: 1, step: 0.05, value: 1, unit: "X", zero: "NONE")
    {
        Block = static () => Missing(static pawn => pawn.Batarang, "NO GADGETS"),
        Tick = ShortenWaits,
        Restore = LastWait.Clear,
    };

    public static readonly Toggle UnlimitedAmmo = new(
        "player.gadgets.unlimited_ammo", "UNLIMITED GADGET AMMO",
        "Batarangs, gel, freeze blasts and the remote charge never run out of charges. They still have to recharge between uses. The disruptor has its own row.")
    {
        Block = static () => Missing(static pawn => pawn.Batarang, "NO GADGETS"),
        Tick = Refill,
    };

    // Why a row for one gadget can't be used right now, or null when it can.
    public static string? Missing<T>(Func<RPawnPlayer, T?> pick, string what)
        where T : GameObject
    {
        if (World.Player is not { IsValid: true } pawn)
        {
            return "NO PLAYER";
        }

        if (Villains.On)
        {
            return "NOT AS A VILLAIN";
        }

        return pick(pawn) is { IsValid: true } ? null : what;
    }

    static void Collect(bool forAmmo)
    {
        Gadgets.Clear();
        if (World.Player is not { IsValid: true } pawn)
        {
            return;
        }

        Add(pawn.Batarang);
        Add(pawn.RCBatarang);
        Add(pawn.SonicBatarang);
        Add(pawn.GooSpray);
        Add(pawn.FreezeSpray);
        Add(pawn.MagneticBlast);
        if (!forAmmo)
        {
            Add(pawn.HarpoonGun);
            Add(pawn.VoiceSynthesiser);
            Add(pawn.SmokeBomb);
        }
    }

    static void Add(RInventoryGadget? gadget)
    {
        if (gadget is { IsValid: true })
        {
            Gadgets.Add(gadget);
        }
    }

    // The wait is set by each gadget when it fires, in different places, so a wait that starts
    // is cut once here instead of changing every one of them.
    static void ShortenWaits()
    {
        if (_epoch != World.Epoch)
        {
            _epoch = World.Epoch;
            LastWait.Clear();
        }

        Collect(false);
        var scale = (float)RechargeTime.Value;
        foreach (var gadget in Gadgets)
        {
            var now = gadget.CurrentRechargeTime;
            if (!LastWait.TryGetValue(gadget.Ptr, out var before))
            {
                before = now;
            }

            if (now > before + StartedAtLeast)
            {
                now *= scale;
                gadget.CurrentRechargeTime = now;
            }

            LastWait[gadget.Ptr] = now;
        }
    }

    static void Refill()
    {
        Collect(true);
        foreach (var gadget in Gadgets)
        {
            if (gadget.MaxAmmo > 0 && gadget.Ammo < gadget.MaxAmmo)
            {
                gadget.RestockAmmo();
            }
        }
    }
}
