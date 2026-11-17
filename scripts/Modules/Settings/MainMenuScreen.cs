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
    const long WaitMilliseconds = 10000;

    // The game's own yes/no box answers with the number of the button, and the first is yes.
    const int YesButton = 1;
    const int NoButton = 2;
    const int CheckMilliseconds = 100;
    const int ArtSearch = 40;
    const int TipWidth = 40;
    const string Question = "This changes the medals and scores in your save and can't be undone. Go on?";

    // The screen reads and sets each row as it builds, and those sets aren't the player's.
    const long QuietMilliseconds = 1500;

    static bool _opening;
    static long _until;
    static RGFxMovieUI_OptionsGame? _screen;
    static string _path = "";
    static long _nextCheck;
    static long _quietUntil;
    static int _focused = -1;
    static Command? _asking;
    static RGFxMoviePopupRequester? _popup;
    static readonly Dictionary<int, Command> Buttons = new();
    static readonly Dictionary<string, string> Labels = new();

    // Our screen's Back goes to the main menu. The screen's own value is kept to put back when
    // the game opens it as Game Options again.
    static string? _backBefore;

    // Opens the screen. False when it can't, and the caller opens the mod's own menu.
    public static bool Begin(RGFxMovieUI_MainMenu menu, int rowId)
    {
        if (menu.bInTransition)
        {
            return false;
        }

        _opening = true;
        _until = Environment.TickCount64 + WaitMilliseconds;
        _screen = null;

        // So the list comes back with this row picked, as it does for the game's own.
        menu.StoreButtonId(rowId);
        menu.TransitionForwards(ScreenName, true);
        return true;
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
        _focused = -1;
        _asking = null;
        _popup = null;
        Buttons.Clear();
        Labels.Clear();

        Button(menu, AllExtrasUid, Progress.AllExtras);
        List(menu, MedalsForUid, Progress.MedalsFor);
        Button(menu, MedalsUid, Progress.ChallengeMedals);
        Button(menu, AchievementsUid, Progress.Achievements);
        Invoke(menu, "SetLastUID", Int(AllExtrasUid));

        _backBefore ??= movie.OnBackCmd.ToString() ?? "";
        movie.OnBackCmd = new FString(MainMenuEvent);

        Log.Info($"Main menu: the KNIGHTFALL screen is built. Back goes to '{movie.OnBackCmd}'.");
    }

    static void Button(GFxObject menu, int uid, Command command)
    {
        Buttons[uid] = command;
        Labels[command.Plain] = command.Plain;
        Invoke(menu, "AddButton", Int(uid), Str(command.Plain), Bool(true), Bool(false), Bool(false), Str(""));
    }

    static void List(GFxObject menu, int uid, Choice choice)
    {
        Labels[choice.Plain] = choice.Plain;
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

    // A row that can't be undone asks first, in the game's own yes/no box.
    [Redirect(typeof(RGFxMovieUI), nameof(RGFxMovieUI.XI_OnClicked))]
    static void Clicked(RGFxMovieUI self, int id)
    {
        if (self is not RGFxMovieUI_OptionsGame screen || !Ours(screen) || !Buttons.TryGetValue(id, out var command))
        {
            Redirects.RunOriginal();
            return;
        }

        try
        {
            Sound(self, "OnClicked");
            if (command == Progress.ChallengeMedals)
            {
                if (_popup is null && !screen.IsInputBlocked())
                {
                    _asking = command;
                    _popup = screen.OpenPopupMessage2("$Menu.Yes", "$Menu.No", Question, NoButton, true);
                }

                return;
            }

            Catalog.Pressed(command);
        }
        catch (Exception error)
        {
            Log.Error("main menu screen", "pressing a row of the KNIGHTFALL screen", error);
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

        var command = _asking;
        _asking = null;
        _popup = null;
        if (buttonId == YesButton && command is not null)
        {
            Confirm.Grant(command);
            Catalog.Pressed(command);
        }
    }

    [Redirect(typeof(RGFxMovieUI_OptionsGame), nameof(RGFxMovieUI_OptionsGame.XI_OnFocus))]
    static void Focus(RGFxMovieUI_OptionsGame self, int id)
    {
        if (!Ours(self))
        {
            Redirects.RunOriginal();
            return;
        }

        _focused = id;
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

    [Redirect(typeof(RGFxMovieUI), nameof(RGFxMovieUI.XI_GetTitle))]
    static FString Title(RGFxMovieUI self)
    {
        if (self is RGFxMovieUI_OptionsGame screen && (Ours(screen) || Waiting))
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

    // Runs every frame. The screen's baked title art is hidden, and the texts are put back when
    // the screen re-localizes them.
    public static void Tick()
    {
        if (_screen is not { IsAlive: true } screen || !screen.bMovieIsOpen || screen.bClosing)
        {
            return;
        }

        HideArt(screen);
        var now = Environment.TickCount64;
        if (now < _nextCheck)
        {
            return;
        }

        _nextCheck = now + CheckMilliseconds;
        var menu = screen.GetVariableObject(_path, GFxObject.StaticClass());
        foreach (var (name, text) in Labels)
        {
            var label = menu?.GetObject(name, GFxObject.StaticClass())?.GetObject("Contents", GFxObject.StaticClass())?.GetObject("Label", GFxObject.StaticClass());
            var field = label?.GetObject("Label", GFxObject.StaticClass()) ?? label?.GetObject("Text", GFxObject.StaticClass()) ?? label;
            if (field is not null && field.GetText().ToString() != text)
            {
                // A field is only as wide as the text it was made with, so it is let grow to fit.
                field.SetString("autoSize", new FString("left"));
                field.SetText(new FString(text));
            }
        }
    }

    // The screen's own tick runs inside Flash before a frame is drawn, so the art is gone from
    // the first frame of the intro, before the screen is even built.
    [Redirect(typeof(RGFxMovieUI), nameof(RGFxMovieUI.XI_Tick))]
    static void FlashTick(RGFxMovieUI self)
    {
        Redirects.RunOriginal();
        if (self is RGFxMovieUI_OptionsGame screen && (Ours(screen) || Waiting))
        {
            try
            {
                HideArt(screen);
            }
            catch (Exception error)
            {
                Log.Warn("main menu screen art", $"Hiding the screen's art failed: {error.Message}");
            }
        }
    }

    // The screen draws GAME and OPTIONS as two pieces of art about 471 by 87 each. Their names
    // change every time the screen opens, so they are found by size.
    static void HideArt(RGFxMovieUI_OptionsGame movie)
    {
        var screen = movie.GetVariableObject("root1.Screen", GFxObject.StaticClass());
        if (screen is null)
        {
            return;
        }

        // The title field is sized for the game's small title text, so it is let grow to fit ours.
        screen.GetObject("Title", GFxObject.StaticClass())?.GetObject("Label", GFxObject.StaticClass())?.GetObject("Text", GFxObject.StaticClass())?.SetString("autoSize", new FString("left"));

        for (var i = 0; i <= ArtSearch; i++)
        {
            var art = screen.GetObject("instance" + i, GFxObject.StaticClass());
            if (art is null || !art.GetBool("visible"))
            {
                continue;
            }

            var width = art.GetFloat("width");
            var height = art.GetFloat("height");
            if (width is > 430f and < 520f && height is > 75f and < 120f)
            {
                art.SetVisible(false);
            }
        }
    }
}
