#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmScript;
using BmSDK.Framework;

namespace Knightfall;

// TELEPORT > WAYPOINT, the MARK rows. They set or take away the map waypoint without opening the
// map, or take you to one as soon as you set it there.
static class Pin
{
    // How far WAYPOINT AT CROSSHAIR looks, which is past anything that's loaded.
    const float Reach = 200000f;

    // Aimed at the sky, the cross that says so stands this far out.
    const float Nothing = 3000f;

    // Game seconds to wait once the map has shut, so the trip doesn't start under its fade.
    const float Settle = 0.3f;

    static bool _pending;
    static float _readyAt = -1f;
    static int _epoch;

    public static readonly Toggle OnSet = new(
        "teleport.waypoint.on_set", "TELEPORT ON WAYPOINT",
        "Takes you to a waypoint as soon as you set it on the map and go back to the game. Only waypoints you set on the map count.")
    {
        Tick = Watch,
    };

    public static readonly Command AtCrosshair = new(
        "teleport.waypoint.at_crosshair", "WAYPOINT AT CROSSHAIR",
        "Sets your map waypoint wherever you are aiming, with its compass marker and route.",
        Mark)
    {
        Preview = static signs =>
        {
            if (Crosshair.Surface(Reach, out var hit))
            {
                signs.Add(WorldMarks.Pin(hit.Spot, "WAYPOINT", "SETS IT HERE"));
            }
            else if (Crosshair.View(out var eye, out var way))
            {
                signs.Add(WorldMarks.Refused(Outcome.Failed("NOTHING THERE"), eye + way * Nothing, "WAYPOINT"));
            }
        },
    };

    public static readonly Command Clear = new(
        "teleport.waypoint.clear", "CLEAR WAYPOINT",
        "Takes away your map waypoint.",
        () => Markers.Clear() ? Outcome.Done("CLEARED") : Outcome.Unchanged("NO WAYPOINT"))
    {
        Block = static () => Teleport.HasPin ? null : "NO WAYPOINT",
    };

    static Outcome Mark()
    {
        if (Teleport.Mover is null || World.Info is null)
        {
            return Outcome.NoPlayer;
        }

        if (!Crosshair.Surface(Reach, out var hit))
        {
            return Outcome.Failed("NOTHING THERE");
        }

        Markers.Set(hit.Spot);
        return Outcome.Done("SET");
    }

    // The map pauses the game, so the wait starts once it's running again.
    static void Watch()
    {
        // A pin set just before a load belongs to the last world's clock.
        if (_epoch != World.Epoch)
        {
            _epoch = World.Epoch;
            _pending = false;
            _readyAt = -1f;
        }

        if (!_pending)
        {
            return;
        }

        if (World.Paused)
        {
            _readyAt = -1f;
            return;
        }

        if (_readyAt < 0f)
        {
            _readyAt = World.Now + Settle;
            return;
        }

        if (World.Now < _readyAt)
        {
            return;
        }

        _pending = false;
        _readyAt = -1f;
        if (Teleport.Waypoint() is not null)
        {
            Catalog.Pressed(Teleport.ToWaypoint);
        }
    }

    // The map calls this when you place or take away a pin, or pick one of its icons as the
    // waypoint. Nothing else in the game does.
    [Redirect(typeof(RGFxMovieBackScreen_Normal), nameof(RGFxMovieBackScreen_Normal.XI_SetCustomWaypoint))]
    static void SetCustomWaypoint(RGFxMovieBackScreen_Normal self, bool bEnabled, float atX, float atY, float atZ, FString WaypointName, FString WaypointTypeName)
    {
        Redirects.RunOriginal();
        _pending = OnSet.On && bEnabled;
        _readyAt = -1f;
    }
}
