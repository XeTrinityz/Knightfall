#nullable enable

using System.Numerics;
using System.Text;
using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    // A solid colour part of the way from one to the other. Triangles can't be see-through, so
    // tints that sit on a known background are mixed like this.
    public static Rgba Mix(Rgba from, Rgba to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new Rgba(
            (byte)(from.R + (to.R - from.R) * amount),
            (byte)(from.G + (to.G - from.G) * amount),
            (byte)(from.B + (to.B - from.B) * amount));
    }

    // Like Mix, but the alpha moves too. For gradients made of see-through boxes.
    public static Rgba Blend(Rgba from, Rgba to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Mix(from, to, amount) with { A = (byte)(from.A + (to.A - from.A) * amount) };
    }
}

// Everything is laid out on a screen that is always 1080 units high, so the menu keeps its
// size on any resolution. Width grows with the aspect ratio.
static class Draw
{
    public const float ScreenHeight = 1080f;


    // The game's fonts are small bitmaps. Below this many pixels of screen height the 16 pixel
    // font is the closer fit, above it the 30 pixel one.
    const float SmallFontBelow = 1600f;

    sealed class Face
    {
        public float LineHeight;
        public readonly Dictionary<string, float> Widths = new();
    }

    static readonly Dictionary<Font, Face> Faces = new(ReferenceEqualityComparer.Instance);
    static readonly Dictionary<(string Text, float Size, float Width), string[]> Wraps = new();
    static readonly string?[] Letters = new string?[128];

    static Canvas? _canvas;
    static Texture2D? _white;
    static Font? _fontBefore;
    static Font? _wrapFont;
    static Face? _face;
    static TArray<Canvas.FCanvasUVTri>? _triangle;
    static TArray<Canvas.FCanvasUVTri>? _quad;
    static float _originX;
    static float _originY;
    static float _zoom = 1f;
    static float _fade = 1f;

    public static float Scale { get; private set; } = 1f;

    // Drawing has begun and not ended yet.
    public static bool Busy => _canvas is not null;

    public static float ScreenWidth { get; private set; } = 1920f;

    public static bool Begin(Canvas canvas)
    {
        var white = canvas.DefaultTexture;
        if (white is null || canvas.ClipX < 1f || canvas.ClipY < 1f)
        {
            return false;
        }

        _canvas = canvas;
        _white = white;
        _fontBefore = canvas.Font;
        _face = null;
        Scale = canvas.ClipY / ScreenHeight;
        ScreenWidth = canvas.ClipX / Scale;
        Place(0f, 0f, 1f);
        Fade(1f);

        var font = PickFont(canvas.ClipY);
        if (!UseFont(font))
        {
            End();
            return false;
        }

        // Wrapped lines were measured in the old font.
        if (!ReferenceEquals(font, _wrapFont))
        {
            _wrapFont = font;
            Wraps.Clear();
        }

        return true;
    }

    // The game and BmSDK's error overlay draw text with whatever font the canvas holds.
    public static void End()
    {
        if (_canvas is not null)
        {
            _canvas.Font = _fontBefore!;
        }

        _canvas = null;
        _white = null;
        _fontBefore = null;
        _face = null;
    }

    // Moves and sizes everything drawn after it. A theme can lay itself out from 0,0 at full
    // size and then be put where it belongs, or shrunk to fit a narrow screen.
    public static void Place(float x, float y, float zoom)
    {
        _originX = x;
        _originY = y;
        _zoom = zoom;
    }

    // Where a point laid out at the current place lands on the screen, in screen units.
    public static float ScreenX(float x) => _originX + x * _zoom;

    public static float ScreenY(float y) => _originY + y * _zoom;

    // Fades everything drawn after it. Triangles ignore it, the game always draws them solid.
    public static void Fade(float amount)
    {
        _fade = Math.Clamp(amount, 0f, 1f);
    }

    // The buffers for triangles are game memory, so they are handed back when the scripts unload.
    public static void Release()
    {
        _triangle?.Dispose();
        _triangle = null;
        _quad?.Dispose();
        _quad = null;
    }

    public static void Rect(float x, float y, float width, float height, Rgba color)
    {
        Box(x, y, x + width, y + height, color);
    }

    // A box given by its four edges. Edges are rounded to whole pixels, so two boxes that share
    // an edge never show a gap or overlap.
    static void Box(float left, float top, float right, float bottom, Rgba color)
    {
        if (_canvas is null)
        {
            return;
        }

        var x0 = PixelX(left);
        var y0 = PixelY(top);
        var x1 = PixelX(right);
        var y1 = PixelY(bottom);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        _canvas.SetPos(x0, y0, 0f);
        _canvas.SetDrawColor(color.R, color.G, color.B, Alpha(color));
        _canvas.DrawRect(x1 - x0, y1 - y0, _white!);
    }

    // The game has no gradient fill, so one is built from a stack of plain bands. Each band is a
    // few units wide, so a long gradient gets more of them and no step shows.
    public static void GradientDown(float x, float y, float width, float height, Rgba top, Rgba bottom)
    {
        var bands = Bands(height);
        for (var i = 0; i < bands; i++)
        {
            var from = y + height * i / bands;
            var to = y + height * (i + 1) / bands;
            Box(x, from, x + width, to, Rgba.Blend(top, bottom, bands > 1 ? i / (bands - 1f) : 0f));
        }
    }

    public static void GradientAcross(float x, float y, float width, float height, Rgba left, Rgba right)
    {
        var bands = Bands(width);
        for (var i = 0; i < bands; i++)
        {
            var from = x + width * i / bands;
            var to = x + width * (i + 1) / bands;
            Box(from, y, to, y + height, Rgba.Blend(left, right, bands > 1 ? i / (bands - 1f) : 0f));
        }
    }

    // A thin line between two points. It fades like boxes do.
    public static void Line(float x0, float y0, float x1, float y1, Rgba color)
    {
        if (_canvas is null)
        {
            return;
        }

        var tint = new GameObject.FColor { R = color.R, G = color.G, B = color.B, A = Alpha(color) };
        _canvas.Draw2DLine(PixelX(x0), PixelY(y0), PixelX(x1), PixelY(y1), tint);
    }

    // Diagonal stripes across a box, each clipped to it.
    public static void Hatch(float x, float y, float width, float height, float step, Rgba color)
    {
        for (var k = -height; k < width; k += step)
        {
            float x0 = k, y0 = height, x1 = k + height, y1 = 0f;
            if (x0 < 0f)
            {
                y0 += x0;
                x0 = 0f;
            }

            if (x1 > width)
            {
                y1 += x1 - width;
                x1 = width;
            }

            if (x1 > x0)
            {
                Line(x + x0, y + y0, x + x1, y + y1, color);
            }
        }
    }

    // An outline, drawn inside the given box.
    public static void Frame(float x, float y, float width, float height, float thickness, Rgba color)
    {
        Rect(x, y, width, thickness, color);
        Rect(x, y + height - thickness, width, thickness, color);
        Rect(x, y + thickness, thickness, height - 2f * thickness, color);
        Rect(x + width - thickness, y + thickness, thickness, height - 2f * thickness, color);
    }

    // The game draws triangles solid whatever alpha they are given.
    public static void Triangle(float ax, float ay, float bx, float by, float cx, float cy, Rgba color)
    {
        if (_canvas is null)
        {
            return;
        }

        if (_triangle is null)
        {
            _triangle = new TArray<Canvas.FCanvasUVTri>(1);
            _triangle.Add(default);
        }

        _triangle[0] = Corners(Pixel(ax, ay), Pixel(bx, by), Pixel(cx, cy));
        _canvas.DrawTris(_white!, _triangle, Solid(color));
    }

    // A four-sided shape, given corner by corner going round. It is two triangles sent to the
    // game in one call, so like them it is always solid.
    public static void Quad(float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy, Rgba color)
    {
        if (_canvas is null)
        {
            return;
        }

        if (_quad is null)
        {
            _quad = new TArray<Canvas.FCanvasUVTri>(2);
            _quad.Add(default);
            _quad.Add(default);
        }

        var a = Pixel(ax, ay);
        var c = Pixel(cx, cy);
        _quad[0] = Corners(a, Pixel(bx, by), c);
        _quad[1] = Corners(a, c, Pixel(dx, dy));
        _canvas.DrawTris(_white!, _quad, Solid(color));
    }

    // Part of a texture, added on top of what is behind it. Black adds nothing, so a dark
    // background baked into a texture disappears and only its bright parts show. The color
    // tints it. Fading dims it, since added light has no alpha of its own.
    public static void Glow(Texture2D texture, float x, float y, float width, float height, float u, float v, float ul, float vl, Rgba color)
    {
        if (_canvas is null || !texture.IsValid)
        {
            return;
        }

        var left = PixelX(x);
        var top = PixelY(y);
        var right = PixelX(x + width);
        var bottom = PixelY(y + height);
        if (right <= left || bottom <= top)
        {
            return;
        }

        var strength = _fade * color.A / 255f;
        var tint = new GameObject.FLinearColor
        {
            R = Linear(color.R) * strength,
            G = Linear(color.G) * strength,
            B = Linear(color.B) * strength,
            A = strength,
        };

        _canvas.SetPos(left, top, 0f);
        _canvas.DrawTile(texture, right - left, bottom - top, u, v, ul, vl, tint, false, EngineTypes.EBlendMode.BLEND_Additive);
    }

    // The game turns draw colours into linear light this way, so a tile tinted with the same
    // colour as a box comes out matching it.
    static float Linear(byte channel) => MathF.Pow(channel / 255f, 2.2f);

    static Vector2 Pixel(float x, float y) => new(PixelX(x), PixelY(y));

    static Canvas.FCanvasUVTri Corners(Vector2 a, Vector2 b, Vector2 c)
    {
        return new Canvas.FCanvasUVTri
        {
            V0_Pos = a,
            V1_Pos = b,
            V2_Pos = c,
            V0_UV = new Vector2(0f, 0f),
            V1_UV = new Vector2(1f, 0f),
            V2_UV = new Vector2(0f, 1f),
        };
    }

    static GameObject.FColor Solid(Rgba color)
    {
        return new GameObject.FColor { R = color.R, G = color.G, B = color.B, A = 255 };
    }

    // Size is the height of a line of text, in units.
    public static void Text(string text, float x, float y, float size, Rgba color)
    {
        if (_canvas is null || _face is null || text.Length == 0)
        {
            return;
        }

        var scale = PixelScale(size);
        Canvas.FFontRenderInfo info = default;
        _canvas.SetPos(PixelX(x), PixelY(y), 0f);
        _canvas.SetDrawColor(color.R, color.G, color.B, Alpha(color));
        _canvas.DrawText(Native(text), false, scale, scale, out info);
    }

    // Draws text that ends at the given x. Returns where it starts.
    public static float TextRight(string text, float right, float y, float size, Rgba color)
    {
        var left = right - TextWidth(text, size);
        Text(text, left, y, size, color);
        return left;
    }

    // Right aligned text that never reaches left of leftmost, so a long value can't run over its
    // row's label. Too wide, it shrinks a little, then keeps the letters that fit and fades out
    // where it's cut. Letters are drawn one by one, so no new strings are made. Returns where it
    // starts.
    public static float TextRightFit(string text, float right, float y, float size, Rgba color, float leftmost)
    {
        var room = right - leftmost;
        var width = TextWidth(text, size);
        if (room <= 0f || width <= room)
        {
            return TextRight(text, right, y, size, color);
        }

        var smaller = MathF.Max(size * FitShrink, size * room / width);
        y += (size - smaller) / 2f;
        if (TextWidth(text, smaller) <= room)
        {
            return TextRight(text, right, y, smaller, color);
        }

        var at = leftmost;
        foreach (var letter in text)
        {
            var letterWidth = LetterWidth(letter, smaller);
            if (at + letterWidth > right)
            {
                break;
            }

            if (letter != ' ')
            {
                Text(Letter(letter), at, y, smaller, CutFade(color, right - at - letterWidth, smaller));
            }

            at += letterWidth;
        }

        return leftmost;
    }

    // The smallest a value shrinks to before it is cut instead.
    const float FitShrink = 0.8f;

    public static float TextWidth(string text, float size)
    {
        if (_canvas is null || _face is null || text.Length == 0)
        {
            return 0f;
        }

        if (!_face.Widths.TryGetValue(text, out var width))
        {
            width = Measure(text).X;
            _face.Widths[text] = width;
        }

        return width * PixelScale(size) / (_zoom * Scale);
    }

    // Text with extra space between the letters, for small captions. Each letter is its own
    // short text, so no new strings are made. Returns where it ends.
    public static float Tracked(string text, float x, float y, float size, float spacing, Rgba color)
    {
        foreach (var letter in text)
        {
            if (letter == ' ')
            {
                x += size * 0.32f + spacing;
                continue;
            }

            var one = Letter(letter);
            Text(one, x, y, size, color);
            x += TextWidth(one, size) + spacing;
        }

        return x - spacing;
    }

    // The start of some tracked text, cut to the whole letters that fit before the limit. The
    // letters fade as they near it, as if the text ran on out of sight. Returns false and draws
    // nothing when fewer than two fit.
    public static bool TrackedHead(string text, float x, float y, float size, float spacing, Rgba color, float limit)
    {
        var fits = 0;
        var at = x;
        foreach (var letter in text)
        {
            var width = LetterWidth(letter, size);
            if (at + width > limit)
            {
                break;
            }

            at += width + spacing;
            fits++;
        }

        if (fits < MinCutLetters)
        {
            return false;
        }

        at = x;
        for (var i = 0; i < fits; i++)
        {
            var width = LetterWidth(text[i], size);
            if (text[i] != ' ')
            {
                Text(Letter(text[i]), at, y, size, CutFade(color, limit - at - width, size));
            }

            at += width + spacing;
        }

        return true;
    }

    // The end of some tracked text, finishing at right and cut to the whole letters that fit
    // after the limit. The other half of TrackedHead.
    public static bool TrackedTail(string text, float right, float y, float size, float spacing, Rgba color, float limit)
    {
        var fits = 0;
        var at = right;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var width = LetterWidth(text[i], size);
            if (at - width < limit)
            {
                break;
            }

            at -= width + spacing;
            fits++;
        }

        if (fits < MinCutLetters)
        {
            return false;
        }

        at = right;
        for (var i = text.Length - 1; i >= text.Length - fits; i--)
        {
            var left = at - LetterWidth(text[i], size);
            if (text[i] != ' ')
            {
                Text(Letter(text[i]), left, y, size, CutFade(color, left - limit, size));
            }

            at = left - spacing;
        }

        return true;
    }

    const int MinCutLetters = 2;

    static float LetterWidth(char letter, float size)
    {
        return letter == ' ' ? size * 0.32f : TextWidth(Letter(letter), size);
    }

    // How strong a letter of cut text is, from how far it sits from the cut.
    static Rgba CutFade(Rgba color, float distance, float size)
    {
        var strength = Math.Clamp(0.25f + distance / (size * 3f), 0f, 1f);
        return color with { A = (byte)(color.A * strength) };
    }

    public static float TrackedWidth(string text, float size, float spacing)
    {
        var width = 0f;
        foreach (var letter in text)
        {
            width += (letter == ' ' ? size * 0.32f : TextWidth(Letter(letter), size)) + spacing;
        }

        return MathF.Max(0f, width - spacing);
    }

    // Words are measured one at a time. Measuring each trial line would make a native string
    // for every one of them, and those are never freed.
    public static string[] Wrap(string text, float size, float width)
    {
        var key = (text, size, width);
        if (Wraps.TryGetValue(key, out var lines))
        {
            return lines;
        }

        var space = TextWidth("a a", size) - TextWidth("aa", size);
        var done = new List<string>();
        var line = new StringBuilder();
        var used = 0f;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var wordWidth = TextWidth(word, size);
            if (line.Length > 0 && used + space + wordWidth > width)
            {
                done.Add(line.ToString());
                line.Clear();
                used = 0f;
            }

            if (line.Length > 0)
            {
                line.Append(' ');
                used += space;
            }

            line.Append(word);
            used += wordWidth;
        }

        if (line.Length > 0)
        {
            done.Add(line.ToString());
        }

        lines = done.ToArray();
        Wraps[key] = lines;
        return lines;
    }

    static int Bands(float length)
    {
        const float unitsPerBand = 4f;
        const int most = 96;
        return Math.Clamp((int)MathF.Ceiling(length / unitsPerBand), 2, most);
    }

    static float PixelX(float x) => MathF.Round((_originX + x * _zoom) * Scale);

    static float PixelY(float y) => MathF.Round((_originY + y * _zoom) * Scale);

    static byte Alpha(Rgba color) => _fade >= 1f ? color.A : (byte)(color.A * _fade);

    // How much the font's own pixels are stretched. A bitmap font is sharpest at whole and half
    // steps, so a size that lands close to one is nudged onto it.
    static float PixelScale(float size)
    {
        var scale = size * _zoom * Scale / _face!.LineHeight;
        var step = MathF.Round(scale * 2f) / 2f;
        return step >= 0.5f && MathF.Abs(scale - step) <= 0.07f * step ? step : scale;
    }

    static string Letter(char letter)
    {
        if (letter >= Letters.Length)
        {
            return letter.ToString();
        }

        return Letters[letter] ??= letter.ToString();
    }

    static Font? PickFont(float pixelsHigh)
    {
        if (!Game.TryGetEngine(out var engine))
        {
            return null;
        }

        var sans = Look.Font.Index switch
        {
            1 => false,
            2 => true,
            _ => pixelsHigh < SmallFontBelow,
        };

        return sans ? engine.SmallFont ?? engine.MediumFont : engine.MediumFont ?? engine.SmallFont;
    }

    static bool UseFont(Font? font)
    {
        if (_canvas is null || font is null || !font.IsValid)
        {
            return false;
        }

        _canvas.Font = font;
        if (!Faces.TryGetValue(font, out _face))
        {
            _face = new Face { LineHeight = MathF.Max(Measure("Ag").Y, 1f) };
            Faces[font] = _face;
        }

        return true;
    }

    static (float X, float Y) Measure(string text)
    {
        var width = 0f;
        var height = 0f;
        _canvas!.TextSize(Native(text), out width, out height, 1f, 1f);
        return (width, height);
    }

    static FString Native(string text) => GameText.Of(text);
}
