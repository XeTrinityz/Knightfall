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

    // A screen of the game's own that asks in its own yes/no box while it is open. It returns
    // false when it can't ask, and the question goes in the box over the menu instead.
    public static Func<Confirmation, Action, bool>? Elsewhere { get; set; }

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

        if (Elsewhere?.Invoke(ask, Yes) != true)
        {
            Menu.Ask(ask, row, Yes);
        }

        return false;
    }
}
