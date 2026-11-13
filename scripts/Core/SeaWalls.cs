#nullable enable

using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// A fix for a crash in the game itself. Each vehicle keeps two fake sea walls in the physics
// scene, each an actor and its shape. When the vehicle's physics is shut down the game frees
// both actors but keeps the shape pointers. If that vehicle's physics starts again, the next
// frame it is away from a sea wall the game switches the stale shape off and crashes. The
// vehicle walk runs from the controller's PreRender, so a shape left without its actor is
// cleared just before it.
static class SeaWalls
{
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.PreRender))]
    static void PreRender(RPlayerController self, Canvas canvas)
    {
        try
        {
            if (self.WorldInfo is { Pauser: null } world && world.Game is RGameInfo info)
            {
                var vehicles = info.Vehicles;
                for (var i = 0; i < vehicles.Count; i++)
                {
                    if (vehicles[i] is not { } vehicle)
                    {
                        continue;
                    }

                    if (vehicle.SeaWallPhysxActor0 == 0 && vehicle.SeaWallPhysxShape0 != 0)
                    {
                        vehicle.SeaWallPhysxShape0 = 0;
                    }

                    if (vehicle.SeaWallPhysxActor1 == 0 && vehicle.SeaWallPhysxShape1 != 0)
                    {
                        vehicle.SeaWallPhysxShape1 = 0;
                    }
                }
            }
        }
        catch (Exception error)
        {
            Log.Error("core.sea_walls", "The sea wall check failed", error);
        }

        Redirects.RunOriginal();
    }
}
