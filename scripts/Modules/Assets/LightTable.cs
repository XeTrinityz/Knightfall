#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK.Engine;

namespace Knightfall;

// The picked texture over the whole screen, to look closer. It zooms, pans and steps through the
// group, and takes the menu's place and keys until Back.
static class LightTable
{
    sealed class Table : Overlay
    {
        public override void Keys() => LightTable.Keys();

        public override void Render(MenuView view) => LightTable.Render(view);

        public override void Shown() => Take(true);

        public override void Gone() => Take(false);
    }

    static readonly Table Instance = new();

    // Each step is this many times as big as the texture when it just fits.
    static readonly float[] Zooms = { 1f, 2f, 4f, 8f, 16f, 32f };
    static readonly string[] ZoomNames = { "FIT", "2X", "4X", "8X", "16X", "32X" };

    static readonly string[] CameraInputs = { "MouseX", "MouseY", "XboxTypeS_RightX", "XboxTypeS_RightY" };

    const float Edge = 40f;
    const float Side = 380f;
    const float StripHeight = 100f;
    const float Gutter = 18f;
    const float InSeconds = 0.2f;

    // How fast the right stick pans, in screen units a second at full tilt.
    const float StickPan = 1100f;
    const float StickDead = 0.18f;

    // Lines between texels once each is at least this many pixels wide.
    const float GridFrom = 12f;
    const int GridMost = 240;

    static readonly Rgba Shade = new(0, 0, 0, 215);

    static Shelf? _shelf;
    static int _zoom;

    // Units to a texel as last drawn.
    static float _scale = 1f;
    static Vector2 _centre;
    static Texture? _framed;
    static bool _lookTaken;
    static long _openedAt;
    static long _lastKeys;

    public static bool Up => ReferenceEquals(Menu.Covering, Instance);

    public static void Open(Shelf shelf)
    {
        if (shelf.Count() == 0)
        {
            return;
        }

        _shelf = shelf;
        _zoom = 0;
        _framed = null;
        _openedAt = Stopwatch.GetTimestamp();
        Menu.Lift(Instance);
    }

    static void Take(bool open)
    {
        if (open)
        {
            Cursor.Centre();
            _lastKeys = 0;
        }

        if (open == _lookTaken)
        {
            return;
        }

        foreach (var name in CameraInputs)
        {
            if (open)
            {
                Swallowed.Take(name);
            }
            else
            {
                Swallowed.Give(name);
            }
        }

        _lookTaken = open;
    }

    static void Keys()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _lastKeys == 0 ? 0f : MathF.Min((float)((now - _lastKeys) / (double)Stopwatch.Frequency), 0.1f);
        _lastKeys = now;
        if (_shelf is not { } shelf)
        {
            Menu.Uncover();
            return;
        }

        var count = shelf.Count();
        if (count > 0)
        {
            if (Controls.Pressed(Act.Left))
            {
                shelf.Pick((shelf.Index() - 1 + count) % count);
            }
            else if (Controls.Pressed(Act.Right))
            {
                shelf.Pick((shelf.Index() + 1) % count);
            }
        }

        if (Controls.Pressed(Act.TabPrev))
        {
            shelf.Turn(-1);
        }
        else if (Controls.Pressed(Act.TabNext))
        {
            shelf.Turn(1);
        }

        if (Controls.Pressed(Act.Up))
        {
            _zoom = Math.Min(_zoom + 1, Zooms.Length - 1);
        }
        else if (Controls.Pressed(Act.Down))
        {
            _zoom = Math.Max(_zoom - 1, 0);
        }

        if (Controls.Pressed(Act.Accept))
        {
            Viewer.Tile.Press();
        }

        if (Controls.Pressed(Act.Favourite))
        {
            Viewer.Channels.Step(1);
        }

        if (Controls.Pressed(Act.Bind) && Viewer.Backdrop.Blocked is null)
        {
            Viewer.Backdrop.Step(1);
        }

        Pan(seconds);
    }

    // The mouse drags the texture. The stick pushes the view.
    static void Pan(float seconds)
    {
        var moved = Cursor.Moved(out var dx, out var dy);
        if (_zoom == 0 || _framed is not { IsValid: true })
        {
            return;
        }

        var pixels = _scale * Draw.Scale;
        if (moved && (dx != 0 || dy != 0))
        {
            _centre -= new Vector2(dx, dy) / pixels;
        }

        var stick = Pad.Right;
        if (stick.Length() > StickDead)
        {
            _centre += stick * (StickPan * Draw.Scale / pixels * seconds);
        }
    }

    static void Render(MenuView view)
    {
        if (_shelf is not { } shelf)
        {
            return;
        }

        var shown = view.Opened * Kit.EaseOut((float)((Stopwatch.GetTimestamp() - _openedAt) / (double)Stopwatch.Frequency) / InSeconds);
        Draw.Place(0f, 0f, 1f);
        Draw.Fade(shown);
        Draw.Rect(0f, 0f, Draw.ScreenWidth, Draw.ScreenHeight, Shade);

        var accent = Viewer.Accent;
        var count = shelf.Count();
        var index = Math.Clamp(shelf.Index(), 0, Math.Max(0, count - 1));
        var entry = count > 0 ? shelf.At(index) : null;

        var boxX = Edge;
        var boxY = Edge + 20f;
        var boxWidth = Draw.ScreenWidth - 2f * Edge - Side - Gutter;
        var boxHeight = Draw.ScreenHeight - boxY - Edge - StripHeight - Gutter;

        Draw.Tracked("ASSETS  //  LIGHT TABLE", Edge, Edge - 6f, 10f, 3f, accent);
        Draw.TextRight(shelf.Title(), boxX + boxWidth, Edge - 8f, 12f, Viewer.Dim);

        Viewer.Back(boxX, boxY, boxWidth, boxHeight, Viewer.Backdrop.Index);
        if (entry is { Picture: { IsValid: true } picture })
        {
            Picture(picture, boxX, boxY, boxWidth, boxHeight, view.Time, accent);
        }
        else
        {
            Draw.Tracked("GONE. PRESS REFRESH ON THE MENU", boxX + 20f, boxY + 20f, 11f, 2f, Kit.Failed);
        }

        Kit.Corners(boxX - 1f, boxY - 1f, boxWidth + 2f, boxHeight + 2f, 16f, accent);
        Column(entry, index, count, Draw.ScreenWidth - Edge - Side, boxY, boxHeight + Gutter + StripHeight, accent);
        Strip(shelf, index, count, boxX, Draw.ScreenHeight - Edge - StripHeight, boxWidth, view.Time, accent);
        Draw.Fade(1f);
    }

    static void Picture(Texture picture, float boxX, float boxY, float boxWidth, float boxHeight, float time, Rgba accent)
    {
        Viewer.Showing(picture);
        var part = Viewer.Frame(picture, time, play: false);
        var copies = Viewer.Tile.On ? 2 : 1;
        var sheetWidth = part.Width * copies;
        var sheetHeight = part.Height * copies;

        if (!ReferenceEquals(picture, _framed))
        {
            _framed = picture;
            _centre = new Vector2(sheetWidth, sheetHeight) / 2f;
        }

        const float pad = 24f;
        var fit = MathF.Min((boxWidth - 2f * pad) / sheetWidth, (boxHeight - 2f * pad) / sheetHeight);
        var scale = fit * Zooms[_zoom];
        _scale = scale;
        if (_zoom == 0)
        {
            _centre = new Vector2(sheetWidth, sheetHeight) / 2f;
        }
        else
        {
            // The texture can't be pushed further than its edge reaching the middle.
            _centre = Vector2.Clamp(_centre, Vector2.Zero, new Vector2(sheetWidth, sheetHeight));
        }

        var left = boxX + boxWidth / 2f - _centre.X * scale;
        var top = boxY + boxHeight / 2f - _centre.Y * scale;

        if (!Viewer.Rested || !Viewer.Ready(picture))
        {
            if (!Kit.SpinningBat(boxX + boxWidth / 2f - 24f, boxY + boxHeight / 2f - 24f, 48f, time, 12f, accent))
            {
                Draw.Tracked("LOADING", boxX + boxWidth / 2f - 30f, boxY + boxHeight / 2f - 6f, 11f, 2f, Viewer.Dim);
            }

            return;
        }

        for (var row = 0; row < copies; row++)
        {
            for (var column = 0; column < copies; column++)
            {
                Viewer.Clipped(picture, part, left + column * part.Width * scale, top + row * part.Height * scale, scale, boxX, boxY, boxWidth, boxHeight);
            }
        }

        Frames(picture, left, top, part, scale, copies, boxX, boxY, boxWidth, boxHeight, accent);
        Grid(left, top, sheetWidth, sheetHeight, scale, boxX, boxY, boxWidth, boxHeight, accent);

        // The share is screen pixels to the texture's own, so 100% is every texel on one pixel.
        var share = $"{scale * Draw.Scale * 100f:0}%";
        var readout = _zoom == 0 ? $"ZOOM FIT   {share}" : $"ZOOM {ZoomNames[_zoom]}   {share}   CENTRE {(int)(_centre.X % part.Width)}, {(int)(_centre.Y % part.Height)}";
        var wide = Draw.TrackedWidth(readout, 10f, 2f);
        Draw.Rect(boxX + 10f, boxY + 10f, wide + 16f, 22f, Viewer.Panel);
        Draw.Tracked(readout, boxX + 18f, boxY + 16f, 10f, 2f, Viewer.Pale);
    }

    // A flipbook's frames, outlined on the whole sheet.
    static void Frames(Texture picture, float left, float top, (float U, float V, float Width, float Height) part, float scale, int copies,
        float boxX, float boxY, float boxWidth, float boxHeight, Rgba accent)
    {
        if (picture is not TextureFlipBook book || book.HorizontalImages < 1 || book.VerticalImages < 1
            || book.HorizontalImages * book.VerticalImages < 2)
        {
            return;
        }

        var line = accent with { A = 110 };
        var across = book.HorizontalImages * copies;
        var down = book.VerticalImages * copies;
        for (var i = 1; i < across; i++)
        {
            var x = left + i * part.Width * copies * scale / across;
            if (x > boxX && x < boxX + boxWidth)
            {
                Draw.Rect(x, MathF.Max(top, boxY), 1f, MathF.Min(top + part.Height * copies * scale, boxY + boxHeight) - MathF.Max(top, boxY), line);
            }
        }

        for (var i = 1; i < down; i++)
        {
            var y = top + i * part.Height * copies * scale / down;
            if (y > boxY && y < boxY + boxHeight)
            {
                Draw.Rect(MathF.Max(left, boxX), y, MathF.Min(left + part.Width * copies * scale, boxX + boxWidth) - MathF.Max(left, boxX), 1f, line);
            }
        }
    }

    // Lines between texels when they're big enough to tell apart.
    static void Grid(float left, float top, float width, float height, float scale, float boxX, float boxY, float boxWidth, float boxHeight, Rgba accent)
    {
        if (scale * Draw.Scale < GridFrom)
        {
            return;
        }

        var line = accent with { A = 45 };
        var x0 = MathF.Max(left, boxX);
        var y0 = MathF.Max(top, boxY);
        var x1 = MathF.Min(left + width * scale, boxX + boxWidth);
        var y1 = MathF.Min(top + height * scale, boxY + boxHeight);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        var drawn = 0;
        for (var column = MathF.Ceiling((x0 - left) / scale); left + column * scale < x1 && drawn < GridMost; column++, drawn++)
        {
            Draw.Rect(left + column * scale, y0, 1f, y1 - y0, line);
        }

        for (var row = MathF.Ceiling((y0 - top) / scale); top + row * scale < y1 && drawn < GridMost; row++, drawn++)
        {
            Draw.Rect(x0, top + row * scale, x1 - x0, 1f, line);
        }
    }

    static void Column(Textures.Entry? entry, int index, int count, float x, float y, float height, Rgba accent)
    {
        Draw.Rect(x, y, Side, height, Viewer.Panel);
        Draw.Rect(x, y, Side, 1f, accent with { A = 120 });
        Draw.Place(x, y, 1f);
        Viewer.Head("TEXTURE", count > 0 ? $"{index + 1} / {count}" : "", entry is { } named ? Textures.Name(named.Path) : "", Side, accent);

        var line = 56f;
        if (entry is { } picked)
        {
            // Each part of the path on its own line, so a long one isn't cut.
            foreach (var part in picked.Path.Split('.'))
            {
                if (!Draw.TrackedHead(part, 14f, line, 10f, 0.5f, Viewer.Dim, Side - 14f))
                {
                    Draw.Text(part, 14f, line, 10f, Viewer.Dim);
                }

                line += 14f;
            }

            if (picked.Picture.IsValid)
            {
                line += 12f;
                var facts = Facts.Of(picked.Picture);
                var width = Side - 28f;
                Viewer.Fact("SIZE", facts.Size, 14f, line, width, Viewer.Pale);
                Viewer.Fact("FORMAT", facts.Format, 14f, line += 20f, width, Viewer.Pale);
                Viewer.Fact("MEMORY", facts.Memory, 14f, line += 20f, width, Viewer.Pale);
                Viewer.Fact("COLOUR", facts.Colour, 14f, line += 20f, width, Viewer.Pale);
                Viewer.Fact("GROUP", facts.Group, 14f, line += 20f, width, Viewer.Pale);
                Viewer.Fact("STREAMS", facts.Streams, 14f, line += 20f, width, Viewer.Pale);
                line += 20f;
                var full = facts.MipsIn >= facts.MipsAll;
                Draw.Tracked("MIPS", 14f, line + 2f, 9f, 2f, Viewer.Dim);
                Draw.TextRight(facts.Mips, Side - 14f, line, 12f, full ? Viewer.Pale : Viewer.Amber);
                Viewer.Mips(78f, line + 3f, facts, full ? accent : Viewer.Amber);
                Viewer.Tags(facts, Side - 14f, line += 26f, accent);
            }
        }

        KeyGuide(height, accent);
        Draw.Place(0f, 0f, 1f);
    }

    static (string[] Keys, string Label)[] _lines = Array.Empty<(string[], string)>();
    static (Device, Pad.Style, bool) _linesFor;

    // Built again only when the device, the button names or TILE change.
    static (string[] Keys, string Label)[] Lines()
    {
        var wanted = (Controls.LastDevice, Pad.Look, Viewer.Tile.On);
        if (_lines.Length > 0 && wanted == _linesFor)
        {
            return _lines;
        }

        _linesFor = wanted;
        var pad = Controls.LastDevice != Device.Keyboard;
        string Key(Act act) => pad ? Controls.PadLabel(act) : Controls.KeyLabel(act);
        _lines = new (string[] Keys, string Label)[]
        {
            (new[] { Key(Act.Left), Key(Act.Right) }, "PREVIOUS  /  NEXT"),
            (new[] { Key(Act.Up), Key(Act.Down) }, "ZOOM IN  /  OUT"),
            (new[] { pad ? "RS" : "MOUSE" }, "MOVE AROUND"),
            (new[] { Key(Act.TabPrev), Key(Act.TabNext) }, "GROUP"),
            (new[] { Key(Act.Accept) }, Viewer.Tile.On ? "TILE OFF" : "TILE 2 X 2"),
            (new[] { Key(Act.Favourite) }, "CHANNELS"),
            (new[] { Key(Act.Bind) }, "BACKDROP"),
            (new[] { Key(Act.Back) }, "BACK TO THE MENU"),
        };

        return _lines;
    }

    static void KeyGuide(float height, Rgba accent)
    {
        var lines = Lines();

        const float lineHeight = 22f;
        var top = height - 14f - lines.Length * lineHeight;
        Draw.Rect(14f, top - 10f, Side - 28f, 1f, Viewer.Ghost with { A = 60 });
        var frame = accent with { A = 140 };
        for (var i = 0; i < lines.Length; i++)
        {
            var middle = top + (i + 0.5f) * lineHeight;
            var x = 14f;
            foreach (var name in lines[i].Keys)
            {
                x += Kit.Cap(name, x, middle, 10f, frame, Viewer.Pale) + 4f;
            }

            Draw.Tracked(lines[i].Label, 120f, middle - 5f, 10f, 2f, Viewer.Dim);
        }
    }

    // The group along the bottom, the picked one in the middle.
    static void Strip(Shelf shelf, int index, int count, float x, float y, float width, float time, Rgba accent)
    {
        const float wide = 120f;
        const float gap = 8f;
        var fits = Math.Max(1, (int)((width + gap) / (wide + gap)));
        var first = Math.Clamp(index - fits / 2, 0, Math.Max(0, count - fits));
        var used = Math.Min(fits, count);
        var left = x + (width - (used * (wide + gap) - gap)) / 2f;
        for (var i = 0; i < used; i++)
        {
            var at = first + i;
            var picked = at == index;
            var height = picked ? StripHeight : StripHeight - 16f;
            var top = y + StripHeight - height;
            var cell = left + i * (wide + gap);
            Viewer.Back(cell, top, wide, height, Viewer.Backdrop.Index);
            if (shelf.At(at) is { Picture: { IsValid: true } picture } && Viewer.Rested && Viewer.Ready(picture))
            {
                Viewer.Fit(picture, cell + 4f, top + 4f, wide - 8f, height - 8f, false, time, play: picked);
            }

            if (!picked)
            {
                Draw.Rect(cell, top, wide, height, Viewer.Panel with { A = 110 });
            }

            Draw.Frame(cell, top, wide, height, picked ? 2f : 1f, picked ? accent : Viewer.Ghost with { A = 110 });
        }
    }

    public static void Release()
    {
        if (Up)
        {
            Menu.Uncover();
        }

        Take(false);
        _shelf = null;
        _framed = null;
    }
}
