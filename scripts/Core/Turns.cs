#nullable enable

using System.Numerics;
using Turn = BmSDK.GameObject.FQuat;

namespace Knightfall;

// The game's quaternion and .NET's, which has the maths. Both are the same four numbers.
static class Turns
{
    public static Quaternion Numeric(Turn turn) => new(turn.X, turn.Y, turn.Z, turn.W);

    public static Turn ToTurn(Quaternion turn) => new() { X = turn.X, Y = turn.Y, Z = turn.Z, W = turn.W };
}
