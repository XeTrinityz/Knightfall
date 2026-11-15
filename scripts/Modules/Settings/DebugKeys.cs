#nullable enable

using System.Diagnostics;
using System.Reflection;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SETTINGS > GAME. BmInput.ini still has the developers' keys, like T to teleport and B for the
// Batmobile. They run cheat manager commands, so they work once Knightfall has made one. A binding
// made only of those commands gets a key name no key has, and its own name back when this is off.
static class DebugKeys
{
    // The game builds its bindings again when the controls change.
    const double Rescan = 2.0;

    // Every developer command is a cheat manager function or starts with Debug, so the list comes
    // from the game's own classes.
    static readonly HashSet<string> CheatCommands = CheatFunctions();

    static readonly List<(int Slot, FName Key, string Command)> Blocked = new();
    static FName _off;
    static nint _input;
    static long _lastScan;
    static bool _reported;

    public static readonly Toggle Block = new(
        "settings.game.block_debug_keys", "BLOCK DEBUG KEYS",
        "Turns off the keys the game's developers left in. T teleports, B brings the Batmobile, H speeds the game up, K knocks everyone out, U unlocks every upgrade and more.",
        on: true)
    {
        Preference = true,
    };

    public static void Tick()
    {
        if (!Block.On)
        {
            Release();
            return;
        }

        if (World.Controller?.PlayerInput is not { IsValid: true } input)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (input.Ptr == _input && (now - _lastScan) / (double)Stopwatch.Frequency < Rescan)
        {
            return;
        }

        _lastScan = now;
        if (input.Ptr != _input)
        {
            Blocked.Clear();
            _input = input.Ptr;
        }

        var blocked = Scan(input.Bindings);
        if (blocked > 0 && !_reported)
        {
            _reported = true;
            Log.Info($"Debug keys: turned off {blocked} developer bindings.");
        }
    }

    public static void Release()
    {
        if (Blocked.Count == 0)
        {
            return;
        }

        try
        {
            // A new world brings a new input with the ini's bindings, so only the one we changed
            // needs its names back.
            if (World.Controller?.PlayerInput is not { IsValid: true } input || input.Ptr != _input)
            {
                return;
            }

            var binds = input.Bindings;
            foreach (var (slot, key, command) in Blocked)
            {
                if (slot >= binds.Count)
                {
                    continue;
                }

                var bind = binds[slot];
                if (IsOff(bind.Name) && bind.Command.ToString() == command)
                {
                    bind.Name = key;
                    binds[slot] = bind;
                }
            }
        }
        finally
        {
            Blocked.Clear();
            _input = 0;
        }
    }

    static int Scan(TArray<Input.FKeyBind> binds)
    {
        if (_off.Index == 0)
        {
            _off = new FName("KnightfallDebugKeyOff");
        }

        var added = 0;
        for (var i = 0; i < binds.Count; i++)
        {
            var bind = binds[i];
            if (IsOff(bind.Name) || bind.Command.ToString() is not { } command || !AllDebug(command))
            {
                continue;
            }

            Blocked.Add((i, bind.Name, command));
            bind.Name = _off;
            binds[i] = bind;
            added++;
        }

        return added;
    }

    static bool IsOff(FName name) => name.Index == _off.Index && name.Number == _off.Number;

    // A binding like "SelectGadget 0 | DebugMenuNumber 1" also does a real job, so it stays.
    static bool AllDebug(string command)
    {
        var seen = 0;
        foreach (var raw in command.Split('|'))
        {
            var part = raw.Trim();
            if (part.StartsWith("OnRelease ", StringComparison.OrdinalIgnoreCase))
            {
                part = part[10..].TrimStart();
            }

            if (part.Length == 0)
            {
                continue;
            }

            var space = part.IndexOf(' ');
            var name = space < 0 ? part : part[..space];
            if (!name.StartsWith("Debug", StringComparison.OrdinalIgnoreCase) && !CheatCommands.Contains(name))
            {
                return false;
            }

            seen++;
        }

        return seen > 0;
    }

    static HashSet<string> CheatFunctions()
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in new[] { typeof(CheatManager), typeof(RCheatManager) })
        {
            foreach (var method in type.GetMethods(declared))
            {
                names.Add(method.Name);
            }
        }

        return names;
    }
}
