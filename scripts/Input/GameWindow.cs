#nullable enable

using System.Runtime.InteropServices;

namespace Knightfall;

// Whether the game is the window in front. The pad readers and the mouse pointer work for the
// whole desktop, so they ask before they act on what they read.
static class GameWindow
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    // An answer is good for this long, about one frame. Several things ask in the same frame.
    const long KeepMilliseconds = 15;

    static bool _front;
    static bool _asked;
    static long _askedAt;

    public static bool InFront
    {
        get
        {
            var now = Environment.TickCount64;
            if (!_asked || now - _askedAt >= KeepMilliseconds)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
                _front = processId == (uint)Environment.ProcessId;
                _askedAt = now;
                _asked = true;
            }

            return _front;
        }
    }
}
