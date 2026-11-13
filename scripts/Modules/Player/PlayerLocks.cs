#nullable enable

using BmSDK.BmGame;

namespace Knightfall;

// What PLAYER rows need from the game to lock themselves. A lock is asked every frame it is drawn,
// so the game is read here on the tick, a few times a second.
static class PlayerLocks
{
    const float LookEvery = 0.25f;
    const int ArmourPools = 2;

    static float _lookedAt = -1f;

    public static bool Challenge { get; private set; }

    public static bool OwnsArmour { get; private set; }

    // The V8.04 batsuit, which the game asks for before it allows a fear takedown.
    public static bool NewBatsuit { get; private set; }

    public static string? InChallenge() => Challenge ? "NOT IN CHALLENGES" : null;

    public static void Tick()
    {
        if (MathF.Abs(World.Now - _lookedAt) < LookEvery)
        {
            return;
        }

        _lookedAt = World.Now;
        Challenge = World.Info is { } game && game.IsChallengeMode();
        NewBatsuit = World.Controller?.OriginalPawn is { IsValid: true, bUpgradedBatsuit: true };

        var owns = false;
        if (World.Player is { } pawn)
        {
            var maxima = pawn.CurrentArmourMaxima;
            for (var i = 0; i < Math.Min(ArmourPools, maxima.Count); i++)
            {
                owns |= maxima[i] > 0;
            }
        }

        OwnsArmour = owns;
    }
}
