#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.Engine;
using BmSDK.Framework;

namespace Knightfall;

// Draws a readout onto a flat panel in the world, in true perspective. The canvas can carry a
// full 4x4 transform for everything drawn on it, so the theme lays the readout out flat as usual
// and the graphics card warps it onto the panel's four corners. The script side can only push a
// move, so the game's own native that pushes any matrix is called directly.
static unsafe class Hologram
{
    // The push that takes any matrix. It is the one direct call inside the native that backs
    // PushTranslationMatrix, and is only used when it starts with the bytes it should, so a
    // different game build is noticed instead of called blindly.

    static delegate* unmanaged<nint, Matrix4x4*, void> _push;
    static bool _looked;
    static Matrix4x4* _matrix;
    static bool _pushed;

    // Set each frame by Frame, used by Place. The canvas's own Project gives the middle of the
    // screen for everything in the callback we draw in, so points are projected here from the
    // camera instead.
    static Canvas? _canvas;
    static Vector3 _eye;
    static Vector3 _forward;
    static Vector3 _right;
    static Vector3 _up;
    static float _focal;
    static float _clipX;
    static float _clipY;

    public static bool Available
    {
        get
        {
            if (!_looked)
            {
                _looked = true;
                Find();
            }

            return _push != null;
        }
    }

    // The camera's place, its turn, and how wide it sees across, in degrees.
    public static void Frame(Canvas canvas, Vector3 eye, Rotator turn, float fieldOfView)
    {
        _canvas = canvas;
        _eye = eye;

        // The camera's own axes, ignoring roll, which the game's cameras don't use.
        _forward = turn.ToDirection();
        var yaw = new Rotator(0f, turn.Yaw, 0f).ToDirection();
        _right = new Vector3(-yaw.Y, yaw.X, 0f);
        _up = Vector3.Cross(_right, _forward) * -1f;
        if (Vector3.Dot(_up, Vector3.UnitZ) < 0f)
        {
            _up = -_up;
        }

        var half = Math.Clamp(fieldOfView, 10f, 170f) * MathF.PI / 360f;
        _focal = canvas.ClipX / 2f / MathF.Tan(half);
        _clipX = canvas.ClipX;
        _clipY = canvas.ClipY;
    }

    // Where a point in the world lands on the canvas, in pixels.
    static Vector2 Project(Canvas canvas, Vector3 point)
    {
        var toward = point - _eye;
        var depth = MathF.Max(Vector3.Dot(toward, _forward), 1f);
        return new Vector2(
            canvas.ClipX / 2f + Vector3.Dot(toward, _right) / depth * _focal,
            canvas.ClipY / 2f - Vector3.Dot(toward, _up) / depth * _focal);
    }

    // A flat panel in the world. Its left edge, halfway down, sits on the anchor, and across and
    // down are how far one unit of the layout goes in the world.
    public readonly record struct Panel(Vector3 Anchor, Vector3 Across, Vector3 Down);

    // How many pixels one unit of the world covers at this point, or 0 behind the eye.
    public static float Pixels(Vector3 point)
    {
        var depth = Vector3.Dot(point - _eye, _forward);
        return depth < 1f ? 0f : _focal / depth;
    }

    public static Vector3 Eye => _eye;

    // How many pixels a small turn of the view moves things on screen, per radian.
    public static float Focal => _focal;

    public static Vector2 Middle => new(_clipX / 2f, _clipY / 2f);

    // Where a point lands on screen with the camera of the last frame drawn. False behind it.
    public static bool Screen(Vector3 point, out Vector2 at)
    {
        var toward = point - _eye;
        var depth = Vector3.Dot(toward, _forward);
        if (depth < 1f || _focal <= 0f)
        {
            at = default;
            return false;
        }

        at = new Vector2(_clipX / 2f + Vector3.Dot(toward, _right) / depth * _focal, _clipY / 2f - Vector3.Dot(toward, _up) / depth * _focal);
        return true;
    }

    public static Vector3 Forward => _forward;

    public static Vector3 Right => _right;

    public static Vector3 Up => _up;

    // Which way a point lies from the middle of the screen, as a direction on the screen with y
    // pointing down. It works for points behind the camera too, for arrows on the screen's edge.
    public static Vector2 Direction(Vector3 point)
    {
        var toward = point - _eye;
        var across = new Vector2(Vector3.Dot(toward, _right), -Vector3.Dot(toward, _up));
        return across.LengthSquared() > 1e-6f ? Vector2.Normalize(across) : Vector2.UnitY;
    }

    // Lays the panel over a layout of this size, in units. False when it can't be seen, and
    // nothing is pushed then. Pop before placing the next one.
    public static bool Place(Panel panel, float width, float height)
    {
        if (_canvas is not { IsValid: true } canvas || !Available)
        {
            return false;
        }

        Pop();
        if (!Corners(panel, width, height, out var corners, out _))
        {
            return false;
        }

        // A panel right against the lens would stretch far past the screen, so it's left out.
        Span<Vector2> screen = stackalloc Vector2[4];
        for (var i = 0; i < 4; i++)
        {
            screen[i] = Project(canvas, corners[i]);
            if (MathF.Abs(screen[i].X - canvas.ClipX / 2f) > canvas.ClipX * 2f || MathF.Abs(screen[i].Y - canvas.ClipY / 2f) > canvas.ClipY * 2f)
            {
                return false;
            }
        }

        if (!Warp(screen, width * Draw.Scale, height * Draw.Scale, out var warp))
        {
            return false;
        }

        if (_matrix == null)
        {
            _matrix = (Matrix4x4*)NativeMemory.AlignedAlloc((nuint)sizeof(Matrix4x4), 16);
        }

        *_matrix = warp;
        _push(Marshal.ReadIntPtr(canvas.Ptr + Offsets.CanvasNative), _matrix);
        _pushed = true;
        Draw.Place(0f, 0f, 1f);
        return true;
    }

    // Faint lines from the point on the car to the panel's corners, drawn flat before the warp.
    public static void Beams(Panel panel, Vector3 emitter, float shown, float width, float height, Rgba color)
    {
        if (_canvas is not { IsValid: true } canvas || !Corners(panel, width, height, out var corners, out _) ||
            Vector3.Dot(emitter - _eye, _forward) < 10f)
        {
            return;
        }

        var from = Project(canvas, emitter) / Draw.Scale;
        Draw.Place(0f, 0f, 1f);
        Draw.Fade(shown * 0.8f);
        foreach (var corner in corners)
        {
            var to = Project(canvas, corner) / Draw.Scale;
            Draw.Line(from.X, from.Y, to.X, to.Y, color with { A = 60 });
        }

        Draw.Rect(from.X - 2f, from.Y - 2f, 4f, 4f, color);
        Draw.Fade(1f);
    }

    // Whether a panel of this size can be seen from the eye: in front of it and not behind a wall.
    public static bool Seen(Panel panel, float width, float height, Actor tracer)
    {
        return Corners(panel, width, height, out _, out var middle) && tracer.FastTrace(middle, _eye, Vector3.Zero, false);
    }

    // The readout's left edge, halfway down, sits on the anchor.
    static bool Corners(Panel panel, float width, float height, out Vector3[] corners, out Vector3 middle)
    {
        var across = panel.Across * width;
        var down = panel.Down * height;
        var topLeft = panel.Anchor - down / 2f;

        // Light shows from both sides. Seen from behind it would read backwards, so it is laid on
        // the other way round there.
        if (Vector3.Dot(Vector3.Cross(across, down), _eye - (topLeft + (across + down) / 2f)) <= 0f)
        {
            topLeft += across;
            across = -across;
        }

        corners = new[] { topLeft, topLeft + across, topLeft + across + down, topLeft + down };
        middle = topLeft + (across + down) / 2f;

        // Every corner in front of the camera itself, with a little room. Then every point of the
        // panel is in front too, and the warp can't flip and smear across the screen. Measured
        // along the view, not toward the panel, which a panel behind the camera also passes.
        foreach (var corner in corners)
        {
            if (Vector3.Dot(corner - _eye, _forward) < 10f)
            {
                return false;
            }
        }

        return true;
    }

    // Takes the last panel's warp off, so drawing goes flat again.
    public static void Pop()
    {
        if (_pushed && _canvas is { IsValid: true } canvas)
        {
            canvas.PopTransform();
        }

        _pushed = false;
    }

    public static void End()
    {
        Pop();
        _canvas = null;
    }

    public static void Release()
    {
        if (_matrix != null)
        {
            NativeMemory.AlignedFree(_matrix);
            _matrix = null;
        }
    }

    // The perspective warp that takes a width by height rectangle, in canvas pixels, onto four
    // screen points going round from the top left. The canvas multiplies row vectors, so the
    // divide sits in the last column.
    static bool Warp(ReadOnlySpan<Vector2> to, float width, float height, out Matrix4x4 warp)
    {
        warp = default;
        var (x0, y0, x1, y1, x2, y2, x3, y3) = (to[0].X, to[0].Y, to[1].X, to[1].Y, to[2].X, to[2].Y, to[3].X, to[3].Y);
        var sx = x0 - x1 + x2 - x3;
        var sy = y0 - y1 + y2 - y3;
        float a, b, d, e, g, h;
        if (MathF.Abs(sx) < 1e-4f && MathF.Abs(sy) < 1e-4f)
        {
            (a, b, d, e, g, h) = (x1 - x0, x3 - x0, y1 - y0, y3 - y0, 0f, 0f);
        }
        else
        {
            var dx1 = x1 - x2;
            var dx2 = x3 - x2;
            var dy1 = y1 - y2;
            var dy2 = y3 - y2;
            var det = dx1 * dy2 - dx2 * dy1;
            if (MathF.Abs(det) < 1e-6f)
            {
                return false;
            }

            g = (sx * dy2 - dx2 * sy) / det;
            h = (dx1 * sy - sx * dy1) / det;
            a = x1 - x0 + g * x1;
            b = x3 - x0 + h * x3;
            d = y1 - y0 + g * y1;
            e = y3 - y0 + h * y3;
        }

        warp = new Matrix4x4(
            a / width, d / width, 0f, g / width,
            b / height, e / height, 0f, h / height,
            0f, 0f, 1f, 0f,
            x0, y0, 0f, 1f);
        return true;
    }

    static void Find()
    {
        try
        {
            var function = Game.FindObject<Function>("Engine.Canvas.PushTranslationMatrix");
            if (function is null)
            {
                return;
            }

            var code = (byte*)Marshal.ReadIntPtr(function.Ptr + Offsets.FunctionNativeCode);
            var module = (byte*)MemUtil.GetBaseAddress();
            if (code < module)
            {
                return;
            }

            for (var i = 0; i < Offsets.MatrixPushSearch; i++)
            {
                if (code[i] != 0xE8)
                {
                    continue;
                }

                var target = code + i + 5 + *(int*)(code + i + 1);
                if (new ReadOnlySpan<byte>(target, Offsets.MatrixPushStart.Length).SequenceEqual(Offsets.MatrixPushStart))
                {
                    _push = (delegate* unmanaged<nint, Matrix4x4*, void>)target;
                    return;
                }
            }
        }
        finally
        {
            if (_push == null)
            {
                Log.Info("ON THE CAR can't find the game's canvas transform, so the speedometer stays flat.");
            }
        }
    }
}
