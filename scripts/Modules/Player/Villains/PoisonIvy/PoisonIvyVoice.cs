#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Poison Ivy's voice, from the night her plant tears up the militia's tanks at the Botanical
// Gardens. That chapter's level is already loaded for her vine. Her attack lines there are one
// line that picks among them at random, played through the game's speech system with her voice.
// To give each moment its own lines, every other line in the pick is weighted out for the one
// call, and the weights are put back a moment later.
static partial class PoisonIvy
{
    const string VoiceBankPath = "Ww_DLG_MAIN_CH07_S08_BOTANICAL_IVY_PLANT_ATTACKS.DLG_MAIN_CH07_S08_BOTANICAL_IVY_PLANT_ATTACKS";
    const string AttackLinePath = "Main_Ch07_S08_Botanical_Ivy_Plant_Attacks.AutoRand.Main_Ch07_S08_Botanical_Ivy_Plant_Attacks_AUTORAND_00";
    const string IvyVoicePath = "DLG_Voices.PoisonIvy";
    const string LineName = "Main_Ch07_S08_Botanical_Ivy_Plant_";

    // When she says something, the line in the pick, and what she says, for the HUD's log.
    static readonly (string Key, string Line, string Text)[] Lines =
    {
        ("vine", "Attack_01", "THINK OF IT AS NATURAL SELECTION."),
        ("vine", "Attacks_02", "NATURE ALWAYS WINS."),
        ("vine", "Attacks_5c", "I'LL DESTROY YOU ALL!"),
        ("vine", "Attacks_10", "MY POWER IS TOO GREAT."),
        ("vine", "Attacks_04", "THANK ME LATER, BATMAN."),
        ("wreck", "Attacks_01", "SO WEAK, THESE LITTLE MACHINES."),
        ("wreck", "Attacks_05", "FLIMSY MACHINES."),
        ("wreck", "Attacks_09", "THEY ARE NO MATCH FOR ME."),
        ("eat", "Attack_01", "THINK OF IT AS NATURAL SELECTION."),
        ("eat", "Attacks_02", "NATURE ALWAYS WINS."),
    };

    // She waits this long between lines, and only says one this often when the moment allows.
    const float VoiceGap = 5f;
    const float VoiceChance = 0.6f;

    // The pick is put back this long after a line starts.
    const float PickBack = 1f;

    static readonly Held<AkDialogueComponent, AkDialogueVoice?> Speaker = new("poison ivy voice", speaker => speaker.Voice, (speaker, voice) => speaker.SetVoice(voice!));

    static bool _voiceTried;
    static AkBank? _voiceBank;
    static AkDialogueLineRandom? _attackLine;
    static AkDialogueVoice? _ivyVoice;

    // Each moment's lines, as their place in the pick and what she says.
    static readonly Dictionary<string, List<(int Index, string Text)>> Voice = new();
    static (int Weighting, bool Used)[]? _pick;
    static float _pickBackAt = -1f;
    static float _nextLine;

    // What she said last and when, for the HUD.
    static string _said = "";
    static float _saidAt = -1f;

    // Her vine's level has to be in first, as it holds the lines.
    static void LoadVoice()
    {
        if (_voiceTried || !_vineTried)
        {
            return;
        }

        _voiceTried = true;
        _voiceBank = Kept(Game.FindObject<AkBank>(VoiceBankPath));
        _voiceBank?.LoadBank(false);
        _attackLine = Kept(Game.FindObject<AkDialogueLineRandom>(AttackLinePath));
        _ivyVoice = Kept(Game.FindObject<AkDialogueVoice>(IvyVoicePath));
        if (_attackLine is not null)
        {
            var events = _attackLine.DialogueEvents;
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i].DialogueEvent is not { IsValid: true } found)
                {
                    continue;
                }

                Kept(found);
                var name = found.Name.ToString() ?? "";
                foreach (var (key, line, text) in Lines)
                {
                    if (name.Equals(LineName + line, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!Voice.TryGetValue(key, out var list))
                        {
                            Voice[key] = list = new List<(int, string)>();
                        }

                        list.Add((i, text));
                    }
                }
            }
        }

        if (_voiceBank is null || _attackLine is null || _ivyVoice is null || Voice.Count == 0)
        {
            Log.Warn("poison_ivy.voice", "Poison Ivy's lines weren't found, so she stays quiet.");
        }
    }

    // One of the lines for a moment, if it is time. Always skips the chance, not the gap.
    static void Say(RPawnPlayer player, string key, bool always = false)
    {
        if (World.Now < _nextLine || (!always && Random.Shared.NextSingle() > VoiceChance)
            || _attackLine is not { IsValid: true } attack || _ivyVoice is not { IsValid: true } voice
            || !Voice.TryGetValue(key, out var list) || list.Count == 0
            || player.GetAkDialogueComponent(false) is not { IsValid: true } speaker)
        {
            return;
        }

        var (index, text) = list[Random.Shared.Next(list.Count)];
        PutPickBack();
        var events = attack.DialogueEvents;
        _pick = new (int, bool)[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            _pick[i] = (item.Weighting, item.Used);
            item.Weighting = i == index ? Math.Max(1, item.Weighting) : 0;
            item.Used = i != index;
            events[i] = item;
        }

        _pickBackAt = World.Now + PickBack;
        Speaker.Hold(speaker, voice);
        if (!player.StartSpeech(attack, default, default))
        {
            Log.Warn("poison_ivy.speech", "The game wouldn't play Poison Ivy's line on the player.");
            PutPickBack();
            return;
        }

        _nextLine = World.Now + VoiceGap;
        _said = text;
        _saidAt = World.Now;
    }

    // Each tick, the pick goes back as the game had it once the line has started.
    static void KeepVoice()
    {
        if (_pickBackAt >= 0f && World.Now >= _pickBackAt)
        {
            PutPickBack();
        }
    }

    static void PutPickBack()
    {
        if (_pick is { } was && _attackLine is { IsValid: true } attack)
        {
            var events = attack.DialogueEvents;
            for (var i = 0; i < events.Count && i < was.Length; i++)
            {
                var item = events[i];
                item.Weighting = was[i].Weighting;
                item.Used = was[i].Used;
                events[i] = item;
            }
        }

        _pick = null;
        _pickBackAt = -1f;
    }

    // Batman's own voice back, for when she is off or in the car.
    static void QuietVoice() => Speaker.Release();

    static void UnloadVoice()
    {
        PutPickBack();
        QuietVoice();
        if (_voiceBank is { IsValid: true } bank)
        {
            bank.UnloadBank(false);
        }

        _voiceBank = null;
        _attackLine = null;
        _ivyVoice = null;
        Voice.Clear();
        _voiceTried = false;
        _nextLine = 0f;
        _said = "";
        _saidAt = -1f;
    }
}
