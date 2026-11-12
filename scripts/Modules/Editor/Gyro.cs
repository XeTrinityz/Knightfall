#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// The gyro on the selected object in EDITOR MODE. Arrows move it along its own facing, gimbal
// rings turn it, and the cube in the middle sizes it. Put the reticle on a handle, hold click and
// move the view: the camera stays still and the look input drives the handle.
static class Gyro
{
    public enum Handle
    {
        None,
        Forward,
        Right,
        Up,
        Turn,
        Tilt,
        Roll,
        Size,
    }

    // How big the gyro is on screen, and how close the reticle has to be to a handle, in pixels.
    const float ScreenRadius = 140f;
    const float Reach = 34f;

    const float ArrowFrom = 0.18f;
    const float RingSize = 0.75f;
    const float CubeSize = 0.11f;
    const int RingPoints = 48;

    // How far the view has to move, in pixels, to double or halve the size.
    const float SizeDoubling = 150f;

    static readonly Rgba Red = new(240, 80, 70);
    static readonly Rgba Green = new(90, 220, 110);
    static readonly Rgba Blue = new(80, 150, 255);
    static readonly Rgba White = new(255, 255, 255);

    public static Handle Hover { get; private set; }

    public static Handle Dragging { get; private set; }

    static Rotator _lockedLook;
    static Vector3 _axis;
    static Vector2 _along;
    static float _unitsPerPixel;
    static Vector2 _tangent;
    static float _pixelsPerRadian;
    static float _sizeAtStart;
    static float _sizeDrag;

    // A drag only counts once the view has moved this many pixels, so a shaky hand can't start one.
    const float Commit = 5f;
    static float _travel;

    public static bool Committed => Dragging != Handle.None && _travel >= Commit;

    // Where everything sits for this object this frame.
    readonly record struct Frame(Vector3 Middle, float Radius, Vector3 Forward, Vector3 Right, Vector3 Up, Vector3 Facing, Vector3 Side, Vector3 Lift);

    static Frame? FrameOf(Actor actor)
    {
        var middle = Things.ShapeOf(actor).Middle;
        var pixels = Hologram.Pixels(middle);
        if (pixels <= 0f)
        {
            return null;
        }

        // The game's turn, taken apart the way its rotation matrix builds it. Yaw spins round
        // straight up, pitch round the turned right side, roll round the pitched forward.
        var turn = actor.Rotation;
        var (sy, cy) = MathF.SinCos(turn.Yaw * MathF.PI / 180f);
        var (sp, cp) = MathF.SinCos(turn.Pitch * MathF.PI / 180f);
        var flat = new Vector3(cy, sy, 0f);
        var right = new Vector3(-sy, cy, 0f);
        var facing = new Vector3(cp * cy, cp * sy, sp);
        var lift = new Vector3(-sp * cy, -sp * sy, cp);
        return new Frame(middle, ScreenRadius / pixels, flat, right, Vector3.UnitZ, facing, right, lift);
    }

    // The two directions each ring lies in, in the order its angle grows as the game's value grows.
    static (Vector3 A, Vector3 B) RingPlane(Frame frame, Handle ring) => ring switch
    {
        Handle.Turn => (frame.Forward, frame.Right),
        Handle.Tilt => (frame.Forward, Vector3.UnitZ),
        _ => (frame.Side, -frame.Lift),
    };

    static Vector3 AxisOf(Frame frame, Handle arrow) => arrow switch
    {
        Handle.Forward => frame.Forward,
        Handle.Right => frame.Right,
        _ => frame.Up,
    };

    static Vector3 RingPoint(Frame frame, Handle ring, float angle)
    {
        var (a, b) = RingPlane(frame, ring);
        return frame.Middle + (a * MathF.Cos(angle) + b * MathF.Sin(angle)) * frame.Radius * RingSize;
    }

    public static bool Usable(Actor? actor) =>
        actor is not null && EditorMode.Active && Things.Fixed(actor) is null && Edits.Find(actor) is not { Removed: true };

    // Which handle the reticle is on, with the camera of the last frame drawn.
    public static void Look(Actor? actor)
    {
        Hover = Handle.None;
        if (Dragging != Handle.None || !Usable(actor) || FrameOf(actor!) is not { } frame)
        {
            return;
        }

        var aim = Hologram.Middle;
        var best = Reach;
        if (Hologram.Screen(frame.Middle, out var middle) && Vector2.Distance(middle, aim) < best)
        {
            best = Vector2.Distance(middle, aim);
            Hover = Handle.Size;
        }

        foreach (var arrow in new[] { Handle.Forward, Handle.Right, Handle.Up })
        {
            var axis = AxisOf(frame, arrow);
            if (Hologram.Screen(frame.Middle + axis * frame.Radius * ArrowFrom, out var from)
                && Hologram.Screen(frame.Middle + axis * frame.Radius, out var to)
                && Apart(aim, from, to) is var gap && gap < best)
            {
                best = gap;
                Hover = arrow;
            }
        }

        foreach (var ring in new[] { Handle.Turn, Handle.Tilt, Handle.Roll })
        {
            for (var i = 0; i < RingPoints; i++)
            {
                if (Hologram.Screen(RingPoint(frame, ring, Angle(i)), out var from)
                    && Hologram.Screen(RingPoint(frame, ring, Angle(i + 1)), out var to)
                    && Apart(aim, from, to) is var gap && gap < best)
                {
                    best = gap;
                    Hover = ring;
                }
            }
        }
    }

    static float Angle(int point) => MathF.Tau * point / RingPoints;

    static float Apart(Vector2 point, Vector2 from, Vector2 to)
    {
        var line = to - from;
        var along = line.LengthSquared() < 1e-4f ? 0f : Math.Clamp(Vector2.Dot(point - from, line) / line.LengthSquared(), 0f, 1f);
        return Vector2.Distance(point, from + line * along);
    }

    // Takes hold of the handle under the reticle. Everything about the drag is worked out now, so
    // the handle doesn't swing about as the object turns under it.
    public static bool Begin(Actor actor, PlayerController controller)
    {
        if (Hover == Handle.None || FrameOf(actor) is not { } frame || !Hologram.Screen(frame.Middle, out var middle))
        {
            return false;
        }

        switch (Hover)
        {
            case Handle.Forward or Handle.Right or Handle.Up:
                _axis = AxisOf(frame, Hover);
                if (!Hologram.Screen(frame.Middle + _axis * frame.Radius, out var tip) || Vector2.Distance(tip, middle) < 4f)
                {
                    return false;
                }

                var length = Vector2.Distance(tip, middle);
                _along = (tip - middle) / length;
                _unitsPerPixel = frame.Radius / length;
                break;

            case Handle.Turn or Handle.Tilt or Handle.Roll:
                // The ring's nearest point to the reticle, and which way it runs there on screen.
                var aim = Hologram.Middle;
                var closest = float.MaxValue;
                for (var i = 0; i < RingPoints; i++)
                {
                    const float Step = 0.05f;
                    if (Hologram.Screen(RingPoint(frame, Hover, Angle(i)), out var at)
                        && Hologram.Screen(RingPoint(frame, Hover, Angle(i) + Step), out var next)
                        && Vector2.Distance(at, aim) < closest && Vector2.Distance(next, at) > 0.01f)
                    {
                        closest = Vector2.Distance(at, aim);
                        _tangent = Vector2.Normalize(next - at);
                        _pixelsPerRadian = Vector2.Distance(next, at) / Step;
                    }
                }

                if (closest == float.MaxValue)
                {
                    return false;
                }

                break;

            case Handle.Size:
                _sizeAtStart = Edits.Find(actor)?.Size ?? 1f;
                _sizeDrag = 0f;
                break;
        }

        _lockedLook = controller.Rotation;
        _travel = 0f;
        Dragging = Hover;
        return true;
    }

    // Lets go of a handle that was never moved, leaving the view as it was.
    public static void Cancel(PlayerController? controller)
    {
        if (Dragging == Handle.None)
        {
            return;
        }

        Dragging = Handle.None;
        controller?.SetRotation(_lockedLook);
    }

    // The view's turn since last tick, as a movement on screen, drives the handle. The view is put
    // back each time so the camera stays where it was. True when the view moved.
    public static bool Drag(Actor actor, PlayerController controller)
    {
        if (Dragging == Handle.None)
        {
            return false;
        }

        var look = controller.Rotation;
        var yaw = Wrap(look.Yaw - _lockedLook.Yaw) * MathF.PI / 180f;
        var pitch = Wrap(look.Pitch - _lockedLook.Pitch) * MathF.PI / 180f;
        controller.SetRotation(_lockedLook);
        var moved = new Vector2(yaw, -pitch) * Hologram.Focal;
        if (moved.LengthSquared() < 1e-6f)
        {
            return false;
        }

        if (_travel < Commit)
        {
            _travel += moved.Length();
            return true;
        }

        var edit = Edits.Begin(actor);
        edit.Changed = true;
        switch (Dragging)
        {
            case Handle.Forward or Handle.Right or Handle.Up:
                var distance = Vector2.Dot(moved, _along) * _unitsPerPixel;
                Edits.Put(actor, actor.Location + _axis * distance, actor.Rotation);
                break;

            case Handle.Turn or Handle.Tilt or Handle.Roll:
                var degrees = Vector2.Dot(moved, _tangent) / MathF.Max(_pixelsPerRadian, 1f) * 180f / MathF.PI;
                var turn = actor.Rotation;
                turn = Dragging switch
                {
                    Handle.Turn => new Rotator(turn.Pitch, turn.Yaw + degrees, turn.Roll),
                    Handle.Tilt => new Rotator(turn.Pitch + degrees, turn.Yaw, turn.Roll),
                    _ => new Rotator(turn.Pitch, turn.Yaw, turn.Roll + degrees),
                };
                Edits.Put(actor, actor.Location, turn);
                break;

            case Handle.Size:
                // Up on screen grows it.
                _sizeDrag -= moved.Y;
                edit.Size = Math.Clamp(_sizeAtStart * MathF.Pow(2f, _sizeDrag / SizeDoubling), 0.1f, 5f);
                Edits.Apply(edit);
                break;
        }

        return true;
    }

    public static void End(Actor? actor)
    {
        if (Dragging == Handle.None)
        {
            return;
        }

        Dragging = Handle.None;
        if (actor is not null)
        {
            // The rows on the MOVE and CHANGE tabs catch up with the new size.
            Moving.Selected(actor);
        }
    }

    static float Wrap(float degrees)
    {
        degrees %= 360f;
        return degrees > 180f ? degrees - 360f : degrees < -180f ? degrees + 360f : degrees;
    }

    // Drawn after the box, while the hologram is framed for this view.
    public static void Render(Actor actor)
    {
        if (!Usable(actor) || FrameOf(actor) is not { } frame)
        {
            return;
        }

        Ring(frame, Handle.Turn, Blue);
        Ring(frame, Handle.Tilt, Green);
        Ring(frame, Handle.Roll, Red);
        Arrow(frame, Handle.Forward, Red);
        Arrow(frame, Handle.Right, Green);
        Arrow(frame, Handle.Up, Blue);

        var cube = frame.Radius * CubeSize;
        var lit = Lit(Handle.Size);
        Marks.Box(new Things.Shape(frame.Middle, frame.Forward * cube, frame.Right * cube, Vector3.UnitZ * cube), lit ? White : new Rgba(230, 230, 230), lit ? Marks.Style.Selected : Marks.Style.Full, 0f);
    }

    static bool Lit(Handle handle) => Dragging == handle || (Dragging == Handle.None && Hover == handle);

    // The handle a click would take stands out, and the rest step back while one is lit.
    static Rgba Shade(Handle handle, Rgba color)
    {
        if (Lit(handle))
        {
            return Rgba.Mix(color, White, 0.55f);
        }

        var anyLit = Dragging != Handle.None || Hover != Handle.None;
        return color with { A = (byte)(anyLit ? 110 : 210) };
    }

    // What the reticle would take, shown under it.
    public static string? Name => (Dragging != Handle.None ? Dragging : Hover) switch
    {
        Handle.Forward => "MOVE FORWARD",
        Handle.Right => "MOVE RIGHT",
        Handle.Up => "MOVE UP",
        Handle.Turn => "TURN",
        Handle.Tilt => "TILT",
        Handle.Roll => "ROLL",
        Handle.Size => "SIZE",
        _ => null,
    };

    static void Arrow(Frame frame, Handle arrow, Rgba color)
    {
        const float Thick = 12f;
        const float Head = 12f;
        var axis = AxisOf(frame, arrow);
        var from = frame.Middle + axis * frame.Radius * ArrowFrom;
        var to = frame.Middle + axis * frame.Radius;
        var middle = (from + to) / 2f;
        var pixels = Hologram.Pixels(middle);
        var side = Vector3.Cross(axis, Vector3.Normalize(Hologram.Eye - middle));
        if (pixels <= 0f || side.Length() < 0.15f)
        {
            return;
        }

        var unit = Draw.Scale / pixels;
        var length = Vector3.Distance(from, to);
        var width = MathF.Max(Head + 4f, length / unit);
        var panel = new Hologram.Panel(from, axis * (length / width), Vector3.Normalize(side) * unit);
        if (!Hologram.Place(panel, width, Thick))
        {
            return;
        }

        var shade = Shade(arrow, color);
        var shaft = Lit(arrow) ? 5f : 3f;
        Draw.Rect(0f, (Thick - shaft) / 2f, width - Head, shaft, shade);
        Draw.Triangle(width - Head, 0f, width, Thick / 2f, width - Head, Thick, shade);
    }

    static void Ring(Frame frame, Handle ring, Rgba color)
    {
        var (a, b) = RingPlane(frame, ring);
        var radius = frame.Radius * RingSize;
        var pixels = Hologram.Pixels(frame.Middle);
        if (pixels <= 0f)
        {
            return;
        }

        var size = MathF.Max(8f, 2f * radius * pixels / Draw.Scale);
        var panel = new Hologram.Panel(frame.Middle - a * radius, a * (2f * radius / size), -b * (2f * radius / size));
        if (!Hologram.Place(panel, size, size))
        {
            return;
        }

        var shade = Shade(ring, color);
        var half = size / 2f;
        for (var i = 0; i < RingPoints; i++)
        {
            var (s0, c0) = MathF.SinCos(Angle(i));
            var (s1, c1) = MathF.SinCos(Angle(i + 1));
            Draw.Line(half + c0 * half, half + s0 * half, half + c1 * half, half + s1 * half, shade);
            var inner = half - 1.2f;
            Draw.Line(half + c0 * inner, half + s0 * inner, half + c1 * inner, half + s1 * inner, shade);
            if (Lit(ring))
            {
                inner = half - 2.4f;
                Draw.Line(half + c0 * inner, half + s0 * inner, half + c1 * inner, half + s1 * inner, shade);
            }
        }
    }
}
