#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Knightfall;

// Puts text on the Windows clipboard, for a path the player wants to paste somewhere else.
static class Clipboard
{
    const uint UnicodeText = 13;
    const uint Moveable = 0x0002;

    [DllImport("user32.dll")]
    static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalFree(IntPtr memory);

    static IntPtr _window;

    // False when another program has the clipboard open.
    public static bool Put(string text)
    {
        // With no window as its owner, Windows refuses the text once the clipboard is emptied.
        if (_window == IntPtr.Zero)
        {
            using var game = Process.GetCurrentProcess();
            _window = game.MainWindowHandle;
        }

        if (_window == IntPtr.Zero)
        {
            return false;
        }

        // Filled before the clipboard is emptied, so a failure leaves what was on it.
        var memory = GlobalAlloc(Moveable, (UIntPtr)((text.Length + 1) * 2));
        if (memory == IntPtr.Zero)
        {
            return false;
        }

        var target = GlobalLock(memory);
        if (target == IntPtr.Zero)
        {
            GlobalFree(memory);
            return false;
        }

        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
        Marshal.WriteInt16(target, text.Length * 2, 0);
        GlobalUnlock(memory);

        if (!OpenClipboard(_window))
        {
            GlobalFree(memory);
            return false;
        }

        try
        {
            EmptyClipboard();

            // Once it's taken, the memory is the clipboard's to free.
            if (SetClipboardData(UnicodeText, memory) == IntPtr.Zero)
            {
                GlobalFree(memory);
                return false;
            }

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }
}
