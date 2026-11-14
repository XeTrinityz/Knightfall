#nullable enable

namespace Knightfall;

// Every number the mod uses to reach game memory itself, where BmSDK has no binding. Each was
// read from the Steam build of the game and is only right for a build laid out the same way.
// The code that uses one checks what it finds first where it can, and goes without when the
// check fails. After a game or BmSDK update these are the first things to test.
static class Offsets
{
    // The object table has a fixed number of 8-byte slots, and the count of slots in use sits
    // this far past the last one. Checked in ObjectTable by the count being in range.
    public const int ObjectSlots = 785000;
    public const int ObjectsUsedAfterSlots = 4;

    // The slot being played is a value of the game's own, written by the controller's
    // SetActiveSaveName. These are where that function sits in the controller's table, where
    // its code is and where the value is, the last two from the start of the game's exe.
    // Checked in Saves by the table entry pointing at that code.
    public const int SetSaveNameEntry = 4464;
    public const nint SetSaveNameCode = 0x142BA0;
    public const nint SaveSlotValue = 0x3101F6C;

    // The game's music player keeps what it last asked for as pairs of numbers, a state group
    // and the state picked in it. This is where the pairs start, from the start of the game's
    // exe. Checked in GameSounds by one of the groups being the soundtrack's.
    public const nint MusicStates = 0x3AF5E4C;
    public const int MusicStatePairs = 4;

    // A script Canvas keeps its native canvas here, and a native Function keeps its code here.
    // The code of PushTranslationMatrix is searched this far for the call to the push that
    // takes any matrix. Checked in Hologram by the first bytes of that push.
    public const int CanvasNative = 148;
    public const int FunctionNativeCode = 188;
    public const int MatrixPushSearch = 0x100;
    public static readonly byte[] MatrixPushStart = { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08 };

    // The target field of Harley's flytrap, RThugEatingPlant. BmSDK's binding for it only takes
    // an armed thug, so the pointer is written here. It is a script field's place, the same
    // kind of number every BmSDK binding is built on. Not checked.
    public const int FlytrapTarget = 692;

    // An enemy's list of the hits a story fight allows, RPawnVillain.DamageAllowed. BmSDK can't
    // read a list of that kind, so its length is read here, after the list's 8-byte pointer.
    // Checked in Sidekick by the length being one the list could have.
    public const int VillainDamageAllowed = 6204;
    public const int ListLengthAfterData = 8;
}
