#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Framework;

namespace Knightfall;

// His fighting, the way Batman fights when the Joker takes over his mind on Stagg's airship. The
// game has two switches for it. One makes every counter a throw counter. The other lets a strike
// pick a downed thug up off the floor to keep beating him, and gives him that scene's hit
// reactions. The game's walk-only state for the scene is left out, so he keeps all his moves.
static partial class JokerVillain
{
    static readonly Held<RPlayerController, bool> Jokerfied = new(
        "joker fighting", controller => controller.bJokerfiedBatman, (controller, on) => controller.bJokerfiedBatman = on);

    static readonly Held<RPawnPlayerCombat, float> JokerfiedCounters = new(
        "joker counters", pawn => pawn.JokerfiedAttacks, (pawn, count) => pawn.JokerfiedAttacks = count);

    static void Fight(RPawnPlayer player, RPlayerController controller)
    {
        if (!Brutal.On)
        {
            StopFighting();
            return;
        }

        Jokerfied.Hold(controller, true);
        if (player is RPawnPlayerCombat fighter)
        {
            JokerfiedCounters.Hold(fighter, 1f);
        }
    }

    static void StopFighting()
    {
        Jokerfied.Release();
        JokerfiedCounters.Release();
    }

    // In that scene the map button plays one of his lines instead of opening the map. Here the
    // map opens as usual.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.OpenLevelUpOrMap))]
    static void OpenLevelUpOrMap(RPlayerController self)
    {
        var was = self.bJokerfiedBatman;
        if (was && Option.On)
        {
            self.bJokerfiedBatman = false;
        }

        try
        {
            Redirects.RunOriginal();
        }
        finally
        {
            if (was && Option.On && self.IsValid)
            {
                self.bJokerfiedBatman = true;
            }
        }
    }
}
