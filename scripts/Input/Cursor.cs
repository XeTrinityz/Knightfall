#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Knightfall;

// The Windows mouse pointer. The game hides it and turns the camera with it, so anything that
// wants the mouse as a pointer reads how far it moved and puts it back in the middle of the game.
// With another window in front the pointer is that window's, so it is left alone.
static class Cursor
{
    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Box
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr window, out Box box);

    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr window, ref Point point);

    static IntPtr _window;

    // How far the pointer moved since it was last put in the middle, then puts it back there.
    public static bool Moved(out int dx, out int dy)
    {
        dx = 0;
        dy = 0;
        if (!GameWindow.InFront || !Middle(out var x, out var y) || !GetCursorPos(out var at))
        {
            return false;
        }

        dx = at.X - x;
        dy = at.Y - y;
        if (dx != 0 || dy != 0)
        {
            SetCursorPos(x, y);
        }

        return true;
    }

    public static void Centre()
    {
        if (GameWindow.InFront && Middle(out var x, out var y))
        {
            SetCursorPos(x, y);
        }
    }

    static bool Middle(out int x, out int y)
    {
        x = 0;
        y = 0;
        if (_window == IntPtr.Zero)
        {
            _window = Process.GetCurrentProcess().MainWindowHandle;
        }

        if (_window == IntPtr.Zero || !GetClientRect(_window, out var box))
        {
            return false;
        }

        var middle = new Point { X = (box.Right - box.Left) / 2, Y = (box.Bottom - box.Top) / 2 };
        if (!ClientToScreen(_window, ref middle))
        {
            return false;
        }

        (x, y) = (middle.X, middle.Y);
        return true;
    }
}
