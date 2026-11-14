#nullable enable

using System.Numerics;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Poison Ivy's marks while a move's button is held, drawn like her herbarium. Thorned brackets close round the
// tank or car her vine will seize, a coil winds at the feet of a thug it will spear, and roots
// crack out where it will burst from the ground. A ring of teeth shows where a flytrap would
// grow and how far it bites. Each is labelled with a specimen tag, and fades while it regrows.
static partial class PoisonIvy
{
    // The vine's target is looked for this often while she aims, since that means looking through
    // every vehicle and thug. A spot on the ground follows the crosshair every tick.
    const float LookEvery = 0.1f;

    const int Roots = 7;
    const int RootBends = 4;
    const int Teeth = 18;
    const int CoilSteps = 48;
    const float CoilTurns = 2.5f;
    const float LabelSize = 8f;
    const float LabelSpacing = 1.5f;
    const float LabelHeight = 22f;

    static Cast? _cast;
    static float _castLookAt;
    static Vector3? _trapSpot;

    public static bool Marking() => Aim.On && (_cast is not null || _trapSpot is not null);

    // What the vine would do if she let go of fire now, kept for her marks while fire is held.
    static void Watch(RPawnPlayer player)
    {
        if (!Aim.Fire || _vineMesh is null || _vineMoves is null)
        {
            _cast = null;
            return;
        }

        var now = World.Now;
        if (now >= _castLookAt || now < _castLookAt - LookEvery)
        {
            _castLookAt = now + LookEvery;
            _cast = Casting(player);
        }
        else if (_cast is { Kind: Lunge.Ground } ground)
        {
            _cast = Aim.Ground(player, VineReach, out var at) ? ground with { At = at } : null;
        }
    }

    public static void Marks(float time)
    {
        if (World.Player is not { IsValid: true } player)
        {
            return;
        }

        if (_cast is { } cast)
        {
            var ready = World.Now >= _nextVine;
            var color = ready ? LeafLight : Faint;
            switch (cast.Kind)
            {
                case Lunge.Tank or Lunge.Car when Things.Alive(cast.Target):
                    Thorns(cast.Target!, color, time, ready ? cast.Kind == Lunge.Tank ? "TEARS IT APART" : "THROWS IT" : "REGROWING");
                    break;
                case Lunge.Thug when cast.Target is Pawn { IsValid: true } thug:
                    Coil(thug, color, time, ready ? "SPEARS HIM" : "REGROWING");
                    break;
                case Lunge.Ground:
                    Cracks(cast.At, color, ready ? Reaching(player, cast.At, VineReach) ? "AT FULL REACH" : "BURSTS HERE" : "REGROWING");
                    break;
            }
        }

        // With the vine on a vehicle or a thug, that's what she's after, so the flytrap stays out
        // of the way.
        if (_trapSpot is { } spot && _cast is null or { Kind: Lunge.Ground })
        {
            var ready = World.Now >= _nextTrap;
            var note = !ready ? "REGROWING" : Traps.Count >= TrapsAtOnce ? "OLDEST WILTS" : Reaching(player, spot, TrapReach) ? "AT FULL REACH" : "GROWS HERE";
            Jaws(spot, ready ? Rose : Faint, time, note);
        }
    }

    // Aim.Ground brings a spot in to her reach, so one this far out was brought in.
    static bool Reaching(RPawnPlayer player, Vector3 spot, float reach) => Vector3.Distance(spot, player.Location) >= reach - 5f;

    // Brackets round the vehicle as it sits on the screen, each arm set with thorns, breathing.
    static void Thorns(Actor target, Rgba color, float time, string note)
    {
        var shape = Things.ShapeOf(target);
        var low = new Vector2(float.MaxValue);
        var high = new Vector2(float.MinValue);
        for (var corner = 0; corner < 8; corner++)
        {
            var point = shape.Middle
                + shape.X * ((corner & 1) == 0 ? 1f : -1f)
                + shape.Y * ((corner & 2) == 0 ? 1f : -1f)
                + shape.Z * ((corner & 4) == 0 ? 1f : -1f);
            if (!WorldMarks.Screen(point, out var at))
            {
                return;
            }

            low = Vector2.Min(low, at);
            high = Vector2.Max(high, at);
        }

        var breathe = 5f + 2f * MathF.Sin(time * 3f);
        low -= new Vector2(breathe);
        high += new Vector2(breathe);
        var arm = Math.Clamp(MathF.Min(high.X - low.X, high.Y - low.Y) * 0.3f, 10f, 30f);
        Bracket(low.X, low.Y, 1f, 1f, arm, color);
        Bracket(high.X, low.Y, -1f, 1f, arm, color);
        Bracket(low.X, high.Y, 1f, -1f, arm, color);
        Bracket(high.X, high.Y, -1f, -1f, arm, color);
        Label(new Vector2((low.X + high.X) / 2f, low.Y), "SEIZE", note, color);
    }

    // One corner: two arms going in, with a thorn standing out from each.
    static void Bracket(float x, float y, float across, float down, float arm, Rgba color)
    {
        Draw.Rect(across > 0f ? x : x - arm, y - 1f, arm, 2f, color);
        Draw.Rect(x - 1f, down > 0f ? y : y - arm, 2f, arm, color);
        var along = x + across * arm * 0.55f;
        Draw.Triangle(along - 2.5f, y, along + 2.5f, y, along - across * 1.5f, y - down * 6f, color);
        var up = y + down * arm * 0.55f;
        Draw.Triangle(x, up - 2.5f, x, up + 2.5f, x - across * 6f, up - down * 1.5f, color);
    }

    // A tendril coiling in at a thug's feet, turning as it closes.
    static void Coil(Pawn thug, Rgba color, float time, string note)
    {
        var height = thug.CylinderComponent is { IsValid: true } body ? body.CollisionHeight : 90f;
        var feet = thug.Location - Vector3.UnitZ * (height - 4f);
        var last = feet;
        for (var i = 0; i <= CoilSteps; i++)
        {
            var share = i / (float)CoilSteps;
            var angle = time * 2f + share * CoilTurns * MathF.Tau;
            var point = feet + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * (70f - 50f * share);
            if (i > 0)
            {
                WorldMarks.Segment(last, point, color with { A = (byte)(90 + 165 * share) });
            }

            last = point;
        }

        if (WorldMarks.Screen(thug.Location + Vector3.UnitZ * (height + 20f), out var head))
        {
            Label(head, "SEIZE", note, color);
        }
    }

    // The ring the burst reaches, with roots cracking out to it from the middle.
    static void Cracks(Vector3 spot, Rgba color, string note)
    {
        var ground = spot + Vector3.UnitZ * 3f;
        WorldMarks.Ring(ground, StrikeReach, color with { A = 170 }, 0f, false);
        for (var root = 0; root < Roots; root++)
        {
            var heading = root * MathF.Tau / Roots + Wobble(root, 0) * 0.3f;
            var last = ground;
            for (var bend = 1; bend <= RootBends; bend++)
            {
                var share = bend / (float)RootBends;
                var turn = heading + Wobble(root, bend) * 0.35f;
                var point = ground + new Vector3(MathF.Cos(turn), MathF.Sin(turn), 0f) * (StrikeReach * share);
                WorldMarks.Segment(last, point, color with { A = (byte)(230 - 90 * share) });
                last = point;
            }
        }

        if (WorldMarks.Screen(ground + Vector3.UnitZ * 40f, out var above))
        {
            Label(above, "VINE", note, color);
        }
    }

    // A fixed bend for each root, so the cracks keep their shape from frame to frame.
    static float Wobble(int root, int bend)
    {
        var value = MathF.Sin(root * 12.9898f + bend * 78.233f) * 43758.547f;
        return (value - MathF.Floor(value)) * 2f - 1f;
    }

    // A ring of teeth pointing in, as wide as the plant bites, around a bud where it comes up.
    static void Jaws(Vector3 spot, Rgba color, float time, string note)
    {
        var ground = spot + Vector3.UnitZ * 3f;
        WorldMarks.Ring(ground, TrapBite, color with { A = 110 }, 0f, false);
        WorldMarks.Ring(ground, 22f, color, 0f, false);
        var bite = 0.86f + 0.04f * MathF.Sin(time * 2.5f);
        var half = MathF.Tau / Teeth * 0.32f;
        for (var tooth = 0; tooth < Teeth; tooth++)
        {
            var angle = tooth * MathF.Tau / Teeth;
            if (WorldMarks.Screen(ground + Flat(angle - half) * TrapBite, out var left)
                && WorldMarks.Screen(ground + Flat(angle + half) * TrapBite, out var right)
                && WorldMarks.Screen(ground + Flat(angle) * (TrapBite * bite), out var tip))
            {
                Draw.Triangle(left.X, left.Y, right.X, right.Y, tip.X, tip.Y, color with { A = 200 });
            }
        }

        if (WorldMarks.Screen(ground + Vector3.UnitZ * 40f, out var above))
        {
            Label(above, "FLYTRAP", note, color);
        }
    }

    static Vector3 Flat(float angle) => new(MathF.Cos(angle), MathF.Sin(angle), 0f);

    // A specimen tag on a string, like the ones on her herbarium sheet.
    static void Label(Vector2 at, string title, string note, Rgba color)
    {
        var width = MathF.Max(Draw.TrackedWidth(title, LabelSize, LabelSpacing), Draw.TextWidth(note, LabelSize)) + 16f;
        var left = at.X - width / 2f;
        var top = at.Y - LabelHeight - 12f;
        Draw.Line(at.X, at.Y, at.X, top + LabelHeight, Faint);
        Draw.Rect(left, top, width, LabelHeight, Paper with { A = 235 });
        Draw.Rect(left, top + LabelHeight - 1f, width, 1f, PaperEdge);
        Draw.Rect(left + 3f, top + 3f, 2f, LabelHeight - 6f, color);
        Draw.Rect(left + width - 7f, top + 3f, 3f, 3f, PaperEdge);
        Draw.Tracked(title, left + 9f, top + 3f, LabelSize, LabelSpacing, Ink);
        Draw.Text(note, left + 9f, top + 12f, LabelSize, Brown);
    }
}
