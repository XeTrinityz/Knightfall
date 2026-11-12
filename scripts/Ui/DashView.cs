#nullable enable

namespace Knightfall;

enum DashSpot
{
    OnCar,
    MiddleRight,
    MiddleLeft,
    BottomLeft,
    BottomRight,
}

// What a theme is given to draw the driving readout. Themes read it and never change it. All the
// text comes from small caches, since every distinct text drawn makes a native string for good.
sealed class DashView
{
    // 1 when fully shown and 0 when gone. It eases in and out.
    public float Shown;

    // Seconds on a clock that keeps running. For things that loop.
    public float Time;

    // Where on the screen it goes.
    public DashSpot Spot;

    // For OnCar, the point on the car the readout's left edge hangs from, and the world
    // directions one unit of the readout goes across and down, so its size in the world.
    public System.Numerics.Vector3 Anchor;
    public System.Numerics.Vector3 Across;
    public System.Numerics.Vector3 Down;

    public Hologram.Panel Panel => new(Anchor, Across, Down);

    // For OnCar, the point on the car the projection comes from.
    public System.Numerics.Vector3 Emitter;

    public string Title = "BATMOBILE";

    // BOOST, AUTOPILOT, BATTLE, or empty while just driving.
    public string Mode = "";

    // True while boosting, which themes light up.
    public bool Hot;

    public string Speed = "0";
    public string Units = "MPH";

    // How far the speed is towards the top of the scale, 0 to 1.
    public float SpeedFill;

    // The distance the autopilot has left, or empty.
    public string Note = "";

    public float Armour;

    // Low enough that themes warn about it.
    public bool LowArmour => Armour < 0.25f;

    public string ArmourText = "";
    public float Power;
    public string PowerText = "";

    // Best speed and longest jump this session, shown when Stats is on.
    public bool Stats;
    public string Best = "";
    public string Jump = "";
}
