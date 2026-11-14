#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.Engine;
using Filter = BmSDK.Component.ECollisionFilter;

namespace Knightfall;

// Where the crosshair is and what it is on. The line runs from the camera as it was drawn,
// through the middle of the screen. Everything that aims at the world, and every line between two
// points that needs to stop on something, asks here.
static class Crosshair
{
    // What a line stops on.
    public enum Meets
    {
        // Walls, floors and the pieces built into the level. Vehicles, people and breakable
        // props are passed through.
        Level,

        // Anything solid that is drawn: the level, breakable props, loose objects and vehicles.
        // People are passed through.
        Solid,

        // All that, and people too, by their limbs.
        Anything,
    }

    // Where a line stopped, on what, and how far along it.
    public readonly record struct Hit(Vector3 Spot, Vector3 Normal, Actor Actor, PrimitiveComponent? Part, float Distance)
    {
        // Ground that can be stood on, rather than a wall or the underside of something.
        public bool Floor => Normal.Z >= Teleport.Level;
    }

    // How far off the line a thing may be and still be the one aimed at, in degrees from its
    // nearest edge. Only asked when the line itself doesn't touch anything wanted.
    public const float Cone = 4f;

    // The game's line for what can be seen. It meets the level, breakable props, vehicles and
    // people by their limbs, and starts clear of the invisible volumes the city is full of. The
    // level's own trace passes through vehicles and breakable props, and the trace for everything
    // stops on the volume the camera is in.
    const Filter Seen = Filter.COF_TraceWorldActors;
    const Filter Ground = Filter.COF_TraceWorld;

    // How many things a line may pass through before it gives up.
    const int Layers = 16;

    // A line that passes a thing starts again this far past where it met it, at least, or past
    // the far side of its box. A huge box only pushes it this far on.
    const float StepPast = 5f;
    const float FurthestSkip = 1500f;

    // A thing just past where the line stops still counts when it's this near, like a person
    // standing on the ground the line meets.
    const float Behind = 60f;

    // Near misses are checked for a clear view, best first, this many at most.
    const int SightChecks = 4;

    // The camera as it was drawn and the way the middle of the screen looks. The camera hook
    // leaves its own view in the game's cache until the next update, so this is what's on screen,
    // free camera and every camera option included.
    public static bool View(out Vector3 eye, out Vector3 way)
    {
        if (World.Controller is not { } controller)
        {
            eye = default;
            way = Vector3.UnitX;
            return false;
        }

        controller.GetPlayerViewPoint(out eye, out var turn);
        way = turn.ToDirection();
        return true;
    }

    // What the crosshair is on, no further than reach. Ignore is passed through as well, like an
    // object being placed.
    public static bool Surface(float reach, out Hit hit, Meets meets = Meets.Solid, Actor? ignore = null)
    {
        hit = default;
        return View(out var eye, out var way) && Teleport.Mover is { } tracer && First(tracer, eye, way, reach, 0f, meets, ignore, out hit);
    }

    // The first thing between two points. The tracer is never met, so it's the one the line is
    // drawn for, usually the player.
    public static bool Line(Actor tracer, Vector3 from, Vector3 to, Meets meets, out Hit hit, Actor? ignore = null)
    {
        var length = Vector3.Distance(from, to);
        if (length < 1f)
        {
            hit = default;
            return false;
        }

        return First(tracer, from, (to - from) / length, length, 0f, meets, ignore, out hit);
    }

    // The thing the crosshair is on, of the kinds wanted and passing accept, no further than
    // range. What the line touches first wins. Failing that a thing the line passes through the
    // box of, before it stops, since a few things have no shape the line can meet. Failing that
    // the thing nearest the line within the cone, if it can be seen. Nothing behind a wall counts.
    public static Actor? Pick(float range, Things.Kind? only = null, Func<Things.Kind, bool>? wanted = null, Func<Actor, bool>? accept = null, float cone = Cone)
    {
        if (!View(out var eye, out var way) || Teleport.Mover is not { } tracer)
        {
            return null;
        }

        // People who aren't wanted are seen through, anything else that isn't wanted hides what's
        // behind it.
        var wall = range;
        var start = 0f;
        for (var i = 0; i < Layers && First(tracer, eye, way, range, start, Meets.Anything, null, out var hit); i++)
        {
            var holder = Holder(hit.Actor);
            if (Wants(holder, only, wanted, accept))
            {
                return holder;
            }

            if (holder is Pawn and not Vehicle)
            {
                start = Past(hit.Actor, hit.Part, eye, way, hit.Distance);
                continue;
            }

            wall = hit.Distance;
            break;
        }

        Actor? entered = null;
        var nearest = float.MaxValue;
        var misses = new List<(float Angle, Actor Actor, Vector3 Point)>();
        var tan = MathF.Tan(cone * MathF.PI / 180f);
        foreach (var item in Things.Near(eye, MathF.Min(range, wall + FurthestSkip), only, wanted))
        {
            var actor = item.Actor;
            if (accept is not null && !accept(actor))
            {
                continue;
            }

            var shape = Things.ShapeOf(actor);
            if (Crosses(shape, eye, way, out var enter, out _))
            {
                if (enter < wall && enter < nearest)
                {
                    nearest = enter;
                    entered = actor;
                }
                else if (enter < wall + Behind)
                {
                    misses.Add((0f, actor, eye + way * enter));
                }

                continue;
            }

            // The point of its box nearest the line, and how far off the line that is.
            var point = Closest(shape, eye, way, out var along);
            if (along < 1f || along > wall + Behind)
            {
                continue;
            }

            var off = Vector3.Distance(point, eye + way * along);
            if (off <= along * tan)
            {
                misses.Add((off / along, actor, point));
            }
        }

        if (entered is not null)
        {
            return entered;
        }

        misses.Sort(static (a, b) => a.Angle.CompareTo(b.Angle));
        for (var i = 0; i < misses.Count && i < SightChecks; i++)
        {
            if (Clear(tracer, eye, misses[i].Point, misses[i].Actor))
            {
                return misses[i].Actor;
            }
        }

        return null;
    }

    static bool Wants(Actor actor, Things.Kind? only, Func<Things.Kind, bool>? wanted, Func<Actor, bool>? accept) =>
        actor is { bHidden: false } && Things.KindOf(actor) is { } kind && (only is null || kind == only)
        && (wanted?.Invoke(kind) ?? true) && (accept?.Invoke(actor) ?? true) && Things.Alive(actor);

    // What a hit belongs to: a gun in a thug's hand is the thug's.
    static Actor Holder(Actor hit)
    {
        var actor = hit;
        for (var i = 0; i < 3; i++)
        {
            if (Things.KindOf(actor) is not null || actor.Owner is not { IsValid: true } owner)
            {
                return actor;
            }

            actor = owner;
        }

        return hit;
    }

    // Nothing solid between the eye and a point, other than the thing that point is on.
    static bool Clear(Actor tracer, Vector3 eye, Vector3 point, Actor actor)
    {
        var length = Vector3.Distance(eye, point);
        if (length < 1f || !First(tracer, eye, (point - eye) / length, length + StepPast, 0f, Meets.Solid, null, out var hit))
        {
            return true;
        }

        return ReferenceEquals(Holder(hit.Actor), actor) || hit.Distance >= length - StepPast;
    }

    // The first thing a line meets from start onward that it doesn't pass through.
    static bool First(Actor tracer, Vector3 from, Vector3 way, float length, float start, Meets meets, Actor? ignore, out Hit hit)
    {
        var filter = meets == Meets.Level ? Ground : Seen;
        var end = from + way * length;
        for (var i = 0; i < Layers && start < length; i++)
        {
            var thing = tracer.Trace(out var spot, out var normal, end, from + way * start, filter, Vector3.Zero, out var info, 0);
            if (thing is null)
            {
                break;
            }

            var at = Vector3.Dot(spot - from, way);
            var part = info.HitComponent;
            if (!Passes(thing, part, meets, ignore))
            {
                hit = new Hit(spot, normal, thing, part, at);
                return true;
            }

            start = Past(thing, part, from, way, at);
        }

        hit = default;
        return false;
    }

    // Invisible boxes, the player, their car and what they carry or wear are always passed.
    // Hidden things are passed when the line is looking for what's drawn.
    static bool Passes(Actor thing, PrimitiveComponent? part, Meets meets, Actor? ignore)
    {
        if (thing is Volume || Ours(thing, ignore) || (thing.Owner is { } owner && Ours(owner, ignore))
            || (thing is not Pawn && thing.Base is { } holder && Ours(holder, ignore)))
        {
            return true;
        }

        return meets switch
        {
            Meets.Level => thing is Pawn,
            Meets.Solid when thing is Pawn and not Vehicle => true,
            _ => thing is Pawn { bHidden: true } || part is MeshComponent { HiddenGame: true },
        };
    }

    static bool Ours(Actor thing, Actor? ignore) =>
        ReferenceEquals(thing, ignore) || ReferenceEquals(thing, World.Player) || ReferenceEquals(thing, World.Driving);

    // Where a line starts again to get past something: past the far side of its box, so it
    // doesn't meet the inside of the same thing. A volume is stepped through a little at a time,
    // since the camera is often inside one that covers a whole district.
    static float Past(Actor thing, PrimitiveComponent? part, Vector3 from, Vector3 way, float at)
    {
        var next = at + StepPast;
        if (thing is Volume or WorldInfo)
        {
            return next;
        }

        next = MathF.Max(next, Leave(thing.CollisionComponent, from, way));
        next = MathF.Max(next, Leave(part, from, way));
        if (thing is Pawn { Mesh: { IsValid: true } mesh })
        {
            next = MathF.Max(next, Leave(mesh, from, way));
        }

        return MathF.Min(next, at + FurthestSkip);
    }

    // How far along a line it leaves a part's box, square to the world.
    static float Leave(PrimitiveComponent? part, Vector3 from, Vector3 way)
    {
        if (part is not { IsValid: true })
        {
            return 0f;
        }

        var bounds = part.Bounds;
        var box = new Things.Shape(bounds.Origin, Vector3.UnitX * bounds.BoxExtent.X, Vector3.UnitY * bounds.BoxExtent.Y, Vector3.UnitZ * bounds.BoxExtent.Z);
        return Crosses(box, from, way, out _, out var leave) && float.IsFinite(leave) ? leave : 0f;
    }

    // How far along a line it enters and leaves a box, turned however the box is. False when it
    // misses, or the box is all behind the start.
    static bool Crosses(Things.Shape box, Vector3 from, Vector3 way, out float enter, out float leave)
    {
        enter = float.MinValue;
        leave = float.MaxValue;
        var off = from - box.Middle;
        return Slab(box.X, off, way, ref enter, ref leave) && Slab(box.Y, off, way, ref enter, ref leave)
            && Slab(box.Z, off, way, ref enter, ref leave) && leave >= 0f;
    }

    // The stretch of the line between one pair of the box's faces.
    static bool Slab(Vector3 side, Vector3 off, Vector3 way, ref float enter, ref float leave)
    {
        var size = side.Length();
        if (size < 1e-3f)
        {
            return true;
        }

        var axis = side / size;
        var start = Vector3.Dot(off, axis);
        var speed = Vector3.Dot(way, axis);
        if (MathF.Abs(speed) < 1e-6f)
        {
            return MathF.Abs(start) <= size;
        }

        var a = (-size - start) / speed;
        var b = (size - start) / speed;
        enter = MathF.Max(enter, MathF.Min(a, b));
        leave = MathF.Min(leave, MathF.Max(a, b));
        return enter <= leave;
    }

    // The point of a box nearest a line that misses it, found by going back and forth between the
    // line and the box a few times. Along is how far down the line that point sits.
    static Vector3 Closest(Things.Shape box, Vector3 from, Vector3 way, out float along)
    {
        along = MathF.Max(Vector3.Dot(box.Middle - from, way), 1f);
        var point = box.Middle;
        for (var i = 0; i < 3; i++)
        {
            point = Clamp(box, from + way * along);
            along = Vector3.Dot(point - from, way);
        }

        return point;
    }

    static Vector3 Clamp(Things.Shape box, Vector3 point)
    {
        var off = point - box.Middle;
        var inside = box.Middle;
        foreach (var side in stackalloc Vector3[] { box.X, box.Y, box.Z })
        {
            var size = side.Length();
            if (size < 1e-3f)
            {
                continue;
            }

            var axis = side / size;
            inside += axis * Math.Clamp(Vector3.Dot(off, axis), -size, size);
        }

        return inside;
    }
}
