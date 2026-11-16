#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// BATMOBILE > SKIN, the BODYWORK rows. The native car tick builds MuddyCar up off road, washes it
// off over time, and pushes it into the body materials only on the frame it changes. So a wash has
// to set the materials too.
static class CarWash
{
    static readonly FName MudDensity = new("Mud_Density");

    public static readonly Command Clean = new(
        "batmobile.skin.clean", "CLEAN BATMOBILE",
        "Washes all the mud off the Batmobile.",
        Wash)
    {
        Block = Car.Missing,
    };

    public static readonly Toggle KeepClean = new(
        "batmobile.skin.keep_clean", "KEEP IT CLEAN",
        "Mud never sticks to the Batmobile.")
    {
        Tick = StayClean,
    };

    static Outcome Wash()
    {
        if (Car.Find() is not { } car)
        {
            return Outcome.Failed("NO CAR");
        }

        if (car.MuddyCar <= 0f)
        {
            return Outcome.Unchanged("ALREADY CLEAN");
        }

        Rinse(car);
        return Outcome.Done("CLEAN");
    }

    static void StayClean()
    {
        if (Car.Find() is { MuddyCar: > 0f } car)
        {
            Rinse(car);
        }
    }

    static void Rinse(RVehicleBatmobileBase car)
    {
        car.MuddyCar = 0f;
        var materials = car.MuddyCarMaterials;
        for (var i = 0; i < materials.Count; i++)
        {
            if (materials[i] is { IsValid: true } material)
            {
                material.SetScalarParameterValue(MudDensity, 0f);
            }
        }
    }
}
