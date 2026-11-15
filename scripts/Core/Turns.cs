#nullable enable

using System.Numerics;
using BmSDK;
using Turn = BmSDK.GameObject.FQuat;

namespace Knightfall;

// The game's quaternion and .NET's, which has the maths. Both are the same four numbers.
static class Turns
{
    public static Quaternion Numeric(Turn turn) => new(turn.X, turn.Y, turn.Z, turn.W);

    public static Turn ToTurn(Quaternion turn) => new() { X = turn.X, Y = turn.Y, Z = turn.Z, W = turn.W };

    // Which way something turned like this points its front and its top in the world.
    public static (Vector3 Forward, Vector3 Up) Axes(Rotator turn)
    {
        const float toRadians = MathF.PI / 180f;
        var (sinPitch, cosPitch) = MathF.SinCos(turn.Pitch * toRadians);
        var (sinYaw, cosYaw) = MathF.SinCos(turn.Yaw * toRadians);
        var (sinRoll, cosRoll) = MathF.SinCos(turn.Roll * toRadians);

        var forward = new Vector3(cosPitch * cosYaw, cosPitch * sinYaw, sinPitch);
        var up = new Vector3(
            -(cosRoll * sinPitch * cosYaw + sinRoll * sinYaw),
            cosYaw * sinRoll - cosRoll * sinPitch * sinYaw,
            cosRoll * cosPitch);
        return (forward, up);
    }
}
