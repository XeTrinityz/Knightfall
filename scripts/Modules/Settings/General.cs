#nullable enable

using System.Diagnostics;

namespace Knightfall;

// SETTINGS > GENERAL.
static class General
{
    // The second press has to come this soon after the first.
    const double ConfirmSeconds = 3;

    static long _armedAt;

    public static readonly Command AllOff = new(
        "settings.general.all_off", "TURN EVERYTHING OFF",
        "Puts every option back to normal. Your look settings stay as they are.",
        () => Catalog.AllOff() > 0 ? Outcome.Done() : Outcome.Unchanged("NONE ON"));

    public static readonly Command ResetAll = new(
        "settings.general.reset_all", "RESET EVERYTHING",
        "Puts Knightfall back as it was when installed. Every option, the look, menu keys, hotkeys, favourites and saved spots. Press it twice.",
        Reset);

    public static readonly Choice SlowTime = WorldTime.SlowChoice(
        "settings.general.slow_time", "SLOW TIME IN MENU",
        "Slows the game while the menu is open, so a fight waits while you change things. It comes back up to speed when the menu closes.");

    public static readonly Feature[] Rows = { AllOff, ResetAll, SlowTime, Toasts.On, Toasts.Corner };

    static Outcome Reset()
    {
        var now = Stopwatch.GetTimestamp();
        if (_armedAt == 0 || (now - _armedAt) / (double)Stopwatch.Frequency > ConfirmSeconds)
        {
            _armedAt = now;
            return Outcome.Unchanged("PRESS AGAIN");
        }

        _armedAt = 0;

        // Each option goes back through the normal path first, so the ones that changed the game
        // put it back on the next step of this tick. Unsaved preferences are left alone, since
        // they only show something live, like how far the editor moved an object.
        foreach (var feature in Catalog.All)
        {
            if (!feature.Preference || feature.Stored is not null)
            {
                feature.Reset();
            }
        }

        // Then everything is read again from an empty file, the same way it is at the first start.
        Settings.Wipe();
        Controls.Rebind();
        Radial.Bind();
        Layout.DefaultKeys();
        Hotkeys.LoadSaved();
        Hotkeys.Relabel();
        Favourites.Load();
        Spots.Reload();
        Catalog.Load();
        Look.LoadMove();
        Settings.Save();
        Log.Info("Everything was reset to how it was when installed.");
        return Outcome.Done("RESET");
    }
}
