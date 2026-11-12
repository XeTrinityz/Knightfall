#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace Knightfall;

// The Joker's laughing gas, fired from under his shotgun's barrel like a grenade. The gas is the
// game's own, from Harley Quinn's pack: her canister flies out in an arc, and where it lands her
// gas cloud gasses the thugs in it, keeps gassing those who walk in, and stirs a predator room
// the way hers does. Thugs it catches drop their guns and laugh helplessly for a while. The
// launch is Firefly's grenade launcher sound.
//
// A gassed thug asks whoever gassed them for Harley's gas gadget, to learn how to react. The
// Joker hasn't got it, so the reaction is picked here instead, by her gadget's own rule.
static partial class JokerVillain
{
    const string HarleyPackage = "Playable_DLCHarleyQuinn";
    const string GasCloudPath = "WBGMDLC_HarleyQuinn_Archetypes.Gadgets.LaughingGas.LaughingGas_Archetype";
    const string ArmedReactionPath = "BmScript.MCombatMove_VillainLaughingGasReaction_Gun";
    const string UnarmedReactionPath = "BmScript.MCombatMove_VillainLaughingGasReaction_Unarmed";
    const string HarleyGadget = "MLaughingGasBase";
    const string CanisterPath = "DLC0_HarleyQuinn_BM3_Weapons.Harleys_SmokeBomb_01";

    // Her cloud's own versions of these. The SDK's binding would run the plain smoke bomb's, which
    // gasses nobody with laughing gas.
    const string StartGasPath = "BmScript.MLaughingGasScreen.InitSmoke";
    const string StartRoomPath = "BmScript.MLaughingGasScreen.StartPredReaction";

    // Firefly's launch sound lives in his story level, which is small.
    const string LaunchSoundPackage = "CityX__ChF478";
    const string LaunchSoundPath = "Ww_CHR_Firefly.Play_Firefly_Grenade_Launch";

    // How far it reaches, how long until the next, how fast the canister flies and how high its
    // arc rises for each unit it goes.
    const float GasReach = 2500f;
    const float GasEvery = 10f;
    const float CanisterSpeed = 2200f;
    const float ShortestFlight = 0.2f;
    const float ArcPerReach = 0.15f;
    const float SpinPerSecond = 720f;
    const float LaunchKick = 6f;

    // The rifles Harley's gadget gives the armed reaction to, and any kind of them. One isn't in
    // the SDK, so all are known by name.
    static readonly string[] Rifles = { "RBMWeaponGuardRifle", "RBMWeaponCombatRifleBase", "RBMWeaponCombatRifle", "RBMWeaponRiflePredLite" };

    static MLaughingGasScreenBase? _gasCloud;
    static SkeletalMesh? _canisterMesh;
    static AkEvent? _launchSound;
    static Class? _armedReaction;
    static Class? _unarmedReaction;
    static bool _gasTried;
    static bool _secondaryWas;
    static float _nextGas;

    static SkeletalMeshComponent? _canister;
    static RPawnPlayer? _canisterOn;
    static Vector3 _flightFrom;
    static Vector3 _flightTo;
    static float _flightStart = -1f;
    static float _flightSeconds;

    // For the HUD: how ready the next canister is, whether one is in the air, and whether the
    // pack it needs is there at all.
    static float GasReady => Math.Clamp(1f - (_nextGas - World.Now) / GasEvery, 0f, 1f);

    static bool GasFlying => _flightStart >= 0f;

    static bool GasMissing => !GameFiles.HasPackage(HarleyPackage);

    // Harley's pack holds his gas and his detective mode's look, so it loads with him.
    static void LoadGas()
    {
        if (_gasTried)
        {
            return;
        }

        _gasTried = true;
        if (!GameFiles.HasPackage(HarleyPackage))
        {
            Log.Info("The Joker's laughing gas needs the Harley Quinn pack, which isn't installed.");
            return;
        }

        if (!Load(HarleyPackage + "_SF", () => Game.FindObject<MLaughingGasScreenBase>(GasCloudPath) is { IsValid: true }
            && Game.FindObject<Class>(ArmedReactionPath) is { IsValid: true } && Game.FindObject<Class>(UnarmedReactionPath) is { IsValid: true }))
        {
            _gasTried = false;
            return;
        }

        Load(LaunchSoundPackage, () => Game.FindObject<AkEvent>(LaunchSoundPath) is { IsValid: true });

        _gasCloud = Kept(Game.FindObject<MLaughingGasScreenBase>(GasCloudPath));
        _canisterMesh = Kept(Game.FindObject<SkeletalMesh>(CanisterPath));
        _launchSound = Kept(Game.FindObject<AkEvent>(LaunchSoundPath));
        _armedReaction = Kept(Game.FindObject<Class>(ArmedReactionPath));
        _unarmedReaction = Kept(Game.FindObject<Class>(UnarmedReactionPath));
        LoadXRay();
        if (_gasCloud is null || _armedReaction is null || _unarmedReaction is null)
        {
            Log.Warn("joker.gas", "The Joker's laughing gas wasn't found in the Harley Quinn pack.");
            _gasCloud = null;
        }
    }

    // Loads a package unless what is wanted from it is in already. False when there was no room,
    // which is worth another try later. A load that failed is not.
    static bool Load(string package, Func<bool> loaded)
    {
        if (loaded())
        {
            return true;
        }

        if (!Packages.HasRoom(package))
        {
            Log.Info($"No room to load {package} for the Joker's laughing gas.");
            return false;
        }

        try
        {
            Game.LoadPackage(package);
        }
        catch (Exception error)
        {
            Log.Error("joker.gas_load", $"{package} failed to load for the Joker's laughing gas", error);
        }

        return true;
    }

    static void Gas(RPawnPlayer player)
    {
        var pressed = Aim.Secondary && !_secondaryWas;
        _secondaryWas = Aim.Secondary;
        Fly(player);
        if (!pressed || World.Now < _nextGas || _flightStart >= 0f || Arms.Raised < 0.9f || Barrel() is not { } barrel)
        {
            return;
        }

        LoadGas();
        if (_gasCloud is not { IsValid: true } || !Aim.Ground(player, GasReach, out var at))
        {
            return;
        }

        Launch(player, barrel.From, at);
        Arms.Kick(LaunchKick);
        Play(player, _launchSound);
        if (_gunDefaults is { IsValid: true, MuzzleFlash: { IsValid: true, Template: { IsValid: true } flash } }
            && Game.TryGetWorldInfo(out var world) && world.GRI?.ObjectPool is { IsValid: true } pool)
        {
            pool.SpawnEmitter(flash, barrel.From, Rotator.FromDirection(barrel.Way), 0, 1f);
        }

        _nextGas = World.Now + GasEvery;
    }

    static void Launch(RPawnPlayer player, Vector3 from, Vector3 to)
    {
        _flightFrom = from;
        _flightTo = to;
        _flightStart = World.Now;
        _flightSeconds = MathF.Max(ShortestFlight, Vector3.Distance(from, to) / CanisterSpeed);
        if (_canisterMesh is not { IsValid: true } mesh)
        {
            return;
        }

        var canister = Made.Tracked(new SkeletalMeshComponent(player));
        canister.SetActorCollision(false, false);
        canister.SetSkeletalMesh(mesh, false);
        canister.SetAbsolute(true, true, false);
        canister.SetTranslation(from);
        player.AttachComponent(canister);
        _canister = canister;
        _canisterOn = player;
    }

    // The canister flies in an arc and the gas bursts where it lands.
    static void Fly(RPawnPlayer player)
    {
        if (_flightStart < 0f)
        {
            return;
        }

        var flown = Math.Clamp((World.Now - _flightStart) / _flightSeconds, 0f, 1f);
        if (_canister is { IsValid: true } canister)
        {
            var rise = Vector3.Distance(_flightFrom, _flightTo) * ArcPerReach;
            var at = Vector3.Lerp(_flightFrom, _flightTo, flown) + Vector3.UnitZ * (rise * 4f * flown * (1f - flown));
            canister.SetTranslation(at);
            canister.SetRotation(new Rotator(World.Now * SpinPerSecond, 0f, 0f));
        }

        if (flown >= 1f)
        {
            Land();
            Burst(player, _flightTo);
        }
    }

    static void Land()
    {
        if (_canister is { IsValid: true } canister && _canisterOn is { IsValid: true } owner)
        {
            owner.DetachComponent(canister);
        }

        _canister = null;
        _canisterOn = null;
        _flightStart = -1f;
    }

    static void Burst(RPawnPlayer player, Vector3 at)
    {
        if (_gasCloud is not { IsValid: true } cloud)
        {
            return;
        }

        var facing = Rotator.FromDirection(player.Location - at);
        if (player.Spawn(cloud.Class, player, FName.None, at, new Rotator(0f, facing.Yaw, 0f), cloud, true) is not MLaughingGasScreenBase { IsValid: true } gas)
        {
            return;
        }

        if (!ScriptCall.Run(gas, StartGasPath, 0, 1))
        {
            gas.InitSmoke(false, true);
        }

        if (!ScriptCall.Run(gas, StartRoomPath, 0))
        {
            gas.StartPredReaction(false);
        }

        Say("gas", always: true);
    }

    // Harley's gadget answers which reaction a gassed thug plays. The Joker has none, so a thug
    // he gassed is answered here the way her gadget would, and the reaction is started the way
    // the game starts it.
    [Redirect(typeof(RPawnVillain), nameof(RPawnVillain.SpawnSmokeBombCombatMove))]
    static bool SpawnSmokeBombCombatMove(RPawnVillain self, Class smokeDamageType, ref RPawnCombat.FDamageInfo dmgInfo)
    {
        try
        {
            if (Option.On && smokeDamageType is { IsValid: true } kind && kind.Ptr == MDmgType_LaughingGas.StaticClass().Ptr
                && dmgInfo.Attacker is RPawnPlayer { IsValid: true } attacker && World.Player is { } player && attacker.Ptr == player.Ptr
                && !HasHarleyGadget(attacker) && Reaction(self) is { } reaction)
            {
                if (self.Spawn(reaction, self, FName.None, self.Location, self.Rotation, null!, true) is not RCombatMove_VillainSmokeBombReaction { IsValid: true } move)
                {
                    return false;
                }

                move.Villain = self;
                move.SmokeDamageType = smokeDamageType;
                move.Initialise();
                return true;
            }
        }
        catch (Exception error)
        {
            Log.Error("joker.gas_reaction", "Starting a laughing gas reaction failed", error);
        }

        return Redirects.RunOriginal<bool>();
    }

    static bool HasHarleyGadget(RPawnPlayer player) =>
        player.InvManager is RInventoryManager { IsValid: true } kit && kit.GetGadgetByName(new FName(HarleyGadget), false) is { IsValid: true };

    static Class? Reaction(RPawnVillain thug)
    {
        if (thug.IsNightwingBoss())
        {
            return null;
        }

        if (thug.CurrWeapon is { IsValid: true } held)
        {
            for (Struct? kind = held.Class; kind is { IsValid: true }; kind = kind.SuperStruct)
            {
                var name = kind.Name.ToString() ?? "";
                if (Array.Exists(Rifles, rifle => rifle.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    return _armedReaction;
                }
            }
        }

        return _unarmedReaction;
    }

    // A canister in flight when the car or a cutscene takes over is dropped.
    static void StopThrow() => Land();

    static void ResetGas()
    {
        StopThrow();
        _nextGas = 0f;
        _secondaryWas = false;
    }

    static void UnloadGas()
    {
        _gasCloud = null;
        _canisterMesh = null;
        _launchSound = null;
        _armedReaction = null;
        _unarmedReaction = null;
        _gasTried = false;
    }
}
