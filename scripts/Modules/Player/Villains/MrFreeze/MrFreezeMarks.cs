#nullable enable

using System.Numerics;
using BmSDK.BmGame;

namespace Knightfall;

// Mr. Freeze's mark while secondary fire is held, in his suit's ice blue. Where the ice spire
// would burst, an ice crystal is laid on the ground: the outer ring is how far it freezes, the
// inner one how far the spike throws. Thugs it would freeze get a diamond and ones the spike
// would throw get a shard. The label is a small panel like his suit's display. His beam needs no
// mark, since the beam itself shows where it goes.
static partial class MrFreeze
{
    // Who the spire would catch is looked for this often, since that means looking through every
    // thug. The spot itself follows the aim every tick.
    const float MarkEvery = 0.1f;

    const float PanelSize = 8f;
    const float PanelSpacing = 1.5f;
    const float PanelHeight = 22f;

    static Vector3? _spireAt;
    static float _markedAt;
    static readonly List<(RPawnVillain Thug, bool Speared)> Caught = new();

    static readonly string[] CaughtText = Enumerable.Range(0, 10).Select(static count => $"{count} CAUGHT").ToArray();

    public static bool Marking() => Aim.On && _spireAt is not null;

    // Where the spire would burst if he let go now, and who it would catch.
    static void Watch(RPawnPlayer player)
    {
        _spireAt = Aim.Secondary && Aim.Ground(player, BlastReach, out var at) ? at : null;
        if (_spireAt is not { } spire)
        {
            Caught.Clear();
            return;
        }

        var now = World.Now;
        if (now - _markedAt < MarkEvery && now >= _markedAt)
        {
            return;
        }

        _markedAt = now;
        Caught.Clear();
        foreach (var thug in Crowd.StandingEnemies())
        {
            if (!Frozen(thug) && InBlast(player, thug, spire, out var speared))
            {
                Caught.Add((thug, speared));
            }
        }
    }

    public static void Marks(float time)
    {
        if (_spireAt is { } spire)
        {
            Spire(spire, time);
        }
    }

    // An ice crystal on the ground: a hexagon as wide as the freeze, a smaller one turned half a
    // point for the spike, and cracks running between their corners.
    static void Spire(Vector3 spot, float time)
    {
        var ready = World.Now >= _nextBlast && !_blastPending;
        var color = ready ? Ice : Dim;
        var ground = spot + Vector3.UnitZ * 3f;
        var shimmer = 0.96f + 0.04f * MathF.Sin(time * 3f);
        for (var corner = 0; corner < 6; corner++)
        {
            var a = corner * MathF.PI / 3f;
            var b = (corner + 1) * MathF.PI / 3f;
            WorldMarks.Segment(ground + Point(a) * BlastRadius * shimmer, ground + Point(b) * BlastRadius * shimmer, color);
            WorldMarks.Segment(ground + Point(a + MathF.PI / 6f) * SpikeReach, ground + Point(b + MathF.PI / 6f) * SpikeReach, color with { A = 200 });
            WorldMarks.Segment(ground + Point(a + MathF.PI / 6f) * SpikeReach, ground + Point(a) * BlastRadius * shimmer, color with { A = 110 });
            WorldMarks.Segment(ground + Point(b + MathF.PI / 6f) * SpikeReach, ground + Point(b) * BlastRadius * shimmer, color with { A = 110 });
        }

        if (ready)
        {
            foreach (var (thug, speared) in Caught)
            {
                Over(thug, speared ? Frost : Ice, speared);
            }
        }

        if (WorldMarks.Screen(ground + Vector3.UnitZ * 40f, out var above))
        {
            var note = !ready ? "RECHARGING"
                : Caught.Count > 0 ? CaughtText[Math.Min(Caught.Count, CaughtText.Length - 1)]
                : Vector3.Distance(spot, World.Player?.Location ?? spot) >= BlastReach - 5f ? "AT FULL REACH"
                : "READY";
            Panel(above, "ICE SPIRE", note, color);
        }
    }

    static Vector3 Point(float angle) => new(MathF.Cos(angle), MathF.Sin(angle), 0f);

    // A frost diamond over a thug's head, or a shard pointing up for one the spike would throw.
    static void Over(RPawnVillain thug, Rgba color, bool speared)
    {
        var height = thug.CylinderComponent is { IsValid: true } body ? body.CollisionHeight : 90f;
        if (!WorldMarks.Screen(thug.Location + Vector3.UnitZ * (height + 18f), out var at))
        {
            return;
        }

        if (speared)
        {
            Draw.Triangle(at.X - 4f, at.Y + 4f, at.X + 4f, at.Y + 4f, at.X, at.Y - 7f, color);
            return;
        }

        Draw.Triangle(at.X, at.Y - 5f, at.X + 4f, at.Y, at.X - 4f, at.Y, color);
        Draw.Triangle(at.X, at.Y + 5f, at.X + 4f, at.Y, at.X - 4f, at.Y, color with { A = 170 });
    }

    // A small panel like his suit's display, sitting over a point.
    static void Panel(Vector2 at, string title, string note, Rgba color)
    {
        var width = MathF.Max(Draw.TrackedWidth(title, PanelSize, PanelSpacing), Draw.TextWidth(note, PanelSize)) + 14f;
        var left = at.X - width / 2f;
        var top = at.Y - PanelHeight - 8f;
        Draw.Rect(left, top, width, PanelHeight, Ink);
        Draw.Frame(left, top, width, PanelHeight, 1f, color with { A = 160 });
        Draw.Rect(left, top, 6f, 1f, color);
        Draw.Rect(left + width - 6f, top + PanelHeight - 1f, 6f, 1f, color);
        Draw.Tracked(title, left + 7f, top + 3f, PanelSize, PanelSpacing, Frost);
        Draw.Text(note, left + 7f, top + 12f, PanelSize, color);
        Draw.Line(at.X, top + PanelHeight, at.X, at.Y, color with { A = 120 });
    }
}
