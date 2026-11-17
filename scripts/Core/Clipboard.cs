#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Knightfall;

// Text to and from the Windows clipboard, for a path the player wants to paste somewhere else
// or a name pasted in.
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

    [DllImport("user32.dll")]
    static extern IntPtr GetClipboardData(uint format);

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

    // The clipboard's text, or empty when it holds none or another program has it open.
    public static string Get()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return "";
        }

        try
        {
            var data = GetClipboardData(UnicodeText);
            var text = data == IntPtr.Zero ? IntPtr.Zero : GlobalLock(data);
            if (text == IntPtr.Zero)
            {
                return "";
            }

            try
            {
                return Marshal.PtrToStringUni(text) ?? "";
            }
            finally
            {
                GlobalUnlock(data);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }
}
