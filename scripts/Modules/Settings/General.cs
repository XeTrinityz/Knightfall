#nullable enable

namespace Knightfall;

// SETTINGS > GENERAL.
static class General
{
    public static readonly Choice SlowTime = WorldTime.SlowChoice(
        "settings.general.slow_time", "SLOW TIME IN MENU",
        "Slows the game while the menu is open, so a fight waits while you change things. It comes back up to speed when the menu closes.");

    public static readonly Command AllOff = new(
        "settings.general.all_off", "TURN EVERYTHING OFF",
        "Puts every option back to normal. Your look, controls and game settings stay as they are.",
        static () => Catalog.AllOff() switch
        {
            0 => Outcome.Unchanged("NONE ON"),
            1 => Outcome.Done("1 OFF"),
            var count => Outcome.Done($"{count} OFF"),
        });

    public static readonly Command Reload = new(
        "settings.general.reload", "RELOAD SETTINGS FILE",
        "Reads settings.toml again, for after you edit it with the game open. Do it before you change anything in the menu, since that saves the menu's values over yours.",
        static () =>
        {
            Reread(fromFile: true);
            Log.Info("settings.toml was read again.");
            return Outcome.Done("RELOADED");
        });

    public static readonly Command ResetAll = new(
        "settings.general.reset_all", "RESET EVERYTHING",
        "Puts Knightfall back as it was when installed. Every option, the look, menu keys, hotkeys, favourites and saved spots. Press it twice.",
        static () =>
        {
            if (!Confirm.Second("reset everything"))
            {
                return Outcome.Unchanged("PRESS AGAIN");
            }

            Reread(fromFile: false);
            Log.Info("Everything was reset to how it was when installed.");
            return Outcome.Done("RESET");
        });

    public static readonly Catalog.Section[] Sections =
    {
        new("MENU", SlowTime, Toasts.On, Toasts.Corner),
        new("OPTIONS", AllOff),
        new("SETTINGS FILE", Reload, ResetAll),
    };

    // Each saved option goes back through the normal path first, so the ones that changed the
    // game put it back on the next step of this tick, and take hold again after if they are
    // still on. A reset does the same for unsaved options. Unsaved preferences are left alone
    // either way, since they only show something live, like how far the editor moved an object.
    // Then everything is read again from the file or from nothing, in the order the start reads
    // it, which is the order settings.toml lists its sections in.
    static void Reread(bool fromFile)
    {
        foreach (var feature in Catalog.All)
        {
            if (feature.Stored is not null || (!fromFile && !feature.Preference))
            {
                feature.Reset();
            }
        }

        if (fromFile)
        {
            Settings.Reload();
        }
        else
        {
            Settings.Wipe();
        }

        Controls.Rebind();
        Radial.Bind();
        Spots.Reload();
        Layout.DefaultKeys();
        Catalog.Load();
        Look.LoadMove();
        Hotkeys.LoadSaved();
        Hotkeys.Relabel();
        Favourites.Load();
        Disguise.KeepStances();
        Settings.Save();
    }
}
