#nullable enable

namespace Knightfall;

// BATMOBILE > SKIN, the LOOKS rows. The looks themselves are the shared LookBook, kept in
// data/looks/batmobile/<skin>/.
static class CarLooks
{
    // Which look each skin puts on when it's applied, by skin.
    public const string PutOnStore = "batmobile.skin_put_on";

    static readonly LookBook Book = new(new LookSpec(
        Prefix: "batmobile.skin",
        Store: PutOnStore,
        Root: Path.Combine(ModFiles.Looks, "batmobile"),
        Noun: "skin",
        NoKey: "NO BATMOBILE HERE",
        Painter: CarPaint.Brush));

    public static readonly Choice Saved = Book.Saved;
    public static readonly Command SaveNew = Book.SaveNew;
    public static readonly Command WearSaved = Book.WearSaved;
    public static readonly Toggle PutOn = Book.PutOn;
    public static readonly Command Delete = Book.Delete;

    public static void Refresh() => Book.Refresh();

    public static Dictionary<string, SuitLook>? PutOnLooks(string skin) => Book.PutOnLooks(skin);
}
