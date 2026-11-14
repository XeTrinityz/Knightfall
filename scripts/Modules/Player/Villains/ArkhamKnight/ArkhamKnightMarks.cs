#nullable enable

using System.Numerics;
using BmSDK.BmGame;

namespace Knightfall;

// The Arkham Knight's mark while secondary fire is held with the rifle, in his visor's teal and
// his militia's red. A drop zone is laid where his laser is on: the teal ring is where the shells
// land, the red one how far their blasts throw, with a radar sweep turning over it. Every thug
// in the zone gets a red lock. The label is a panel cut like his visor.
static partial class ArkhamKnight
{
    // Who is in the zone is looked for this often, since that means looking through every thug.
    // The zone itself follows the laser every tick.
    const float MarkEvery = 0.1f;

    // A shell lands this far out at most and throws this far round it.
    const float ZoneReach = Spread + ShellSplash;

    const float PanelSize = 8f;
    const float PanelSpacing = 1.5f;
    const float PanelHeight = 22f;
    const float PanelCut = 6f;

    static Vector3? _zoneAt;
    static float _zonedAt;
    static readonly List<RPawnVillain> InZone = new();

    static readonly string[] InZoneText = Enumerable.Range(0, 10).Select(static count => $"{count} IN ZONE").ToArray();

    public static bool Marking() => Aim.On && _zoneAt is not null;

    // Where the strike would land if he let go now, and who it would throw.
    static void Watch(RPawnPlayer player)
    {
        _zoneAt = Aim.Secondary && !_pistols ? _markAt : null;
        if (_zoneAt is not { } zone)
        {
            InZone.Clear();
            return;
        }

        var now = World.Now;
        if (now - _zonedAt < MarkEvery && now >= _zonedAt)
        {
            return;
        }

        _zonedAt = now;
        InZone.Clear();
        foreach (var thug in Crowd.StandingEnemies())
        {
            if (Vector3.DistanceSquared(thug.Location, zone) <= ZoneReach * ZoneReach)
            {
                InZone.Add(thug);
            }
        }
    }

    public static void Marks(float time)
    {
        if (_zoneAt is not { } zone)
        {
            return;
        }

        var ready = World.Now >= _nextStrike;
        var ground = zone + Vector3.UnitZ * 3f;
        var teal = ready ? Teal : TealDim;
        var red = ready ? Red : RedDim;
        WorldMarks.Ring(ground, ZoneReach, red, time * 0.3f, true);
        WorldMarks.Ring(ground, Spread, teal, 0f, false);

        // Cross ticks between the rings, and the sweep going round.
        for (var tick = 0; tick < 4; tick++)
        {
            var way = Flat(tick * MathF.PI / 2f);
            WorldMarks.Segment(ground + way * (Spread * 1.15f), ground + way * (ZoneReach * 0.9f), teal with { A = 170 });
        }

        if (ready)
        {
            var sweep = time * 2.5f;
            for (var trail = 0; trail < 6; trail++)
            {
                var way = Flat(sweep - trail * 0.06f);
                WorldMarks.Segment(ground, ground + way * ZoneReach, Teal with { A = (byte)(160 - trail * 25) });
            }

            foreach (var thug in InZone)
            {
                Lock(thug, time);
            }
        }

        if (WorldMarks.Screen(ground + Vector3.UnitZ * 40f, out var above))
        {
            var note = !ready ? "RECHARGING" : InZone.Count > 0 ? InZoneText[Math.Min(InZone.Count, InZoneText.Length - 1)] : "LOCKED";
            Panel(above, "DRONE STRIKE", note, ready ? Teal : Muted);
        }
    }

    static Vector3 Flat(float angle) => new(MathF.Cos(angle), MathF.Sin(angle), 0f);

    // Red corners closing on a thug's head, tightening and opening a little.
    static void Lock(RPawnVillain thug, float time)
    {
        var height = thug.CylinderComponent is { IsValid: true } body ? body.CollisionHeight : 90f;
        if (!WorldMarks.Screen(thug.Location + Vector3.UnitZ * (height * 0.6f), out var at))
        {
            return;
        }

        var size = 8f + 1.5f * MathF.Sin(time * 7f);
        const float Arm = 4f;
        foreach (var (x, y) in stackalloc[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
        {
            var cx = at.X + x * size;
            var cy = at.Y + y * size;
            Draw.Rect(x < 0f ? cx : cx - Arm, cy - 0.5f, Arm, 1f, Red);
            Draw.Rect(cx - 0.5f, y < 0f ? cy : cy - Arm, 1f, Arm, Red);
        }
    }

    // A dark panel with its top right corner cut off like his visor, sitting over a point.
    static void Panel(Vector2 at, string title, string note, Rgba color)
    {
        var width = MathF.Max(Draw.TrackedWidth(title, PanelSize, PanelSpacing), Draw.TextWidth(note, PanelSize)) + 16f;
        var left = at.X - width / 2f;
        var top = at.Y - PanelHeight - 8f;
        var right = left + width;
        Draw.Line(at.X, top + PanelHeight, at.X, at.Y, color with { A = 130 });
        Draw.Rect(left, top + PanelCut, width, PanelHeight - PanelCut, Solid);
        Draw.Rect(left, top, width - PanelCut, PanelCut, Solid);
        Draw.Triangle(right - PanelCut, top, right, top + PanelCut, right - PanelCut, top + PanelCut, Solid);
        Draw.Line(left, top, right - PanelCut, top, color);
        Draw.Line(right - PanelCut, top, right, top + PanelCut, color);
        Draw.Rect(left, top, 2f, PanelHeight, color);
        Draw.Tracked(title, left + 7f, top + 3f, PanelSize, PanelSpacing, Bright);
        Draw.Text(note, left + 7f, top + 12f, PanelSize, color);
    }
}
