#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Hides the player's own body, parts and cape while something else is shown in their place.
// The body goes on animating unseen, so moves, gadgets and sockets all still work.
//
// The game shows the player's own parts again now and then, when it builds the body again or
// attaches something. So Hide runs every tick. Held gadgets belong to the gadget and stay on show.
sealed class Cover
{
    readonly Held<PrimitiveComponent, bool> _hidden;
    readonly Held<RCapeComponent, bool> _cape;
    readonly Held<PrimitiveComponent, bool> _capeMesh;
    readonly Held<SkeletalMeshComponent, bool> _animated;

    public Cover(string name)
    {
        _hidden = new($"{name} hidden body", part => part.HiddenGame, (part, hidden) => part.SetHidden(hidden));
        _cape = new($"{name} hidden cape", cape => cape.IsCapeHiddenGame, (cape, hidden) => cape.CapeSetHiddenGame(hidden));
        _capeMesh = new($"{name} hidden cape mesh", part => part.HiddenGame, (part, hidden) => part.SetHidden(hidden));
        _animated = new($"{name} animation", body => body.bUpdateSkelWhenNotRendered, (body, on) => body.bUpdateSkelWhenNotRendered = on);
    }

    // Ours is whatever the caller put on the player itself, which stays on show.
    public void Hide(RPawnPlayer player, SkeletalMeshComponent own, Func<nint, bool> ours, bool keepCape)
    {
        _animated.Hold(own, true);
        _hidden.Hold(own, true);

        var extras = player.ExtraMeshes;
        for (var i = 0; i < extras.Count; i++)
        {
            if (extras[i] is { IsValid: true } extra && !ours(extra.Ptr))
            {
                _hidden.Hold(extra, true);
            }
        }

        var capeAt = player is RPawnPlayerBmBase { CapeSkeletalMesh: { IsValid: true } ownCape } ? ownCape.Ptr : 0;
        var attached = own.Attachments;
        for (var i = 0; i < attached.Count; i++)
        {
            if (attached[i].Component is PrimitiveComponent { IsValid: true } prop && !ours(prop.Ptr) && prop.Outer is { } owner && owner.Ptr == player.Ptr
                && prop.Ptr != capeAt)
            {
                _hidden.Hold(prop, true);
            }
        }

        if (!keepCape && player is RPawnPlayerBmBase caped)
        {
            if (caped.CapeSkeletalMesh is { IsValid: true } capeMesh)
            {
                _capeMesh.Hold(capeMesh, true);
            }

            if (caped.CapeComponent is { IsValid: true } cape)
            {
                _cape.Hold(cape, true);
            }
        }
    }

    public void ShowCape()
    {
        _cape.Release();
        _capeMesh.Release();
    }

    public void Show()
    {
        _hidden.Release();
        ShowCape();
        _animated.Release();
    }
}
