#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// WORLD > CITY, the STREETS rows.
static class Streets
{
    // How high a flipped car is lifted first, so it has room to land on its roof.
    const float FlipLift = 300f;

    // The game's own off switch for street life, the one its debug options use.
    static readonly Held<RPopulationManager, bool> Off = new(
        "street population", people => people.bForceDisable, (people, off) => people.bForceDisable = off);

    // The street life cleared once already, so it isn't cleared on every tick.
    static RPopulationManager? _cleared;

    public static readonly Toggle Empty = new(
        "world.city.empty_streets", "EMPTY STREETS",
        "Clears roaming thugs, rioters and militia patrols off the streets and stops new ones. Mission enemies and militia bases stay. Off lets the city refill.")
    {
        Tick = KeepEmpty,
        Restore = Refill,
    };

    public static readonly Command Flip = new(
        "world.city.flip_enemy_cars", "FLIP ENEMY CARS",
        "Turns every thug car, militia tank and APC around you upside down. Police cars, drones and the Batmobile are left alone.",
        FlipAll);

    public static readonly Catalog.Section Section = new("STREETS", Empty, Flip);

    static void KeepEmpty()
    {
        if (World.Info is not { } game || game.IsChallengeMode() || game.PopulationManager is not { IsValid: true } people)
        {
            return;
        }

        Off.Hold(people, true);

        // The game's own clear-out, which it runs before a Most Wanted mission or AR challenge.
        // It leaves mission enemies alone.
        if (!ReferenceEquals(_cleared, people))
        {
            _cleared = people;
            people.bRemovePopulationOnNextTick = true;
        }
    }

    static void Refill()
    {
        Off.Release();
        _cleared = null;
    }

    static Outcome FlipAll()
    {
        if (World.Info is not { } game)
        {
            return Outcome.NoPlayer;
        }

        var flipped = 0;
        var vehicles = game.Vehicles;
        foreach (var vehicle in vehicles)
        {
            if (!Enemy(vehicle))
            {
                continue;
            }

            // BmSDK's Rotator is in degrees. Position and turn go in one call, so neither undoes
            // the other.
            var turned = vehicle.Rotation;
            turned.Roll += 180f;
            vehicle.SetRBTransform(vehicle.Location + new Vector3(0f, 0f, FlipLift), turned, true);
            flipped++;
        }

        if (flipped > 0)
        {
            return Outcome.Done($"{flipped} FLIPPED");
        }

        return Outcome.Unchanged("NONE AROUND");
    }

    // Only computer-driven vehicles, so never your Batmobile. Police cars are the ones the game
    // calls friendly. Drones are left out, since they fly and would only spin.
    static bool Enemy(RVehicle? vehicle) =>
        vehicle is RVehicleNPC { IsValid: true, bDeleteMe: false, IsFriendly: false, IsHackedFriendly: false }
        and not RVehicleFlying
        and not RVehicleNPCBatmobile;
}
