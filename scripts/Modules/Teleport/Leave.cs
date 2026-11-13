#nullable enable

using System.Numerics;
using BmSDK.BmGame;

namespace Knightfall;

// TELEPORT > MOVE, the way out of an interior. Far teleports don't work indoors, so this uses the
// room's own door to the city. The door puts the player on its city side and loads the city the
// same way walking through it does.
static class Leave
{
    public static readonly Command Building = new(
        "teleport.move.leave_building", "LEAVE BUILDING",
        "Takes you outside through the door of the room you're in, as if you walked out. Works where that room has a door to the city.",
        Go)
    {
        Block = static () => !World.InCity ? "NOT IN THE CITY" : Teleport.Away is null ? "NOT INDOORS" : null,
    };

    public static readonly Catalog.Section Section = new("INDOORS", Building);

    static Outcome Go()
    {
        if (World.Player is not { } player || World.Controller is not { } controller || World.Info is not { } info || Teleport.Ri is not { } ri)
        {
            return Outcome.NoPlayer;
        }

        if (controller.bCinematicMode || player.IsControlledByMatinee())
        {
            return Outcome.Failed("NOT NOW");
        }

        if (ri.IsOverworldGameplay())
        {
            return Outcome.Unchanged("NOT INDOORS");
        }

        var city = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var volumes = info.LevelVolumeList;
        for (var i = 0; i < volumes.Count; i++)
        {
            if (volumes[i].IsCity)
            {
                city.Add(volumes[i].LevelName.ToString());
            }
        }

        var here = ri.CurrentLevel.ToString();
        var doors = info.LevelTransitions;
        RLevelTransition? best = null;
        var nearest = float.MaxValue;
        var locked = false;
        for (var i = 0; i < doors.Count; i++)
        {
            if (doors[i] is not { IsValid: true } door)
            {
                continue;
            }

            var front = door.LevelInFront.ToString();
            var behind = door.LevelBehind.ToString();
            var leads = (Same(front, here) && city.Contains(behind)) || (Same(behind, here) && city.Contains(front));
            if (!leads)
            {
                continue;
            }

            // A door the game has shut, like in a lockdown or a mission, stays shut.
            if (door is RLevelTransitionDoorBase { IsValid: true } shut && (shut.DoorLocked || !shut.bCanUse || shut.IsGlobalLockActive(controller)))
            {
                locked = true;
                continue;
            }

            var distance = Vector3.DistanceSquared(door.Location, player.Location);
            if (distance < nearest)
            {
                nearest = distance;
                best = door;
            }
        }

        if (best is null)
        {
            return Outcome.Failed(locked ? "DOOR LOCKED" : "NO WAY OUT HERE");
        }

        // The door sends the player to the side it isn't entered from.
        best.TeleportPlayerToHere(city.Contains(best.LevelBehind.ToString()));
        Log.Info($"Leaving {here} through {best.Name}.");
        return Outcome.Done("OUTSIDE");
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
