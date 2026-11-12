#nullable enable

using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// A villain's own voice lines, played the way the game plays a bark, through its speech system.
// The speech system only plays a line for a speaker with that line's voice, so the player speaks
// with each line's voice while it is in use, and Batman's is put back after.
//
// Each set of lines is found by the start of its path, like "side_firefly_taunts_bomb_hit.".
// The lines can come from several packages. A bank name ending in "_" stands for every bank whose
// name starts with it, and any other is that one bank.
sealed class VillainVoice
{
    readonly string _lender;
    readonly string[] _packages;
    readonly string[] _bankNames;
    readonly (string Key, string Prefix)[] _sets;
    readonly Dictionary<string, List<AkDialogueLineSingle>> _lines = new();
    readonly HashSet<string> _found = new(StringComparer.OrdinalIgnoreCase);
    readonly List<AkBank> _banks = new();
    bool _loadTried;
    float _next;
    float _quietFrom;

    // Lines left out of their sets, by name. A boss's lines are mostly said to Batman, and those
    // make no sense said to a thug.
    public string[] Without { get; init; } = Array.Empty<string>();

    // The player's speaker, held at a villain's voice. Every villain shares the one hold, so
    // Batman's voice is what gets put back whichever part of a villain lets go first.
    public static readonly Held<AkDialogueComponent, AkDialogueVoice?> Speaker =
        new("villain voice", speaker => speaker.Voice, (speaker, voice) => speaker.SetVoice(voice!));

    // When any villain last started a line. A line can be in another voice than the villain's
    // own, like his suit's computer, and that voice is left on the speaker while it plays.
    public static float SpokeAt { get; private set; } = float.MinValue;

    // The path of the line spoken last, for a HUD that shows what was said.
    public string LastLine { get; private set; } = "";

    // It waits this long between lines, and speaks this often when asked.
    const float Gap = 4f;
    const float Chance = 0.6f;

    public VillainVoice(string lender, string[] packages, string[] bankNames, params (string Key, string Prefix)[] sets)
    {
        _lender = lender;
        _packages = packages;
        _bankNames = bankNames;
        _sets = sets;
    }

    void Load()
    {
        if (_loadTried)
        {
            return;
        }

        _loadTried = true;
        if (!Gather())
        {
            foreach (var package in _packages)
            {
                Packages.TryLoad(package, $"{package} for a villain's voice", $"{_lender}.voice");
            }

            if (!Gather())
            {
                var missing = string.Join(", ", _sets.Select(set => set.Prefix).Where(prefix => !_found.Contains(prefix)).Distinct());
                Log.Warn($"{_lender}.voice_lines", $"Only {_found.Count} of {_sets.Length} of a villain's lines were found. Missing: {missing}");
            }
        }

        foreach (var bank in Game.FindObjects<AkBank>())
        {
            var name = bank.IsValid ? bank.Name.ToString() ?? "" : "";
            if (Array.Exists(_bankNames, wanted => wanted.EndsWith('_')
                ? name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
                : name.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
            {
                Borrowed.Keep(bank, _lender);
                bank.LoadBank(false);
                _banks.Add(bank);
            }
        }
    }

    // True once every set's start has found a line.
    bool Gather()
    {
        _lines.Clear();
        _found.Clear();
        foreach (var line in Game.FindObjects<AkDialogueLineSingle>())
        {
            if (!line.IsValid)
            {
                continue;
            }

            var path = line.GetPathName();
            if (Array.Exists(Without, name => path.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var (key, prefix) in _sets)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (!_lines.TryGetValue(key, out var list))
                    {
                        _lines[key] = list = new List<AkDialogueLineSingle>();
                    }

                    list.Add(Borrowed.Keep(line, _lender));
                    if (line.DialogueVoice is { IsValid: true } voice)
                    {
                        Borrowed.Keep(voice, _lender);
                    }

                    _found.Add(prefix);
                    break;
                }
            }
        }

        return _found.Count == _sets.Length;
    }

    // One of the lines in a set, if it is time. Always skips the chance, not the gap.
    public bool Speak(string key, bool always = false)
    {
        Load();
        if (World.Now < _next || (!always && Random.Shared.NextSingle() > Chance)
            || !_lines.TryGetValue(key, out var list) || list.Count == 0 || World.Player is not { IsValid: true } player)
        {
            return false;
        }

        var line = list[Random.Shared.Next(list.Count)];
        if (!line.IsValid)
        {
            return false;
        }

        if (line.DialogueVoice is { IsValid: true } voice && player.GetAkDialogueComponent(false) is { IsValid: true } speaker)
        {
            Speaker.Hold(speaker, voice);
        }

        if (!player.StartSpeech(line))
        {
            Log.Warn($"{_lender}.voice", "The game wouldn't play a villain's line on the player.");
            return false;
        }

        _next = World.Now + Gap;
        _quietFrom = World.Now;
        SpokeAt = World.Now;
        LastLine = line.GetPathName();
        return true;
    }

    // A line from the set when nothing has been said for this long. True when one was spoken.
    public bool Quiet(string key, float seconds)
    {
        if (World.Now - _quietFrom < seconds)
        {
            return false;
        }

        _quietFrom = World.Now;
        return Speak(key, always: true);
    }

    // Batman's own voice back, for when the villain is off or in the car.
    public void Release() => Speaker.Release();

    public void Unload()
    {
        Release();
        foreach (var bank in _banks)
        {
            if (bank.IsValid)
            {
                bank.UnloadBank(false);
            }
        }

        _banks.Clear();
        _lines.Clear();
        _found.Clear();
        _loadTried = false;
        Borrowed.Let(_lender);
    }
}
