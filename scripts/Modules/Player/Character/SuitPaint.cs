#nullable enable

using BmSDK.BmGame;

namespace Knightfall;

// PLAYER > SUIT, the PAINT rows. The painting itself is the shared Painter.
static class SuitPaint
{
    public static readonly Painter Brush = new(new PaintSpec(
        Prefix: "player.suit",
        Store: "player.suit_colours",
        Noun: "suit",
        Whole: "suit",
        ClearAllId: "clear_suit",
        NoSubject: "NO PLAYER",
        Subject: () => World.Player,
        KeyOf: worn => SuitParts.SuitKey((RPawnPlayer)worn),
        Find: worn => SuitParts.Find((RPawnPlayer)worn))
    {
        // The put-on look goes on when the suit is put on, which a reload of the mod isn't.
        PutOn = suit =>
        {
            if (Survive.Get(Survive.SuitWorn, "") == suit)
            {
                return null;
            }

            Survive.Set(Survive.SuitWorn, suit);
            return SuitLooks.PutOnLooks(suit);
        },
        Scanned = () => SuitLooks.Refresh(),
    });

    public static readonly Toggle Paint = Brush.Paint;
    public static readonly Choice Which = Brush.Which;
    public static readonly Choice Colour = Brush.Colour;
    public static readonly Slider Strength = Brush.Strength;
    public static readonly Slider Brightness = Brush.Brightness;
    public static readonly Command CopyLook = Brush.CopyLook;
    public static readonly Command PasteLook = Brush.PasteLook;
    public static readonly Command Shuffle = Brush.Shuffle;
    public static readonly Command Back = Brush.Back;
    public static readonly Command Unmark = Brush.Unmark;
    public static readonly Command ClearPart = Brush.ClearPart;
    public static readonly Command ClearSuit = Brush.ClearAll;

    // The suit worn now, as its colours are kept.
    public static string Suit => Brush.Key;

    // Anything is painted on the suit worn now.
    public static bool Painted => Brush.Painted;

    public static Dictionary<string, SuitLook> Snapshot() => Brush.Snapshot();

    public static int Wear(IReadOnlyDictionary<string, SuitLook> looks) => Brush.Wear(looks);

    public static void Tick() => Brush.Tick();

    public static void Release() => Brush.Release();

    // Keeps every suit's colours and put-on look in settings.toml, not only the suit worn now.
    public static void KeepSettings()
    {
        Brush.KeepStore();
        Settings.KeepAll(SuitLooks.PutOnStore);
    }
}
