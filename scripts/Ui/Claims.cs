#nullable enable

namespace Knightfall;

// What the overlays drew on screen this frame, so notifications can keep clear of them. Each
// overlay is measured as one box while it draws.
static class Claims
{
    static readonly List<Theme.Area> Areas = new();

    public static IReadOnlyList<Theme.Area> All => Areas;

    public static void Clear() => Areas.Clear();

    public static void Measure(Action<BmSDK.Engine.Canvas> render, BmSDK.Engine.Canvas canvas)
    {
        Draw.Measure();
        try
        {
            render(canvas);
        }
        finally
        {
            if (Draw.Measured() is { } area)
            {
                Areas.Add(area);
            }
        }
    }

    // For a panel drawn inside another overlay's frame, like one beside the menu.
    public static void Measure(Action draw)
    {
        Draw.Measure();
        try
        {
            draw();
        }
        finally
        {
            if (Draw.Measured() is { } area)
            {
                Areas.Add(area);
            }
        }
    }
}
