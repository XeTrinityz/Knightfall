#nullable enable

using System.Numerics;
using BmSDK.BmGame;

namespace Knightfall;

// Man-Bat's mark while SWOOP or SNATCH is held, the way he finds his prey in the dark. Echoes ping out
// in rings from the feet of the thug he will dive on, a line runs from him down to them, and a
// tag over their head says how far it is. Letting go dives.
static partial class ManBat
{
    // Who he would dive on is looked for this often, since that means looking through every
    // thug. The marks follow them every frame.
    const float MarkEvery = 0.1f;

    const int Echoes = 3;
    const float EchoSeconds = 1.1f;
    const float EchoReach = 140f;
    const float DashLength = 40f;
    const float TagSize = 8f;
    const float TagSpacing = 1.5f;
    const float TagHeight = 22f;

    static readonly Rgba Blood = new(196, 44, 40);
    static readonly Rgba Bone = new(232, 218, 194);
    static readonly Rgba Ink = new(12, 6, 6, 230);

    static readonly string[] Metres = Enumerable.Range(0, 31).Select(metres => $"{metres} M AWAY").ToArray();

    static bool _aiming;
    static float _lookedAt = -100f;

    public static bool Marking() => _aiming && _prey is { IsAlive: true } && World.Player is { IsValid: true };

    // While the key is held he keeps picking the thug nearest the crosshair. Let go, he dives on
    // the last one picked. Used from the menu or the wheel there is no key to hold, so he dives
    // at once.
    static void Aiming(RPawnPlayer player)
    {
        if (!_aiming)
        {
            return;
        }

        if (Hotkeys.IsHeld(_snatching ? SnatchKey : SwoopKey))
        {
            var now = World.Now;
            if (now - _lookedAt >= MarkEvery || now < _lookedAt)
            {
                _lookedAt = now;
                _prey = Prey(player);
            }

            return;
        }

        _aiming = false;
        if (_prey is { IsAlive: true } prey && Crowd.Standing(prey))
        {
            _act = Act.Dive;
            _swoopAt = World.Now;
            _stuck = 0f;
        }
        else
        {
            _prey = null;
        }
    }

    public static void Marks(float time)
    {
        if (World.Player is not { IsValid: true } player || _prey is not { IsAlive: true } prey || prey.CylinderComponent is not { IsValid: true } body)
        {
            return;
        }

        var height = body.CollisionHeight;
        var feet = prey.Location - Vector3.UnitZ * (height - 3f);

        // Echoes going out from him, each fading as it spreads.
        for (var i = 0; i < Echoes; i++)
        {
            var phase = (time / EchoSeconds + (float)i / Echoes) % 1f;
            var fade = (1f - phase) * (1f - phase);
            WorldMarks.Ring(feet, 30f + EchoReach * phase, Blood with { A = (byte)(40 + 200 * fade) }, time * 0.3f + i, dashed: i % 2 == 1);
        }

        WorldMarks.Ring(feet, 26f, Bone with { A = 200 }, -time * 0.8f, dashed: true);

        // The way he will dive, dashed and running toward them.
        var from = player.Location;
        var to = prey.Location;
        var way = to - from;
        var length = way.Length();
        if (length > 1f)
        {
            var step = way / length;
            var shift = time * 120f % (DashLength * 2f);
            for (var at = shift; at < length; at += DashLength * 2f)
            {
                var end = MathF.Min(at + DashLength, length);
                WorldMarks.Segment(from + step * at, from + step * end, Blood with { A = 150 });
            }
        }

        if (WorldMarks.Screen(prey.Location + Vector3.UnitZ * (height + 25f), out var above))
        {
            var metres = (int)MathF.Round(length / Teleport.UnitsPerMetre);
            Tag(above, _snatching ? "SNATCH" : "SWOOP", metres >= 0 && metres < Metres.Length ? Metres[metres] : "LET GO TO DIVE", time);
        }
    }

    // A dark tag with his two ears notched in its top, sitting over a point.
    static void Tag(Vector2 at, string title, string note, float time)
    {
        var width = MathF.Max(Draw.TrackedWidth(title, TagSize, TagSpacing), Draw.TextWidth(note, TagSize)) + 14f;
        var left = at.X - width / 2f;
        var top = at.Y - TagHeight - 8f;
        Draw.Line(at.X, top + TagHeight, at.X, at.Y, Blood with { A = 140 });
        Draw.Rect(left, top, width, TagHeight, Ink);
        Draw.Frame(left, top, width, TagHeight, 1f, Blood with { A = 170 });
        var ear = 5f + MathF.Sin(time * 6f) * 0.5f;
        Draw.Triangle(left + 3f, top, left + 9f, top, left + 4f, top - ear, Blood);
        Draw.Triangle(left + width - 9f, top, left + width - 3f, top, left + width - 4f, top - ear, Blood);
        Draw.Tracked(title, left + 7f, top + 3f, TagSize, TagSpacing, Bone);
        Draw.Text(note, left + 7f, top + 12f, TagSize, Blood);
    }
}
