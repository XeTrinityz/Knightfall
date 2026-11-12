#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// The gauntlet menu drawn into a texture that the game's own hologram screen shows in place of a
// call's video. The screen is a real model in the world, so Batman's head and hand pass in front
// of it, and the game's hologram look and its fade in and out apply to it.
static class GauntletScreen
{
    // The screen shows a centred window of its video, not all of it, and fades out at the
    // window's edges. Measured in game with a grid drawn into the texture.
    const float WindowLeft = 0.13f;
    const float WindowRight = 0.87f;
    const float WindowTop = 0.05f;
    const float WindowBottom = 0.95f;

    // The window is stretched to the screen's shape, 44.4 by 25. The texture is made wide enough
    // that the window already has that shape, so nothing is stretched.
    const float ScreenAspect = 44.4f / 25f;
    const int TextureHeight = 512;
    const int TextureWidth = (int)(TextureHeight * ScreenAspect * (WindowBottom - WindowTop) / (WindowRight - WindowLeft));

    // The slot of the screen's material that shows the video.
    static readonly FName Slot = new("MovieTexture");

    static ScriptedTexture? _texture;
    static MaterialInstanceConstant? _material;
    static Texture? _before;

    // Our texture is on the game's screen.
    public static bool Active => _material is not null;

    // Puts our texture on the screen the projector just turned on. False leaves the screen as it
    // is, and the menu is drawn over it instead.
    public static bool Take(RGauntletProjector projector)
    {
        if (Active)
        {
            return true;
        }

        if (projector.HologramMovieMaterial is not { IsValid: true } material || OurTexture() is not { } texture)
        {
            return false;
        }

        // A changed game would show its video some other way, and the menu goes over the screen.
        if (!material.GetTextureParameterValue(Slot, out var before) || before is null || projector.PrimaryRenderTarget is not { } video || before.Ptr != video.Ptr)
        {
            Log.Warn("gauntlet.slot", "The gauntlet screen doesn't show its video where expected, so the menu is drawn over it.");
            return false;
        }

        material.SetTextureParameterValue(Slot, texture);
        _material = material;
        _before = before;
        return true;
    }

    // Puts the game's own video back on the screen.
    public static void Give()
    {
        if (_material is { IsValid: true } material && _before is { IsValid: true } before)
        {
            material.SetTextureParameterValue(Slot, before);
        }

        _material = null;
        _before = null;
    }

    public static void Release()
    {
        Give();
        if (_texture is { IsValid: true } texture)
        {
            texture.RemoveFromRoot();
        }

        _texture = null;
    }

    // The texture is only drawn again when asked, so it is asked every frame it is on show.
    public static void Refresh()
    {
        if (Active && _texture is { IsValid: true } texture)
        {
            texture.bNeedsUpdate = true;
        }
    }

    static ScriptedTexture? OurTexture()
    {
        if (_texture is { IsValid: true })
        {
            return _texture;
        }

        // Create makes an object of the class it runs on, so running it on the scripted texture's
        // default object makes a scripted texture. It is kept from the garbage collector while
        // it is off the screen.
        var made = TextureRenderTarget2DStatics.Create(ScriptedTexture.DefaultObject, TextureWidth, TextureHeight, Texture.EPixelFormat.PF_A8R8G8B8);
        if (made is not ScriptedTexture { IsValid: true } texture)
        {
            Log.Warn("gauntlet.texture", "A texture for the gauntlet screen couldn't be made, so the menu is drawn over it.");
            return null;
        }

        texture.AddToRoot();
        _texture = texture;
        return texture;
    }

    // The engine asks a scripted texture to draw itself through this when it needs drawing.
    [Redirect(typeof(ScriptedTexture), nameof(ScriptedTexture.Render))]
    static void Render(ScriptedTexture self, Canvas canvas)
    {
        try
        {
            if (_texture is null || self.Ptr != _texture.Ptr)
            {
                self.Render(canvas);
                return;
            }

            // Never inside another drawing of ours, which would lose its place.
            if (Draw.Busy || !Draw.Begin(canvas))
            {
                return;
            }

            try
            {
                // The menu fills the window the screen shows, in screen units.
                var left = WindowLeft * canvas.ClipX / Draw.Scale;
                var top = WindowTop * canvas.ClipY / Draw.Scale;
                var width = (WindowRight - WindowLeft) * canvas.ClipX / Draw.Scale;
                var height = (WindowBottom - WindowTop) * canvas.ClipY / Draw.Scale;
                var zoom = width / GauntletMenu.Width;
                Draw.Place(left, top, zoom);

                var view = Menu.Current;
                GauntletMenu.Render(view, height / zoom, view.Opened);
            }
            finally
            {
                Draw.End();
            }
        }
        catch (Exception error)
        {
            Log.Error("gauntlet.screen", "The gauntlet screen couldn't be drawn", error);
        }
    }
}
