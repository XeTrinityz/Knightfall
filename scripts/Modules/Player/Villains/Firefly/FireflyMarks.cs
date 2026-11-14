#nullable enable

using System.Numerics;
using BmSDK.BmGame;

namespace Knightfall;

// Firefly's mark while secondary fire is held, in his HUD's fire colours. Dashes follow the arc
// the grenade will fly. Where it comes down, a ring of flame tongues flickers as wide as its fire reaches, with a small hazard
// diamond in the middle. Every thug the fire would reach gets a flame over his head. The label
// is a dark plate with an orange bar, like his HUD. His flamethrower needs no mark, since its
// stream shows where it goes.
static partial class Firefly
{
    const int FlameTongues = 20;
    const float PlateSize = 8f;
    const float PlateSpacing = 1.5f;
    const float PlateHeight = 22f;

    const int ArcDashes = 24;
    static Toss? _toss;
    static readonly List<RPawnVillain> Scorching = new();

    public static bool Marking() => Aim.On && _toss is not null;

    // Where the grenade would go if he let go now, from the same plan the throw uses.
    static void Watch(RPawnPlayer player)
    {
        Scorching.Clear();
        _toss = Aim.Secondary ? Tossing(player) : null;
        if (_toss is not { } toss)
        {
            return;
        }

        var at = toss.Lands;
        foreach (var thug in Nearby(at))
        {
            if (Crowd.Standing(thug) && Vector3.Distance(thug.Location, at) <= FireReach)
            {
                Scorching.Add(thug);
            }
        }
    }

    public static void Marks(float time)
    {
        if (_toss is not { } toss)
        {
            return;
        }

        var spot = toss.Lands;

        var missing = _grenadeClass is not { IsValid: true };
        var ready = !missing && World.Now >= _nextThrow;
        var color = ready ? Orange : Unlit;
        if (ready)
        {
            Flight(toss, time);
        }

        FireRing(spot, color, time, ready);
        if (ready)
        {
            foreach (var thug in Scorching)
            {
                Flicker(thug, time);
            }
        }

        if (WorldMarks.Screen(spot + Vector3.UnitZ * 40f, out var above))
        {
            Plate(above, "GRENADE", missing ? "NO GRENADES" : ready ? "BURSTS HERE" : "RELOADING", color);
        }
    }

    // A ring of flame tongues as wide as the fire reaches, each licking up and down on its own,
    // round a small hazard diamond.
    static void FireRing(Vector3 spot, Rgba color, float time, bool lit)
    {
        var ground = spot + Vector3.UnitZ * 3f;
        WorldMarks.Ring(ground, FireReach, color with { A = 150 }, 0f, false);
        var half = MathF.Tau / FlameTongues * 0.4f;
        for (var tongue = 0; tongue < FlameTongues; tongue++)
        {
            var angle = tongue * MathF.Tau / FlameTongues;
            var lick = lit ? 0.12f + 0.08f * MathF.Sin(time * 9f + tongue * 1.7f) : 0.08f;
            if (WorldMarks.Screen(ground + Out(angle - half) * FireReach, out var left)
                && WorldMarks.Screen(ground + Out(angle + half) * FireReach, out var right)
                && WorldMarks.Screen(ground + Out(angle) * (FireReach * (1f + lick)), out var tip))
            {
                Draw.Triangle(left.X, left.Y, right.X, right.Y, tip.X, tip.Y, tongue % 2 == 0 ? color : (lit ? Yellow : color) with { A = 200 });
            }
        }

        if (WorldMarks.Screen(ground, out var middle))
        {
            const float Size = 6f;
            Draw.Triangle(middle.X, middle.Y - Size, middle.X + Size, middle.Y, middle.X - Size, middle.Y, lit ? Red : color);
            Draw.Triangle(middle.X, middle.Y + Size, middle.X + Size, middle.Y, middle.X - Size, middle.Y, lit ? Blue : color);
        }
    }

    static Vector3 Out(float angle) => new(MathF.Cos(angle), MathF.Sin(angle), 0f);

    // The arc the grenade will fly, in marching dashes of orange and yellow.
    static void Flight(Toss toss, float time)
    {
        var gravity = Vector3.UnitZ * (0.5f * _grenadeFall);
        var shift = time * 1.5f % 1f;
        for (var i = 0; i < ArcDashes; i++)
        {
            var a = (i + shift) / ArcDashes;
            var b = MathF.Min((i + shift + 0.5f) / ArcDashes, 1f);
            if (a < 1f)
            {
                var ta = a * toss.Seconds;
                var tb = b * toss.Seconds;
                WorldMarks.Segment(toss.From + toss.Velocity * ta + gravity * (ta * ta), toss.From + toss.Velocity * tb + gravity * (tb * tb), i % 2 == 0 ? Orange : Yellow);
            }
        }
    }

    // A small flame over a thug the fire would reach.
    static void Flicker(RPawnVillain thug, float time)
    {
        var height = thug.CylinderComponent is { IsValid: true } body ? body.CollisionHeight : 90f;
        if (!WorldMarks.Screen(thug.Location + Vector3.UnitZ * (height + 18f), out var at))
        {
            return;
        }

        var lick = 1.5f * MathF.Sin(time * 10f + thug.Location.Y);
        Draw.Triangle(at.X - 4f, at.Y + 3f, at.X + 4f, at.Y + 3f, at.X + lick * 0.5f, at.Y - 8f - lick, Orange);
        Draw.Triangle(at.X - 2f, at.Y + 3f, at.X + 2f, at.Y + 3f, at.X + lick * 0.3f, at.Y - 3f - lick * 0.5f, Yellow);
    }

    // A dark plate with an orange bar along its top, sitting over a point.
    static void Plate(Vector2 at, string title, string note, Rgba color)
    {
        var width = MathF.Max(Draw.TrackedWidth(title, PlateSize, PlateSpacing), Draw.TextWidth(note, PlateSize)) + 14f;
        var left = at.X - width / 2f;
        var top = at.Y - PlateHeight - 8f;
        Draw.Line(at.X, top + PlateHeight, at.X, at.Y, color with { A = 130 });
        Draw.Rect(left, top, width, PlateHeight, Ink);
        Draw.Rect(left, top, width, 2f, color);
        Draw.Tracked(title, left + 7f, top + 4f, PlateSize, PlateSpacing, Cream);
        Draw.Text(note, left + 7f, top + 13f, PlateSize, color);
    }
}
