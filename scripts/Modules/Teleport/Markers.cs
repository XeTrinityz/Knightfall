#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// The player's map waypoint and the current mission marker. Exact is false for a pin dropped on
// the map, whose height is made up.
static class Markers
{
    public static (Vector3 At, bool Exact)? Waypoint()
    {
        if (World.Info?.PersistentData is not { IsValid: true } data || !data.GetCustomWaypointEnabled())
        {
            return null;
        }

        return (data.GetCustomWaypoint(true), data.CustomWaypoint_Type != RPersistentData.ECustomWayPointType.CustomWayType_Proximity);
    }

    public static (Vector3 At, bool Exact)? Objective()
    {
        if (World.Info is not { } info)
        {
            return null;
        }

        var markers = info.ObjectiveMarkers;
        var picked = info.HighestPriorityNonCustomObjectiveMarker;
        if (picked >= 0 && picked < markers.Count && Usable(markers[picked]))
        {
            return (markers[picked].Location, true);
        }

        RObjectiveMarker? best = null;
        foreach (var marker in markers)
        {
            if (Usable(marker) && (best is null || marker.Priority > best.Priority))
            {
                best = marker;
            }
        }

        if (best is not null)
        {
            return (best.Location, true);
        }

        var stored = info.PersistentData is { IsValid: true } data ? data.vObjectiveLocation : Vector3.Zero;
        return stored == Vector3.Zero ? null : (stored, true);
    }

    // The game's waypoint cheat sets the pin, compass, map marker and route in one go.
    public static void Set(Vector3 at) => Game.GetCheatManager().SetWaypoint(at.X, at.Y, at.Z);

    // Undoes what the map does when a pin goes down. The controller notices the pin is off on its
    // next frame and drops the marker in the world itself, as when the map removes one.
    public static bool Clear()
    {
        if (World.Info is not { } info || info.PersistentData is not { IsValid: true } data || !data.GetCustomWaypointEnabled())
        {
            return false;
        }

        var at = data.GetCustomWaypoint(false);
        var name = data.CustomWaypointName;
        data.SetCustomWaypointEnabled(false);
        data.CustomWaypoint_NotReached = 0;
        info.Map3DRenderingData.WaypointPosition = Vector4.Zero;

        if (World.Controller is { } controller)
        {
            // The waypoint cheat adds its own compass entry called Custom.
            if (controller.ModularHud is { IsValid: true } hud && hud.TargetsModule is { IsValid: true } targets)
            {
                targets.DeregisterCompassItem("Custom");
            }

            if (controller.Map3DActRef is { IsValid: true } map)
            {
                map.SetUserMarker(false, false, at.X, at.Y, at.Z, name);
                map.CalcRoute(false, name, at.X, at.Y, at.Z);
            }
        }

        return true;
    }

    static bool Usable(RObjectiveMarker? marker) => marker is { IsValid: true, bDeleteMe: false, IsMarkerActive: true, IsCustomWaypoint: false };
}
