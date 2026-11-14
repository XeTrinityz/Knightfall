#nullable enable

using System.Numerics;
using BmSDK.BmGame;

namespace Knightfall;

// The Joker's mark while secondary fire is held, in his card's colours. Dashes run from his gun
// along the arc the canister will fly, green and purple by turns. Where it lands, a puffing
// cloud is as wide as the gas stuns, and every thug inside it gets a HA. The label is a little
// playing card, like the one on his HUD.
static partial class JokerVillain
{
    // Who the gas would catch is looked for this often, since that means looking through every
    // thug. The spot follows the aim every tick.
    const float MarkEvery = 0.1f;

    // How wide the gas is when its own stun reach can't be read, and how far up or down a thug
    // can stand and still be in it.
    const float GasWide = 450f;
    const float GasTall = 250f;

    const int ArcDashes = 24;
    const int CloudSides = 64;
    const float LabelSize = 8f;
    const float LabelSpacing = 1.5f;
    const float LabelHeight = 22f;

    static Vector3? _gasAt;
    static Vector3? _gasFrom;
    static float _markedAt;
    static readonly List<RPawnVillain> Gassed = new();

    public static bool Marking() => Aim.On && _gasAt is not null;

    static float GasRadius => _gasCloud is { IsValid: true } cloud && cloud.StunRadius > 1f ? cloud.StunRadius : GasWide;

    // Where the canister would land if he let go now, where it would leave his gun, and who the
    // gas would catch.
    static void Watch(RPawnPlayer player)
    {
        _gasAt = Aim.Secondary && Aim.Ground(player, GasReach, out var at) ? at : null;
        if (_gasAt is not { } spot)
        {
            Gassed.Clear();
            return;
        }

        _gasFrom = Barrel()?.From;
        var now = World.Now;
        if (now - _markedAt < MarkEvery && now >= _markedAt)
        {
            return;
        }

        _markedAt = now;
        Gassed.Clear();
        var reach = GasRadius;
        foreach (var thug in Crowd.StandingEnemies())
        {
            var off = thug.Location - spot;
            if (off.X * off.X + off.Y * off.Y <= reach * reach && MathF.Abs(off.Z) <= GasTall)
            {
                Gassed.Add(thug);
            }
        }
    }

    public static void Marks(float time)
    {
        if (_gasAt is not { } spot || World.Player is not { IsValid: true } player)
        {
            return;
        }

        var flying = _flightStart >= 0f;
        var refilling = World.Now < _nextGas;
        var missing = _gasTried && _gasCloud is not { IsValid: true };
        var ready = !flying && !refilling && !missing;
        var color = ready ? Green : Empty;

        if (_gasFrom is { } from && ready)
        {
            Lob(from, spot, time);
        }

        Cloud(spot, GasRadius, color, time);
        if (ready)
        {
            foreach (var thug in Gassed)
            {
                Laugh(thug, time);
            }
        }

        if (WorldMarks.Screen(spot + Vector3.UnitZ * 40f, out var above))
        {
            var note = missing ? "NO GAS HERE"
                : flying ? "ONE IN THE AIR"
                : refilling ? "REFILLING"
                : Vector3.Distance(spot, player.Location) >= GasReach - 5f ? "AT FULL REACH"
                : "LANDS HERE";
            PlayingCard(above, "LAUGHING GAS", note, ready);
        }
    }

    // The canister's own arc, the one it flies, in marching dashes of green and purple.
    static void Lob(Vector3 from, Vector3 to, float time)
    {
        var rise = Vector3.Distance(from, to) * ArcPerReach;
        var shift = time * 1.5f % 1f;
        for (var i = 0; i < ArcDashes; i++)
        {
            var a = (i + shift) / ArcDashes;
            var b = MathF.Min((i + shift + 0.5f) / ArcDashes, 1f);
            if (a < 1f)
            {
                WorldMarks.Segment(Flight(from, to, rise, a), Flight(from, to, rise, b), i % 2 == 0 ? Green : Purple);
            }
        }
    }

    static Vector3 Flight(Vector3 from, Vector3 to, float rise, float flown) =>
        Vector3.Lerp(from, to, flown) + Vector3.UnitZ * (rise * 4f * flown * (1f - flown));

    // A puffing outline as wide as the gas, and a slower one inside it turning the other way.
    static void Cloud(Vector3 spot, float radius, Rgba color, float time)
    {
        var ground = spot + Vector3.UnitZ * 3f;
        Puff(ground, radius, 7f, time * 2f, color with { A = 210 });
        Puff(ground, radius * 0.55f, 5f, -time * 1.5f, color with { A = 110 });
    }

    static void Puff(Vector3 middle, float radius, float lobes, float turn, Rgba color)
    {
        var last = Vector3.Zero;
        for (var i = 0; i <= CloudSides; i++)
        {
            var angle = i * MathF.Tau / CloudSides;
            var swell = radius * (1f + 0.07f * MathF.Sin(angle * lobes + turn));
            var point = middle + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * swell;
            if (i > 0)
            {
                WorldMarks.Segment(last, point, color);
            }

            last = point;
        }
    }

    // A HA over a thug the gas would catch, bobbing a little.
    static void Laugh(RPawnVillain thug, float time)
    {
        var height = thug.CylinderComponent is { IsValid: true } body ? body.CollisionHeight : 90f;
        if (WorldMarks.Screen(thug.Location + Vector3.UnitZ * (height + 18f), out var at))
        {
            var width = Draw.TrackedWidth("HA", LabelSize, LabelSpacing);
            Draw.Tracked("HA", at.X - width / 2f, at.Y - 10f + MathF.Sin(time * 6f + thug.Location.X) * 1.5f, LabelSize, LabelSpacing, Green);
        }
    }

    // A small playing card over a point, with a red pip in its corner.
    static void PlayingCard(Vector2 at, string title, string note, bool ready)
    {
        var width = MathF.Max(Draw.TrackedWidth(title, LabelSize, LabelSpacing), Draw.TextWidth(note, LabelSize)) + 18f;
        var left = at.X - width / 2f;
        var top = at.Y - LabelHeight - 10f;
        Draw.Line(at.X, top + LabelHeight, at.X, at.Y, (ready ? Green : Empty) with { A = 140 });
        Draw.Rect(left, top, width, LabelHeight, Cream);
        Draw.Frame(left, top, width, LabelHeight, 1f, CardEdge);
        Draw.Triangle(left + width - 6f, top + 3f, left + width - 3f, top + 6f, left + width - 9f, top + 6f, Red);
        Draw.Triangle(left + width - 6f, top + 9f, left + width - 3f, top + 6f, left + width - 9f, top + 6f, Red);
        Draw.Tracked(title, left + 6f, top + 3f, LabelSize, LabelSpacing, Purple);
        Draw.Text(note, left + 6f, top + 12f, LabelSize, ready ? GreenDeep : Red);
    }
}
