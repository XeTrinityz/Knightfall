#nullable enable

using System.Runtime.InteropServices;

namespace Knightfall;

// Xbox pads, and anything Steam Input presents as one.
static class XInputPad
{
    [StructLayout(LayoutKind.Sequential)]
    struct State
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;
    }

    const int Slots = 4;
    const double ScanSeconds = 2;
    const byte TriggerPressed = 128;

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetState(uint slot, out State state);

    static readonly bool[] Present = new bool[Slots];
    static DateTime _nextScan;

    public static bool Connected { get; private set; }

    // The right stick of the last pad read, -1 to 1, right and down positive like the screen.
    public static System.Numerics.Vector2 Right { get; private set; }

    // XInput's button bits are the same values as Pad.Button.
    public static Pad.Button Read()
    {
        // Asking XInput about an empty slot is slow, so empty slots are only checked now and then.
        var scan = DateTime.UtcNow >= _nextScan;
        if (scan)
        {
            _nextScan = DateTime.UtcNow.AddSeconds(ScanSeconds);
        }

        var held = Pad.Button.None;
        var any = false;
        var right = System.Numerics.Vector2.Zero;

        for (var slot = 0; slot < Slots; slot++)
        {
            if (!Present[slot] && !scan)
            {
                continue;
            }

            var found = XInputGetState((uint)slot, out var state) == 0;
            if (found != Present[slot])
            {
                Present[slot] = found;
                Log.Info($"xinput.{slot}.{found}", found ? $"Controller {slot + 1} connected." : $"Controller {slot + 1} disconnected.");
            }

            if (found)
            {
                any = true;
                held |= (Pad.Button)state.Buttons & ~(Pad.Button.LT | Pad.Button.RT);
                var stick = new System.Numerics.Vector2(state.RightX / 32767f, -state.RightY / 32767f);
                if (stick.LengthSquared() > right.LengthSquared())
                {
                    right = stick;
                }

                if (state.LeftTrigger > TriggerPressed)
                {
                    held |= Pad.Button.LT;
                }

                if (state.RightTrigger > TriggerPressed)
                {
                    held |= Pad.Button.RT;
                }
            }
        }

        Connected = any;
        Right = right;
        return held;
    }

    public static void Stop()
    {
        Connected = false;
        Array.Clear(Present);
    }
}
