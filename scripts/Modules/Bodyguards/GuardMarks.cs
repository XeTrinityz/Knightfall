#nullable enable

using System.Numerics;
using BmSDK.Engine;

namespace Knightfall;

// SPAWNER > BODYGUARDS, MARKERS. A marker over each bodyguard with their health, their name and
// what they are doing when near, and an arrow on the edge of the screen for one out of view.
static class GuardMarks
{
    // How far over their head the marker floats.
    const float Above = 28f;

    // Names show inside this, and arrows for ones off screen inside the other.
    const float NameRange = 3000f;
    const float ArrowRange = 12000f;

    const float BarWidth = 30f;
    const float BarHeight = 3f;
    const float NameSize = 8f;

    static readonly Rgba WithYou = new(96, 226, 150);
    static readonly Rgba Fighting = new(255, 178, 70);
    static readonly Rgba Down = new(150, 156, 166);
    static readonly Rgba Ink = new(8, 10, 14);
    static readonly Rgba White = new(255, 255, 255);

    public static readonly Toggle On = new(
        "spawner.bodyguards.markers", "BODYGUARD MARKERS",
        "A marker over each bodyguard with their health and name, and an edge arrow for any you can't see. Green is with you, amber is fighting, grey is knocked out.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Catalog.Section Section = new("MARKERS", On);

    public static bool Showing => On.On && Bodyguards.All.Count > 0;

    // Drawn while the hologram is framed for this view, flat on the screen.
    public static void Render(float time)
    {
        if (!Showing)
        {
            return;
        }

        Hologram.Pop();
        Draw.Place(0f, 0f, 1f);
        Draw.Fade(1f);
        var you = Teleport.Mover is { IsValid: true } mover ? mover.Location : Hologram.Eye;
        foreach (var guard in Bodyguards.All)
        {
            var body = guard.Body;
            if (body.IsValid && !body.bDeleteMe && !body.bHidden)
            {
                Mark(guard, you, time);
            }
        }
    }

    static void Mark(Guard guard, Vector3 you, float time)
    {
        var body = guard.Body;
        var down = !Crowd.Standing(body);
        var color = down ? Down : guard.Target is not null ? Fighting : WithYou;
        var height = body.CylinderComponent is { IsValid: true } shape ? shape.CollisionHeight : 90f;
        var head = body.Location + Vector3.UnitZ * (height * body.DrawScale + Above);
        var away = Vector3.Distance(body.Location, you);

        if (Hologram.Screen(head, out var pixels))
        {
            var spot = pixels / Draw.Scale;
            if (spot.X >= Threats.Edge && spot.X <= Draw.ScreenWidth - Threats.Edge && spot.Y >= Threats.Edge && spot.Y <= Draw.ScreenHeight - Threats.Edge)
            {
                Pin(guard, spot, color, down, away <= NameRange, time);
                return;
            }
        }

        if (!down && away <= ArrowRange)
        {
            Threats.Arrow(Hologram.Direction(head), guard.Label, Threats.Distance(away), color);
        }
    }

    // A chevron pointing down at them, their health over it, and their name over that when near.
    static void Pin(Guard guard, Vector2 at, Rgba color, bool down, bool named, float time)
    {
        // A slow bob, each a little out of step, so it reads as a marker and not part of the world.
        var y = at.Y + MathF.Sin(time * 3f + guard.Slot) * 1.5f;
        Draw.Triangle(at.X - 6f, y - 8f, at.X + 6f, y - 8f, at.X, y, color);

        var body = guard.Body;
        var full = Math.Max(Math.Max(body.HealthMax, body.MaxHealth), 1);
        var fill = down ? 0f : Math.Clamp(body.Health / (float)full, 0f, 1f);
        var left = at.X - BarWidth / 2f;
        var top = y - 8f - 4f - BarHeight;
        Draw.Rect(left - 1f, top - 1f, BarWidth + 2f, BarHeight + 2f, Ink with { A = 170 });
        Draw.Rect(left, top, BarWidth * fill, BarHeight, color);

        if (!named)
        {
            return;
        }

        var state = down ? "DOWN" : guard.Target is not null ? "FIGHTING" : Bodyguards.Orders.Index switch
        {
            Bodyguards.Hold => "HOLDING",
            Bodyguards.StandDown => "STOOD DOWN",
            _ => "WITH YOU",
        };
        var width = MathF.Max(Draw.TrackedWidth(guard.Label, NameSize, 1.5f), Draw.TextWidth(state, NameSize)) + 12f;
        var tagTop = top - 5f - Threats.TagHeight;
        Draw.Rect(at.X - width / 2f, tagTop, width, Threats.TagHeight, Ink with { A = 170 });
        Draw.Rect(at.X - width / 2f, tagTop, 2f, Threats.TagHeight, color);
        Draw.Tracked(guard.Label, at.X - width / 2f + 6f, tagTop + 3f, NameSize, 1.5f, White);
        Draw.Text(state, at.X - width / 2f + 6f, tagTop + 12f, NameSize, color);
    }
}
