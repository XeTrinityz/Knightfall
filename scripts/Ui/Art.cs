#nullable enable

using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// A picture cut from one of the game's own interface textures, given in its texels. The texture
// is looked up the first time it's drawn and kept. A missing one is asked about again now and
// then, since the front end doesn't load the in-game atlases.
sealed class Art
{
    const double RetrySeconds = 2;

    readonly string _path;
    readonly float _u;
    readonly float _v;
    readonly float _width;
    readonly float _height;
    Texture2D? _texture;
    DateTime _retry;

    public Art(string path, float u, float v, float width, float height)
    {
        _path = path;
        _u = u;
        _v = v;
        _width = width;
        _height = height;
    }

    // Draws it stretched over a box, tinted. Returns false when the texture can't be drawn, so
    // the caller can draw something of its own.
    public bool At(float x, float y, float width, float height, Rgba color)
    {
        var texture = Texture();
        if (texture is null)
        {
            return false;
        }

        Draw.Image(texture, x, y, width, height, _u, _v, _width, _height, color);
        return true;
    }

    // Draws it centred on a point at this height, keeping its shape.
    public bool Centred(float x, float y, float height, Rgba color)
    {
        var width = height * _width / _height;
        return At(x - width / 2f, y - height / 2f, width, height, color);
    }

    Texture2D? Texture()
    {
        if (_texture is null || !_texture.IsValid)
        {
            _texture = null;
            if (DateTime.UtcNow < _retry)
            {
                return null;
            }

            // Looking it up is a call into the game, so a missing texture is only asked about now and then.
            _retry = DateTime.UtcNow.AddSeconds(RetrySeconds);
            _texture = Game.FindObject<Texture2D>(_path);
            if (_texture is null)
            {
                return null;
            }
        }

        return Viewer.Ready(_texture) ? _texture : null;
    }
}
