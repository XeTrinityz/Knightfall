#nullable enable

using System.Reflection;
using System.Runtime.InteropServices;
using BmSDK;

namespace Knightfall;

// Runs one of the game's script functions by its full path, on an object of a class BmSDK has no
// binding for, like a DLC's own classes. A binding always runs the function of the class it was
// made for, so a DLC class's own version of a function is only reached this way.
//
// The arguments are written as 4-byte words in order, which covers bools, ints and floats. BmSDK
// runs its calls through the game's ProcessEvent, which it keeps internal, so that is found once.
static class ScriptCall
{
    // Room for any argument list a call here needs. The game copies only the function's own.
    const int ArgumentRoom = 512;

    static Action<IntPtr, IntPtr, IntPtr, IntPtr>? _processEvent;
    static bool _looked;
    static readonly Dictionary<string, Function?> Functions = new(StringComparer.OrdinalIgnoreCase);

    // False when the function or the game's call couldn't be found. Nothing is run then.
    public static unsafe bool Run(GameObject target, string path, params int[] words)
    {
        if (!target.IsValid || Find(path) is not { IsValid: true } function || ProcessEvent() is not { } call)
        {
            return false;
        }

        var room = stackalloc byte[ArgumentRoom + 16];
        var arguments = (byte*)(((nint)room + 15) & ~(nint)15);
        new Span<byte>(arguments, ArgumentRoom).Clear();
        for (var i = 0; i < words.Length && (i + 1) * 4 <= ArgumentRoom; i++)
        {
            *(int*)(arguments + i * 4) = words[i];
        }

        call(target.Ptr, function.Ptr, (IntPtr)arguments, IntPtr.Zero);
        return true;
    }

    static Function? Find(string path)
    {
        if (!Functions.TryGetValue(path, out var function) || function is not { IsValid: true })
        {
            function = Game.FindObject<Function>(path);
            Functions[path] = function;
        }

        return function;
    }

    static Action<IntPtr, IntPtr, IntPtr, IntPtr>? ProcessEvent()
    {
        if (_looked)
        {
            return _processEvent;
        }

        _looked = true;
        try
        {
            var functions = typeof(GameObject).Assembly.GetType("BmSDK.Framework.GameFunctions");
            var found = functions?.GetProperty("ProcessEvent", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as Delegate;
            if (found is not null)
            {
                var pointer = Marshal.GetFunctionPointerForDelegate(found);
                _processEvent = (self, function, parms, result) => Native(pointer, self, function, parms, result);
            }
        }
        catch (Exception error)
        {
            Log.Error("script_call", "The game's script call couldn't be found", error);
        }

        if (_processEvent is null)
        {
            Log.Warn("script_call", "The game's script call wasn't found, so some DLC actions fall back to the base game's.");
        }

        return _processEvent;
    }

    static unsafe void Native(IntPtr pointer, IntPtr self, IntPtr function, IntPtr parms, IntPtr result) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)pointer)(self, function, parms, result);
}
