#nullable enable

using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// TELEPORT > PLACES, the SAVED SPOTS rows. Spots are kept in settings.toml, without the city's
// shift, so they last between sessions and still match after an interior.
static class Spots
{
    const int Count = 10;
    const string Section = "teleport.spots";

    static readonly Vector3?[] Saved = new Vector3?[Count];
    static bool _read;

    public static readonly Choice Spot = new(
        "teleport.places.spot", "SPOT",
        "One of ten places of your own. Each is named by the area it is in.",
        Labels(), saved: false);

    public static readonly Command SaveHere = new(
        "teleport.places.save_spot", "SAVE HERE",
        "Keeps where you are now in the spot picked above. It is kept between sessions.",
        Save);

    public static readonly Command GoThere = new(
        "teleport.places.go_spot", "GO TO SPOT",
        "Takes you back to the spot picked above.",
        () => Saved[Spot.Index] is { } at ? Teleport.Start(GoThere!, at + Teleport.CityOffset, true) : Outcome.Failed("EMPTY"));

    public static readonly Command MarkThere = new(
        "teleport.places.waypoint_spot", "WAYPOINT TO SPOT",
        "Sets your map waypoint on the spot picked above.",
        Mark);

    public static readonly Command Clear = new(
        "teleport.places.clear_spot", "CLEAR SPOT",
        "Empties the spot picked above.",
        Forget);

    static (string, string)[] Labels()
    {
        if (!_read)
        {
            _read = true;
            for (var i = 0; i < Count; i++)
            {
                Saved[i] = Parse(Settings.Text(Section, Key(i), ""));
            }
        }

        return Enumerable.Range(0, Count).Select(i => (Key(i), Kit.Number(i + 1) + "  " + (Saved[i] is { } at ? Name(at) : "EMPTY"))).ToArray();
    }

    // Reads the spots again after settings.toml was emptied.
    public static void Reload()
    {
        _read = false;
        Array.Clear(Saved);
        Refresh();
    }

    // Area names only come once the map is read, so the labels are made again when they might
    // have changed.
    public static void Refresh() => Spot.SetOptions(Labels(), Spot.Index);

    static string Name(Vector3 at) => Places.AreaNear(at) is { Length: > 0 } area ? area : "SAVED";

    static Outcome Save()
    {
        Pawn? mover = World.Driving is { IsValid: true } driving ? driving : World.Player;
        if (mover is not { IsValid: true })
        {
            return Outcome.NoPlayer;
        }

        var at = mover.Location - Teleport.CityOffset;
        Saved[Spot.Index] = at;
        Settings.Set(Section, Key(Spot.Index), string.Create(CultureInfo.InvariantCulture, $"{at.X:0} {at.Y:0} {at.Z:0}"));
        Refresh();
        return Outcome.Done("SAVED");
    }

    static Outcome Mark()
    {
        if (Saved[Spot.Index] is not { } spot)
        {
            return Outcome.Failed("EMPTY");
        }

        var at = spot + Teleport.CityOffset;
        Markers.Set(at);
        return Outcome.Done("SET");
    }

    static Outcome Forget()
    {
        if (Saved[Spot.Index] is null)
        {
            return Outcome.Unchanged("EMPTY");
        }

        Saved[Spot.Index] = null;
        Settings.Set(Section, Key(Spot.Index), "");
        Refresh();
        return Outcome.Done("CLEARED");
    }

    static string Key(int i) => "spot_" + (i + 1);

    static Vector3? Parse(string? text)
    {
        var parts = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: 3 }
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
                ? new Vector3(x, y, z)
                : null;
    }
}
