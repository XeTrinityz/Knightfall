#nullable enable

using System.Diagnostics;
using BmSDK.Engine;

namespace Knightfall;

// A run of textures to step through with one picked, like a group on the TEXTURES tab or the
// pages of a font. Turn moves to the next run.
sealed record Shelf(
    Func<int> Count,
    Func<int> Index,
    Func<int, Textures.Entry?> At,
    Action<int> Pick,
    Action<int> Turn,
    Func<string> Title);

// What the viewer and the light table say about a texture.
readonly record struct Facts(string Size, string Format, string Group, int MipsIn, int MipsAll, string Memory, string Colour, string Streams, string Note)
{
    static Texture? _last;
    static int _lastMips = -1;
    static Facts _cached;

    public string Mips => $"{MipsIn} OF {MipsAll}";

    public static Facts Of(Texture picture)
    {
        var mips = picture is Texture2D still ? still.ResidentMips : 1;
        if (ReferenceEquals(picture, _last) && mips == _lastMips)
        {
            return _cached;
        }

        _last = picture;
        _lastMips = mips;
        _cached = Read(picture);
        return _cached;
    }

    static Facts Read(Texture picture)
    {
        var group = Words(picture.LODGroup.ToString(), "TEXTUREGROUP_");
        var colour = picture.SRGB ? "SRGB" : "LINEAR";
        switch (picture)
        {
            case Texture2D still:
            {
                var all = Math.Max(1, still.Mips.ArrayNum);
                var resident = Math.Clamp(still.ResidentMips, 0, all);
                var bytes = 0L;
                for (var mip = all - resident; mip < all; mip++)
                {
                    bytes += MipBytes(still.Format, Math.Max(1, still.SizeX >> mip), Math.Max(1, still.SizeY >> mip));
                }

                var note = still switch
                {
                    TextureFlipBook book => $"FLIPBOOK {book.HorizontalImages} X {book.VerticalImages}",
                    _ when still.Outer is Font => "FONT PAGE",
                    _ => "",
                };

                var streams = still.NeverStream || !still.bIsStreamable ? "NEVER" : "YES";
                return new Facts($"{still.SizeX} X {still.SizeY}", Words(still.Format.ToString(), "PF_"), group, resident, all, Bytes(bytes), colour, streams, note);
            }
            case TextureRenderTarget2D target:
            {
                var bytes = MipBytes(target.Format, Math.Max(1, target.SizeX), Math.Max(1, target.SizeY));
                return new Facts($"{target.SizeX} X {target.SizeY}", Words(target.Format.ToString(), "PF_"), group, 1, 1, Bytes(bytes), colour, "NEVER", "LIVE");
            }
            default:
                return new Facts("?", "?", group, 0, 0, "?", colour, "?", "");
        }
    }

    static string Words(string name, string prefix) =>
        (name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name).Replace('_', ' ').ToUpperInvariant();

    // Compressed formats store blocks of 4 by 4 pixels, so a small mip still takes a whole block.
    static long MipBytes(Texture.EPixelFormat format, int width, int height)
    {
        long blocks = (long)((width + 3) / 4) * ((height + 3) / 4);
        return format switch
        {
            Texture.EPixelFormat.PF_DXT1 => blocks * 8,
            Texture.EPixelFormat.PF_DXT3 or Texture.EPixelFormat.PF_DXT5 or Texture.EPixelFormat.PF_BC5
                or Texture.EPixelFormat.PF_BC6H or Texture.EPixelFormat.PF_BC7 => blocks * 16,
            _ => (long)width * height * PixelBytes(format),
        };
    }

    static int PixelBytes(Texture.EPixelFormat format) => format switch
    {
        Texture.EPixelFormat.PF_G8 or Texture.EPixelFormat.PF_A1 => 1,
        Texture.EPixelFormat.PF_G16 or Texture.EPixelFormat.PF_V8U8 or Texture.EPixelFormat.PF_G8R8 or Texture.EPixelFormat.PF_B5G6R5
            or Texture.EPixelFormat.PF_R16F or Texture.EPixelFormat.PF_R16F_FILTER or Texture.EPixelFormat.PF_UYVY => 2,
        Texture.EPixelFormat.PF_FloatRGBA or Texture.EPixelFormat.PF_A16B16G16R16 or Texture.EPixelFormat.PF_G32R32F => 8,
        Texture.EPixelFormat.PF_B32G32R32F => 12,
        Texture.EPixelFormat.PF_A32B32G32R32F => 16,
        _ => 4,
    };

    static string Bytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0} MB" : $"{Math.Max(1, (bytes + 512) / 1024)} KB";
}

// The panel beside the menu that shows the picked texture. It also draws textures for the light
// table and the fonts tab, over a backdrop and only once they have settled.
static class Viewer
{
    public static readonly Choice Channels = new(
        "assets.textures.channels", "CHANNELS",
        "COLOUR ONLY leaves the alpha out. ALPHA SHAPE shows where it's solid in black on white, which is where masks and cut-outs hide.",
        new[] { ("both", "COLOUR AND ALPHA"), ("colour", "COLOUR ONLY"), ("alpha", "ALPHA SHAPE") })
    {
        Preference = true,
    };

    public static readonly Choice Backdrop = new(
        "assets.textures.backdrop", "BACKDROP",
        "What shows through the see-through parts.",
        new[] { ("checker", "CHECKER"), ("black", "BLACK"), ("grey", "GREY"), ("white", "WHITE") })
    {
        Preference = true,
        Block = static () => Channels.Index == 2 ? "WHITE FOR THE SHAPE" : null,
    };

    public static readonly Toggle Tile = new(
        "assets.textures.tile", "TILE 2 X 2",
        "Repeats it four times, to check whether it tiles without a seam.")
    {
        Preference = true,
    };

    public static readonly Toggle Detail = new(
        "assets.textures.full_detail", "FULL DETAIL",
        "Keeps every mip of a streamed texture loaded while you look at it, so it shows sharp. The game takes them back a few seconds after you move on.",
        on: true)
    {
        Preference = true,
    };

    // A texture is only drawn once the pick has rested this long and the game isn't streaming it.
    // Drawing one that is streaming in or out can crash the game's render thread.
    const double RestSeconds = 0.35;

    // How long each ask to keep every mip lasts. It's asked again before it runs out.
    const float HoldSeconds = 3f;
    const double HoldEvery = 1;

    const float Width = 500f;
    const float Inset = 14f;
    const float HeadHeight = 52f;
    const float BoxHeight = 330f;
    const float StripTop = HeadHeight + BoxHeight + 10f;
    const float StripHeight = 50f;
    const float FactsTop = StripTop + StripHeight + 14f;
    const float FactLine = 18f;
    const float Gap = 12f;
    const float SlideSeconds = 0.22f;
    const float Slide = 18f;
    // A checker square is a share of the box, so a big box doesn't cost thousands of draws.
    const float CheckerSmallest = 10f;
    const float CheckerAcross = 12f;

    public static readonly Rgba Panel = new(5, 8, 12, 236);
    public static readonly Rgba White = new(255, 255, 255);
    public static readonly Rgba Pale = new(214, 222, 230);
    public static readonly Rgba Dim = new(140, 152, 164);
    public static readonly Rgba Ghost = new(92, 102, 114);
    public static readonly Rgba Amber = new(250, 184, 78);
    static readonly Rgba CheckDark = new(46, 50, 56);
    static readonly Rgba CheckLight = new(70, 76, 84);
    static readonly Rgba Grey = new(122, 127, 134);
    static readonly Rgba Paper = new(240, 240, 240);
    static readonly Rgba Black = new(0, 0, 0);

    static Texture? _rested;
    static long _restSince;
    static Texture? _held;
    static long _heldAt;

    // The texture drawn last frame, for the tick to keep its mips in.
    static Texture? _shown;
    static long _shownAt;

    public static Rgba Accent => Look.AccentOr(Theme.Current.Accent);

    // Every tick. Asks the game to keep every mip of the texture on screen, while FULL DETAIL is on.
    public static void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        if (_shown is not Texture2D { IsValid: true, bIsStreamable: true, NeverStream: false } still
            || !Detail.On || Seconds(now - _shownAt) > 0.5)
        {
            _held = null;
            return;
        }

        if (!ReferenceEquals(still, _held) || Seconds(now - _heldAt) >= HoldEvery)
        {
            _held = still;
            _heldAt = now;
            still.SetForceMipLevelsToBeResident(HoldSeconds, 0);
        }
    }

    static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    // The main texture on screen this frame. Picking another starts the rest again.
    public static void Showing(Texture picture)
    {
        var now = Stopwatch.GetTimestamp();
        if (!ReferenceEquals(picture, _rested))
        {
            _rested = picture;
            _restSince = now;
        }

        _shown = picture;
        _shownAt = now;
    }

    public static bool Rested => Seconds(Stopwatch.GetTimestamp() - _restSince) >= RestSeconds;

    // Whether the game has the texture ready to draw and isn't changing it.
    public static bool Ready(Texture picture) => picture switch
    {
        Texture2D still => still.IsValid && still.Resource != IntPtr.Zero && still.ResidentMips > 0
            && !still.bAsyncResourceReleaseHasBeenStarted && still.PendingMipChangeRequestStatus.Value == 0,
        TextureRenderTarget2D target => target.IsValid && target.Resource != IntPtr.Zero && !target.bAsyncResourceReleaseHasBeenStarted,
        _ => false,
    };

    public static (float Width, float Height) SizeOf(Texture picture) => picture switch
    {
        Texture2D still => (Math.Max(1, still.SizeX), Math.Max(1, still.SizeY)),
        TextureRenderTarget2D target => (Math.Max(1, target.SizeX), Math.Max(1, target.SizeY)),
        _ => (1, 1),
    };

    // The part of a flipbook on show now, or the whole texture.
    public static (float U, float V, float Width, float Height) Frame(Texture picture, float time, bool play)
    {
        var (width, height) = SizeOf(picture);
        if (!play || picture is not TextureFlipBook book || book.HorizontalImages < 1 || book.VerticalImages < 1
            || book.HorizontalImages * book.VerticalImages < 2)
        {
            return (0f, 0f, width, height);
        }

        var across = book.HorizontalImages;
        var frames = across * book.VerticalImages;
        var rate = book.FrameRate > 0f ? book.FrameRate : 10f;
        var frame = (int)(time * rate) % frames;
        var cellWidth = width / across;
        var cellHeight = height / book.VerticalImages;
        return (frame % across * cellWidth, frame / across * cellHeight, cellWidth, cellHeight);
    }

    // What shows behind a texture.
    public static void Back(float x, float y, float width, float height, int backdrop)
    {
        if (Channels.Index == 2)
        {
            backdrop = 3;
        }

        switch (backdrop)
        {
            case 1:
                Draw.Rect(x, y, width, height, Black);
                break;
            case 2:
                Draw.Rect(x, y, width, height, Grey);
                break;
            case 3:
                Draw.Rect(x, y, width, height, Paper);
                break;
            default:
                var cell = MathF.Max(CheckerSmallest, MathF.Min(width, height) / CheckerAcross);
                Draw.Rect(x, y, width, height, CheckLight);
                for (var row = 0; row * cell < height; row++)
                {
                    var top = y + row * cell;
                    var tall = MathF.Min(cell, y + height - top);
                    for (var column = row % 2; column * cell < width; column += 2)
                    {
                        var left = x + column * cell;
                        Draw.Rect(left, top, MathF.Min(cell, x + width - left), tall, CheckDark);
                    }
                }

                break;
        }
    }

    // A part of a texture with its top left at left and top, scale units to a texel, cut to the box.
    public static void Clipped(Texture picture, (float U, float V, float Width, float Height) part, float left, float top, float scale,
        float boxX, float boxY, float boxWidth, float boxHeight)
    {
        var x0 = MathF.Max(left, boxX);
        var y0 = MathF.Max(top, boxY);
        var x1 = MathF.Min(left + part.Width * scale, boxX + boxWidth);
        var y1 = MathF.Min(top + part.Height * scale, boxY + boxHeight);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        var u = part.U + (x0 - left) / scale;
        var v = part.V + (y0 - top) / scale;
        Paint(picture, x0, y0, x1 - x0, y1 - y0, u, v, (x1 - x0) / scale, (y1 - y0) / scale);
    }

    static void Paint(Texture picture, float x, float y, float width, float height, float u, float v, float ul, float vl)
    {
        switch (Channels.Index)
        {
            case 1:
                Draw.Image(picture, x, y, width, height, u, v, ul, vl, White, solid: true);
                break;
            case 2:
                Draw.Image(picture, x, y, width, height, u, v, ul, vl, Black);
                break;
            default:
                Draw.Image(picture, x, y, width, height, u, v, ul, vl, White);
                break;
        }
    }

    // The texture as big as fits in the box, twice each way when tiled.
    public static void Fit(Texture picture, float x, float y, float width, float height, bool tile, float time, bool play = true)
    {
        var part = Frame(picture, time, play);
        var copies = tile ? 2 : 1;
        var scale = MathF.Min(width / (part.Width * copies), height / (part.Height * copies));
        var left = x + (width - part.Width * copies * scale) / 2f;
        var top = y + (height - part.Height * copies * scale) / 2f;
        for (var row = 0; row < copies; row++)
        {
            for (var column = 0; column < copies; column++)
            {
                Paint(picture, left + column * part.Width * scale, top + row * part.Height * scale, part.Width * scale, part.Height * scale,
                    part.U, part.V, part.Width, part.Height);
            }
        }
    }

    // A box with the texture in it, or why it isn't. Main says it's the one the viewer is about.
    public static void Shown(Texture? picture, float x, float y, float width, float height, bool tile, float time, bool main, Rgba accent)
    {
        Back(x, y, width, height, Backdrop.Index);
        if (picture is not { IsValid: true })
        {
            if (main)
            {
                Note("GONE. PRESS REFRESH", x, y, width, height, Kit.Failed);
            }

            return;
        }

        if (main)
        {
            Showing(picture);
        }

        if (!Rested || !Ready(picture))
        {
            if (main && !Kit.SpinningBat(x + width / 2f - 16f, y + height / 2f - 16f, 32f, (float)Seconds(Stopwatch.GetTimestamp()), 12f, accent))
            {
                Note("LOADING", x, y, width, height, Dim);
            }

            return;
        }

        Fit(picture, x + 6f, y + 6f, width - 12f, height - 12f, tile, time);
    }

    static void Note(string text, float x, float y, float width, float height, Rgba color)
    {
        const float size = 11f;
        var wide = Draw.TrackedWidth(text, size, 2f);
        Draw.Rect(x + (width - wide) / 2f - 8f, y + height / 2f - 12f, wide + 16f, 24f, Panel);
        Draw.Tracked(text, x + (width - wide) / 2f, y + height / 2f - size / 2f, size, 2f, color);
    }

    // Puts a panel of this size beside the menu, on whichever side has room. False when neither has.
    public static bool Place(float width, float height, float slide)
    {
        if (Theme.MenuArea is not { } menu)
        {
            return false;
        }

        var zoom = Look.Zoom;
        const float edge = 24f;
        var room = Draw.ScreenWidth - edge - menu.Right - Gap * zoom;
        var roomLeft = menu.Left - edge - Gap * zoom;
        var right = room >= roomLeft;
        var space = right ? room : roomLeft;
        if (space < width * zoom)
        {
            zoom = space / width;
        }

        zoom = MathF.Min(zoom, (Draw.ScreenHeight - 2f * edge) / height);
        if (zoom < 0.55f)
        {
            return false;
        }

        var x = right ? menu.Right + Gap * zoom - slide * zoom : menu.Left - Gap * zoom - width * zoom + slide * zoom;
        var y = Math.Clamp(menu.Top, edge, Draw.ScreenHeight - edge - height * zoom);
        Draw.Place(x, y, zoom);
        return true;
    }

    // Drawn after the menu, while a tab with a viewer is on screen.
    public static void Beside(MenuView view)
    {
        if (view.Pages.Count == 0 || view.Page.Tabs.Count == 0)
        {
            return;
        }

        var rows = view.Rows;
        var textures = rows.Contains(Textures.Picture);
        if (!textures && !rows.Contains(Fonts.Pick))
        {
            return;
        }

        var arrived = Kit.EaseOut(view.Arrived / SlideSeconds);
        var height = textures ? TextureHeight : Fonts.PanelHeight;
        if (!Place(Width, height, Slide * (1f - arrived)))
        {
            return;
        }

        Draw.Fade(view.Opened * arrived);
        var accent = Accent;
        Kit.Shadow(0f, 0f, Width, height, 0f, 0f);
        Draw.Rect(0f, 0f, Width, height, Panel);
        Draw.Frame(0f, 0f, Width, height, 1f, accent with { A = 45 });
        Draw.Rect(0f, 0f, Width, 1f, accent with { A = 130 });
        Kit.Corners(-2f, -2f, Width + 4f, height + 4f, 8f, accent);
        if (textures)
        {
            TexturePanel(view, accent);
        }
        else
        {
            Fonts.Panel(view, Width, accent);
        }

        Draw.Fade(1f);
    }

    const float TextureHeight = FactsTop + FactLine * 4f + 52f;

    // The top of a panel: a caption, a count and a name.
    public static void Head(string caption, string count, string name, float width, Rgba accent)
    {
        Draw.Tracked(caption, Inset, 10f, 9f, 2.5f, accent);
        if (count.Length > 0)
        {
            Draw.TextRight(count, width - Inset, 9f, 11f, Dim);
        }

        if (!Draw.TrackedHead(name, Inset, 24f, 17f, 0.5f, White, width - Inset))
        {
            Draw.Text(name, Inset, 24f, 17f, White);
        }
    }

    // A caption and its value, in one of the two columns of facts.
    public static void Fact(string label, string value, float x, float y, float width, Rgba color)
    {
        Draw.Tracked(label, x, y + 2f, 9f, 2f, Dim);
        Draw.TextRightFit(value, x + width, y, 12f, color, x + 64f);
    }

    // The full path at the bottom of a panel.
    public static void Foot(string path, float width, float height)
    {
        Draw.Rect(Inset, height - 30f, width - 2f * Inset, 1f, Ghost with { A = 60 });
        if (!Draw.TrackedHead(path, Inset, height - 21f, 10f, 0.5f, Dim, width - Inset))
        {
            Draw.Text(path, Inset, height - 21f, 10f, Dim);
        }
    }

    static void TexturePanel(MenuView view, Rgba accent)
    {
        var shelf = Textures.Shelf;
        var count = shelf.Count();
        var index = shelf.Index();
        var entry = Textures.Picked();
        var caption = Textures.Listing && Textures.From.Length > 0 ? "ON " + Textures.From : shelf.Title();
        Head(caption.Length > 0 ? caption : "TEXTURES", count > 0 ? $"{index + 1} / {count}" : "", entry is { } named ? Textures.Name(named.Path) : "NOTHING LISTED", Width, accent);

        var boxWidth = Width - 2f * Inset;
        Shown(entry?.Picture, Inset, HeadHeight, boxWidth, BoxHeight, Tile.On, view.Time, main: entry is not null, accent);
        Kit.Corners(Inset - 1f, HeadHeight - 1f, boxWidth + 2f, BoxHeight + 2f, 12f, accent);

        Strip(shelf, count, index, view.Time, accent);

        if (entry is not { } picked || !picked.Picture.IsValid)
        {
            Foot(entry?.Path ?? "", Width, TextureHeight);
            return;
        }

        var facts = Facts.Of(picked.Picture);
        var half = (Width - 2f * Inset - 24f) / 2f;
        var second = Inset + half + 24f;
        Fact("SIZE", facts.Size, Inset, FactsTop, half, Pale);
        Fact("FORMAT", facts.Format, second, FactsTop, half, Pale);
        Fact("MEMORY", facts.Memory, Inset, FactsTop + FactLine, half, Pale);
        Fact("COLOUR", facts.Colour, second, FactsTop + FactLine, half, Pale);
        Fact("GROUP", facts.Group, Inset, FactsTop + FactLine * 2f, half, Pale);
        Fact("STREAMS", facts.Streams, second, FactsTop + FactLine * 2f, half, Pale);

        var line = FactsTop + FactLine * 3f;
        Draw.Tracked("MIPS", Inset, line + 2f, 9f, 2f, Dim);
        var full = facts.MipsIn >= facts.MipsAll;
        Mips(Inset + 64f, line + 3f, facts, full ? accent : Amber);
        Draw.Text(facts.Mips, Inset + 64f + MipsWidth(facts) + 8f, line, 12f, full ? Pale : Amber);
        Tags(facts, Width - Inset, line, accent);

        Foot(picked.Path, Width, TextureHeight);
    }

    // A block for each mip, lit for the ones the game has in.
    public static void Mips(float x, float y, Facts facts, Rgba lit)
    {
        for (var i = 0; i < facts.MipsAll; i++)
        {
            Draw.Rect(x + i * 9f, y, 7f, 8f, i < facts.MipsIn ? lit : Ghost with { A = 90 });
        }
    }

    public static float MipsWidth(Facts facts) => facts.MipsAll * 9f - 2f;

    // Small tags for how it's shown, ending at right.
    public static void Tags(Facts facts, float right, float y, Rgba accent)
    {
        if (facts.Note.Length > 0)
        {
            right = Tag(facts.Note, right, y, accent) - 6f;
        }

        if (Tile.On)
        {
            right = Tag("TILED", right, y, accent) - 6f;
        }

        if (Channels.Index != 0)
        {
            Tag(Channels.ValueText, right, y, accent);
        }
    }

    static float Tag(string text, float right, float y, Rgba color)
    {
        const float size = 9f;
        var wide = Draw.TrackedWidth(text, size, 1.5f) + 10f;
        Draw.Frame(right - wide, y, wide, 15f, 1f, color with { A = 150 });
        Draw.Tracked(text, right - wide + 5f, y + 3f, size, 1.5f, color);
        return right - wide;
    }

    // The textures either side of the picked one, so it's clear what's coming.
    static void Strip(Shelf shelf, int count, int index, float time, Rgba accent)
    {
        const int side = 3;
        const float wide = 60f;
        const float picked = 76f;
        var gap = (Width - 2f * Inset - picked - 2 * side * wide) / (2 * side);
        var x = Inset;
        for (var offset = -side; offset <= side; offset++)
        {
            var width = offset == 0 ? picked : wide;
            var height = offset == 0 ? StripHeight : StripHeight - 10f;
            var top = StripTop + StripHeight - height;
            var at = index + offset;
            if (count > 0 && at >= 0 && at < count)
            {
                var entry = shelf.At(at);
                Back(x, top, width, height, Backdrop.Index);
                if (offset != 0 && entry is { Picture: { IsValid: true } picture } && Rested && Ready(picture))
                {
                    Fit(picture, x + 3f, top + 3f, width - 6f, height - 6f, false, time, play: false);
                }
                else if (offset == 0 && entry is { Picture: { IsValid: true } own } && Rested && Ready(own))
                {
                    Fit(own, x + 3f, top + 3f, width - 6f, height - 6f, false, time);
                }

                if (offset != 0)
                {
                    Draw.Rect(x, top, width, height, Panel with { A = (byte)(60 + 40 * Math.Abs(offset)) });
                }

                Draw.Frame(x, top, width, height, offset == 0 ? 2f : 1f, offset == 0 ? accent : Ghost with { A = 110 });
            }
            else
            {
                Draw.Frame(x, top, width, height, 1f, Ghost with { A = 40 });
            }

            x += width + gap;
        }
    }

    public static void Release()
    {
        LightTable.Release();
        _shown = null;
        _held = null;
        _rested = null;
    }
}
