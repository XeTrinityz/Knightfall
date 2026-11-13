#nullable enable

namespace Knightfall;

// A hot reload builds a new assembly, so our statics start over and our own types become
// different types. Only plain .NET values may be kept here.
static class Survive
{
    public const string LogStarted = "Knightfall.LogStarted";
    public const string MenuOpen = "Knightfall.MenuOpen";
    public const string MenuPage = "Knightfall.MenuPage";
    public const string MenuTab = "Knightfall.MenuTab";
    public const string MenuRow = "Knightfall.MenuRow";
    public const string WheelSteerVotes = "Knightfall.WheelSteerVotes";
    public const string WheelRollVotes = "Knightfall.WheelRollVotes";
    public const string BorrowedPaths = "Knightfall.BorrowedPaths";
    public const string BorrowedLetAt = "Knightfall.BorrowedLetAt";
    public const string SwitchStory = "Knightfall.SwitchStory";
    public const string SwitchPlaying = "Knightfall.SwitchPlaying";
    public const string SwitchGadget = "Knightfall.SwitchGadget";
    public const string SwitchDamage = "Knightfall.SwitchDamage";
    public const string SwitchMesh = "Knightfall.SwitchMesh";
    public const string SidekickName = "Knightfall.SidekickName";
    public const string SidekickSlot = "Knightfall.SidekickSlot";
    public const string SidekickOwnsSlot = "Knightfall.SidekickOwnsSlot";
    public const string SidekickBody = "Knightfall.SidekickBody";
    public const string SidekickBot = "Knightfall.SidekickBot";
    public const string SidekickArena = "Knightfall.SidekickArena";
    public const string BodyguardBodies = "Knightfall.BodyguardBodies";
    public const string BodyguardLabels = "Knightfall.BodyguardLabels";
    public const string BodyguardRecruited = "Knightfall.BodyguardRecruited";
    public const string BodyguardHostile = "Knightfall.BodyguardHostile";
    public const string BodyguardHealth = "Knightfall.BodyguardHealth";
    public const string BodyguardOrders = "Knightfall.BodyguardOrders";

    public static T Get<T>(string key, T fallback)
    {
        return AppDomain.CurrentDomain.GetData(key) is T value ? value : fallback;
    }

    public static void Set<T>(string key, T value)
    {
        AppDomain.CurrentDomain.SetData(key, value);
    }
}
