#nullable enable

using BmSDK.BmGame;

namespace Knightfall;

// The player's Batmobile, driven or parked.
static class Car
{
    const float SearchSeconds = 1f;

    static RVehicleBatmobileBase? _found;
    static float _searchedAt = -1f;

    // Refreshed every game tick for rows that lock themselves, which are asked while drawing.
    public static bool Here { get; private set; }

    public static bool ByRemote { get; private set; }

    public static void Tick()
    {
        Here = Find() is not null;
        ByRemote = World.Driving is { bRemoteDrive: true };
    }

    public static string? Missing() => Here ? null : "NO BATMOBILE HERE";

    // For rows that only work in the car, driven or by remote.
    public static string? NotDriving() => World.Driving is null ? "NOT IN THE CAR" : null;

    // For rows that only work on foot.
    public static string? InCar() => World.Driving is null ? null : "GET OUT FIRST";

    public static RVehicleBatmobileBase? Find()
    {
        if (World.Driving is { } driving)
        {
            return driving;
        }

        if (World.Info?.GetBatmobile() is { IsValid: true, bDeleteMe: false, IsClassDefaultObject: false } known)
        {
            return known;
        }

        // The game's own pointer is empty in some places. Its list of Batmobiles also holds the
        // template new ones are made from, which sits at the world origin, so only a car placed
        // in a loaded level counts. Walking the list is only done now and then.
        if (_found is { IsValid: true, bDeleteMe: false })
        {
            return _found;
        }

        if (MathF.Abs(World.Now - _searchedAt) < SearchSeconds)
        {
            return null;
        }

        _searchedAt = World.Now;
        _found = null;
        foreach (var car in Game.FindObjects<RVehicleBatmobileBase>())
        {
            if (car.IsValid && !car.bDeleteMe && car.GetPathName(null).Contains("PersistentLevel", StringComparison.Ordinal))
            {
                _found = car;
                break;
            }
        }

        return _found;
    }

    public static RVehicleSimBatmobile? Sim(RVehicleBatmobileBase car) =>
        car.SimObj is RVehicleSimBatmobile { IsValid: true } sim ? sim : null;
}
