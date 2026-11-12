#nullable enable

using System.Numerics;
using BmSDK.BmGame;
using SelfDrive = BmSDK.BmGame.RVehicle.ESelfDriveMode;

namespace Knightfall;

// The remote control gadget's drive, without the gadget. Forcing it skips the game's "can the
// car be called here" check. Its range is the edge of the loaded city, and that stays.
static class Remote
{
    static readonly Held<RVehicleBatmobileBase, bool> KeepInFights = new(
        "remote control in fights",
        car => car.DoNotBreakRemoteControlIfBatmanInCombat,
        (car, keep) => car.DoNotBreakRemoteControlIfBatmanInCombat = keep);

    public static readonly Command Drive = new(
        "batmobile.driving.remote_control", "REMOTE CONTROL",
        "Drives the Batmobile by remote without the gadget, even where the game won't let you call it. Press again to stop.",
        Toggle);

    public static readonly Toggle InFights = new(
        "batmobile.driving.remote_in_fights", "REMOTE CONTROL IN FIGHTS",
        "Enemies going for Batman don't cut the remote control off, and it can be started mid-fight.")
    {
        Tick = HoldInFights,
        Restore = KeepInFights.Release,
    };

    static void HoldInFights()
    {
        if (Car.Find() is { } car)
        {
            KeepInFights.Hold(car, true);
        }
    }

    static Outcome Toggle()
    {
        if (World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        if (controller.IsBatmobileRemoteDrive())
        {
            return World.Driving is { } driven && driven.DriverLeave(true, false) ? Outcome.Done("ENDED") : Outcome.Failed("REFUSED");
        }

        if (World.Driving is not null)
        {
            return Outcome.Failed("IN THE CAR");
        }

        if (World.Player is not { } player)
        {
            return Outcome.NoPlayer;
        }

        if (!InCity(player.Location))
        {
            return Outcome.Failed("INDOORS");
        }

        if (Car.Find() is not { } car)
        {
            return Outcome.Failed("NO CAR");
        }

        // A car the story is holding or driving stays the story's.
        if (car.IsStolen || car.bControlledBySeqAct || car.SelfDriveMode != SelfDrive.SELFDRIVE_None)
        {
            return Outcome.Failed("BUSY");
        }

        // The game refuses while Batman is under attack unless the area allows it. That is lifted
        // for this one call only.
        ref var area = ref controller.AffectPlayerInfo;
        var allowed = area.CanUseRemoteControlInCombat;
        bool started;
        try
        {
            if (InFights.On)
            {
                area.CanUseRemoteControlInCombat = true;
            }

            started = controller.RemoteControlBatmobile(car, true);
        }
        finally
        {
            area.CanUseRemoteControlInCombat = allowed;
        }

        return started ? Outcome.Done("STARTED") : Outcome.Failed(InFights.On ? "REFUSED" : "UNDER ATTACK");
    }

    // The open city is made of district levels named City. Inside a building there is none, and
    // the car can't be brought in.
    static bool InCity(Vector3 at)
    {
        foreach (var volume in Game.FindObjects<RLevelVolume>())
        {
            if (volume.IsValid && volume.Level.ToString().StartsWith("City", StringComparison.OrdinalIgnoreCase) && volume.EncompassesPoint(at))
            {
                return true;
            }
        }

        return false;
    }
}
