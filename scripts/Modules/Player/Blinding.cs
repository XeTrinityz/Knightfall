#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > GADGETS, the remote hacking device's rows.
static class Blinding
{
    static readonly Held<RSentryGunBase, float> SentryJam = new("sentry blind time", gun => gun.Settings.jamDuration, (gun, time) => gun.Settings.jamDuration = time);
    static readonly Held<RTurret_WatchtowerBase, float> TowerJam = new("watchtower blind time", gun => gun.jamDuration, (gun, time) => gun.jamDuration = time);

    static readonly Held<RRHD, bool> BlindDrones = new("hacking device drones", device => device.bBatmanHasBlindDroneUpgrade, (device, on) => device.bBatmanHasBlindDroneUpgrade = on);
    static readonly Held<RRHD, bool> SecureTargets = new("hacking device secure", device => device.bCanResonateHS, (device, on) => device.bCanResonateHS = on);

    static string? NoDevice() => Equipment.Missing(static pawn => pawn.RHD, "NO HACKING DEVICE");

    public static readonly Toggle AnyDrone = new(
        "player.gadgets.blind_any_drone", "BLIND ANY DRONE",
        "The remote hacking device can blind drones, vehicles and helicopters without the upgrade.")
    {
        Block = NoDevice,
        Tick = () => HoldDevice(BlindDrones),
        Restore = BlindDrones.Release,
    };

    public static readonly Toggle SecureHacks = new(
        "player.gadgets.hack_secure", "HACK HIGH SECURITY",
        "The remote hacking device works on high security targets without the upgrade.")
    {
        Block = NoDevice,
        Tick = () => HoldDevice(SecureTargets),
        Restore = SecureTargets.Release,
    };

    public static readonly Slider HackRange = new(
        "player.gadgets.hack_range", "HACK RANGE",
        "How far away the remote hacking device can reach a target. 1X is the game's.",
        min: 0.5, max: 3, step: 0.25, value: 1, unit: "X")
    {
        Block = NoDevice,
    };

    public static readonly Toggle ManyTargets = new(
        "player.gadgets.blind_many", "BLIND MANY TARGETS",
        "Blinding a gun, drone or tank with the remote hacking device no longer wakes the last one up. The countdown only shows on the newest.");

    public static readonly Slider BlindTime = new(
        "player.gadgets.blind_time", "BLIND TIME",
        "Multiplies how long a target stays blinded by the remote hacking device. 1X is the game's.",
        min: 1, max: 10, step: 0.5, value: 1, unit: "X")
    {
        Restore = () =>
        {
            SentryJam.Release();
            TowerJam.Release();
        },
    };

    static void HoldDevice(Held<RRHD, bool> flag)
    {
        if (World.Player?.RHD is { IsValid: true } device)
        {
            flag.Hold(device, true);
        }
    }

    // Every target check passes the range it allows through here.
    [Redirect(typeof(RRHD), nameof(RRHD.TargetOutOfRange))]
    static bool TargetOutOfRange(RRHD self, Actor target, float fRange, out float fDistanceUntilInRange)
    {
        var range = fRange;
        try
        {
            if (!HackRange.IsDefault && self.Owner is { IsValid: true } owner && owner.Ptr == World.Player?.Ptr)
            {
                range *= (float)HackRange.Value;
            }
        }
        catch (Exception error)
        {
            Log.Error("gadgets.hack_range", "The hacking range could not be changed", error);
        }

        return self.TargetOutOfRange(target, range, out fDistanceUntilInRange);
    }

    // Blinding a new target frees the old one through here, so only that call is skipped.
    [Redirect(typeof(RRHD), nameof(RRHD.UnBlindActor))]
    static void UnBlindActor(RRHD self, Actor target)
    {
        try
        {
            if (ManyTargets.On && target is { } old && self.CurrentTarget is { } aimed && old.Ptr != aimed.Ptr)
            {
                return;
            }
        }
        catch (Exception error)
        {
            Log.Error("gadgets.blind_many", "The blinded target check failed", error);
        }

        Redirects.RunOriginal();
    }

    // Each target's Trigger sets its own blind time as the RHD fires, so it is scaled after.
    [Redirect(typeof(RRHD), nameof(RRHD.Fire))]
    static void Fire(RRHD self, bool secondary)
    {
        Redirects.RunOriginal();
        try
        {
            var scale = (float)BlindTime.Value;
            if (scale <= 1f || self.CurrentBlindedDroneTarget is not { IsValid: true } target)
            {
                return;
            }

            // A fresh blind is exactly 60 or 120 s, so a failed shot at one already scaled
            // doesn't scale it again.
            switch (target)
            {
                case RVehicleNPC { fBlindedRemainingTime: 60f or 120f } vehicle:
                    vehicle.fBlindedRemainingTime *= scale;
                    break;
                case RHelicopterBase { BlindedDuration: 60f or 120f } airship:
                    airship.BlindedDuration *= scale;
                    break;
                case RSentryGunBase gun:
                    SentryJam.Hold(gun, time => time * scale);
                    break;
                case RTurret_WatchtowerBase tower:
                    TowerJam.Hold(tower, time => time * scale);
                    break;
            }
        }
        catch (Exception error)
        {
            Log.Error("gadgets.blind_time", "The blind time could not be changed", error);
        }
    }
}
