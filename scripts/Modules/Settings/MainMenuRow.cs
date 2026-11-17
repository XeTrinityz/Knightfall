#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.GFxUI;
using FASValue = BmSDK.GFxUI.GFxMoviePlayer.FASValue;

namespace Knightfall;

// A KNIGHTFALL row on the game's own main menu list. The game builds the list in its setup
// script, one row after another, so the row is added from inside it. The last thing setup asks
// before it adds EXTRAS, OPTIONS and EXIT is how much concept art there is, so the row goes in
// there. The button call itself is never redirected, since that takes the game down, and native
// calls never reach a redirect at all.
static class MainMenuRow
{
    const int Uid = 9600;
    const string Label = "KNIGHTFALL";

    static RGFxMovieUI_MainMenu? _setup;
    static string _path = "";
    static bool _added;

    [Redirect(typeof(RGFxMovieUI_MainMenu), nameof(RGFxMovieUI_MainMenu.XI_SetupMenu))]
    static void Setup(RGFxMovieUI_MainMenu self, FString targetPath)
    {
        _setup = self;
        _path = targetPath.ToString() ?? "";
        _added = false;
        try
        {
            Redirects.RunOriginal();
        }
        finally
        {
            _setup = null;
        }

        // The game's script changed shape, so the row goes at the end rather than not at all.
        if (!_added)
        {
            Add(self, _path);
        }
    }

    [Redirect(typeof(RGameInfo), nameof(RGameInfo.ConceptGetTotalCollected))]
    static int ConceptArt(RGameInfo self, out int nTotalPossible, out int nNewCollected)
    {
        var collected = self.ConceptGetTotalCollected(out nTotalPossible, out nNewCollected);
        if (_setup is { } menu && !_added)
        {
            Add(menu, _path);
        }

        return collected;
    }

    static void Add(RGFxMovieUI_MainMenu movie, string path)
    {
        _added = true;
        try
        {
            var menu = movie.GetVariableObject(path, GFxObject.StaticClass());
            if (menu is null)
            {
                Log.Warn("main menu row", "The main menu's list wasn't found, so there is no KNIGHTFALL row.");
                return;
            }

            var args = new TArray<FASValue>();
            args.Add(new FASValue { Type = GFxMoviePlayer.ASType.AS_Int, I = Uid });
            args.Add(new FASValue { Type = GFxMoviePlayer.ASType.AS_String, S = new FString(Label) });
            args.Add(new FASValue { Type = GFxMoviePlayer.ASType.AS_Boolean, B = true });
            args.Add(new FASValue { Type = GFxMoviePlayer.ASType.AS_Boolean, B = false });
            args.Add(new FASValue { Type = GFxMoviePlayer.ASType.AS_Boolean, B = false });
            args.Add(new FASValue { Type = GFxMoviePlayer.ASType.AS_String, S = new FString("") });
            menu.Invoke("AddButton", args);
            Log.Info("Main menu: KNIGHTFALL row added.");
        }
        catch (Exception error)
        {
            Log.Error("main menu row", "adding the main menu row", error);
        }
    }

    [Redirect(typeof(RGFxMovieUI_MainMenu), nameof(RGFxMovieUI_MainMenu.XI_OnClicked))]
    static void Clicked(RGFxMovieUI_MainMenu self, int id)
    {
        if (id != Uid)
        {
            Redirects.RunOriginal();
            return;
        }

        try
        {
            if (self.IsInputBlocked() || self.bClosing)
            {
                return;
            }

            Sound(self, "OnClicked");
            if (!MainMenuScreen.Begin(self, Uid))
            {
                Menu.OpenFromGame();
            }
        }
        catch (Exception error)
        {
            Log.Error("main menu row", "opening the menu from the main menu row", error);
        }
    }

    [Redirect(typeof(RGFxMovieUI_MainMenu), nameof(RGFxMovieUI_MainMenu.XI_OnFocus))]
    static void Focus(RGFxMovieUI_MainMenu self, int id)
    {
        Redirects.RunOriginal();
        if (id == Uid)
        {
            Sound(self, "OnFocus");
        }
    }

    // The game picks its menu sounds by the id of the row, so a row of ours has none. It borrows
    // the sounds of EXTRAS.
    static void Sound(RGFxMovieUI_MainMenu self, string what)
    {
        try
        {
            self.TriggerAudioEvent(0, "MainMenu", what, "ExtrasMenu");
        }
        catch (Exception error)
        {
            Log.Warn("main menu row sound", $"The {what} sound for the main menu row failed: {error.Message}");
        }
    }
}
