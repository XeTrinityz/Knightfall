#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK.Engine;

namespace Knightfall;

// Marks in the world for what the highlighted row would do, drawn where it would happen before it
// is pressed. A row's preview fills them from the same plan its press follows, so the mark and the
// press always agree.
static class WorldMarks
{
    public static readonly Toggle On = new(
        "settings.general.world_markers", "WORLD MARKERS",
        "While a row that moves or puts something somewhere, or acts on who you aim at, is highlighted, the world shows where it will happen and on whom. Red shows why it can't.",
        on: true)
    {
        Preference = true,
    };

    public enum Shape
    {
        Stand,
        Car,
        Air,
        Pin,
        Spot,
        Ghost,
        Target,
        Cross,
    }

    // One mark. At is the floor, or the point in the air for Air. Facing is in degrees, NaN for
    // any way. Height is how tall what stands there is, or how far above the ground an Air mark
    // is. Width is a person's or a spot's radius or a car's half width, Length a car's half
    // length. From is where the trail starts. Tone says whether it would work, do nothing or fail.
    // Box is where a Ghost goes, On what a Target follows.
    public readonly record struct Sign(Shape Shape, Vector3 At, float Facing, float Height, float Width, float Length, string Title, string Note,
        Vector3? From, OutcomeKind Tone = OutcomeKind.Done, Things.Shape Box = default, Actor? On = null);

    // Who an aimed row would act on, or why it can't. A refusal can still have someone in mind,
    // like an enemy who is already a bodyguard.
    public readonly record struct Picked(Outcome? Refused, Actor? Who);

    // Where a person or a car would stand.
    public static Sign Stand(Pawn mover, Vector3 floor, float facing, string title, string note)
    {
        var feet = mover.Location - Vector3.UnitZ * Teleport.Lift(mover);
        if (mover is Vehicle)
        {
            var shape = Things.ShapeOf(mover);
            return new Sign(Shape.Car, floor, facing, shape.Z.Length() * 2f, shape.Y.Length(), shape.X.Length(), title, note, feet);
        }

        var body = mover.CylinderComponent is { IsValid: true } pole ? pole : null;
        return new Sign(Shape.Stand, floor, facing, (body?.CollisionHeight ?? 90f) * 2f, body?.CollisionRadius ?? 35f, 0f, title, note, feet);
    }

    // A point in the air, this high above the ground.
    public static Sign Air(Pawn mover, Vector3 at, float drop, string title, string note) =>
        new(Shape.Air, at, 0f, drop, 0f, 0f, title, note, mover.Location - Vector3.UnitZ * Teleport.Lift(mover));

    // A marker on the map, like the waypoint.
    public static Sign Pin(Vector3 at, string title, string note) => new(Shape.Pin, at, 0f, 0f, 0f, 0f, title, note, null);

    // A spot on the floor something would be put, like a spawn. Facing is NaN when it's random.
    public static Sign Spot(Vector3 floor, float facing, float radius, string title, string note) =>
        new(Shape.Spot, floor, facing, 0f, radius, 0f, title, note, null);

    // A box where an object would go, with a line from where its middle is now when it moves.
    public static Sign Ghost(Things.Shape box, Vector3? from, string title, string note) =>
        new(Shape.Ghost, box.Middle, 0f, 0f, 0f, 0f, title, note, from, Box: box);

    // Brackets that follow someone or something an aimed row would act on.
    public static Sign Target(Actor who, string title, string note, OutcomeKind tone = OutcomeKind.Done) =>
        new(Shape.Target, who.Location, 0f, 0f, 0f, 0f, title, note, null, tone, On: who);

    // Why the row can't do it, at the spot it tried. Nothing to do yet is told apart from failing.
    public static Sign Refused(Outcome why, Vector3 at, string title) =>
        new(Shape.Cross, at, 0f, 0f, 0f, 0f, title, why.Text, null, why.Kind);

    // The preview of an aimed row: brackets on who it picks, or a cross where you aim and why.
    public static Action<List<Sign>> Picks(Func<Picked> pick, string title, string note) => signs =>
    {
        var picked = pick();
        if (picked.Who is { } who)
        {
            signs.Add(Target(who, title, picked.Refused?.Text ?? note, picked.Refused?.Kind ?? OutcomeKind.Done));
        }
        else if (picked.Refused is { } why)
        {
            signs.Add(Refused(why, AimedAt(), title));
        }
    };

    // Where the crosshair is on something solid, or a little way out when it's on nothing.
    public static Vector3 AimedAt()
    {
        const float Reach = 20000f;
        const float Nothing = 3000f;
        if (Crosshair.Surface(Reach, out var hit))
        {
            return hit.Spot;
        }

        return Crosshair.View(out var eye, out var way) ? eye + way * Nothing : default;
    }

    // The plan is worked out again every frame the view or the player moves, which costs well
    // under a millisecond, and now and then while still, since the world moves too.
    const double Latest = 0.5;
    const double FadeIn = 0.15;
    const float Moved = 1f;
    const float Turned = 0.999999f;

    static readonly List<Sign> Signs = new();
    static Feature? _row;
    static string _value = "";
    static long _madeAt;
    static long _since;
    static Vector3 _eye;
    static Vector3 _way;
    static Vector3 _mover;

    static readonly Rgba Ink = new(8, 10, 14);
    static readonly Rgba White = new(255, 255, 255);
    static readonly Rgba Red = new(255, 84, 84);
    static readonly Rgba Grey = new(190, 196, 204);

    const float TagSize = 8f;
    const float TagSpacing = 1.5f;
    const float TagHeight = 22f;
    const int RingSides = 40;
    const int TrailDashes = 22;

    // Works the highlighted row's marks out again when it's due. True when there's something to
    // draw, from a row or from a villain aiming.
    public static bool Ready() => On.On && (Plan() | Villains.Marking());

    static bool Plan()
    {
        var row = Menu.Showing ? Menu.Current.Focus : null;
        if (row?.Preview is not { } preview || row.Blocked is not null || !Crosshair.View(out var eye, out var way))
        {
            _row = null;
            Signs.Clear();
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        var age = (now - _madeAt) / (double)Stopwatch.Frequency;
        var at = Teleport.Mover is { } mover ? mover.Location : default;
        var moved = Vector3.DistanceSquared(eye, _eye) > Moved * Moved || Vector3.Dot(way, _way) < Turned || Vector3.DistanceSquared(at, _mover) > Moved * Moved;
        var fresh = !ReferenceEquals(row, _row);

        // A slider like BLINK RANGE moves the marks as soon as its value does.
        var value = row.ValueText;
        if (!fresh && !moved && value == _value && age < Latest)
        {
            return Signs.Count > 0;
        }

        if (fresh)
        {
            _since = now;
        }

        _row = row;
        _value = value;
        _madeAt = now;
        (_eye, _way, _mover) = (eye, way, at);
        Signs.Clear();
        try
        {
            preview(Signs);
        }
        catch (Exception error)
        {
            Signs.Clear();
            Log.Error("marks." + row.Id, $"The world marker for {row.Plain} failed", error);
        }

        return Signs.Count > 0;
    }

    // Drawn flat on the screen by the editor's mark pass, once the camera is framed.
    public static void Render(float time)
    {
        if (!On.On)
        {
            return;
        }

        Hologram.Pop();
        Draw.Place(0f, 0f, 1f);
        if (Villains.Marking())
        {
            Villains.Marks(time);
        }

        if (Signs.Count == 0)
        {
            return;
        }

        Draw.Fade((float)Math.Clamp((Stopwatch.GetTimestamp() - _since) / (double)Stopwatch.Frequency / FadeIn, 0.0, 1.0));
        var accent = Look.AccentOr(Theme.Current.Accent);
        foreach (var sign in Signs)
        {
            var color = sign.Tone switch
            {
                OutcomeKind.Failed => Red,
                OutcomeKind.Unchanged => Grey,
                _ => accent,
            };

            if (sign.From is { } from)
            {
                Trail(from, sign.At, color with { A = 140 }, time);
            }

            switch (sign.Shape)
            {
                case Shape.Stand:
                    Standing(sign, color, time);
                    break;
                case Shape.Car:
                    Parked(sign, color, time);
                    break;
                case Shape.Air:
                    Hanging(sign, color, time);
                    break;
                case Shape.Pin:
                    Pinned(sign, color, time);
                    break;
                case Shape.Spot:
                    Placed(sign, color, time);
                    break;
                case Shape.Ghost:
                    Marks.Box(sign.Box, color, Marks.Style.Full, time);
                    Tag(sign.Box.Middle + Vector3.UnitZ * (sign.Box.Half.Z + 25f), sign.Title, sign.Note, color);
                    break;
                case Shape.Target:
                    Bracketed(sign, color, time);
                    break;
                default:
                    Cross(sign, color, time);
                    break;
            }
        }

        Draw.Fade(1f);
    }

    // A ring on the floor the size of the body, a slower dashed ring inside it, the way they'll
    // face, and a thin post as tall as they are, so the size reads at any distance.
    static void Standing(Sign sign, Rgba color, float time)
    {
        var pulse = 1f + 0.05f * MathF.Sin(time * 4f);
        var radius = (sign.Width + 15f) * pulse;
        var floor = sign.At + Vector3.UnitZ * 3f;
        Ring(floor, radius, color, 0f, false);
        Ring(floor, radius * 0.6f, color with { A = 150 }, time * 1.5f, true);
        Facing(floor, sign.Facing, radius, color);

        var top = floor + Vector3.UnitZ * sign.Height;
        Segment(floor, top, color with { A = 80 });
        var across = Hologram.Right * 14f;
        Segment(top - across, top + across, color with { A = 200 });
        Tag(top + Vector3.UnitZ * 25f, sign.Title, sign.Note, color);
    }

    // The car's footprint as it would stand, with its nose marked.
    static void Parked(Sign sign, Rgba color, float time)
    {
        var (sin, cos) = MathF.SinCos(sign.Facing * MathF.PI / 180f);
        var ahead = new Vector3(cos, sin, 0f);
        var side = new Vector3(-sin, cos, 0f);
        var floor = sign.At + Vector3.UnitZ * 4f;
        var length = MathF.Max(sign.Length, 100f);
        var width = MathF.Max(sign.Width, 60f);
        Span<Vector3> corners = stackalloc Vector3[]
        {
            floor + ahead * length + side * width,
            floor + ahead * length - side * width,
            floor - ahead * length - side * width,
            floor - ahead * length + side * width,
        };

        for (var i = 0; i < 4; i++)
        {
            Segment(corners[i], corners[(i + 1) % 4], color);
        }

        var inset = 0.8f + 0.04f * MathF.Sin(time * 4f);
        for (var i = 0; i < 4; i++)
        {
            Segment(floor + (corners[i] - floor) * inset, floor + (corners[(i + 1) % 4] - floor) * inset, color with { A = 70 });
        }

        Facing(floor, sign.Facing, length, color);
        Tag(floor + Vector3.UnitZ * (sign.Height + 30f), sign.Title, sign.Note, color);
    }

    // A point in the air with a line falling to the ground under it.
    static void Hanging(Sign sign, Rgba color, float time)
    {
        Diamond(sign.At, 7f, color);
        if (sign.Height > 1f)
        {
            var ground = sign.At - Vector3.UnitZ * sign.Height;
            Dashed(sign.At, ground, color with { A = 120 }, 16, time);
            Ring(ground + Vector3.UnitZ * 3f, 35f, color with { A = 150 }, 0f, false);
        }

        Tag(sign.At + Vector3.UnitZ * 40f, sign.Title, sign.Note, color);
    }

    // A ring and a beam rising from it, fading as it goes up.
    static void Pinned(Sign sign, Rgba color, float time)
    {
        var floor = sign.At + Vector3.UnitZ * 3f;
        Ring(floor, 45f, color, time * 0.8f, true);
        Ring(floor, 20f * (1f + 0.15f * MathF.Sin(time * 4f)), color, 0f, false);
        const int Steps = 8;
        const float Rise = 900f;
        for (var i = 0; i < Steps; i++)
        {
            var alpha = (byte)(220 * (1f - i / (float)Steps));
            Segment(floor + Vector3.UnitZ * (Rise * i / Steps), floor + Vector3.UnitZ * (Rise * (i + 1) / Steps), color with { A = alpha });
        }

        Tag(floor + Vector3.UnitZ * 140f, sign.Title, sign.Note, color);
    }

    // A ring where something would be put, which way it faces, and the tag on the first only.
    static void Placed(Sign sign, Rgba color, float time)
    {
        var floor = sign.At + Vector3.UnitZ * 3f;
        Ring(floor, sign.Width, color, 0f, false);
        Ring(floor, sign.Width * 0.45f * (1f + 0.08f * MathF.Sin(time * 4f)), color with { A = 140 }, 0f, false);
        if (!float.IsNaN(sign.Facing))
        {
            Facing(floor, sign.Facing, sign.Width, color);
        }

        if (sign.Title.Length > 0)
        {
            Tag(floor + Vector3.UnitZ * 60f, sign.Title, sign.Note, color);
        }
    }

    // Corner brackets round what an aimed row picks, following it as it moves.
    static void Bracketed(Sign sign, Rgba color, float time)
    {
        if (!Things.Alive(sign.On))
        {
            return;
        }

        var shape = Things.ShapeOf(sign.On!);
        Marks.Box(shape, color, Marks.Style.Corners, time);
        Tag(shape.Middle + Vector3.UnitZ * (shape.Half.Z + 25f), sign.Title, sign.Note, color);
    }

    // A cross where it would have gone, with the reason. Nothing to do shows without the cross.
    static void Cross(Sign sign, Rgba color, float time)
    {
        Ring(sign.At + Vector3.UnitZ * 3f, 30f, color with { A = 160 }, 0f, false);
        if (sign.Tone == OutcomeKind.Failed && Screen(sign.At + Vector3.UnitZ * 3f, out var at))
        {
            const float Arm = 7f;
            Draw.Line(at.X - Arm, at.Y - Arm, at.X + Arm, at.Y + Arm, color);
            Draw.Line(at.X - Arm, at.Y + Arm, at.X + Arm, at.Y - Arm, color);
        }

        Tag(sign.At + Vector3.UnitZ * 60f, sign.Title, sign.Note, color);
    }

    // A small arrow just outside a ring, pointing the way they'll face.
    static void Facing(Vector3 middle, float facing, float radius, Rgba color)
    {
        var (sin, cos) = MathF.SinCos(facing * MathF.PI / 180f);
        var ahead = new Vector3(cos, sin, 0f);
        var side = new Vector3(-sin, cos, 0f);
        if (Screen(middle + ahead * (radius + 30f), out var tip) && Screen(middle + ahead * (radius + 8f) + side * 12f, out var left)
            && Screen(middle + ahead * (radius + 8f) - side * 12f, out var right))
        {
            Draw.Triangle(tip.X, tip.Y, left.X, left.Y, right.X, right.Y, color);
        }
    }

    // An arc from where they are to where they'd go, dashes marching toward the end.
    static void Trail(Vector3 from, Vector3 to, Rgba color, float time)
    {
        var lift = Math.Clamp(Vector3.Distance(from, to) * 0.2f, 50f, 600f);
        var shift = time * 2f % 1f;
        for (var i = 0; i < TrailDashes; i++)
        {
            var a = (i + shift) / TrailDashes;
            var b = MathF.Min((i + shift + 0.5f) / TrailDashes, 1f);
            if (a < 1f)
            {
                Segment(Arc(from, to, lift, a), Arc(from, to, lift, b), color);
            }
        }
    }

    static Vector3 Arc(Vector3 from, Vector3 to, float lift, float t) =>
        Vector3.Lerp(from, to, t) + Vector3.UnitZ * (lift * 4f * t * (1f - t) + 3f);

    public static void Ring(Vector3 middle, float radius, Rgba color, float turn, bool dashed)
    {
        var last = Vector2.Zero;
        var had = false;
        for (var i = 0; i <= RingSides; i++)
        {
            var angle = turn + i * MathF.Tau / RingSides;
            var seen = Screen(middle + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * radius, out var here);
            if (seen && had && (!dashed || i % 2 == 0))
            {
                Draw.Line(last.X, last.Y, here.X, here.Y, color);
            }

            last = here;
            had = seen;
        }
    }

    static void Dashed(Vector3 from, Vector3 to, Rgba color, int dashes, float time)
    {
        var shift = time * 2f % 1f;
        for (var i = 0; i < dashes; i++)
        {
            var a = (i + shift) / dashes;
            if (a < 1f)
            {
                Segment(Vector3.Lerp(from, to, a), Vector3.Lerp(from, to, MathF.Min((i + shift + 0.5f) / dashes, 1f)), color);
            }
        }
    }

    // Flat on the screen, so it stays the same size at any distance.
    static void Diamond(Vector3 point, float size, Rgba color)
    {
        if (!Screen(point, out var at))
        {
            return;
        }

        Draw.Line(at.X, at.Y - size, at.X + size, at.Y, color);
        Draw.Line(at.X + size, at.Y, at.X, at.Y + size, color);
        Draw.Line(at.X, at.Y + size, at.X - size, at.Y, color);
        Draw.Line(at.X - size, at.Y, at.X, at.Y - size, color);
    }

    public static void Segment(Vector3 a, Vector3 b, Rgba color)
    {
        if (Screen(a, out var p) && Screen(b, out var q))
        {
            Draw.Line(p.X, p.Y, q.X, q.Y, color);
        }
    }

    // Where a point lands on the screen, in layout units.
    public static bool Screen(Vector3 point, out Vector2 at)
    {
        if (!Hologram.Screen(point, out var pixels))
        {
            at = default;
            return false;
        }

        at = pixels / Draw.Scale;
        return true;
    }

    // A small card over a point, with a pointer down to it.
    static void Tag(Vector3 point, string title, string note, Rgba color)
    {
        if (!Screen(point, out var spot))
        {
            return;
        }

        var width = MathF.Max(Draw.TrackedWidth(title, TagSize, TagSpacing), Draw.TextWidth(note, TagSize)) + 12f;
        var top = spot.Y - 10f - TagHeight;
        var left = spot.X - width / 2f;
        Draw.Rect(left, top, width, TagHeight, Ink with { A = 170 });
        Draw.Rect(left, top, 2f, TagHeight, color);
        Draw.Tracked(title, left + 6f, top + 3f, TagSize, TagSpacing, White);
        Draw.Text(note, left + 6f, top + 12f, TagSize, color);
        Draw.Triangle(spot.X - 4f, spot.Y - 9f, spot.X + 4f, spot.Y - 9f, spot.X, spot.Y - 3f, color);
    }
}
