#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > GADGETS > BATARANG and REMOTE BATARANG. Gadget values are held and put back. Values on
// a projectile are changed as it is made, since it is gone after the throw.
static class Batarangs
{
    static readonly Held<RBatarang, float> Reach = new("batarang range", gun => gun.BatarangRange, (gun, range) => gun.BatarangRange = range);
    static readonly Held<RBatarang, int> Chain = new("batarang chain", gun => gun.MaxNumQuickBatarangs, (gun, count) => gun.MaxNumQuickBatarangs = count);
    static readonly Held<RBatarang, int> Remote = new("remote batarang hits", gun => gun.MaxNumEnemiesHit, (gun, count) => gun.MaxNumEnemiesHit = count);

    static readonly Func<float, float> ReachScaled = static was => was * (float)Range!.Value;
    static readonly Func<int, int> ChainRaised = static was => was + (int)ExtraThrows!.Value;
    static readonly Func<int, int> RemoteRaised = static was => was + (int)RemoteHits!.Value;

    public static readonly Slider Range = new(
        "player.gadgets.batarang_range", "BATARANG RANGE",
        "How far you can aim a batarang, for the normal and the remote batarang.",
        min: 0.5, max: 3, step: 0.25, value: 1, unit: "X")
    {
        Block = static () => Equipment.Missing(static pawn => pawn.Batarang, "NO BATARANG"),
        Tick = KeepRange,
        Restore = Reach.Release,
    };

    public static readonly Toggle Electrified = new(
        "player.gadgets.electrified_batarangs", "ELECTRIFIED BATARANGS",
        "Every batarang you throw is electrified. It shocks the thug it hits and sets off fuse boxes.")
    {
        Block = static () => Equipment.Missing(static pawn => pawn.Batarang, "NO BATARANG"),
    };

    public static readonly Slider ExtraThrows = new(
        "player.gadgets.batarang_chain", "EXTRA QUICK THROWS",
        "How many more batarangs a quick throw can send out in a row, at different thugs.",
        min: 0, max: 4, step: 1, value: 0, zero: "OFF")
    {
        Block = static () => Equipment.Missing(static pawn => pawn.Batarang, "NO BATARANG"),
        Tick = KeepChain,
        Restore = Chain.Release,
    };

    public static readonly Slider RemoteTime = new(
        "player.gadgets.remote_time", "REMOTE BATARANG TIME",
        "How long you can steer the remote batarang before it gives out. 1X is the game's.",
        min: 0.5, max: 5, step: 0.25, value: 1, unit: "X")
    {
        Block = static () => Equipment.Missing(static pawn => pawn.RCBatarang, "NO REMOTE BATARANG"),
    };

    public static readonly Slider RemoteHits = new(
        "player.gadgets.remote_hits", "EXTRA REMOTE HITS",
        "How many more thugs the remote batarang can hit before it ends.",
        min: 0, max: 10, step: 1, value: 0, zero: "OFF")
    {
        Block = static () => Equipment.Missing(static pawn => pawn.RCBatarang, "NO REMOTE BATARANG"),
        Tick = KeepRemoteHits,
        Restore = Remote.Release,
    };

    public static readonly Slider RemoteBounces = new(
        "player.gadgets.remote_bounces", "EXTRA REMOTE BOUNCES",
        "How many more walls the remote batarang can bounce off before it ends.",
        min: 0, max: 10, step: 1, value: 0, zero: "OFF")
    {
        Block = static () => Equipment.Missing(static pawn => pawn.RCBatarang, "NO REMOTE BATARANG"),
    };

    static void KeepRange()
    {
        if (World.Player is not { IsValid: true } pawn)
        {
            return;
        }

        if (pawn.Batarang is { IsValid: true } normal)
        {
            Reach.Hold(normal, ReachScaled);
        }

        if (pawn.RCBatarang is { IsValid: true } remote)
        {
            Reach.Hold(remote, ReachScaled);
        }
    }

    static void KeepChain()
    {
        if (World.Player?.Batarang is { IsValid: true } gun)
        {
            Chain.Hold(gun, ChainRaised);
        }
    }

    static void KeepRemoteHits()
    {
        if (World.Player?.RCBatarang is { IsValid: true } gun)
        {
            Remote.Hold(gun, RemoteRaised);
        }
    }

    static bool IsPlayers(Actor? owner) => owner is { IsValid: true } && owner.Ptr == World.Player?.Ptr;

    // The game electrifies a throw itself only for a quick throw with a mutator on.
    [Redirect(typeof(RBatarang), nameof(RBatarang.ThrowBatarangCommon))]
    static void ThrowBatarangCommon(RBatarang self, RBatarangProjectile projectile)
    {
        Redirects.RunOriginal();
        try
        {
            if (Electrified.On && IsPlayers(self.Owner) && projectile is { IsValid: true, bElectrified: false })
            {
                projectile.Electrify();
            }
        }
        catch (Exception error)
        {
            Log.Error("gadgets.electrified", "The batarang could not be electrified", error);
        }
    }

    // The steering timer and the wall count are read from the projectile, which is made here.
    [Redirect(typeof(RBatarangProjectile_Controllable), nameof(RBatarangProjectile_Controllable.InitBatarang))]
    static void InitRemote(RBatarangProjectile_Controllable self, Vector3 throwDirection, RBatarang launcher, bool bFlightPathMirrored, FName boneTarget, float speedMod, bool bStraightPath)
    {
        Redirects.RunOriginal();
        try
        {
            if (!self.IsValid || !IsPlayers(launcher?.Owner))
            {
                return;
            }

            if (!RemoteTime.IsDefault)
            {
                self.ControlledTime *= (float)RemoteTime.Value;
                self.ExtendHomingTime(0f);
            }

            if (!RemoteBounces.IsDefault)
            {
                self.MaxWallHits += (int)RemoteBounces.Value;
            }
        }
        catch (Exception error)
        {
            Log.Error("gadgets.remote_batarang", "The remote batarang could not be changed", error);
        }
    }
}
