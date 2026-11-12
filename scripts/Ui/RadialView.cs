#nullable enable

using System.Numerics;

namespace Knightfall;

// What the favourites radial shows this frame. Radial fills it in and the current theme draws it.
sealed class RadialView
{
    public const int MostSlots = 12;

    // The favourites on this page. The wheel has one slot for each.
    public readonly List<Feature> Slots = new();

    // The slot pointed at, or -1 while the pointer is in the hub.
    public int Hover = -1;

    // Where the pointer is, in units from the middle at full size.
    public Vector2 Pointer;

    // How many favourites come before this page, for numbering the slots.
    public int First;

    public int Page;

    public int Pages;

    // TAP stays open after a use. HOLD uses the slot pointed at on letting go.
    public bool Tap;

    // Seconds since it opened, and since the page last turned.
    public float Age;

    public float PageAge;

    // The slot used last, and how long ago.
    public int Flashed = -1;

    public float FlashAge;

    // RADIAL SIZE.
    public float Size = 1f;

    // A clock for looping effects, in seconds.
    public float Time;

    // Now, as a Stopwatch timestamp, for values that flash after a change.
    public long Now;

    // How far each slot has lifted towards being pointed at, 0 to 1, eased.
    public readonly float[] Lifts = new float[MostSlots];

    public readonly List<GuideLine> Hints = new();

    public Feature? Focus => Hover >= 0 && Hover < Slots.Count ? Slots[Hover] : null;

    // A toggle that is on, or a choice moved off its default.
    public static bool Lit(Feature feature) => feature switch
    {
        Toggle toggle => toggle.On,
        Choice choice => !choice.IsDefault,
        _ => false,
    };

    public static string Kind(Feature feature) => feature switch
    {
        Toggle => "TOGGLE",
        Choice => "CHOICE",
        _ => "COMMAND",
    };
}
