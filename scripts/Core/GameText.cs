#nullable enable

using BmSDK;

namespace Knightfall;

// An FString made from a C# string takes game memory that nothing gives back, and freeing one
// that went through a script call can crash the game.
static class GameText
{
    const int Warning = 4000;

    static readonly Dictionary<string, FString> Made = new();

    // Made once and reused. Only for native functions that just read the text, like drawing.
    // A script function shares the text's memory with us, and if its code assigns to that
    // parameter the game frees the memory under our cache. Reusing it then corrupts the heap and
    // crashes the game later somewhere else.
    public static FString Of(string text)
    {
        if (!Made.TryGetValue(text, out var native))
        {
            native = new FString(text);
            Made[text] = native;

            if (Made.Count == Warning)
            {
                Log.Warn("game_text", $"{Warning} different texts have been made. Something is making text that changes too often.");
            }
        }

        return native;
    }

    // A new FString for one call to a script function. It is never freed by us, because the game
    // may already have freed or replaced its memory, so keep these calls rare.
    public static FString Once(string text) => new(text);
}
