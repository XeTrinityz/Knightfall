#nullable enable

using System.Diagnostics;

namespace Knightfall;

// Rows that can't be undone take two presses. The second one counts for as long as the row still
// says PRESS AGAIN, and pressing anything else that asks starts over.
static class Confirm
{
    static string _armed = "";
    static long _at;

    // True on the second press of the same thing.
    public static bool Second(string what)
    {
        var now = Stopwatch.GetTimestamp();
        if (_armed != what || Stopwatch.GetElapsedTime(_at, now).TotalSeconds > Command.ShowSeconds)
        {
            _armed = what;
            _at = now;
            return false;
        }

        _armed = "";
        return true;
    }
}
