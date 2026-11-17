#nullable enable

using System.Diagnostics;

namespace Knightfall;

// Rows that can't be undone ask first, in a box over the menu. The row does its own checks, then
// asks. A yes runs it again and that run goes through, so nothing happens on a yes the checks
// would now refuse.
static class Confirm
{
    // How long a yes waits for its row to run again.
    const double YesSeconds = 2;

    static (Command Row, Confirmation Ask)? _yes;
    static long _yesAt;

    // True when the player just said yes to this same question for this row. Otherwise it asks,
    // and a yes runs the row again, through again when it isn't a plain press.
    public static bool Asked(Command row, Confirmation ask, Action? again = null)
    {
        var yes = _yes;
        _yes = null;
        if (yes == (row, ask) && Stopwatch.GetElapsedTime(_yesAt).TotalSeconds <= YesSeconds)
        {
            return true;
        }

        void Yes()
        {
            _yes = (row, ask);
            _yesAt = Stopwatch.GetTimestamp();
            if (again is null)
            {
                Catalog.Pressed(row);
            }
            else
            {
                again();
            }
        }

        // The KNIGHTFALL screen on the main menu asks in the game's own yes/no box while it is
        // open. Anywhere else the question goes in the box over the menu.
        if (!MainMenuScreen.Ask(ask, Yes))
        {
            Menu.Ask(ask, row, Yes);
        }

        return false;
    }
}
