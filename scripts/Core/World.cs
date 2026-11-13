#nullable enable

using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Game state the rest of the mod asks about, looked up once and shared.
static class World
{
    // Refreshed every drawn frame, so it is right while the game is paused too.
    public static bool Paused { get; private set; } = true;

    // The rest is refreshed every game tick, which is when options act on the game.
    public static RPawnPlayer? Player { get; private set; }

    public static RPlayerController? Controller { get; private set; }

    public static RGameInfo? Info { get; private set; }

    // The Batmobile the player is in, driving it or by remote.
    public static RVehicleBatmobileBase? Driving { get; private set; }

    // Seconds since the last tick, in game time. Slow motion makes it smaller.
    public static float Delta { get; private set; }

    // Game time in seconds. It starts over when a new world loads.
    public static float Now { get; private set; }

    // Goes up by one each time a new world takes over, like a load, a restart or a retry.
    public static int Epoch { get; private set; }

    static WorldInfo? _seen;
    static bool? _inCity;

    // The open city's own map. A story pack or a challenge map is a world of its own, and things
    // placed for the city don't belong in it. Asked once per world.
    public static bool InCity
    {
        get
        {
            if (_inCity is null && _seen is { IsValid: true } world)
            {
                _inCity = world.GetPathName(null).StartsWith("BatEntry.", StringComparison.OrdinalIgnoreCase);
            }

            return _inCity ?? false;
        }
    }

    // Runs every drawn frame. A new world took the last one's player and controller with it, so
    // what was looked up in the last tick is dropped before anything drawn can ask for it.
    public static void Refresh()
    {
        if (!Game.TryGetWorldInfo(out var world))
        {
            Paused = true;
            return;
        }

        Paused = world.Pauser is not null;

        // The SDK keeps one wrapper for each live object, so another wrapper is another world.
        if (!ReferenceEquals(world, _seen))
        {
            if (_seen is not null)
            {
                Epoch++;
                Player = null;
                Controller = null;
                Info = null;
                Driving = null;
            }

            _seen = world;
            _inCity = null;
        }
    }

    public static void Tick()
    {
        Player = null;
        Controller = null;
        Info = null;
        Driving = null;
        Delta = 0f;

        Refresh();
        if (!Game.TryGetWorldInfo(out var world))
        {
            return;
        }

        Delta = world.DeltaSeconds;
        Now = world.TimeSeconds;
        Info = Game.TryGetGameInfo(out var info) ? info : null;

        if (Game.TryGetPlayerController(out var controller))
        {
            Controller = controller;

            // In the Batmobile the controller's pawn is the car. The combat pawn can be empty then,
            // and the character being played is still the original pawn.
            var pawn = controller.Pawn as RPawnPlayer ?? controller.CombatPawn ?? controller.OriginalPawn;
            // A pawn being torn down, like during a level change, can point at freed data.
            Player = pawn is { IsValid: true, bDeleteMe: false, bPendingDelete: false } ? pawn : null;
            Driving = controller.Pawn is RVehicleBatmobileBase { IsValid: true, bDeleteMe: false, bPendingDelete: false } car ? car : null;
        }
    }
}
