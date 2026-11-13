#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Every Batmobile plays all its car sounds from one sound set: engine, gears, boost, drift and
// skids. Only the worn skin's package is in memory, so another skin's set is loaded, kept in
// memory, and the car's sound player is started again with it. The skins themselves are never
// touched, so a skin change or a reload starts clean.
static class EngineSound
{
    const double BankWaitSeconds = 6;

    readonly record struct Set(string Id, string Label, string Package, string Path);

    // One per distinct engine. Reskins share these.
    static readonly Set[] Sets =
    {
        new("ak", "ARKHAM KNIGHT", "Playable_Batmobile_SF", "VEH_Player.Batmobile"),
        new("mk2", "ARKHAM KNIGHT MK2", "Playable_BatmobileMK2_SF", "VEH_Player.Batmobile_MK2"),
        new("classic", "1966 TV SERIES", "Playable_BatmobileClassic_SF", "VEH_Batmobile_Classic.Batmobile_Classic"),
        new("burton", "1989 MOVIE", "Playable_BatmobileBurton_SF", "VEH_Batmobile_Burton.Batmobile_Burton"),
        new("tumbler", "TUMBLER", "Playable_BatmobileTumbler_SF", "VEH_Batmobile_Tumbler.Batmobile_Tumbler"),
        new("snyder", "BATMAN V SUPERMAN", "Playable_BatmobileSnyder_SF", "VEH_Batmobile_Snyder.Batmobile_Snyder"),
        new("asylum", "ARKHAM ASYLUM", "Playable_BatmobileAsylum_SF", "VEH_Batmobile_Asylum.Batmobile_Asylum"),
    };

    static readonly Dictionary<string, AkPlayerVehicleSoundInfo> Loaded = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);

    static RVehicleBatmobileBase? _car;
    static AkVehicleSoundInfo? _original;
    static string _applied = "";
    static string _waitingFor = "";
    static long _waitingSince;

    public static readonly Choice Sound = new(
        "batmobile.skin.engine_sound", "ENGINE SOUND",
        "Gives the Batmobile another skin's engine, boost and tyre sounds, whatever skin it wears. The first use of each can take a moment to load.",
        Options())
    {
        Tick = Keep,
        Restore = PutBack,
    };

    // Skins whose package isn't installed, like DLC the player doesn't own, aren't offered.
    static (string Id, string Label)[] Options()
    {
        var options = new List<(string, string)> { ("own", "SKIN'S OWN") };
        foreach (var set in Sets)
        {
            if (GameFiles.HasPackage(set.Package))
            {
                options.Add((set.Id, set.Label));
            }
        }

        return options.ToArray();
    }

    static void Keep()
    {
        var id = Sound.CurrentId;
        if (Missing.Contains(id) || Car.Find() is not { bDeleteMe: false } car || car.NewVehicleSound is not { IsValid: true } player)
        {
            return;
        }

        if (!ReferenceEquals(car, _car))
        {
            _car = car;
            _original = player.m_VehicleInfo;
            _applied = "";
        }

        if (_applied.Equals(id, StringComparison.OrdinalIgnoreCase) || Donor(id) is not { } donor || !BankReady(donor, id))
        {
            return;
        }

        Swap(car, player, donor);
        _applied = id;
        _waitingFor = "";
    }

    static void PutBack()
    {
        var car = _car;
        var original = _original;
        _car = null;
        _original = null;
        _applied = "";
        _waitingFor = "";

        if (car is { IsValid: true, bDeleteMe: false } && original is { IsValid: true } &&
            car.NewVehicleSound is { IsValid: true } player && !ReferenceEquals(player.m_VehicleInfo, original))
        {
            Swap(car, player, original);
        }

        foreach (var info in Loaded.Values)
        {
            if (info.IsValid)
            {
                info.RemoveFromRoot();
            }
        }

        Loaded.Clear();
        Missing.Clear();
    }

    static AkPlayerVehicleSoundInfo? Donor(string id)
    {
        if (Loaded.TryGetValue(id, out var known) && known.IsValid)
        {
            return known;
        }

        var set = Array.Find(Sets, entry => entry.Id == id);
        if (set.Id is null)
        {
            Missing.Add(id);
            return null;
        }

        var info = Game.FindObject<AkPlayerVehicleSoundInfo>(set.Path);
        if (info is not { IsValid: true })
        {
            if (!Packages.TryLoad(set.Package, $"the {set.Label} engine sound", "engine_sound.load"))
            {
                Missing.Add(id);
                return null;
            }

            info = Game.FindObject<AkPlayerVehicleSoundInfo>(set.Path);
        }

        if (info is not { IsValid: true })
        {
            Missing.Add(id);
            Log.Info($"Engine sound {set.Label} was not in {set.Package}.");
            return null;
        }

        // Kept in memory so the sound set and its sound bank outlive the rest of the package.
        info.AddToRoot();
        Loaded[id] = info;
        foreach (var sound in info.StartLoopingEvents)
        {
            if (sound is { IsValid: true })
            {
                sound.RequestAssetBankLoad();
            }
        }

        _waitingFor = id;
        _waitingSince = Stopwatch.GetTimestamp();
        return info;
    }

    // Swapping before the sound bank is in leaves the engine silent. A bank that never says it
    // is ready is tried anyway after a few seconds.
    static bool BankReady(AkPlayerVehicleSoundInfo donor, string id)
    {
        var ready = true;
        foreach (var sound in donor.StartLoopingEvents)
        {
            if (sound is { IsValid: true } && !sound.IsAssetBankLoaded())
            {
                ready = false;
            }
        }

        if (ready)
        {
            return true;
        }

        if (_waitingFor != id)
        {
            _waitingFor = id;
            _waitingSince = Stopwatch.GetTimestamp();
        }

        return (Stopwatch.GetTimestamp() - _waitingSince) / (double)Stopwatch.Frequency >= BankWaitSeconds;
    }

    static void Swap(RVehicleBatmobileBase car, AkVehicleSoundVar player, AkVehicleSoundInfo info)
    {
        var running = player.EngineStarted();
        var driving = World.Driving == car;

        // Tyre squeal loops belong to each wheel and outlive a restart. Stopped while the old set
        // can still find them, or they screech with the car standing still.
        StopWheels(car);
        player.StopEngine(true);
        player.StopSockets();
        player.StopCreationEvents();

        if (info is AkPlayerVehicleSoundInfo playerInfo)
        {
            car.PlayerVehicleInfo = playerInfo;
        }

        player.Initialize(info);
        StopWheels(car);

        // The game's own order: set up, then the driver gets in and the engine starts.
        if (driving)
        {
            player.PlayerEntered();
        }

        if (driving || running)
        {
            player.StartEngine();
        }
    }

    static void StopWheels(RVehicleBatmobileBase car)
    {
        foreach (var wheel in car.Wheels)
        {
            if (wheel is { IsValid: true })
            {
                car.AudioStopWheelSqueal(wheel.BoneName);
            }
        }

        car.AudioStopAllWheels();
        car.AudioSquealing(0f);
    }
}
