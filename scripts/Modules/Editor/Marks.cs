#nullable enable

using System.Numerics;
using BmSDK.Engine;

namespace Knightfall;

// The editor's marks in the world: a box round the selected object and round what SELECT AIMED
// would take, the size and turn of the model itself. Each face is laid out flat and warped onto
// its place in the world, the way the Batmobile's speedometer is, so the box stands in true
// perspective.
static class Marks
{
    // A face is never laid out smaller than this many units, so its warp stays steady.
    const float Smallest = 4f;

    const float CardHeight = 24f;
    const float CardGap = 6f;
    const float TitleSize = 11f;
    const float NoteSize = 8f;

    static readonly Rgba White = new(255, 255, 255);
    static readonly Rgba Ink = new(8, 10, 14);

    // The line under the name on the card, one for each kind and mix of states.
    static readonly Dictionary<int, string> Notes = new();

    public static void Render(Canvas canvas)
    {
        var selected = Editor.Selected;
        var preview = Editor.Preview;
        if (ReferenceEquals(preview, selected))
        {
            preview = null;
        }

        var reticle = EditorMode.Active && !Menu.Showing;
        if ((selected is null && preview is null && !reticle && !Markings.Showing && !Threats.Showing && !GuardMarks.Showing && !Sidekick.ShowingOrders) || World.Paused || World.Controller is not { } controller || !Draw.Begin(canvas))
        {
            return;
        }

        if (reticle)
        {
            Reticle(canvas);
        }

        if (!Hologram.Available)
        {
            Draw.End();
            return;
        }

        try
        {
            controller.GetPlayerViewPoint(out var eye, out var turn);
            var fieldOfView = controller.PlayerCamera is { IsValid: true } camera ? camera.GetFOVAngle() : controller.GetFOVAngle();
            Hologram.Frame(canvas, eye, turn, fieldOfView);

            var time = (float)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency % 3600.0);
            Markings.Render(time);
            if (preview is not null)
            {
                Box(Things.ShapeOf(preview), White, Style.Full, time);
            }

            if (selected is not null)
            {
                var accent = Look.AccentOr(Theme.Current.Accent);
                var shape = Things.ShapeOf(selected);
                Box(shape, accent, Style.Selected, time);
                Card(selected, shape, accent);
                Gyro.Render(selected);
            }

            // Last, since they draw flat on the screen and leave the hologram's framing behind.
            GuardMarks.Render(time);
            Threats.Render(time);
            Sidekick.RenderOrders(time);
        }
        finally
        {
            Hologram.End();
            Draw.End();
        }
    }

    // The middle of the screen, where a click in EDITOR MODE picks from.
    static void Reticle(Canvas canvas)
    {
        var x = canvas.ClipX / 2f / Draw.Scale;
        var y = canvas.ClipY / 2f / Draw.Scale;
        var color = Look.AccentOr(Theme.Current.Accent);
        Draw.Place(0f, 0f, 1f);
        Draw.Rect(x - 1.5f, y - 1.5f, 3f, 3f, Ink with { A = 160 });
        Draw.Rect(x - 1f, y - 1f, 2f, 2f, color);
        Draw.Rect(x - 9f, y - 0.5f, 5f, 1f, color with { A = 170 });
        Draw.Rect(x + 4f, y - 0.5f, 5f, 1f, color with { A = 170 });
        Draw.Rect(x - 0.5f, y - 9f, 1f, 5f, color with { A = 170 });
        Draw.Rect(x - 0.5f, y + 4f, 1f, 5f, color with { A = 170 });
        if (Gyro.Name is { } handle)
        {
            const float Size = 8f;
            const float Spacing = 2f;
            var width = Draw.TrackedWidth(handle, Size, Spacing);
            Draw.Rect(x - width / 2f - 5f, y + 14f, width + 10f, Size + 6f, Ink with { A = 170 });
            Draw.Tracked(handle, x - width / 2f, y + 17f, Size, Spacing, White);
        }
    }

    // How a box is drawn. A full box has every edge, the selected one a fill as well, and corners
    // only the corner ticks on the near faces.
    public enum Style
    {
        Selected,
        Full,
        Corners,
    }

    public static void Box(Things.Shape shape, Rgba color, Style look, float time, float strength = 1f)
    {
        var pulse = look == Style.Selected ? 0.8f + 0.2f * MathF.Sin(time * 4f) : 1f;
        strength *= pulse;
        Span<Vector3> sides = stackalloc Vector3[] { shape.X, shape.Y, shape.Z };

        // Which of the six faces turn toward the camera, by axis and side.
        Span<bool> front = stackalloc bool[6];
        for (var axis = 0; axis < 3; axis++)
        {
            front[axis * 2] = Vector3.Dot(sides[axis], Hologram.Eye - (shape.Middle + sides[axis])) > 0f;
            front[axis * 2 + 1] = Vector3.Dot(-sides[axis], Hologram.Eye - (shape.Middle - sides[axis])) > 0f;
        }

        // The selected box gets a faint fill on its near faces, laid onto each face in the world.
        if (look == Style.Selected)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var (a, b) = (sides[(axis + 1) % 3], sides[(axis + 2) % 3]);
                for (var side = 0; side < 2; side++)
                {
                    if (front[axis * 2 + side])
                    {
                        var sign = side == 0 ? 1f : -1f;
                        Fill(shape.Middle + sides[axis] * sign, a, b, color with { A = Alpha(20 * strength) });
                    }
                }
            }
        }

        // Each of the twelve edges, as a line between its corners as the camera sees them. An
        // edge is near when either face it joins turns toward the camera.
        Hologram.Pop();
        Draw.Place(0f, 0f, 1f);
        var thin = MathF.Max(1f, MathF.Round(Draw.Scale));
        for (var along = 0; along < 3; along++)
        {
            var (j, k) = ((along + 1) % 3, (along + 2) % 3);
            for (var corner = 0; corner < 4; corner++)
            {
                var sj = (corner & 1) == 0 ? 1f : -1f;
                var sk = (corner & 2) == 0 ? 1f : -1f;
                var near = front[j * 2 + (sj > 0f ? 0 : 1)] || front[k * 2 + (sk > 0f ? 0 : 1)];
                if (!near && look == Style.Corners)
                {
                    continue;
                }

                var middle = shape.Middle + sides[j] * sj + sides[k] * sk;
                var from = middle - sides[along];
                var to = middle + sides[along];
                if (look != Style.Corners)
                {
                    var faint = near ? (look == Style.Selected ? 170 : 130) : (look == Style.Selected ? 70 : 45);
                    Line(from, to, color with { A = Alpha(faint * strength) }, thin);
                }

                if (near)
                {
                    Ticks(from, to, color with { A = Alpha(255 * strength) }, thin + 1f);
                }
            }
        }
    }

    // A face's fill, warped onto the face.
    static void Fill(Vector3 middle, Vector3 across, Vector3 down, Rgba color)
    {
        var pixels = Hologram.Pixels(middle);
        if (pixels <= 0f)
        {
            return;
        }

        var perUnit = Draw.Scale / pixels;
        var width = MathF.Max(Smallest, 2f * across.Length() / perUnit);
        var height = MathF.Max(Smallest, 2f * down.Length() / perUnit);
        if (Hologram.Place(new Hologram.Panel(middle - across, across * 2f / width, -down * 2f / height), width, height))
        {
            Draw.Rect(0f, 0f, width, height, color);
        }
    }

    // Bright short pieces at both ends of an edge, so each corner the camera can see reads as a
    // bracket.
    static void Ticks(Vector3 from, Vector3 to, Rgba color, float thick)
    {
        if (!Clip(ref from, ref to) || !Hologram.Screen(from, out var a) || !Hologram.Screen(to, out var b))
        {
            return;
        }

        var length = Vector2.Distance(a, b);
        if (length < 1f)
        {
            return;
        }

        var part = MathF.Min(0.3f, TickPixels * Draw.Scale / length);
        Lines(a, a + (b - a) * part, color, thick);
        Lines(b, b + (a - b) * part, color, thick);
    }

    const float TickPixels = 12f;

    static void Line(Vector3 from, Vector3 to, Rgba color, float thick)
    {
        if (Clip(ref from, ref to) && Hologram.Screen(from, out var a) && Hologram.Screen(to, out var b))
        {
            Lines(a, b, color, thick);
        }
    }

    // Cuts the part of an edge behind the camera off, so a box you stand inside still draws.
    static bool Clip(ref Vector3 from, ref Vector3 to)
    {
        const float Nearest = 10f;
        var a = Vector3.Dot(from - Hologram.Eye, Hologram.Forward);
        var b = Vector3.Dot(to - Hologram.Eye, Hologram.Forward);
        if (a < Nearest && b < Nearest)
        {
            return false;
        }

        if (a < Nearest)
        {
            from = Vector3.Lerp(from, to, (Nearest - a) / (b - a));
        }
        else if (b < Nearest)
        {
            to = Vector3.Lerp(to, from, (Nearest - b) / (a - b));
        }

        return true;
    }

    // A line in screen pixels, made thicker with lines laid side by side.
    static void Lines(Vector2 a, Vector2 b, Rgba color, float thick)
    {
        var way = b - a;
        if (way.LengthSquared() < 0.01f)
        {
            return;
        }

        var side = Vector2.Normalize(new Vector2(-way.Y, way.X));
        var count = (int)thick;
        for (var i = 0; i < count; i++)
        {
            var shift = side * (i - (count - 1) / 2f);
            Draw.Line((a.X + shift.X) / Draw.Scale, (a.Y + shift.Y) / Draw.Scale, (b.X + shift.X) / Draw.Scale, (b.Y + shift.Y) / Draw.Scale, color);
        }
    }

    static byte Alpha(float value) => (byte)Math.Clamp(value, 0f, 255f);

    // What kind of thing it is and what the editor has done to it.
    static string NoteOf(BmSDK.Engine.Actor actor)
    {
        var kind = Things.KindOf(actor) is { } found ? (int)found : Things.Labels.Length;
        var edit = Edits.Find(actor);
        var states = edit is null ? 0 : 1 | (edit.Frozen ? 2 : 0) | (edit.Removed ? 4 : 0) | (edit.Ghost ? 8 : 0);
        var key = kind * 16 + states;
        if (Notes.TryGetValue(key, out var note))
        {
            return note;
        }

        note = kind < Things.Labels.Length ? Things.Labels[kind] : "OBJECT";
        if (states == 1)
        {
            note += "  CHANGED";
        }

        if (edit is { Frozen: true })
        {
            note += "  FROZEN";
        }

        if (edit is { Removed: true })
        {
            note += "  REMOVED";
        }

        if (edit is { Ghost: true })
        {
            note += "  NO COLLISION";
        }

        Notes[key] = note;
        return note;
    }

    // The name card floats just above the box, the same size on screen at any distance.
    static void Card(BmSDK.Engine.Actor actor, Things.Shape shape, Rgba color)
    {
        var top = shape.Middle + Vector3.UnitZ * shape.Half.Z;
        var pixels = Hologram.Pixels(top);
        if (pixels <= 0f)
        {
            return;
        }

        var unit = Draw.Scale / pixels;
        var toward = Teleport.Flat(top - Hologram.Eye);
        var across = new Vector3(-toward.Y, toward.X, 0f);
        var name = Things.NameOf(actor);
        var note = NoteOf(actor);
        var width = MathF.Max(Draw.TextWidth(name, TitleSize), Draw.TextWidth(note, NoteSize)) + 16f;
        var lift = top + Vector3.UnitZ * (CardGap + CardHeight / 2f) * unit;
        var card = new Hologram.Panel(lift - across * width / 2f * unit, across * unit, -Vector3.UnitZ * unit);
        if (Hologram.Place(card, width, CardHeight))
        {
            Draw.Rect(0f, 0f, width, CardHeight, Ink with { A = 190 });
            Draw.Rect(0f, 0f, 2f, CardHeight, color);
            Draw.Text(name, 8f, 2f, TitleSize, White);
            Draw.Text(note, 8f, 14f, NoteSize, color);
        }
    }
}
