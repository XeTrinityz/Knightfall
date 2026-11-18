#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.GFxUI;
using FASValue = BmSDK.GFxUI.GFxMoviePlayer.FASValue;

namespace Knightfall;

// The game's own Game Options screen, filled with Knightfall's rows. The main menu opens each of
// its screens with TransitionForwards, so the KNIGHTFALL row opens that one directly, and the
// screen builds our rows there instead of the game's. The values it asks for and the presses it
// gets for those rows are answered here. Every hook falls back to the game's own when the
// screen is not ours.
static class MainMenuScreen
{
    const string ScreenName = "FE_OptionsGame";

    // The level's event that brings the main menu up, as the Options list's Back does.
    const string MainMenuEvent = "FE_MainMenu";
    const int AllExtrasUid = 9610;
    const int MedalsForUid = 9611;
    const int MedalsUid = 9612;
    const int AchievementsUid = 9613;
    const int ChangesUid = 9614;
    const long WaitMilliseconds = 10000;

    // The game's own yes/no box answers with the number of the button, and the first is yes.
    const int YesButton = 1;
    const int NoButton = 2;
    const int CheckMilliseconds = 100;
    const int ArtSearch = 40;
    const int ArtPieces = 2;
    const int TipWidth = 40;

    // The screen reads and sets each row as it builds, and those sets aren't the player's.
    const long QuietMilliseconds = 1500;

    static bool _opening;
    static long _until;
    static RGFxMovieUI_OptionsGame? _screen;
    static string _path = "";
    static long _nextCheck;
    static long _nextArtScan;
    static long _quietUntil;
    static Action? _onYes;

    // The row pressed last, until its result has been shown.
    static Command? _waiting;
    static long _pressedAt;
    static RGFxMoviePopupRequester? _popup;
    static readonly Dictionary<int, Command> Buttons = new();

    // WHAT'S NEW, a page at a time. OK opens the next one.
    const int NoteRows = 9;
    const int NoteRowChars = 80;
    static readonly List<string> Pages = new();
    static int _page;
    static bool _paging;
    static bool _pageDue;

    // Each row's text, which is also the name the list gives the row.
    static readonly List<string> Labels = new();

    static readonly string[] ArtNames = Enumerable.Range(0, ArtSearch + 1).Select(static i => "instance" + i).ToArray();

    // The names the title art has while this screen is open, as last found.
    static readonly List<string> Art = new();

    // Our screen's Back goes to the main menu. The screen's own value is kept to put back when
    // the game opens it as Game Options again.
    static string? _backBefore;

    public static void Begin(RGFxMovieUI_MainMenu menu, int rowId)
    {
        _opening = true;
        _until = Environment.TickCount64 + WaitMilliseconds;
        _screen = null;
        Art.Clear();

        // So the list comes back with this row picked, as it does for the game's own.
        menu.StoreButtonId(rowId);
        menu.TransitionForwards(ScreenName, true);
    }

    static bool Waiting => _opening && Environment.TickCount64 <= _until;

    static bool Ours(RGFxMovieUI movie) => _screen is { } screen && screen.Ptr == movie.Ptr;

    [Redirect(typeof(RGFxMovieUI_OptionsGame), nameof(RGFxMovieUI_OptionsGame.XI_SetupMenu))]
    static void Setup(RGFxMovieUI_OptionsGame self, FString targetPath)
    {
        if (!Waiting)
        {
            _screen = null;
            if (_backBefore is { } back)
            {
                self.OnBackCmd = new FString(back);
                _backBefore = null;
            }

            Redirects.RunOriginal();
            return;
        }

        _opening = false;
        try
        {
            Build(self, targetPath.ToString() ?? "");
        }
        catch (Exception error)
        {
            _screen = null;
            Log.Error("main menu screen", "building the KNIGHTFALL screen", error);
        }
    }

    static void Build(RGFxMovieUI_OptionsGame movie, string path)
    {
        _path = path;
        movie.MenuPath = new FString(path);
        var menu = movie.GetVariableObject(path, GFxObject.StaticClass()) ?? throw new InvalidOperationException("menu path didn't resolve");
        _screen = movie;
        _quietUntil = Environment.TickCount64 + QuietMilliseconds;
        _onYes = null;
        _popup = null;
        _paging = false;
        _pageDue = false;
        Buttons.Clear();
        Labels.Clear();

        Button(menu, AllExtrasUid, Progress.AllExtras);
        List(menu, MedalsForUid, Progress.MedalsFor);
        Button(menu, MedalsUid, Progress.ChallengeMedals);
        Button(menu, AchievementsUid, Progress.Achievements);
        Button(menu, ChangesUid, Updates.Changes);
        Invoke(menu, "SetLastUID", Int(AllExtrasUid));

        _backBefore ??= movie.OnBackCmd.ToString() ?? "";
        movie.OnBackCmd = new FString(MainMenuEvent);

        Log.Info("Main menu: the KNIGHTFALL screen is built.");
    }

    static void Button(GFxObject menu, int uid, Command command)
    {
        Buttons[uid] = command;
        Labels.Add(command.Plain);
        AddButton(menu, uid, command.Plain);
    }

    // A plain row on one of the game's own menu lists.
    public static void AddButton(GFxObject menu, int uid, string label) =>
        Invoke(menu, "AddButton", Int(uid), Str(label), Bool(true), Bool(false), Bool(false), Str(""));

    static void List(GFxObject menu, int uid, Choice choice)
    {
        Labels.Add(choice.Plain);
        var values = new List<string>();
        for (var i = 0; i < choice.Count; i++)
        {
            values.Add(">" + choice.LabelAt(i).Replace(",", " ").Replace('.', '·') + "<");
        }

        Invoke(menu, "AddListItem", Int(uid), Str(choice.Plain), Str(string.Join(",", values)), Bool(false), Str(""));
    }

    static FASValue Int(int value) => new() { Type = GFxMoviePlayer.ASType.AS_Int, I = value };

    static FASValue Bool(bool value) => new() { Type = GFxMoviePlayer.ASType.AS_Boolean, B = value };

    static FASValue Str(string value) => new() { Type = GFxMoviePlayer.ASType.AS_String, S = new FString(value) };

    static void Invoke(GFxObject target, string method, params FASValue[] values)
    {
        var args = new TArray<FASValue>();
        foreach (var value in values)
        {
            args.Add(value);
        }

        target.Invoke(method, args);
    }

    [Redirect(typeof(RGFxMovieUI_OptionsGame), nameof(RGFxMovieUI_OptionsGame.XI_Get))]
    static int Get(RGFxMovieUI_OptionsGame self, int id, FString type)
    {
        if (Ours(self))
        {
            return id == MedalsForUid ? Progress.MedalsFor.Index : 0;
        }

        return Redirects.RunOriginal<int>();
    }

    [Redirect(typeof(RGFxMovieUI_OptionsGame), nameof(RGFxMovieUI_OptionsGame.XI_GetDefault))]
    static int GetDefault(RGFxMovieUI_OptionsGame self, int id, FString type)
    {
        if (Ours(self))
        {
            return id == MedalsForUid ? Progress.MedalsFor.DefaultIndex : 0;
        }

        return Redirects.RunOriginal<int>();
    }

    [Redirect(typeof(RGFxMovieUI_OptionsGame), nameof(RGFxMovieUI_OptionsGame.XI_Set))]
    static void Set(RGFxMovieUI_OptionsGame self, int id, FString type, int value)
    {
        if (!Ours(self))
        {
            Redirects.RunOriginal();
            return;
        }

        if (id == MedalsForUid && Environment.TickCount64 > _quietUntil)
        {
            Progress.MedalsFor.SetTo(value);
        }
    }

    // The rows run as they do in the mod's own menu. Any that ask first ask in the game's own box.
    [Redirect(typeof(RGFxMovieUI_OptionsGame), nameof(RGFxMovieUI_OptionsGame.XI_OnClicked))]
    static void Clicked(RGFxMovieUI_OptionsGame self, int id)
    {
        if (!Ours(self) || !Buttons.TryGetValue(id, out var command))
        {
            Redirects.RunOriginal();
            return;
        }

        try
        {
            Sound(self, "OnClicked");
            _waiting = command;
            _pressedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            Catalog.Pressed(command);
        }
        catch (Exception error)
        {
            Log.Error("main menu screen", "pressing a row of the KNIGHTFALL screen", error);
        }
    }

    // The latest changelog in the game's own OK box.
    public static Outcome ShowNotes()
    {
        if (Updates.Notes() is not { } text)
        {
            return Outcome.Unchanged("NO NOTES YET");
        }

        if (_screen is not { IsAlive: true } screen || !screen.bMovieIsOpen || screen.bClosing || _popup is not null)
        {
            return Outcome.Failed("NOT OPEN");
        }

        _waiting = null;
        Paginate(text);
        _page = 0;
        _paging = true;
        _popup = screen.OpenPopupMessage1("$Menu.Ok", Pages[0], true);
        return Outcome.Asking;
    }

    // The game's box doesn't scroll and runs over its button past about this many wrapped rows.
    // Each page repeats the section heading it continues under.
    static void Paginate(string text)
    {
        Pages.Clear();
        var lines = text.Split('\n');
        var page = new List<string>();
        var rows = 0;
        var heading = "";
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var bullet = line.StartsWith("- ", StringComparison.Ordinal);
            if (!bullet)
            {
                heading = line;
            }

            var cost = (line.Length + NoteRowChars - 1) / NoteRowChars + (bullet ? 0 : 1);
            if (page.Count > 0 && rows + cost > NoteRows)
            {
                Pages.Add(string.Join("\n", page));
                page.Clear();
                rows = 0;
                if (bullet)
                {
                    page.Add(heading);
                    rows = 1;
                }
            }

            if (page.Count > 0 && !bullet)
            {
                page.Add("");
            }

            page.Add(line);
            rows += cost;
        }

        if (page.Count > 0)
        {
            Pages.Add(string.Join("\n", page));
        }

        for (var i = 0; i < Pages.Count && Pages.Count > 1; i++)
        {
            Pages[i] = $"{lines[0]}  ({i + 1}/{Pages.Count})\n\n{Pages[i]}";
        }

        if (Pages.Count == 1)
        {
            Pages[0] = lines[0] + "\n\n" + Pages[0];
        }
    }

    [Redirect(typeof(RGFxMovieUI_OptionsGame), nameof(RGFxMovieUI_OptionsGame.PopupRequester_Callback))]
    static void Answered(RGFxMovieUI_OptionsGame self, RGFxMoviePopupRequester thePopUpMsg, int buttonId)
    {
        if (_popup is not { } popup || thePopUpMsg is null || popup.Ptr != thePopUpMsg.Ptr)
        {
            Redirects.RunOriginal();
            return;
        }

        var onYes = _onYes;
        _onYes = null;
        _popup = null;
        if (_paging)
        {
            _paging = ++_page < Pages.Count;
            _pageDue = _paging;
        }

        if (buttonId == YesButton)
        {
            onYes?.Invoke();
        }
        else if (onYes is not null)
        {
            _waiting = null;
        }
    }

    // A row that can't be undone asks here, in the game's own yes/no box, while the screen is up.
    public static bool Ask(Confirmation ask, Action onYes)
    {
        if (_screen is not { IsAlive: true } screen || !screen.bMovieIsOpen || screen.bClosing || _popup is not null)
        {
            return false;
        }

        _onYes = onYes;
        _popup = screen.OpenPopupMessage2("$Menu.Yes", "$Menu.No", $"{ask.Text} Go on?", NoButton, true);
        return true;
    }

    [Redirect(typeof(RGFxMovieUI_OptionsGame), nameof(RGFxMovieUI_OptionsGame.XI_OnFocus))]
    static void Focus(RGFxMovieUI_OptionsGame self, int id)
    {
        if (!Ours(self))
        {
            Redirects.RunOriginal();
            return;
        }

        Sound(self, "OnFocus");
        Tip(id == MedalsForUid ? Progress.MedalsFor.Help : Buttons.TryGetValue(id, out var command) ? command.Help : "");
    }

    [Redirect(typeof(RGFxMovieUI_OptionsGame), nameof(RGFxMovieUI_OptionsGame.XI_SetToolTip))]
    static void ToolTip(RGFxMovieUI_OptionsGame self, FString text, FString option)
    {
        if (!Ours(self))
        {
            Redirects.RunOriginal();
        }
    }

    [Redirect(typeof(RGFxMovieUI_OptionsGame), nameof(RGFxMovieUI_OptionsGame.XI_GetTitle))]
    static FString Title(RGFxMovieUI_OptionsGame self)
    {
        if (Ours(self) || Waiting)
        {
            // Big and grey like the art it stands in for.
            return new FString("<FONT FACE=\"$Game\" SIZE=\"72\" COLOR=\"#8A8A8A\">KNIGHTFALL</FONT>");
        }

        return Redirects.RunOriginal<FString>();
    }

    static void Tip(string text)
    {
        try
        {
            Game.GetGameRI().FrontMostMovie?.SetToolTip(new FString(Wrap(text)));
        }
        catch (Exception error)
        {
            Log.Warn("main menu screen tip", $"The help text for the KNIGHTFALL screen failed: {error.Message}");
        }
    }

    // The help box doesn't wrap long text by itself, so it is broken into lines.
    static string Wrap(string text)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > TipWidth)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = line.Length == 0 ? word : line + " " + word;
            }
        }

        if (line.Length > 0)
        {
            lines.Add(line);
        }

        return string.Join("\n", lines);
    }

    static void Sound(RGFxMovieUI movie, string what)
    {
        try
        {
            movie.TriggerAudioEvent(0, "Pause", what, "OptionsGame");
        }
        catch (Exception error)
        {
            Log.Warn("main menu screen sound", $"The {what} sound for the KNIGHTFALL screen failed: {error.Message}");
        }
    }

    // Runs every frame. A row's result is shown, and the texts are put back when the screen
    // re-localizes them.
    public static void Tick()
    {
        // The screen is drawn a few frames before it asks to be set up, so its art is hidden from here.
        if (_screen is null && Waiting)
        {
            try
            {
                foreach (var movie in Game.FindObjects<RGFxMovieUI_OptionsGame>())
                {
                    if (movie.IsValid)
                    {
                        HideArt(movie);
                    }
                }
            }
            catch (Exception error)
            {
                Log.Warn("main menu screen art", $"Hiding the screen's art failed: {error.Message}");
            }

            return;
        }

        if (_screen is not { IsAlive: true } screen || !screen.bMovieIsOpen || screen.bClosing)
        {
            return;
        }

        try
        {
            HideArt(screen);
        }
        catch (Exception error)
        {
            Log.Warn("main menu screen art", $"Hiding the screen's art failed: {error.Message}");
        }

        ShowResult(screen);
        if (_pageDue && _popup is null)
        {
            _pageDue = false;
            _popup = screen.OpenPopupMessage1("$Menu.Ok", Pages[_page], true);
        }

        var now = Environment.TickCount64;
        if (now < _nextCheck)
        {
            return;
        }

        _nextCheck = now + CheckMilliseconds;
        var menu = screen.GetVariableObject(_path, GFxObject.StaticClass());
        foreach (var text in Labels)
        {
            var label = menu?.GetObject(text, GFxObject.StaticClass())?.GetObject("Contents", GFxObject.StaticClass())?.GetObject("Label", GFxObject.StaticClass());
            var field = label?.GetObject("Label", GFxObject.StaticClass()) ?? label?.GetObject("Text", GFxObject.StaticClass()) ?? label;
            if (field is not null && field.GetText().ToString() != text)
            {
                // A field is only as wide as the text it was made with, so it is let grow to fit.
                field.SetString("autoSize", new FString("left"));
                field.SetText(new FString(text));
            }
        }
    }

    // The game's screen has nowhere to show what a row did, so the result comes up in the
    // game's own OK box. A row still working shows only its final result.
    static void ShowResult(RGFxMovieUI_OptionsGame screen)
    {
        if (_waiting is not { } command || _popup is not null || !command.Reported(_pressedAt, out var outcome))
        {
            return;
        }

        if (outcome.Text.StartsWith("WORKING", StringComparison.Ordinal))
        {
            return;
        }

        _waiting = null;
        _onYes = null;
        _popup = screen.OpenPopupMessage1("$Menu.Ok", $"{command.Plain}: {outcome.Text}", true);
    }

    // The screen's own tick runs inside Flash before a frame is drawn, so the art is gone from
    // the first frame of the intro, before the screen is even built.
    [Redirect(typeof(RGFxMovieUI_OptionsGame), nameof(RGFxMovieUI_OptionsGame.XI_Tick))]
    static void FlashTick(RGFxMovieUI_OptionsGame self)
    {
        Redirects.RunOriginal();
        if (Ours(self) || Waiting)
        {
            try
            {
                HideArt(self);
            }
            catch (Exception error)
            {
                Log.Warn("main menu screen art", $"Hiding the screen's art failed: {error.Message}");
            }
        }
    }

    // The screen draws GAME and OPTIONS as two pieces of art about 471 by 87 each. Their names
    // change every time the screen opens, so they are found by size. Once both are found, those
    // are checked every frame and the full search runs a few times a second in case the art
    // comes back under new names.
    static void HideArt(RGFxMovieUI_OptionsGame movie)
    {
        var screen = movie.GetVariableObject("root1.Screen", GFxObject.StaticClass());
        if (screen is null)
        {
            return;
        }

        // The title field is sized for the game's small title text, so it is let grow to fit ours.
        screen.GetObject("Title", GFxObject.StaticClass())?.GetObject("Label", GFxObject.StaticClass())?.GetObject("Text", GFxObject.StaticClass())?.SetString("autoSize", new FString("left"));

        var now = Environment.TickCount64;
        if (Art.Count >= ArtPieces && now < _nextArtScan)
        {
            foreach (var name in Art)
            {
                Hide(screen, name);
            }

            return;
        }

        _nextArtScan = now + CheckMilliseconds;
        foreach (var name in ArtNames)
        {
            if (Hide(screen, name) && !Art.Contains(name))
            {
                Art.Add(name);
            }
        }
    }

    // True when this was a piece of the title art on show, which is now hidden.
    static bool Hide(GFxObject screen, string name)
    {
        var art = screen.GetObject(name, GFxObject.StaticClass());
        if (art is null || !art.GetBool("visible"))
        {
            return false;
        }

        var width = art.GetFloat("width");
        var height = art.GetFloat("height");
        if (width is > 430f and < 520f && height is > 75f and < 120f)
        {
            art.SetVisible(false);
            return true;
        }

        return false;
    }
}
