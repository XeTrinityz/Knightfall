#nullable enable

using BmSDK.Framework;

namespace Knightfall;

// The game's object table has a fixed 785,000 slots. When they are all taken it dies with
// "FNullAllocator is not allowed to allocate!". A character's packages add 14,000 to 32,000
// objects, so anything that loads one checks the room left first.
static unsafe class ObjectTable
{
    public const int Capacity = Offsets.ObjectSlots;

    // The slots sit inline with the used count right after them. A freed slot is left empty, so
    // the live ones are counted. It only runs before a package load.
    public static int Alive
    {
        get
        {
            try
            {
                var slots = (ulong*)MemUtil.GetIntPointer(GameDefine.Current.GObjObjects);
                var used = *(int*)((byte*)slots + Capacity * 8L + Offsets.ObjectsUsedAfterSlots);
                if (used <= 0 || used > Capacity)
                {
                    return -1;
                }

                var alive = 0;
                for (var i = 0; i < used; i++)
                {
                    if (slots[i] != 0)
                    {
                        alive++;
                    }
                }

                return alive;
            }
            catch
            {
                return -1;
            }
        }
    }

    public static int Headroom => Alive is var alive and >= 0 ? Capacity - alive : int.MaxValue;
}
