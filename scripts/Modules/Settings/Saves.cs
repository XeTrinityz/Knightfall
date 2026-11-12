#nullable enable

using System.Diagnostics;
using System.Globalization;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// SETTINGS > SAVES. Every save on disk is a row: the slot's own files, the backups the game makes of each
// write, and copies kept here. Pressing one loads it the way the slot screen does. Its bytes go
// to SetupActiveSave and the game restarts from its active save, as the pause menu's restart does.
static class Saves
{
    const double ConfirmSeconds = 4;
    const double KeepSeconds = 15;
    const double RescanSeconds = 3;
    const int PlaceLetters = 20;

    // The save manager's numbers for the two runs one save file holds.
    const int MainStory = 0;
    const int PlusStory = 2;

    static readonly Dictionary<string, Command> Rows = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<int, string> PlaceNames = new();

    static List<SaveFile> _all = new();
    static bool _scanDue;
    static bool _buildDue;
    static long _scanAt;
    static long _keepBy;
    static Outcome _kept;
    static int _lastSlot = -1;
    static string _armedFor = "";
    static long _armedAt;
    static (SaveFile File, Command Row, bool Delete)? _asked;
    static (string Path, Outcome Outcome)? _flash;

    public static readonly Command SaveAndKeep = new(
        "settings.saves.save_and_keep", "SAVE AND KEEP A COPY",
        "Saves now and keeps a copy of that save where the game never writes over it. Good before trying something you may want to undo.",
        SaveKeeping);

    public static readonly Command Restart = new(
        "settings.saves.restart", "RESTART FROM CHECKPOINT",
        "Loads your last save, the same as the pause menu's restart. Press twice to confirm.",
        RestartNow);

    public static readonly Command NewGamePlus = new(
        "settings.saves.new_game_plus", "START NEW GAME + NOW",
        "Starts a fresh New Game + run right here. It wipes any run under way and anything since your last save, but keeps a copy of your save first. Press twice.",
        StartPlus);

    public static readonly Choice Show = new(
        "settings.saves.show", "SHOW",
        "THIS SLOT lists the saves of the slot you are playing. ALL SLOTS lists every slot.",
        new[] { ("slot", "THIS SLOT"), ("all", "ALL SLOTS") })
    {
        Preference = true,
        Opening = static () => _scanDue = true,
        Changed = static () => _buildDue = true,
    };

    public static readonly Choice Story = new(
        "settings.saves.load_into", "LOAD INTO",
        "A save holds your main story and your New Game + run. SAME STORY carries on with the one you are playing. The others pick one.",
        new[] { ("same", "SAME STORY"), ("main", "MAIN STORY"), ("plus", "NEW GAME +") }, saved: false);

    static readonly Catalog.Section[] Fixed =
    {
        new("NOW", Progress.SaveNow, SaveAndKeep, Restart, NewGamePlus),
        new("LIST", Show, Story),
    };

    static Catalog.Section[] _sections = Fixed;

    // The fixed rows, then the saves as they were last read.
    public static IReadOnlyList<Catalog.Section> Sections() => _sections;

    public static void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        if (_keepBy != 0 && now > _keepBy)
        {
            _keepBy = 0;
            SaveAndKeep.Report(Outcome.Failed("NO SAVE MADE"));
        }

        if (_asked is { } asked)
        {
            _asked = null;
            Answer(asked.File, asked.Row, asked.Delete);
        }

        if (_scanAt != 0 && now > _scanAt)
        {
            _scanAt = 0;
            _scanDue = true;
        }

        if (_scanDue)
        {
            _scanDue = false;
            _all = SaveFiles.List();
            _buildDue = true;
        }

        if (_buildDue)
        {
            _buildDue = false;
            Build();
            Menu.Reshow(Show);
            if (_flash is { } flash)
            {
                _flash = null;
                if (Rows.TryGetValue(flash.Path, out var row))
                {
                    row.Report(flash.Outcome);
                }
            }
        }
    }

    // Every save the game writes to disk comes through here first, with the file's name and
    // all of its bytes.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.BeginRealSave))]
    static void BeginRealSave(RPlayerController self, FString filename, TArray<byte> data, FString saveDescription)
    {
        try
        {
            Writing(filename.ToString() ?? "", data);
        }
        catch (Exception error)
        {
            Log.Error("saves.write", "A save could not be looked at on its way to disk", error);
        }

        Redirects.RunOriginal();
    }

    static void Writing(string name, TArray<byte> data)
    {
        if (!SaveFiles.Named(name, out var slot, out _))
        {
            return;
        }

        _lastSlot = slot;
        _scanAt = Stopwatch.GetTimestamp() + (long)(RescanSeconds * Stopwatch.Frequency);
        if (_keepBy == 0)
        {
            return;
        }

        _keepBy = 0;
        try
        {
            var made = SaveFiles.Keep(name, data.AsSpan(), out var path);
            _kept = made ? Outcome.Done("KEPT") : path.Length > 0 ? Outcome.Unchanged("ALREADY KEPT") : Outcome.Failed("NOT A SAVE");
            if (path.Length > 0)
            {
                _flash = (path, _kept);
            }
        }
        catch
        {
            _kept = Outcome.Failed("FAILED");
            throw;
        }
        finally
        {
            SaveAndKeep.Report(_kept);
        }
    }

    static Outcome SaveKeeping()
    {
        if (World.Info is not { } game || World.Player is null)
        {
            return Outcome.NoPlayer;
        }

        if (game.IsChallengeMode())
        {
            return Outcome.Failed("NOT IN CHALLENGES");
        }

        if (_keepBy != 0 || game.GetIsSaveInProgress())
        {
            return Outcome.Unchanged("ALREADY SAVING");
        }

        // The copy is taken when the save comes through BeginRealSave, which can be inside this
        // call or a few ticks on. The game skips a save asked for too soon after the last one.
        _keepBy = Stopwatch.GetTimestamp() + (long)(KeepSeconds * Stopwatch.Frequency);
        Game.GetCheatManager().AutoSaveGame();
        return _keepBy == 0 ? _kept : Outcome.Unchanged("SAVING");
    }

    static Outcome RestartNow()
    {
        if (World.Info is not { } game || World.Player is null)
        {
            return Outcome.NoPlayer;
        }

        if (_keepBy != 0 || game.GetIsSaveInProgress())
        {
            return Outcome.Unchanged("STILL SAVING");
        }

        if (!Confirmed("restart"))
        {
            return Outcome.Unchanged("PRESS AGAIN");
        }

        Menu.Close();
        game.Game_Restart();
        return Outcome.Done("RESTARTING");
    }

    // The main menu's own steps for NEW GAME +, in its order, run from inside the game. The run
    // becomes the active one, its old story data is dropped, the options go to the save in
    // memory, and the game starts at the story's opening.
    static Outcome StartPlus()
    {
        if (World.Info is not { } game || World.Controller is not { IsValid: true } controller || World.Player is null)
        {
            return Outcome.NoPlayer;
        }

        if (!Game.TryGetGameRI(out var state) || state.PersistentOptions is not { IsValid: true } options || controller.GetSaveGameManager() is not { IsValid: true } manager)
        {
            return Outcome.Failed("NO SAVE DATA");
        }

        if (game.IsChallengeMode())
        {
            return Outcome.Failed("NOT IN CHALLENGES");
        }

        if (options.NewGamePlusEnabled == 0)
        {
            return Outcome.Failed("FINISH THE STORY FIRST");
        }

        if (Switcher.Away || Sidekick.Waiting is not null)
        {
            return Outcome.Failed("SWITCH BACK FIRST");
        }

        if (_keepBy != 0 || game.GetIsSaveInProgress())
        {
            return Outcome.Unchanged("STILL SAVING");
        }

        if (!Confirmed("new game plus"))
        {
            return Outcome.Unchanged("PRESS AGAIN");
        }

        _all = SaveFiles.List();
        _buildDue = true;
        if (!KeepNewestOf(ActiveSlot(), "starting New Game + over"))
        {
            return Outcome.Failed("KEEP FAILED");
        }

        // The story opens before the upgraded Batmobile exists, so the menu takes that one off.
        const string upgraded = "Playable_BatmobileMk2_Std";
        if (string.Equals(options.GetActivePlayerSkin(GameText.Once("Batmobile"), true, false, false).ToString(), upgraded, StringComparison.OrdinalIgnoreCase))
        {
            options.ClearActivePlayerSkin(GameText.Once("Batmobile"));
        }

        Log.Info("Saves: starting a fresh New Game + run.");
        Menu.Close();
        game.SetGameSpeed(1f);
        manager.SetActiveStorySlotID(RSaveGameManager.StorySlots.StorySlot_StoryPlus);
        controller.Live_InvalidateStoryData();
        controller.SaveGameOptionsToMemory();
        controller.SetReturnPoint(GameText.Once("FE_MainMenu"));

        // As a restart does, so nothing from this world plays on over the opening.
        BmSDK.Engine.AkWwise.FullReset(false);
        game.Game_StartGameAt(GameText.Once("FE_NewGame"));
        return Outcome.Done("STARTING");
    }

    static Outcome Load(SaveFile file)
    {
        if (!file.Readable)
        {
            return Outcome.Failed("NOT A SAVE");
        }

        if (World.Info is not { } game || World.Controller is not { IsValid: true } controller || World.Player is null)
        {
            return Outcome.NoPlayer;
        }

        if (game.IsChallengeMode())
        {
            return Outcome.Failed("NOT IN CHALLENGES");
        }

        if (Switcher.Away || Sidekick.Waiting is not null)
        {
            return Outcome.Failed("SWITCH BACK FIRST");
        }

        if (_keepBy != 0 || game.GetIsSaveInProgress())
        {
            return Outcome.Unchanged("STILL SAVING");
        }

        if (controller.GetSaveGameManager() is not { IsValid: true } manager)
        {
            return Outcome.Failed("NO SAVE DATA");
        }

        // A save with no New Game + run only has the main story to carry on with.
        var playing = (int)manager.GetActiveStorySlotID();
        var story = Story.Index switch { 1 => MainStory, 2 => PlusStory, _ => playing };
        if (story == PlusStory && file.Plus <= 0f)
        {
            if (Story.Index == 2)
            {
                return Outcome.Failed("NO NEW GAME + RUN");
            }

            story = MainStory;
        }

        if (!Confirmed("load " + file.Path))
        {
            return Outcome.Unchanged("PRESS AGAIN");
        }

        var data = SaveFiles.Read(file.Path);
        if (!KeepNewest(file))
        {
            return Outcome.Failed("KEEP FAILED");
        }

        using (var bytes = new TArray<byte>(data))
        {
            controller.SetupActiveSave(file.Slot, file.Number, bytes);
        }

        // The main menu's CONTINUE STORY and CONTINUE STORY+ pick the run the same way.
        if (story != playing)
        {
            manager.SetActiveStorySlotID((RSaveGameManager.StorySlots)story);
            manager.DecompressSavedGame(false);
        }

        Log.Info($"Saves: loading {Path.GetFileName(file.Path)} into slot {file.Slot + 1}, story {story}.");
        _lastSlot = file.Slot;
        _scanDue = true;
        Menu.Close();
        game.Game_Restart();
        return Outcome.Done("LOADING");
    }

    // The game writes over a slot's three files in turn. So loading an older save into the slot
    // being played would soon push its newest save out, and that one is kept first. Loading the
    // newest itself, or a save of another slot, puts nothing at risk.
    static bool KeepNewest(SaveFile loading)
    {
        _all = SaveFiles.List();
        _buildDue = true;
        var newest = _all.FirstOrDefault(file => file.Kind == SaveKind.File && file.Readable && file.Slot == loading.Slot);
        return loading.Slot != ActiveSlot() || newest is null || newest.Stamp == loading.Stamp
            || KeepNewestOf(loading.Slot, "loading an older one");
    }

    // Keeps a copy of a slot's newest file. False when it couldn't be kept, so the caller can
    // leave everything as it is.
    static bool KeepNewestOf(int slot, string before)
    {
        try
        {
            var newest = _all.FirstOrDefault(file => file.Kind == SaveKind.File && file.Readable && file.Slot == slot);
            if (newest is not null && SaveFiles.Keep(newest, out _))
            {
                Log.Info($"Saves: kept the newest save of slot {slot + 1} before {before}.");
            }

            return true;
        }
        catch (Exception error)
        {
            Log.Error("saves.keep", "The newest save could not be kept, so nothing was changed", error);
            return false;
        }
    }

    // The hotkey and favourite keys on a save's row. The work waits for the game tick.
    static void Ask(SaveFile file, Command row, bool delete)
    {
        row.TouchedAt = Stopwatch.GetTimestamp();
        _asked = (file, row, delete);
    }

    static void Answer(SaveFile file, Command row, bool delete)
    {
        Outcome outcome;
        try
        {
            outcome = delete ? Delete(file) : KeepCopy(file);
        }
        catch
        {
            row.Report(Outcome.Failed("FAILED"));
            throw;
        }

        row.Report(outcome);
    }

    static Outcome KeepCopy(SaveFile file)
    {
        if (!file.Readable)
        {
            return Outcome.Failed("NOT A SAVE");
        }

        // A kept backup moves up to the kept copies, so the result is shown on its new row too.
        var outcome = SaveFiles.Keep(file, out var path) ? Outcome.Done("KEPT") : Outcome.Unchanged("ALREADY KEPT");
        _flash = (path, outcome);
        _scanDue = true;
        return outcome;
    }

    static Outcome Delete(SaveFile file)
    {
        if (file.Kind != SaveKind.Kept)
        {
            return Outcome.Failed("NOT A KEPT COPY");
        }

        if (!Confirmed("delete " + file.Path))
        {
            return Outcome.Unchanged("PRESS AGAIN");
        }

        SaveFiles.Delete(file);
        _scanDue = true;
        return Outcome.Done("DELETED");
    }

    // True on the second press of the same thing, soon enough after the first.
    static bool Confirmed(string what)
    {
        var now = Stopwatch.GetTimestamp();
        if (_armedFor != what || Stopwatch.GetElapsedTime(_armedAt, now).TotalSeconds > ConfirmSeconds)
        {
            _armedFor = what;
            _armedAt = now;
            return false;
        }

        _armedFor = "";
        return true;
    }

    static void Build()
    {
        var slot = ActiveSlot();
        var mine = Show.Index == 0 && slot >= 0;
        var sections = new List<Catalog.Section>(Fixed);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Already newest first, so each group is too and a slot's first file is its newest.
        foreach (var group in _all.Where(file => !mine || file.Slot == slot).GroupBy(file => (file.Slot, file.Kind)).OrderBy(group => group.Key.Slot).ThenBy(group => group.Key.Kind))
        {
            var rows = new List<Feature>();
            foreach (var file in group)
            {
                rows.Add(Row(file, newest: file.Kind == SaveKind.File && rows.Count == 0));
                listed.Add(file.Path);
            }

            sections.Add(new Catalog.Section(Heading(group.Key.Slot, group.Key.Kind), rows.ToArray()));
        }

        if (listed.Count == 0)
        {
            sections.Add(new Catalog.Section(_all.Count == 0 ? "NO SAVES FOUND" : "NO SAVES IN THIS SLOT"));
        }

        foreach (var gone in Rows.Keys.Where(path => !listed.Contains(path)).ToArray())
        {
            Rows.Remove(gone);
        }

        _sections = sections.ToArray();
    }

    static string Heading(int slot, SaveKind kind) => kind switch
    {
        SaveKind.Kept => $"SLOT {slot + 1} KEPT COPIES",
        SaveKind.File => $"SLOT {slot + 1} GAME FILES",
        _ => $"SLOT {slot + 1} GAME BACKUPS",
    };

    // A save keeps its row for as long as what the row says holds, so the menu isn't handed a
    // new one for every read of the folders.
    static Command Row(SaveFile file, bool newest)
    {
        var label = Label(file);
        var help = Help(file, newest);
        if (Rows.TryGetValue(file.Path, out var known) && known.Plain == label && known.Help == help)
        {
            return known;
        }

        Command? row = null;
        var kept = file.Kind == SaveKind.Kept;
        row = new Command($"settings.saves.{file.Kind}_{Path.GetFileName(file.Path).Replace('.', '_')}", label, help, () => Load(file))
        {
            Passing = true,
            Cap = "LOAD",
            PressName = "LOAD",
            BindKey = kept ? null : new RowKey("KEEP A COPY", () => Ask(file, row!, delete: false)),
            FavouriteKey = kept ? new RowKey("DELETE", () => Ask(file, row!, delete: true)) : null,
        };
        Rows[file.Path] = row;
        return row;
    }

    static string Label(SaveFile file)
    {
        var when = file.When.ToString(file.When.Year == DateTime.Now.Year ? "d MMM HH:mm" : "d MMM yyyy HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant();
        if (!file.Readable)
        {
            return when + "  NOT A SAVE";
        }

        if (file.Percent >= 0)
        {
            when += $"  {file.Percent}%";
        }

        var place = Place(file.Place);
        return place.Length == 0 ? when : when + "  " + (place.Length > PlaceLetters ? place[..PlaceLetters].TrimEnd() : place);
    }

    static string Help(SaveFile file, bool newest)
    {
        if (!file.Readable)
        {
            return "This file isn't a save this version of the game can read.";
        }

        var facts = new List<string>();
        if (Place(file.Place) is { Length: > 0 } place)
        {
            facts.Add(place);
        }

        if (file.Difficulty.Length > 0)
        {
            facts.Add(file.Difficulty.ToUpperInvariant());
        }

        if (file.Percent >= 0)
        {
            facts.Add($"{file.Percent}% COMPLETE");
        }

        if (file.Plus > 0f)
        {
            facts.Add($"NEW GAME + {(int)file.Plus}%");
        }

        var what = file.Kind switch
        {
            SaveKind.Kept => "A copy you kept.",
            SaveKind.File when newest => "The slot's newest file, the one the game loads.",
            SaveKind.File => "One of the slot's three files, older than its newest.",
            _ => "A backup the game made.",
        };

        var saved = file.When.ToString("dddd d MMMM yyyy 'at' HH:mm:ss", CultureInfo.InvariantCulture);
        var about = facts.Count > 0 ? string.Join(", ", facts) + ". " : "";
        return $"Slot {file.Slot + 1}, saved {saved}. {about}{what} Press twice to load it.";
    }

    // The name the map gives the area a save was made in.
    static string Place(int id)
    {
        if (id < 0)
        {
            return "";
        }

        if (PlaceNames.TryGetValue(id, out var known))
        {
            return known;
        }

        if (World.Controller is not { IsValid: true } controller || controller.GetSaveGameManager() is not { IsValid: true } manager)
        {
            return "";
        }

        var key = manager.GetStringForLocationID(id).ToString() ?? "";
        var name = key.Length > 0 ? GameObject.Localize(GameText.Of("Map"), GameText.Of(key), GameText.Of("GFxUI")).ToString() ?? "" : "";
        if (name.Length == 0 || name.StartsWith('?'))
        {
            name = key;
        }

        // The game's fonts have no curly apostrophe.
        name = name.Trim().Replace('’', '\'').ToUpperInvariant();
        PlaceNames[id] = name;
        return name;
    }

    // The slot being played. The game's own value is used when it can be trusted, then the
    // slot of the last save seen being written, then the slot written to last.
    static int ActiveSlot()
    {
        if (GameSlot() is { } slot)
        {
            return slot;
        }

        return _lastSlot >= 0 ? _lastSlot : _all.FirstOrDefault(file => file.Kind == SaveKind.File)?.Slot ?? -1;
    }

    // The game keeps the slot being played in a value of its own. Its place is only right for
    // the exe it was found in, so it is read only when the controller's table points at the
    // function known to write it.
    static unsafe int? GameSlot()
    {
        if (World.Controller is not { IsValid: true } controller)
        {
            return null;
        }

        var module = MemUtil.GetBaseAddress();
        var table = *(nint*)controller.Ptr;
        if (table == 0 || *(nint*)(table + Offsets.SetSaveNameEntry) != module + Offsets.SetSaveNameCode)
        {
            return null;
        }

        var slot = *(int*)(module + Offsets.SaveSlotValue);
        return slot is >= 0 and <= 9 ? slot : null;
    }
}
