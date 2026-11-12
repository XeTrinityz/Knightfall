#nullable enable

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Knightfall;

// DualShock 4 and DualSense pads, read straight from their HID reports. Windows has no API for
// them, and XInput only sees them when Steam Input or a similar tool is translating.
static class SonyPad
{
    sealed class Device
    {
        public required string Path;
        public required string Model;
        public required bool DualSense;
        public required bool Bluetooth;
        public required FileStream Stream;
        public required byte[] Report;
        public ValueTask<int> Pending;
        public bool Reading;
        public Pad.Button Held;
        public System.Numerics.Vector2 Right;
    }

    // The right stick of the pad pushed furthest, -1 to 1, right and down positive like the screen.
    public static System.Numerics.Vector2 Right { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    struct RawDevice
    {
        public IntPtr Handle;
        public uint Type;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct RawInfo
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public uint Vendor;
        [FieldOffset(12)] public uint Product;
        [FieldOffset(20)] public ushort UsagePage;
        [FieldOffset(22)] public ushort Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct Caps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputLength;
        public ushort OutputLength;
        public ushort FeatureLength;
        public fixed ushort Rest[27];
    }

    const uint Sony = 0x054C;
    const uint TypeHid = 2;
    const uint DeviceName = 0x20000007;
    const uint DeviceInfo = 0x2000000B;
    const uint Failed = uint.MaxValue;
    const uint GenericRead = 0x80000000;
    const uint ShareReadWrite = 0x00000003;
    const uint OpenExisting = 3;
    const uint Overlapped = 0x40000000;
    const int CapsOk = 0x00110000;
    const int UsbReportLength = 64;
    const int ReportsPerFrame = 4;
    const double ScanSeconds = 2;

    [DllImport("user32.dll")]
    static extern uint GetRawInputDeviceList([Out] RawDevice[]? list, ref uint count, uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, ref RawInfo info, ref uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceName(IntPtr device, uint command, [Out] char[]? name, ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr data, out Caps caps);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetNumInputBuffers(SafeFileHandle device, uint count);

    static readonly List<Device> Devices = new();
    static readonly HashSet<string> Refused = new(StringComparer.OrdinalIgnoreCase);
    static DateTime _nextScan;

    public static bool Connected => Devices.Count > 0;

    public static Pad.Button Read()
    {
        if (DateTime.UtcNow >= _nextScan)
        {
            _nextScan = DateTime.UtcNow.AddSeconds(ScanSeconds);
            Scan();
        }

        var held = Pad.Button.None;
        var right = System.Numerics.Vector2.Zero;
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            var device = Devices[i];
            if (Pump(device))
            {
                held |= device.Held;
                if (device.Right.LengthSquared() > right.LengthSquared())
                {
                    right = device.Right;
                }

                continue;
            }

            Devices.RemoveAt(i);
            device.Stream.Dispose();
            Log.Info("sony.gone." + device.Path, $"{device.Model} disconnected.");
        }

        Right = right;
        return held;
    }

    public static void Stop()
    {
        foreach (var device in Devices)
        {
            device.Stream.Dispose();
        }

        Devices.Clear();
    }

    // Takes the reports that have arrived and keeps one read waiting. No thread of ours is
    // involved, which matters because a hot reload has to be able to unload this code.
    static bool Pump(Device device)
    {
        try
        {
            for (var i = 0; i < ReportsPerFrame; i++)
            {
                if (!device.Reading)
                {
                    device.Pending = device.Stream.ReadAsync(device.Report.AsMemory());
                    device.Reading = true;
                }

                if (!device.Pending.IsCompleted)
                {
                    break;
                }

                device.Reading = false;
                var length = device.Pending.Result;
                if (length <= 0)
                {
                    return false;
                }

                Parse(device, length);
            }

            return true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Both pads use the same three button bytes. Only where they sit in the report differs, by
    // model, by cable or Bluetooth, and by which report the pad is sending.
    static void Parse(Device device, int length)
    {
        var report = device.Report;
        var id = report[0];
        int at;

        if (device.DualSense)
        {
            at = id == 0x31 ? 9 : id == 0x01 ? (device.Bluetooth ? 5 : 8) : -1;
        }
        else
        {
            at = id == 0x11 ? 7 : id == 0x01 ? 5 : -1;
        }

        if (at < 0 || at + 1 >= length)
        {
            return;
        }

        var face = report[at];
        var other = report[at + 1];

        // The sticks sit just before the buttons, apart from the DualSense's triggers and counter.
        var sticks = device.DualSense ? (id == 0x31 ? 2 : 1) : (id == 0x11 ? 3 : 1);
        if (sticks + 3 < length)
        {
            device.Right = new System.Numerics.Vector2((report[sticks + 2] - 128) / 127f, (report[sticks + 3] - 128) / 127f);
        }

        var held = (face & 0x0F) switch
        {
            0 => Pad.Button.DPadUp,
            1 => Pad.Button.DPadUp | Pad.Button.DPadRight,
            2 => Pad.Button.DPadRight,
            3 => Pad.Button.DPadRight | Pad.Button.DPadDown,
            4 => Pad.Button.DPadDown,
            5 => Pad.Button.DPadDown | Pad.Button.DPadLeft,
            6 => Pad.Button.DPadLeft,
            7 => Pad.Button.DPadLeft | Pad.Button.DPadUp,
            _ => Pad.Button.None,
        };

        if ((face & 0x10) != 0) held |= Pad.Button.X;
        if ((face & 0x20) != 0) held |= Pad.Button.A;
        if ((face & 0x40) != 0) held |= Pad.Button.B;
        if ((face & 0x80) != 0) held |= Pad.Button.Y;
        if ((other & 0x01) != 0) held |= Pad.Button.LB;
        if ((other & 0x02) != 0) held |= Pad.Button.RB;
        if ((other & 0x04) != 0) held |= Pad.Button.LT;
        if ((other & 0x08) != 0) held |= Pad.Button.RT;
        if ((other & 0x10) != 0) held |= Pad.Button.Back;
        if ((other & 0x20) != 0) held |= Pad.Button.Start;
        if ((other & 0x40) != 0) held |= Pad.Button.LeftStick;
        if ((other & 0x80) != 0) held |= Pad.Button.RightStick;

        device.Held = held;
    }

    static void Scan()
    {
        var size = (uint)Marshal.SizeOf<RawDevice>();
        uint count = 0;
        if (GetRawInputDeviceList(null, ref count, size) == Failed || count == 0)
        {
            return;
        }

        var list = new RawDevice[count];
        var found = GetRawInputDeviceList(list, ref count, size);
        if (found == Failed)
        {
            return;
        }

        for (var i = 0; i < found; i++)
        {
            if (list[i].Type != TypeHid)
            {
                continue;
            }

            var info = new RawInfo { Size = 32 };
            uint infoSize = 32;
            if (GetRawInputDeviceInfo(list[i].Handle, DeviceInfo, ref info, ref infoSize) == Failed)
            {
                continue;
            }

            // Usage page 1, usage 5 is the gamepad itself. The pads expose other things too.
            if (info.Vendor != Sony || info.UsagePage != 1 || info.Usage != 5 || !Known(info.Product, out var model, out var dualSense))
            {
                continue;
            }

            var path = PathOf(list[i].Handle);
            if (path is null || Devices.Any(device => string.Equals(device.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Open(path, model, dualSense);
        }
    }

    static bool Known(uint product, out string model, out bool dualSense)
    {
        (model, dualSense) = product switch
        {
            0x05C4 or 0x09CC or 0x0BA0 => ("DualShock 4", false),
            0x0CE6 => ("DualSense", true),
            0x0DF2 => ("DualSense Edge", true),
            _ => ("", false),
        };

        return model.Length > 0;
    }

    static string? PathOf(IntPtr handle)
    {
        uint length = 0;
        GetRawInputDeviceName(handle, DeviceName, null, ref length);
        if (length == 0 || length > 1024)
        {
            return null;
        }

        var name = new char[length];
        var copied = GetRawInputDeviceName(handle, DeviceName, name, ref length);
        if (copied == Failed || copied == 0)
        {
            return null;
        }

        return new string(name).TrimEnd('\0');
    }

    static void Open(string path, string model, bool dualSense)
    {
        var handle = CreateFileW(path, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, Overlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            if (Refused.Add(path))
            {
                Log.Info($"{model} found but it could not be opened. Another program may be holding it. It still works if Steam Input is on.");
            }

            return;
        }

        Refused.Remove(path);
        var length = InputLength(handle);
        if (length <= 0)
        {
            handle.Dispose();
            return;
        }

        // Windows queues reports for us. A short queue keeps what we read close to right now.
        HidD_SetNumInputBuffers(handle, 2);

        var bluetooth = length > UsbReportLength;
        Devices.Add(new Device
        {
            Path = path,
            Model = model,
            DualSense = dualSense,
            Bluetooth = bluetooth,
            Stream = new FileStream(handle, FileAccess.Read, 1, isAsync: true),
            Report = new byte[length],
        });

        Log.Info("sony.found." + path, $"{model} connected over {(bluetooth ? "Bluetooth" : "USB")}.");
    }

    static int InputLength(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var data))
        {
            return 0;
        }

        try
        {
            return HidP_GetCaps(data, out var caps) == CapsOk ? caps.InputLength : 0;
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }
}
