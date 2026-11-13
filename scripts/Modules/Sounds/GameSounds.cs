#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// SOUNDS > GAME SOUNDS. Holds the soundtrack on a mood, and plays any of the game's own sounds
// through its own audio engine. Only what's loaded where you are can be found, so the list is
// read each time the tab comes up.
static class GameSounds
{
    // The soundtrack is one state group, which the game switches between moods like exploring
    // and fighting.
    const string MoodPath = "Ww_MUSIC_GameState.MUSIC_";

    static readonly (string Id, string Label, string State)[] Moods =
    {
        ("own", "GAME'S OWN", ""),
        ("silence", "SILENCE", "Silence"),
        ("explore", "EXPLORE", "Explore"),
        ("explore_1", "EXPLORE SETPIECE 1", "Explore_Setpiece_PT1"),
        ("explore_2", "EXPLORE SETPIECE 2", "Explore_Setpiece_PT2"),
        ("explore_3", "EXPLORE SETPIECE 3", "Explore_Setpiece_PT3"),
        ("explore_4", "EXPLORE SETPIECE 4", "Explore_Setpiece_PT4"),
        ("combat", "COMBAT", "Combat"),
        ("combat_a", "COMBAT A", "Combat_A"),
        ("combat_b", "COMBAT B", "Combat_B"),
        ("combat_c", "COMBAT C", "Combat_C"),
        ("combat_setpiece", "COMBAT SETPIECE", "Combat_Setpiece"),
        ("predator_intro", "PREDATOR INTRO", "Predator_Intro"),
        ("predator", "PREDATOR", "Predator_Layered"),
        ("predator_silent", "PREDATOR SILENT TAKEDOWN", "Predator_SilentTD"),
        ("predator_noisy", "PREDATOR NOISY TAKEDOWN", "Predator_NoisyTD"),
        ("chase", "CHASE", "Chase"),
        ("chase_1", "CHASE SETPIECE 1", "Chase_Setpiece_PT1"),
        ("chase_2", "CHASE SETPIECE 2", "Chase_Setpiece_PT2"),
        ("riddler_chase", "RIDDLER CHASE", "RiddlerChase"),
        ("puzzle", "PUZZLE", "Puzzle"),
        ("puzzle_2", "PUZZLE 2", "Puzzle_Pt2"),
        ("forensics", "FORENSIC TRAIL", "ForensicTrail"),
        ("hero_shot", "HERO SHOT", "Hero_Shot"),
        ("cutscene_1", "CUTSCENE 1", "Cutscene_1"),
        ("cutscene_2", "CUTSCENE 2", "Cutscene_2"),
        ("cutscene_4", "CUTSCENE 4", "Cutscene_4"),
        ("intro_1", "INTRO 1", "Intro_PT1"),
        ("intro_2", "INTRO 2", "Intro_PT2"),
        ("outro", "OUTRO", "Outro"),
        ("death", "DEATH", "Death"),
        ("combat_won", "COMBAT WON", "CombatSuccess"),
        ("combat_lost", "COMBAT LOST", "CombatFail"),
        ("setpiece_won", "COMBAT SETPIECE WON", "Combat_Setpiece_Success"),
        ("setpiece_lost", "COMBAT SETPIECE LOST", "Combat_Setpiece_Fail"),
        ("predator_won", "PREDATOR WON", "Predator_Success"),
        ("predator_lost", "PREDATOR LOST", "Predator_Fail"),
        ("chase_won", "CHASE WON", "Chase_Success"),
        ("chase_lost", "CHASE LOST", "Chase_Fail"),
        ("riddler_won", "RIDDLER WON", "RiddlerSuccess"),
        ("riddler_lost", "RIDDLER LOST", "RiddlerFail"),
        ("beacon_won", "COMMAND BEACON WON", "Command_Beacon_Success"),
        ("beacon_lost", "COMMAND BEACON LOST", "Command_Beacon_Fail"),
        ("tank_silence", "TANK SILENCE", "Tank_Silence"),
        ("stop", "STOPPED", "Stop"),
    };

    // Sound events live in packages named Ww_<code>_, where the code says what kind of sound it
    // is. Voice lines all share one code, so they go by the word after it too.
    sealed record Kind(string Id, string Label, params string[] Codes);

    static readonly Kind[] Kinds =
    {
        new("characters", "CHARACTERS AND GADGETS", "CHR", "DVC"),
        new("batmobile", "BATMOBILE", "BML"),
        new("vehicles", "VEHICLES", "VEH"),
        new("weapons", "WEAPONS", "WPN"),
        new("enemies", "ENEMIES", "NPC"),
        new("objects", "WORLD OBJECTS", "OBJ", "PSO", "PHY"),
        new("ambience", "AMBIENCE AND WEATHER", "AMB", "WEA"),
        new("setpieces", "SET PIECES", "SEQ"),
        new("effects", "EFFECTS", "VFX", "SFX", "SBJ"),
        new("interface", "MENUS AND HUD", "UI"),
        new("voices_story", "VOICES: STORY", "DLG"),
        new("voices_streets", "VOICES: STREETS", "DLG_OW", "DLG_CARS", "DLG_DRONE"),
        new("voices_fights", "VOICES: FIGHTS", "DLG_COMBAT", "DLG_EMOTES"),
        new("voices_stealth", "VOICES: STEALTH", "DLG_PRED"),
        new("other", "EVERYTHING ELSE"),
    };

    static readonly Dictionary<string, int> KindOfCode = Codes();

    // The most sounds one group lists. A bigger set is split by the words of its names.
    const int GroupMost = 100;

    // How long a pick has to rest before PREVIEW plays it, so holding a key doesn't play them all.
    const double PreviewRestSeconds = 0.3;

    // A sound bank is in by the next tick. One that never arrives is given up on.
    const double BankWaitSeconds = 3;

    static readonly (string Id, string Label)[] NotInGame = { ("", "OPEN THE MENU IN GAME") };
    static readonly (string Id, string Label)[] Nothing = { ("", "NONE LOADED HERE") };

    readonly record struct Entry(AkEvent Sound, string[] Words);

    // A run of the sorted sounds. Depth is how many leading words the group's name already says.
    readonly record struct Bunch(string Label, int Start, int Count, int Depth);

    static readonly List<AkEvent>[] ByKind = Kinds.Select(_ => new List<AkEvent>()).ToArray();
    static readonly Dictionary<GameObject, int> KindOfPackage = new();
    static readonly List<Bunch> Bunches = new();
    static Entry[] _entries = Array.Empty<Entry>();
    static int _total;
    static int _listedKind = -1;
    static int _listedGroup = -1;
    static int _epoch = -1;
    static bool _stale = true;
    static bool _shown;
    static long _previewed;

    static AkStateName? _mood;
    static string _moodId = "";
    static AkStateName? _held;

    static Actor? _playedOn;
    static AkWwise.FAkSoundHandle _handle;
    static AkEvent? _banked;
    static (AkEvent Sound, string Label)? _waiting;
    static long _waitingSince;

    public static readonly Choice Music = new(
        "sounds.game_sounds.soundtrack", "SOUNDTRACK",
        "Keeps the game's music on one mood, whatever is going on. GAME'S OWN hands it back.",
        Moods.Select(mood => (mood.Id, mood.Label)).ToArray())
    {
        Tick = HoldMood,
        Restore = GiveBack,
    };

    public static readonly Choice Show = new(
        "sounds.game_sounds.show", "SHOW",
        "Which kind of game sound to pick from.",
        Kinds.Select(kind => (kind.Id, kind.Label)).ToArray())
    {
        Preference = true,
    };

    public static readonly Choice Group = new(
        "sounds.game_sounds.group", "GROUP",
        "Sounds of that kind with names that start the same way. The number is how many it has.",
        NotInGame, saved: false);

    public static readonly Command Play = new(
        "sounds.game_sounds.play", "PLAY SOUND",
        "Plays the picked sound on you. It stops the last one you played.",
        PlayPicked);

    public static readonly Choice Sound = new(
        "sounds.game_sounds.sound", "SOUND",
        "A sound the game has loaded where you are. Select plays it.",
        NotInGame, saved: false)
    {
        Pressed = static () => Play.Press(),
        PressName = "PLAY",
    };

    public static readonly Command Stop = new(
        "sounds.game_sounds.stop", "STOP SOUND",
        "Stops the sound you played.",
        () => Silence() ? Outcome.Done("STOPPED") : Outcome.Unchanged("NOTHING PLAYING"));

    public static readonly Toggle Preview = new(
        "sounds.game_sounds.preview", "PREVIEW",
        "Plays each sound as you stop on it in SOUND.")
    {
        Preference = true,
    };

    public static readonly Command Refresh = new(
        "sounds.game_sounds.refresh", "REFRESH SOUND LIST",
        "Reads which sounds the game has loaded right now. That changes as you move around the city.",
        () =>
        {
            if (World.Info is null)
            {
                return Outcome.Failed("NOT IN GAME");
            }

            Scan();
            return _total == 0 ? Outcome.Unchanged("NONE LOADED") : Outcome.Done($"{_total} SOUNDS");
        });

    public static readonly Catalog.Section[] Sections =
    {
        new("MUSIC", Music),
        new("FIND A SOUND", Show, Group, Sound),
        new("PLAY", Play, Stop, Preview, Refresh),
    };

    // Every tick. The list is read when the tab comes up, and follows SHOW and GROUP from then on.
    public static void Tick()
    {
        // A new world took every sound with it. Nothing kept from the last one is touched again.
        if (_epoch != World.Epoch)
        {
            _epoch = World.Epoch;
            Forget();
        }

        Await();

        var shown = Menu.Shows(Sound);
        if (shown && (_stale || !_shown) && World.Info is not null)
        {
            Scan();
        }

        _shown = shown;
        if (_stale)
        {
            return;
        }

        // Off the tab too, since a hotkey can change either.
        if (_listedKind != Show.Index)
        {
            ListGroups();
        }
        else if (_listedGroup != Group.Index)
        {
            ListSounds(null);
        }

        // Only a pick made on the tab with PREVIEW on plays, not one from before.
        var touched = Sound.TouchedAt;
        if (!shown || !Preview.On)
        {
            _previewed = touched;
        }
        else if (touched != _previewed
            && (Stopwatch.GetTimestamp() - touched) / (double)Stopwatch.Frequency >= PreviewRestSeconds)
        {
            _previewed = touched;
            Play.Report(PlayPicked());
        }
    }

    static Dictionary<string, int> Codes()
    {
        var codes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Kinds.Length; i++)
        {
            foreach (var code in Kinds[i].Codes)
            {
                codes[code] = i;
            }
        }

        return codes;
    }

    // Set again every tick. The game only sets the mood when its own pick changes, and its
    // audio layer drops a state that is already the one set, so this costs nothing until then.
    static void HoldMood()
    {
        // Not at the main menu, which has music of its own.
        if (World.Player is null)
        {
            return;
        }

        var id = Music.CurrentId;
        if (_moodId != id || _mood is not { IsValid: true })
        {
            _moodId = id;
            _mood = Game.FindObject<AkStateName>(MoodPath + Moods[Music.Index].State);
        }

        if (_mood is { IsValid: true } mood)
        {
            Actor.SetGlobalAudioState(mood);
            _held = mood;
        }
    }

    static void GiveBack()
    {
        var held = _held;
        _held = null;
        _mood = null;
        _moodId = "";
        if (held is not { IsValid: true })
        {
            return;
        }

        if (GameMood(held) is { } own)
        {
            Actor.SetGlobalAudioState(own);
        }
        else
        {
            // No state at all, until the game next picks one.
            Actor.ResetGlobalAudioState(held);
        }
    }

    // The mood the game last asked for itself. Holding ours doesn't change what it remembers.
    static unsafe AkStateName? GameMood(AkStateName held)
    {
        if (held.ParentStateGroup is not { IsValid: true } group)
        {
            return null;
        }

        using var process = Process.GetCurrentProcess();
        if (process.MainModule is not { } exe || exe.ModuleMemorySize < Offsets.MusicStates + Offsets.MusicStatePairs * 2 * sizeof(int))
        {
            return null;
        }

        var pairs = (int*)(MemUtil.GetBaseAddress() + Offsets.MusicStates);
        var wanted = group.HashValue;
        for (var i = 0; i < Offsets.MusicStatePairs; i++)
        {
            if (pairs[i * 2] != wanted)
            {
                continue;
            }

            var hash = pairs[i * 2 + 1];
            foreach (var state in Game.FindObjects<AkStateName>())
            {
                if (state.IsValid && state.HashValue == hash && ReferenceEquals(state.ParentStateGroup, group))
                {
                    return state;
                }
            }
        }

        return null;
    }

    static void Scan()
    {
        _stale = false;
        _total = 0;
        KindOfPackage.Clear();
        foreach (var list in ByKind)
        {
            list.Clear();
        }

        foreach (var sound in Game.FindObjects<AkEvent>())
        {
            if (!sound.IsValid || sound.Outer is not { } package)
            {
                continue;
            }

            // A package's kind is worked out once, however many sounds it holds.
            if (!KindOfPackage.TryGetValue(package, out var kind))
            {
                var top = package;
                while (top.Outer is { } outer)
                {
                    top = outer;
                }

                kind = KindOf(top.Name.ToString() ?? "");
                KindOfPackage[package] = kind;
            }

            ByKind[kind].Add(sound);
            _total++;
        }

        KindOfPackage.Clear();
        ListGroups();
    }

    // The code in the package's name, like AMB in Ww_AMB_Rooftops. What has no code of its own
    // goes in the last kind.
    static int KindOf(string package)
    {
        var parts = package.Split('_', 4);
        if (parts.Length < 2 || !parts[0].Equals("Ww", StringComparison.OrdinalIgnoreCase))
        {
            return Kinds.Length - 1;
        }

        if (parts.Length > 2 && KindOfCode.TryGetValue(parts[1] + "_" + parts[2], out var kind))
        {
            return kind;
        }

        return KindOfCode.TryGetValue(parts[1], out kind) ? kind : Kinds.Length - 1;
    }

    // The shown kind's sounds, sorted by the words of their names and split into groups.
    static void ListGroups()
    {
        var picked = _listedKind == Show.Index && _listedGroup >= 0 && _listedGroup < Bunches.Count ? Bunches[_listedGroup].Label : null;
        var sound = PickedSound();

        _listedKind = Show.Index;
        var entries = new List<Entry>();
        foreach (var found in ByKind[Math.Clamp(_listedKind, 0, Kinds.Length - 1)])
        {
            if (found.IsValid && Words(found) is { } words)
            {
                entries.Add(new Entry(found, words));
            }
        }

        entries.Sort(static (a, b) => Compare(a.Words, b.Words));
        _entries = entries.ToArray();
        Bunches.Clear();
        if (_entries.Length > 0)
        {
            Split(0, _entries.Length, 0, "");
        }

        if (Bunches.Count == 0)
        {
            _listedGroup = 0;
            Group.SetOptions(World.Info is null ? NotInGame : Nothing, 0);
            Sound.SetOptions(World.Info is null ? NotInGame : Nothing, 0);
            return;
        }

        var index = picked is null ? 0 : Math.Max(0, Bunches.FindIndex(bunch => bunch.Label == picked));
        Group.SetOptions(Bunches.Select((bunch, i) => (i.ToString(), $"{bunch.Label}  ({bunch.Count})")).ToArray(), index);
        ListSounds(sound);
    }

    static void ListSounds(AkEvent? keep)
    {
        _listedGroup = Group.Index;
        if (_listedGroup < 0 || _listedGroup >= Bunches.Count)
        {
            return;
        }

        var bunch = Bunches[_listedGroup];
        var options = new (string, string)[bunch.Count];
        var index = 0;
        for (var i = 0; i < bunch.Count; i++)
        {
            var entry = _entries[bunch.Start + i];
            options[i] = (i.ToString(), Label(entry, bunch.Depth));
            if (ReferenceEquals(entry.Sound, keep))
            {
                index = i;
            }
        }

        Sound.SetOptions(options, index);
    }

    // The name without the Play_ every sound starts with. Stop_ events only end a sound that is
    // playing, so they aren't listed.
    static string[]? Words(AkEvent sound)
    {
        var name = sound.Name.ToString() ?? "";
        if (name.StartsWith("Stop_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (name.StartsWith("Play_", StringComparison.OrdinalIgnoreCase))
        {
            name = name[5..];
        }

        var words = name.ToUpperInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? null : words;
    }

    static int Compare(string[] a, string[] b)
    {
        var shared = Math.Min(a.Length, b.Length);
        for (var i = 0; i < shared; i++)
        {
            var order = string.CompareOrdinal(a[i], b[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return a.Length - b.Length;
    }

    static string WordAt(int entry, int depth)
    {
        var words = _entries[entry].Words;
        return depth < words.Length ? words[depth] : "";
    }

    // Splits a run that is too long by the next word of its names. Runs that share a word and
    // are still too long are split again by the word after. Small neighbours are put together,
    // named by the first and last word they cover.
    static void Split(int start, int end, int depth, string prefix)
    {
        if (end - start <= GroupMost)
        {
            Bunches.Add(new Bunch(prefix.Length == 0 ? "ALL" : prefix, start, end - start, depth));
            return;
        }

        var from = start;
        var held = 0;
        var first = "";
        var last = "";

        void Flush()
        {
            if (held == 0)
            {
                return;
            }

            var one = first == last;
            var words = one ? first : first.Length == 0 ? "TO " + last : first + " - " + last;
            Bunches.Add(new Bunch(Join(prefix, words), from, held, one && first.Length > 0 ? depth + 1 : depth));
            held = 0;
        }

        for (var i = start; i < end;)
        {
            var word = WordAt(i, depth);
            var next = i + 1;
            while (next < end && WordAt(next, depth) == word)
            {
                next++;
            }

            var size = next - i;
            if (size > GroupMost)
            {
                Flush();
                if (word.Length > 0)
                {
                    Split(i, next, depth + 1, Join(prefix, word));
                }
                else
                {
                    // Names that are the same to the last word can't be split any further.
                    Bunches.Add(new Bunch(prefix.Length == 0 ? "ALL" : prefix, i, size, depth));
                }
            }
            else
            {
                if (held + size > GroupMost)
                {
                    Flush();
                }

                if (held == 0)
                {
                    from = i;
                    first = word;
                }

                last = word;
                held += size;
            }

            i = next;
        }

        Flush();
    }

    static string Join(string prefix, string words) =>
        prefix.Length == 0 ? words : words.Length == 0 ? prefix : prefix + " " + words;

    // The words the group's name doesn't already say.
    static string Label(Entry entry, int depth) =>
        depth > 0 && depth < entry.Words.Length ? string.Join(' ', entry.Words, depth, entry.Words.Length - depth) : string.Join(' ', entry.Words);

    static Entry? Picked()
    {
        if (_listedGroup < 0 || _listedGroup >= Bunches.Count || !int.TryParse(Sound.CurrentId, out var index))
        {
            return null;
        }

        var bunch = Bunches[_listedGroup];
        return index >= 0 && index < bunch.Count ? _entries[bunch.Start + index] : null;
    }

    static AkEvent? PickedSound() => Picked()?.Sound;

    static Outcome PlayPicked()
    {
        if (World.Player is null)
        {
            return Outcome.NoPlayer;
        }

        // Asked for by a key before the tab was ever up, or after a new world.
        if (_stale)
        {
            Scan();
        }

        if (Picked() is not { } entry)
        {
            return Outcome.Failed("NONE PICKED");
        }

        var sound = entry.Sound;
        if (!sound.IsValid)
        {
            _stale = true;
            return Outcome.Failed("NOT LOADED NOW");
        }

        var label = Label(entry, Bunches[_listedGroup].Depth);
        Quiet();

        // Most sounds, every voice line among them, sit in a bank the game only loads when it
        // wants one played. Ours is asked for, and let go when another sound takes its place.
        var last = _banked;
        _banked = null;
        if (last is { IsValid: true } && ReferenceEquals(last.ParentBank, sound.ParentBank))
        {
            _banked = last;
        }
        else
        {
            if (!sound.IsAssetBankLoaded())
            {
                if (!sound.RequestAssetBankLoad())
                {
                    LetGo(last);
                    return Outcome.Failed("NO SOUND BANK");
                }

                _banked = sound;
            }

            LetGo(last);
        }

        if (!sound.IsAssetBankLoaded())
        {
            _waiting = (sound, label);
            _waitingSince = Stopwatch.GetTimestamp();
            return Outcome.Done("LOADING");
        }

        return Begin(sound, label);
    }

    static Outcome Begin(AkEvent sound, string label)
    {
        if (World.Player is not { } player)
        {
            return Outcome.NoPlayer;
        }

        // Played on you, like your gadgets, so a sound with a short range is still heard.
        var handle = player.StartAudioEvent(sound);
        if (handle.EventInstanceID == 0)
        {
            return Outcome.Failed("DID NOT PLAY");
        }

        _handle = handle;
        _playedOn = player;
        return Outcome.Done(label);
    }

    // A sound waiting for its bank plays as soon as that is in.
    static void Await()
    {
        if (_waiting is not { } due)
        {
            return;
        }

        if (!due.Sound.IsValid)
        {
            _waiting = null;
            _banked = null;
            _stale = true;
            Play.Report(Outcome.Failed("NOT LOADED NOW"));
        }
        else if (due.Sound.IsAssetBankLoaded())
        {
            _waiting = null;
            Play.Report(Begin(due.Sound, due.Label));
        }
        else if ((Stopwatch.GetTimestamp() - _waitingSince) / (double)Stopwatch.Frequency >= BankWaitSeconds)
        {
            _waiting = null;
            LetGo(_banked);
            _banked = null;
            Play.Report(Outcome.Failed("COULD NOT LOAD"));
        }
    }

    static void LetGo(AkEvent? banked)
    {
        if (banked is { IsValid: true })
        {
            banked.RequestAssetBankUnload();
        }
    }

    // Stops the sound and leaves its bank in, for the next one to share or let go.
    static bool Quiet()
    {
        var stopped = _waiting is not null;
        _waiting = null;
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

    static bool Silence()
    {
        var stopped = Quiet();
        LetGo(_banked);
        _banked = null;
        return stopped;
    }

    static void Forget()
    {
        _playedOn = null;
        _handle = default;
        _banked = null;
        _waiting = null;
        _entries = Array.Empty<Entry>();
        Bunches.Clear();
        foreach (var list in ByKind)
        {
            list.Clear();
        }

        _total = 0;
        _listedGroup = -1;
        _stale = true;
    }

    public static void Release() => Silence();
}
