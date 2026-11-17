#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.GFxUI;

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

    [Redirect(typeof(RGFxMovieUI_MainMenu), nameof(RGFxMovieUI_MainMenu.XI_SetupMenu))]
    static void Setup(RGFxMovieUI_MainMenu self, FString targetPath)
    {
        _setup = self;
        _path = targetPath.ToString() ?? "";
        try
        {
            Redirects.RunOriginal();
        }
        finally
        {
            _setup = null;
        }
    }

    [Redirect(typeof(RGameInfo), nameof(RGameInfo.ConceptGetTotalCollected))]
    static int ConceptArt(RGameInfo self, out int nTotalPossible, out int nNewCollected)
    {
        var collected = self.ConceptGetTotalCollected(out nTotalPossible, out nNewCollected);
        if (_setup is { } menu)
        {
            // Once per setup.
            _setup = null;
            Add(menu, _path);
        }

        return collected;
    }

    static void Add(RGFxMovieUI_MainMenu movie, string path)
    {
        try
        {
            var menu = movie.GetVariableObject(path, GFxObject.StaticClass());
            if (menu is null)
            {
                Log.Warn("main menu row", "The main menu's list wasn't found, so there is no KNIGHTFALL row.");
                return;
            }

            MainMenuScreen.AddButton(menu, Uid, Label);
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
            // What the game's own rows wait for, too.
            if (self.IsInputBlocked() || self.bInTransition || self.bClosing || self.PopupType != 0)
            {
                return;
            }

            Sound(self, "OnClicked");
            MainMenuScreen.Begin(self, Uid);
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
