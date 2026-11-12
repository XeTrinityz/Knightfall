#nullable enable

using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// Riddler's own voice from the mech fight, in the orphanage level loaded with the mech. His lines
// play the way the game plays a bark, through its speech system, and the ticker types out his
// words. His dialogue banks are loaded while the mech is.
//
// A line's sound alone plays nothing. The speech system picks the recording for the voice, and
// only plays a line for a speaker with that voice. So the player speaks with Riddler's voice
// while the mech is on, and Batman's is put back after.
static partial class RiddlerMech
{
    static readonly Held<AkDialogueComponent, AkDialogueVoice?> SpeakerVoice =
        new("mech voice", speaker => speaker.Voice, (speaker, voice) => speaker.SetVoice(voice!));
    static AkDialogueVoice? _riddlerVoice;

    // A line to say. A single line is always the same words. A random line picks one of its own
    // set as it is said, and marks it used, which shows which one it picked.
    sealed record Spoken(AkDialogueSpeech Line, string[]? Typed, AkDialogueLineRandom? Pick, Dictionary<nint, string[]>? Picked);

    const string BankPrefix = "DLG_RIDDLER_HUB_MECH_";

    static readonly List<Spoken> SmashVoice = new();
    static readonly List<Spoken> BeamVoice = new();
    static readonly List<Spoken> SurgeVoice = new();
    static readonly List<Spoken> TauntVoice = new();
    static readonly List<AkBank> Banks = new();

    // He waits this long between lines, speaks on an attack this often, and taunts this often
    // when it is quiet.
    const float VoiceGap = 4f;
    const float VoiceChance = 0.6f;
    const float VoiceQuiet = 25f;
    static float _nextVoice;

    static bool Voiced => TauntVoice.Count > 0 || SmashVoice.Count > 0;

    static List<Spoken>? SetFor(string path) =>
        path.StartsWith("Riddler_Hub_Mech_Barks_Smash_Attack.", StringComparison.OrdinalIgnoreCase) ? SmashVoice
        : path.StartsWith("Riddler_Hub_Mech_Barks_Beam_Attack.", StringComparison.OrdinalIgnoreCase) ? BeamVoice
        : path.StartsWith("Riddler_Hub_Mech_Barks_Revive_Robots.", StringComparison.OrdinalIgnoreCase) ? SurgeVoice
        : path.StartsWith("Riddler_Hub_Mech_P1_Taunts.", StringComparison.OrdinalIgnoreCase) ? TauntVoice
        : null;

    static void LoadVoice()
    {
        foreach (var bank in Game.FindObjects<AkBank>())
        {
            if (bank.IsValid && bank.Name.ToString().StartsWith(BankPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Kept(bank);
                bank.LoadBank(false);
                Banks.Add(bank);
            }
        }

        foreach (var line in Game.FindObjects<AkDialogueLineSingle>())
        {
            if (line.IsValid && line.DialogueEvent is { IsValid: true } said && SetFor(line.GetPathName()) is { } set)
            {
                _riddlerVoice ??= Kept(line.DialogueVoice);
                Kept(line);
                set.Add(new Spoken(line, Typed(Words(said))[0], null, null));
            }
        }

        foreach (var line in Game.FindObjects<AkDialogueLineRandom>())
        {
            if (!line.IsValid || SetFor(line.GetPathName()) is not { } set)
            {
                continue;
            }

            var picked = new Dictionary<nint, string[]>();
            var items = line.DialogueEvents;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].DialogueEvent is { IsValid: true } said)
                {
                    picked[said.Ptr] = Typed(Words(said))[0];
                }
            }

            if (picked.Count > 0)
            {
                _riddlerVoice ??= Kept(line.DialogueVoice);
                Kept(line);
                set.Add(new Spoken(line, null, line, picked));
            }
        }
    }

    // His words as the ticker shows them, in capitals on one line. A line with no subtitle is
    // still heard, and the ticker trails off for it.
    static string Words(AkDialogueEvent line)
    {
        var text = line.GetSubtitle().ToString() ?? "";
        var words = string.Join(' ', text.Replace('"', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return words.Length > 0 ? words : "...";
    }

    // One of his lines for the moment, spoken and typed out. Without his voice the ticker types
    // one of the written lines instead.
    static void Speak(List<Spoken> voice, string[][] written, bool always = false)
    {
        if (!Voiced)
        {
            Say(written);
            return;
        }

        if (voice.Count == 0 || World.Now < _nextVoice || (!always && Random.Shared.NextSingle() > VoiceChance)
            || World.Player is not { IsValid: true } player)
        {
            return;
        }

        var spoken = voice[Random.Shared.Next(voice.Count)];
        if (!spoken.Line.IsValid)
        {
            return;
        }

        _pickBefore = spoken.Pick is { IsValid: true } pick ? new bool[pick.DialogueEvents.Count] : Array.Empty<bool>();
        for (var i = 0; i < _pickBefore.Length; i++)
        {
            _pickBefore[i] = spoken.Pick!.DialogueEvents[i].Used;
        }

        if (_riddlerVoice is { IsValid: true } riddler && player.GetAkDialogueComponent(false) is { IsValid: true } speaker)
        {
            SpeakerVoice.Hold(speaker, riddler);
        }

        // Refused, his words are still typed out.
        if (!player.StartSpeech(spoken.Line))
        {
            Log.Warn("riddler_mech.voice", "The game wouldn't play Riddler's line on the player, so only its words show.");
        }

        _nextVoice = World.Now + VoiceGap;
        _quietFrom = World.Now;
        if (spoken.Typed is { } typed)
        {
            ShowLine(typed);
            _picking = null;
            return;
        }

        // A random line marks its pick once the line starts to play, a moment later, so the
        // ticker waits for it.
        _picking = spoken;
        _pickBy = World.Now + PickWait;
    }

    const float PickWait = 1f;
    static Spoken? _picking;
    static bool[] _pickBefore = Array.Empty<bool>();
    static float _pickBy;

    static void ShowLine(string[] typed)
    {
        _line = typed;
        _lineAt = World.Now;
    }

    // Every tick, until the random line has marked its pick.
    static void WaitForPick()
    {
        if (_picking is null)
        {
            return;
        }

        if (PickedWords(_picking, _pickBefore) is { } typed)
        {
            ShowLine(typed);
            _picking = null;
        }
        else if (World.Now >= _pickBy)
        {
            _picking = null;
        }
    }

    // The words of the line a random line picked: the one marked used that wasn't before. When
    // every line had been used it starts over, so fewer are marked and the one left is the pick.
    static string[]? PickedWords(Spoken spoken, bool[] before)
    {
        if (spoken.Pick is not { IsValid: true } pick || spoken.Picked is not { } picked)
        {
            return null;
        }

        var items = pick.DialogueEvents;
        string[]? only = null;
        var marked = 0;
        var markedBefore = before.Count(used => used);
        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].Used || items[i].DialogueEvent is not { IsValid: true } said || !picked.TryGetValue(said.Ptr, out var words))
            {
                continue;
            }

            if (i < before.Length && !before[i])
            {
                return words;
            }

            only = words;
            marked++;
        }

        return marked == 1 && markedBefore > 1 ? only : null;
    }

    // Batman's own voice back, for when the mech is off or in the car.
    static void Unvoice() => SpeakerVoice.Release();

    static void UnloadVoice()
    {
        Unvoice();
        _riddlerVoice = null;
        foreach (var bank in Banks)
        {
            if (bank.IsValid)
            {
                bank.UnloadBank(false);
            }
        }

        Banks.Clear();
        SmashVoice.Clear();
        BeamVoice.Clear();
        SurgeVoice.Clear();
        TauntVoice.Clear();
    }
}
