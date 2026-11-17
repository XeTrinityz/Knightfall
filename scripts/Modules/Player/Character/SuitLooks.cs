#nullable enable

namespace Knightfall;

// PLAYER > SUIT, the LOOKS rows. The looks themselves are the shared LookBook.
static class SuitLooks
{
    // Which look each suit puts on when it's worn, by suit.
    public const string PutOnStore = "player.suit_put_on";

    static readonly LookBook Book = new(new LookSpec(
        Prefix: "player.suit",
        Store: PutOnStore,
        Root: ModFiles.Looks,
        Noun: "suit",
        NoKey: "NO SUIT",
        Painter: SuitPaint.Brush));

    public static readonly Choice Saved = Book.Saved;
    public static readonly Command SaveNew = Book.SaveNew;
    public static readonly Command WearSaved = Book.WearSaved;
    public static readonly Toggle PutOn = Book.PutOn;
    public static readonly Command Delete = Book.Delete;

    public static void Refresh() => Book.Refresh();

    public static Dictionary<string, SuitLook>? PutOnLooks(string suit) => Book.PutOnLooks(suit);
}
