using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Scanner.Server.Services;

public enum BluetoothLinkState { Unknown, Connected, Disconnected }

public interface IBluetoothConnectionProbe
{
    BluetoothLinkState Check(BarcodeReaderChoice reader);
}

/// <summary>
/// Reads Windows' connection flag for the selected classic Bluetooth device.
/// This does not issue an inquiry, connect to the reader, or use time since its last scan.
/// Windows can take time to report radio loss; this is not a reader heartbeat.
/// Call only from a background worker.
/// </summary>
public sealed class WindowsBluetoothConnectionProbe : IBluetoothConnectionProbe
{
    private const int ErrorNoMoreItems = 259;

    public BluetoothLinkState Check(BarcodeReaderChoice reader)
    {
        if (!OperatingSystem.IsWindows() || !TryGetAddress(reader.DeviceId, out var address)) return BluetoothLinkState.Unknown;
        try { return ReadState(address); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or System.Security.SecurityException or MarshalDirectiveException)
        {
            // Unsupported API/access must not be interpreted as a disconnected reader.
            return BluetoothLinkState.Unknown;
        }
    }

    /// <summary>Extracts the nonzero 48-bit address from the saved outgoing SPP identity.</summary>
    public static bool TryGetAddress(string? deviceId, out ulong address)
    {
        address = 0;
        if (deviceId is null || deviceId.Length < 14 || deviceId[12] != '_') return false;
        if (!deviceId.AsSpan(13).ContainsAnyExcept("0123456789abcdefABCDEF"))
            return ulong.TryParse(deviceId.AsSpan(0, 12), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address)
                && address != 0;
        return false;
    }

    private static BluetoothLinkState ReadState(ulong address)
    {
        // hRadio=null explicitly searches all local radios. Include remembered and
        // disconnected devices, but never start a radio inquiry on this polling path.
        // https://learn.microsoft.com/windows/win32/api/bluetoothapis/ns-bluetoothapis-bluetooth_device_search_params
        var search = new DeviceSearchParameters
        {
            Size = (uint)Marshal.SizeOf<DeviceSearchParameters>(),
            ReturnAuthenticated = 1, ReturnRemembered = 1, ReturnUnknown = 1, ReturnConnected = 1,
            IssueInquiry = 0, TimeoutMultiplier = 0, Radio = IntPtr.Zero
        };
        var info = DeviceInfo.Create();
        using var handle = BluetoothFindFirstDevice(ref search, ref info);
        if (handle.IsInvalid) return BluetoothLinkState.Unknown;
        var foundDisconnected = false;
        do
        {
            if (info.Address == address)
            {
                if (info.Connected != 0) return BluetoothLinkState.Connected;
                foundDisconnected = true;
            }
            info = DeviceInfo.Create();
        } while (BluetoothFindNextDevice(handle, ref info));

        // Multiple radios may retain records for the same address. A connected
        // match wins; only a complete, successful enumeration can confirm absence
        // of a connection. A missing device record is unknown, not disconnected.
        if (Marshal.GetLastPInvokeError() != ErrorNoMoreItems) return BluetoothLinkState.Unknown;
        return foundDisconnected ? BluetoothLinkState.Disconnected : BluetoothLinkState.Unknown;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSearchParameters
    {
        public uint Size;
        public int ReturnAuthenticated, ReturnRemembered, ReturnUnknown, ReturnConnected, IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr Radio;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    // BLUETOOTH_ADDRESS is an 8-byte union; Win32 BOOL fields are 4 bytes.
    // BLUETOOTH_MAX_NAME_SIZE is 248 WCHARs. Default Win32 packing aligns Address to 8.
    // https://learn.microsoft.com/windows/win32/api/bluetoothapis/ns-bluetoothapis-bluetooth_device_info_struct
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInfo
    {
        public uint Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected, Remembered, Authenticated;
        public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
        public static DeviceInfo Create() => new() { Size = (uint)Marshal.SizeOf<DeviceInfo>(), Name = string.Empty };
    }

    private sealed class DeviceSearchHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DeviceSearchHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => BluetoothFindDeviceClose(handle);
    }

    [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
    private static extern DeviceSearchHandle BluetoothFindFirstDevice(ref DeviceSearchParameters search, ref DeviceInfo info);
    [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(DeviceSearchHandle handle, ref DeviceInfo info);
    [DllImport("bthprops.cpl", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr handle);
}
