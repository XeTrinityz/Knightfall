#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// BATMOBILE > SKIN. The pick is saved in the game's options at once, the way the showcase menu
// does it. The car is only swapped once nobody is in it, over the next few seconds.
static class Skins
{
    const string SkinPrefix = "Playable_Batmobile";
    const string JokerSkin = "Playable_BatmobileJoker";

    // The car has to stay free this long, so a swap never lands mid-exit.
    const float SettleSeconds = 2f;
    const double TimeoutSeconds = 20;

    enum Step
    {
        Idle,
        WaitingForFree,
        Loading,
        WaitingToSwap,
    }

    static readonly Dictionary<string, FName> Names = new(StringComparer.OrdinalIgnoreCase);

    static Step _step;
    static string _pending = "";
    static int _damage;
    static float _settle;
    static long _since;

    public static readonly Choice Skin = new(
        "batmobile.skin.skin", "SKIN",
        "Pick an unlocked skin, then press APPLY SKIN. It starts on the skin the car wears.",
        new[] { ("", "OPEN IN GAME") }, saved: false)
    {
        Opening = Refresh,
    };

    public static readonly Command Apply = new(
        "batmobile.skin.apply", "APPLY SKIN",
        "Puts the picked skin on the Batmobile once nobody is in it. Get out first if you are driving.",
        Start)
    {
        Block = static () => Skin.CurrentId.Length == 0 ? "NO SKIN TO APPLY" : null,
    };

    public static readonly Command Cancel = new(
        "batmobile.skin.cancel", "CANCEL SKIN CHANGE",
        "Stops a skin change that is still waiting for the Batmobile to be free.",
        () => _step == Step.Idle ? Outcome.Unchanged("NOT WAITING") : Stop(Outcome.Done("CANCELLED")))
    {
        Block = static () => _step == Step.Idle ? "NOTHING WAITING" : null,
    };

    static void Refresh()
    {
        if (World.Info is not { } info || !Game.TryGetGameRI(out var game) || game.PersistentOptions is not { IsValid: true } options)
        {
            return;
        }

        info.ShowcaseValidateSorting();
        var list = new List<(string Id, string Label)>();
        foreach (var item in info.ShowcaseItems)
        {
            var skin = item.Skin.ToString() ?? "";
            var key = item.Item.ToString() ?? "";
            if (!skin.StartsWith(SkinPrefix, StringComparison.OrdinalIgnoreCase) || key.Length == 0 || !Showcase.Unlocked(options, item, key) ||
                list.Exists(entry => entry.Id.Equals(skin, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            list.Add((skin, Showcase.Label(key, skin)));
        }

        if (list.Count == 0)
        {
            Skin.SetOptions(new[] { ("", "NONE UNLOCKED") }, 0);
            return;
        }

        var loaded = game.PlayerBatmobiles_0;
        var index = list.FindIndex(entry => entry.Id.Equals(loaded.BatmobileName.ToString(), StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = list.FindIndex(entry => entry.Id.Equals(loaded.BatmobileDataName.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        if (index < 0)
        {
            var active = ActiveSkin(options);
            index = list.FindIndex(entry => entry.Id.Equals(active, StringComparison.OrdinalIgnoreCase));
        }

        Skin.SetOptions(list.ToArray(), Math.Max(0, index));
    }

    // Both skin calls are script functions that rewrite their text parameter, so each call gets
    // its own string.
    static string ActiveSkin(RPersistentOptions options) =>
        options.GetActivePlayerSkin(GameText.Once("Batmobile"), true, false, true).ToString() ?? "";

    static Outcome Start()
    {
        var requested = Skin.CurrentId;
        if (requested.Length == 0)
        {
            return Outcome.Failed("NO SKIN");
        }

        if (!Game.TryGetGameRI(out var game) || game.PersistentOptions is not { IsValid: true } options || World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        options.SetActivePlayerSkin(GameText.Once(requested));
        controller.SaveGameOptionsToMemory();

        var resolved = ActiveSkin(options);
        if (resolved.Length == 0)
        {
            return Outcome.Failed("UNKNOWN");
        }

        var current = game.PlayerBatmobiles_0;
        var wearing = current.BatmobileName.ToString() ?? "";
        if (wearing.Equals(resolved, StringComparison.OrdinalIgnoreCase))
        {
            Reset();
            return Outcome.Unchanged("WEARING IT");
        }

        // The story's Jokerized car can't be swapped out. The pick is still saved for the next one.
        if (wearing.Equals(JokerSkin, StringComparison.OrdinalIgnoreCase))
        {
            Reset();
            return Outcome.Unchanged("SAVED");
        }

        _pending = resolved;
        _damage = current.DamageLevel;
        _settle = 0f;
        _step = Step.WaitingForFree;
        _since = Stopwatch.GetTimestamp();
        return Outcome.Done(World.Driving is not null ? "GET OUT" : "APPLYING");
    }

    public static void Tick()
    {
        if (_step == Step.Idle || World.Controller is not { } controller || World.Info is not { } info)
        {
            return;
        }

        switch (_step)
        {
            case Step.WaitingForFree:
                if (!Settled(controller, info))
                {
                    return;
                }

                info.LoadPB(NameOf(_pending), _damage);
                _step = Step.Loading;
                _since = Stopwatch.GetTimestamp();
                return;

            case Step.Loading:
                if (!Game.TryGetGameRI(out var game) || !Loaded(game.PlayerBatmobiles_0))
                {
                    if (TimedOut())
                    {
                        Log.Info($"The Batmobile skin {_pending} took too long to load.");
                        Stop(Outcome.Failed("TIMED OUT"));
                    }

                    return;
                }

                info.PersistentData.SetPlayableBatmobile(NameOf(_pending));
                _step = Step.WaitingToSwap;
                _settle = 0f;
                _since = Stopwatch.GetTimestamp();
                return;

            case Step.WaitingToSwap:
                if (!Settled(controller, info))
                {
                    if (TimedOut())
                    {
                        Stop(Outcome.Unchanged("NEXT CALL"));
                    }

                    return;
                }

                controller.CheatBatmobileSkinRepossess();
                if (Game.TryGetGameRI(out var after))
                {
                    // The old skin's mesh is let go, or both stay in memory.
                    var mesh = after.PlayerBatmobiles_0.MeshName.ToString() ?? "";
                    if (mesh.Length > 0)
                    {
                        after.ClearOutRootSetBatmobiles(GameText.Of(mesh));
                    }
                }

                Stop(Outcome.Done("CHANGED"));
                return;
        }
    }

    static bool Loaded(RGameRI.FLoadedPlayerBatmobile loaded) =>
        loaded.BatmobileData is not null && ((loaded.MeshName.ToString() ?? "").Length == 0 || loaded.MeshData is not null);

    static bool Settled(RPlayerController controller, RGameInfo info)
    {
        var car = info.GetBatmobile();
        var free = car is not { IsValid: true } || (controller.Pawn != car && car.Driver is null);
        _settle = free ? _settle + World.Delta : 0f;
        return _settle >= SettleSeconds;
    }

    static bool TimedOut() => (Stopwatch.GetTimestamp() - _since) / (double)Stopwatch.Frequency > TimeoutSeconds;

    static FName NameOf(string skin)
    {
        if (!Names.TryGetValue(skin, out var name))
        {
            name = new FName(skin);
            Names[skin] = name;
        }

        return name;
    }

    static Outcome Stop(Outcome outcome)
    {
        Reset();
        Apply.Report(outcome);
        return outcome;
    }

    static void Reset()
    {
        _step = Step.Idle;
        _pending = "";
        _settle = 0f;
    }

    public static void Release() => Reset();
}
