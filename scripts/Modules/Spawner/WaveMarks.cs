#nullable enable

using System.Numerics;
using BmSDK.BmGame;

namespace Knightfall;

// SPAWNER > WAVES. Once only a few of a wave are left, each gets a marker over their head and an
// arrow on the edge of the screen when out of view, so the last of them are easy to find.
static class WaveMarks
{
    // Markers show once this many or fewer are left.
    const int Last = 3;

    const float Above = 28f;
    const float DistanceSize = 8f;

    static readonly Rgba Live = new(250, 184, 78);
    static readonly Rgba Ink = new(8, 10, 14);

    public static bool Showing => Waves.On.On && Waves.Left is > 0 and <= Last;

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
        var index = 0;
        foreach (var body in Waves.Standing)
        {
            Mark(body, you, time, index++);
        }
    }

    static void Mark(RPawnVillain body, Vector3 you, float time, int index)
    {
        var height = body.CylinderComponent is { IsValid: true } shape ? shape.CollisionHeight : 90f;
        var head = body.Location + Vector3.UnitZ * (height * body.DrawScale + Above);
        var away = Vector3.Distance(body.Location, you);
        if (Hologram.Screen(head, out var pixels))
        {
            var spot = pixels / Draw.Scale;
            if (spot.X >= Threats.Edge && spot.X <= Draw.ScreenWidth - Threats.Edge && spot.Y >= Threats.Edge && spot.Y <= Draw.ScreenHeight - Threats.Edge)
            {
                // A slow bob, each a little out of step, so it reads as a marker and not part of the world.
                var y = spot.Y + MathF.Sin(time * 3f + index) * 1.5f;
                Draw.Triangle(spot.X - 7f, y - 9f, spot.X + 7f, y - 9f, spot.X, y, Live);
                var text = Threats.Distance(away);
                var width = Draw.TextWidth(text, DistanceSize) + 10f;
                Draw.Rect(spot.X - width / 2f, y - 9f - 4f - 13f, width, 13f, Ink with { A = 170 });
                Draw.Text(text, spot.X - width / 2f + 5f, y - 9f - 4f - 11.5f, DistanceSize, Live);
                return;
            }
        }

        Threats.Arrow(Hologram.Direction(head), "ENEMY", Threats.Distance(away), Live);
    }
}
