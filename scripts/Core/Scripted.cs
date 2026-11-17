#nullable enable

using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// Textures the mod draws into itself. The engine asks a scripted texture to draw through Render
// whenever bNeedsUpdate is set, and this hands the canvas to whoever made that texture.
static class Scripted
{
    static readonly Dictionary<nint, Action<Canvas>> Painters = new();

    // Kept from the garbage collector until Drop.
    public static ScriptedTexture? Make(int width, int height, Action<Canvas> paint)
    {
        // Create makes an object of the class it runs on, so running it on the scripted texture's
        // default object makes a scripted texture.
        var made = TextureRenderTarget2DStatics.Create(ScriptedTexture.DefaultObject, width, height, Texture.EPixelFormat.PF_A8R8G8B8);
        if (made is not ScriptedTexture { IsValid: true } texture)
        {
            return null;
        }

        texture.AddToRoot();
        Painters[texture.Ptr] = paint;
        return texture;
    }

    public static void Drop(ScriptedTexture? texture)
    {
        if (texture is null)
        {
            return;
        }

        Painters.Remove(texture.Ptr);
        if (texture.IsValid)
        {
            texture.RemoveFromRoot();
        }
    }

    [Redirect(typeof(ScriptedTexture), nameof(ScriptedTexture.Render))]
    static void Render(ScriptedTexture self, Canvas canvas)
    {
        if (!Painters.TryGetValue(self.Ptr, out var paint))
        {
            self.Render(canvas);
            return;
        }

        Kernel.Run("scripted texture", paint, canvas);
    }
}
