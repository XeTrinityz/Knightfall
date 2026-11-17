#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > GADGETS, the remote hacking device's blind rows.
static class Blinding
{
    static readonly Held<RSentryGunBase, float> SentryJam = new("sentry blind time", gun => gun.Settings.jamDuration, (gun, time) => gun.Settings.jamDuration = time);
    static readonly Held<RTurret_WatchtowerBase, float> TowerJam = new("watchtower blind time", gun => gun.jamDuration, (gun, time) => gun.jamDuration = time);

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
