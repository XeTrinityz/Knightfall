#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// PLAYER > GADGETS rows that switch on a gadget's upgrades without buying them. The game asks
// HasUpgrade by name at every place an upgrade matters, so a yes for the names of a row is all
// it takes, and with the row off nothing of ours is left. Nothing is added to the save.
static class GadgetUpgrades
{
    static string? Here() => World.Player is null ? "NO PLAYER" : Villains.On ? "NOT AS A VILLAIN" : null;

    public static readonly Toggle Batarang = new(
        "player.gadgets.batarang_upgrades", "BATARANG UPGRADES",
        "Longer stuns, harder hits in a combo and the combo batarang, without buying them.")
    {
        Block = Here,
    };

    public static readonly Toggle Batclaw = new(
        "player.gadgets.batclaw_upgrades", "BATCLAW UPGRADES",
        "The batclaw rips guns out of thugs' hands and stuns harder, without buying the upgrades.")
    {
        Block = Here,
    };

    public static readonly Toggle BoostTakedown = new(
        "player.gadgets.boost_takedown", "GRAPPLE BOOST TAKEDOWN",
        "Take a thug down straight out of a grapple boost, without the upgrade.")
    {
        Block = Here,
    };

    public static readonly Toggle SimplerHacks = new(
        "player.gadgets.simpler_hacks", "SIMPLER HACKS",
        "Remote hacking device hacks are easier, as if you bought the upgrade.")
    {
        Block = Here,
    };

    static readonly (Toggle Row, string[] Names)[] Rows =
    {
        (Batarang, new[] { "Unlocked_BatarangStunDuration", "Unlocked_BatarangCombatDamage", "Unlocked_ComboBatarang" }),
        (Batclaw, new[] { "Unlocked_BatclawDisarmGun", "Unlocked_BatclawSuperStun" }),
        (BoostTakedown, new[] { "Unlocked_GrappleBoostTd" }),
        (SimplerHacks, new[] { "Unlocked_RHDSimplify" }),
    };

    // Asked a lot, so it only looks at the name once a row is on.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.HasUpgrade))]
    static bool HasUpgrade(RPlayerController self, FString upgradeFlag)
    {
        var owned = Redirects.RunOriginal<bool>();
        if (owned)
        {
            return true;
        }

        try
        {
            string? name = null;
            foreach (var (row, names) in Rows)
            {
                if (!row.On)
                {
                    continue;
                }

                name ??= upgradeFlag.ToString();
                if (Array.IndexOf(names, name) >= 0)
                {
                    return true;
                }
            }
        }
        catch (Exception error)
        {
            Log.Error("gadgets.upgrades", "The gadget upgrade check failed, so the game's answer stands", error);
        }

        return owned;
    }
}
