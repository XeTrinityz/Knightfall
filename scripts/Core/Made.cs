#nullable enable

using BmSDK;

namespace Knightfall;

// Game objects the mod makes itself, like a mesh part put on the player.
//
// The SDK marks a wrapper as gone when the game destroys its object, but only a wrapper the SDK
// handed out. One that comes from `new` is never told, so it goes on reading as valid after a
// reload has freed what it points at, and the next call on it crashes the game. Looking the new
// object up by its path gives the wrapper the SDK does keep track of.
static class Made
{
    public static T Tracked<T>(T made)
        where T : GameObject, IGameObject
    {
        if (Game.FindObject<T>(made.GetPathName()) is { IsValid: true } tracked && tracked.Ptr == made.Ptr)
        {
            return tracked;
        }

        Log.Warn("made.untracked", $"A new {typeof(T).Name} couldn't be looked up again, so the mod can't tell when it is destroyed.");
        return made;
    }
}
