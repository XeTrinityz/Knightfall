#nullable enable

using System.Numerics;
using BmSDK.BmGame;

namespace Knightfall;

// The mech's mark while its beam charges, in its HUD's green. A strip as wide as the beam strikes
// runs along the ground the way the mech faces, out as far as the beam gets or the first wall in the
// way, and lights up from the mech outward as the charge builds. Every thug it will cross gets a
// question mark. Smash and surge go off on a press, so they need no mark.
static partial class RiddlerMech
{
    // Who the beam would cross is looked for this often, since that means looking through every
    // thug. The strip itself follows the mech every frame.
    const float MarkEvery = 0.1f;

    // The beam runs at about knee height when its path is checked for walls.
    const float PathHeight = 60f;

    const int PathChevrons = 12;
    const float PanelSize = 8f;
    const float PanelSpacing = 1.5f;
    const float PanelHeight = 22f;

    static float _crossedAt = -100f;
    static readonly List<RPawnVillain> Crossed = new();

    public static bool Marking() => _move == Move.Charge && World.Player is { IsValid: true };

    public static void Marks(float time)
    {
        if (World.Player is not { IsValid: true } player || player.CylinderComponent is not { IsValid: true } body)
        {
            return;
        }

        var ahead = Teleport.Flat(player.Rotation.ToDirection());
        var side = new Vector3(-ahead.Y, ahead.X, 0f);
        var feet = player.Location - Vector3.UnitZ * body.CollisionHeight + Vector3.UnitZ * 3f;
        var knee = feet + Vector3.UnitZ * PathHeight;
        var range = BeamRange();
        var length = Crosshair.Line(player, knee, knee + ahead * range, Crosshair.Meets.Level, out var wall) ? wall.Distance : range;
        var end = feet + ahead * length;

        Look(player, feet, ahead, length);

        // The edges, the part the charge has lit, and chevrons running out along it.
        var charge = Charge;
        WorldMarks.Segment(feet + side * BeamWidth, end + side * BeamWidth, Green with { A = 150 });
        WorldMarks.Segment(feet - side * BeamWidth, end - side * BeamWidth, Green with { A = 150 });
        WorldMarks.Segment(end + side * BeamWidth, end - side * BeamWidth, Green);
        var lit = feet + ahead * (length * charge);
        WorldMarks.Segment(feet, lit, Lime);
        var shift = time * 1.2f % 1f;
        for (var i = 0; i < PathChevrons; i++)
        {
            var along = (i + shift) / PathChevrons;
            var tip = feet + ahead * (length * along);
            var back = tip - ahead * (BeamWidth * 0.5f);
            var color = along <= charge ? Lime : Green with { A = 110 };
            WorldMarks.Segment(back + side * (BeamWidth * 0.6f), tip, color);
            WorldMarks.Segment(back - side * (BeamWidth * 0.6f), tip, color);
        }

        foreach (var thug in Crossed)
        {
            Riddle(thug, time);
        }

        if (WorldMarks.Screen(end + Vector3.UnitZ * 40f, out var above))
        {
            Panel(above, "BEAM", _charged ? "LET GO TO FIRE" : Percents[(int)MathF.Round(charge * 100f)], _charged ? Lime : Green);
        }
    }

    // The thugs within the beam's reach of its path, as its strike would find them.
    static void Look(RPawnPlayer player, Vector3 feet, Vector3 ahead, float length)
    {
        var now = World.Now;
        if (now - _crossedAt < MarkEvery && now >= _crossedAt)
        {
            return;
        }

        _crossedAt = now;
        Crossed.Clear();
        foreach (var thug in Crowd.StandingEnemies())
        {
            var off = thug.Location - feet;
            var along = Vector3.Dot(off, ahead);
            if (along < 0f || along > length + BeamWidth)
            {
                continue;
            }

            var across = off - ahead * along;
            if (across.X * across.X + across.Y * across.Y <= BeamWidth * BeamWidth && MathF.Abs(off.Z) <= BeamHeight)
            {
                Crossed.Add(thug);
            }
        }
    }

    // A question mark over a thug the beam will cross, swaying a little.
    static void Riddle(RPawnVillain thug, float time)
    {
        var height = thug.CylinderComponent is { IsValid: true } body ? body.CollisionHeight : 90f;
        if (WorldMarks.Screen(thug.Location + Vector3.UnitZ * (height + 20f), out var at))
        {
            var width = Draw.TextWidth("?", 12f);
            Draw.Text("?", at.X - width / 2f + MathF.Sin(time * 4f + thug.Location.X) * 1.5f, at.Y - 14f, 12f, Lime);
        }
    }

    // A dark panel with a green question mark notch, sitting over a point.
    static void Panel(Vector2 at, string title, string note, Rgba color)
    {
        var width = MathF.Max(Draw.TrackedWidth(title, PanelSize, PanelSpacing), Draw.TextWidth(note, PanelSize)) + 22f;
        var left = at.X - width / 2f;
        var top = at.Y - PanelHeight - 8f;
        Draw.Line(at.X, top + PanelHeight, at.X, at.Y, color with { A = 130 });
        Draw.Rect(left, top, width, PanelHeight, Ink);
        Draw.Frame(left, top, width, PanelHeight, 1f, color with { A = 150 });
        Draw.Text("?", left + 4f, top + 3f, 14f, color);
        Draw.Tracked(title, left + 14f, top + 3f, PanelSize, PanelSpacing, Text);
        Draw.Text(note, left + 14f, top + 12f, PanelSize, color);
    }
}
