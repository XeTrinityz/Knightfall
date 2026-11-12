#nullable enable

using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// SOUNDS > GAME SOUNDS. Plays the game's own sounds and soundtrack moods through its own audio
// engine. Only what's loaded where you are can be found, so the list is read on request.
static class GameSounds
{
    // The soundtrack is one state group with MUSIC in its name, which the game switches between
    // moods like exploring and fighting. Sound events live in packages named Ww_<code>_, where the
    // code says what kind of sound it is.
    sealed record Kind(string Id, string Label, string Code);

    const string Moods = "";
    const string Rest = "*";

    static readonly Kind[] Kinds =
    {
        new("moods", "SOUNDTRACK MOODS", Moods),
        new("music", "MUSIC CUES", "MUS"),
        new("ambience", "AMBIENCE", "AMB"),
        new("weather", "WEATHER", "WEA"),
        new("voices", "VOICES", "DLG"),
        new("enemies", "ENEMIES", "NPC"),
        new("batmobile", "BATMOBILE", "BML"),
        new("vehicles", "VEHICLES", "VEH"),
        new("weapons", "WEAPONS", "WPN"),
        new("world", "WORLD OBJECTS", "PSO"),
        new("other", "EVERYTHING ELSE", Rest),
    };

    static readonly (string Id, string Label)[] Nothing = { ("", "USE REFRESH SOUND LIST") };

    static readonly List<(GameObject Sound, string Label, int Kind)> Found = new();
    static readonly List<(GameObject Sound, string Label)> Shown = new();
    static int _listed = -1;

    static AkStateName? _mood;
    static Actor? _playedOn;
    static AkWwise.FAkSoundHandle _handle;

    public static readonly Choice Show = new(
        "sounds.game_sounds.show", "SHOW",
        "Which kind of game sound SOUND lists.",
        Kinds.Select(kind => (kind.Id, kind.Label)).ToArray())
    {
        Preference = true,
    };

    public static readonly Choice Sound = new(
        "sounds.game_sounds.sound", "SOUND",
        "A sound the game has loaded where you are. A soundtrack mood takes over the game's music until STOP SOUND.",
        Nothing, saved: false);

    public static readonly Command Refresh = new(
        "sounds.game_sounds.refresh", "REFRESH SOUND LIST",
        "Reads which sounds the game has loaded right now. That changes as you move around the city.",
        RefreshList);

    public static readonly Command Play = new(
        "sounds.game_sounds.play", "PLAY SOUND",
        "Plays the picked sound on Batman, or switches the soundtrack to the picked mood.",
        PlayPicked);

    public static readonly Command Stop = new(
        "sounds.game_sounds.stop", "STOP SOUND",
        "Stops the sound you played, or gives the soundtrack back to the game.",
        () => Silence() ? Outcome.Done("STOPPED") : Outcome.Unchanged("NOTHING PLAYING"));

    public static readonly Catalog.Section[] Sections =
    {
        new("GAME SOUNDS", Show, Sound, Refresh, Play, Stop),
    };

    // Every tick. A new SHOW lists from what was found last time.
    public static void Tick()
    {
        if (_listed != Show.Index && Found.Count > 0)
        {
            List();
        }
    }

    static Outcome RefreshList()
    {
        Found.Clear();
        foreach (var mood in Game.FindObjects<AkStateName>())
        {
            if (mood.IsValid && !mood.IsClassDefaultObject && mood.ParentStateGroup is { IsValid: true } group
                && (group.Name.ToString() ?? "").Contains("MUSIC", StringComparison.OrdinalIgnoreCase))
            {
                Found.Add((mood, (mood.Name.ToString() ?? "").ToUpperInvariant(), 0));
            }
        }

        foreach (var sound in Game.FindObjects<AkEvent>())
        {
            if (sound.IsValid && !sound.IsClassDefaultObject)
            {
                Found.Add((sound, (sound.Name.ToString() ?? "").ToUpperInvariant(), KindOf(sound)));
            }
        }

        List();
        return Found.Count == 0 ? Outcome.Unchanged("NONE LOADED") : Outcome.Done($"{Shown.Count} OF {Found.Count}");
    }

    // The code in the name of the package the event lives in, like AMB in Ww_AMB_Rooftops.
    static int KindOf(GameObject sound)
    {
        var top = sound;
        while (top.Outer is { } outer)
        {
            top = outer;
        }

        var name = top.Name.ToString() ?? "";
        var end = name.StartsWith("Ww_", StringComparison.OrdinalIgnoreCase) ? name.IndexOf('_', 3) : -1;
        var code = end > 3 ? name[3..end] : "";
        var found = Array.FindIndex(Kinds, kind => kind.Code.Equals(code, StringComparison.OrdinalIgnoreCase) && kind.Code.Length > 0);
        return found >= 0 ? found : Kinds.Length - 1;
    }

    static void List()
    {
        _listed = Show.Index;
        Shown.Clear();
        foreach (var (sound, label, kind) in Found)
        {
            if (kind == _listed)
            {
                Shown.Add((sound, label));
            }
        }

        Shown.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
        Sound.SetOptions(Shown.Count == 0 ? Nothing : Shown.Select((entry, i) => (i.ToString(), entry.Label)).ToArray(), 0);
    }

    static Outcome PlayPicked()
    {
        if (!int.TryParse(Sound.CurrentId, out var index) || index < 0 || index >= Shown.Count)
        {
            return Outcome.Failed("NONE PICKED");
        }

        var (sound, label) = Shown[index];
        if (!sound.IsValid)
        {
            return Outcome.Failed("NOT LOADED HERE");
        }

        Silence();
        switch (sound)
        {
            case AkStateName mood:
                Actor.SetGlobalAudioState(mood);
                _mood = mood;
                break;

            case AkEvent played:
                if (World.Player is not { } player)
                {
                    return Outcome.NoPlayer;
                }

                // Played on Batman, like his gadgets, so a sound with a short range is still heard.
                _handle = player.StartAudioEvent(played);
                _playedOn = player;
                break;
        }

        return Outcome.Done(label);
    }

    static bool Silence()
    {
        var stopped = false;
        if (_mood is { } mood)
        {
            if (mood.IsValid)
            {
                Actor.ResetGlobalAudioState(mood);
            }

            _mood = null;
            stopped = true;
        }

        if (_playedOn is { } actor)
        {
            if (Things.Alive(actor))
            {
                var handle = _handle;
                actor.StopAudioEvent(out handle);
            }

            _playedOn = null;
            _handle = default;
            stopped = true;
        }

        return stopped;
    }

    public static void Release() => Silence();
}
