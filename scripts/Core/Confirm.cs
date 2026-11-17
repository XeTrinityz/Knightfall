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
    static (Command Row, long At)? _granted;

    // For a screen that asks in its own way, like pressing a row twice. The row's next run goes
    // through without asking.
    public static void Grant(Command row) => _granted = (row, Stopwatch.GetTimestamp());

    // True when the player just said yes to this same question for this row. Otherwise it asks,
    // and a yes runs the row again, through again when it isn't a plain press.
    public static bool Asked(Command row, Confirmation ask, Action? again = null)
    {
        if (_granted is { } granted)
        {
            _granted = null;
            if (granted.Row == row && Stopwatch.GetElapsedTime(granted.At).TotalSeconds <= YesSeconds)
            {
                return true;
            }
        }

        var yes = _yes;
        _yes = null;
        if (yes == (row, ask) && Stopwatch.GetElapsedTime(_yesAt).TotalSeconds <= YesSeconds)
        {
            return true;
        }

        Menu.Ask(ask, row, () =>
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
        });
        return false;
    }
}
