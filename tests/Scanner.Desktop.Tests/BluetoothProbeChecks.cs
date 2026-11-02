using System.Reflection;
using System.Runtime.InteropServices;
using Scanner.Server.Services;

internal static class BluetoothProbeChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(WindowsBluetoothConnectionProbe.TryGetAddress("AE3B00256A8D_C00000000", out var address) && address == 0xAE3B00256A8D,
            "Saved SPP identity preserves the Windows 48-bit Bluetooth address without reversing its bytes");
        check(WindowsBluetoothConnectionProbe.TryGetAddress("ae3b00256a8d_c00000000", out address) && address == 0xAE3B00256A8D,
            "Bluetooth identity parsing accepts registry hex case differences");
        check(WindowsBluetoothConnectionProbe.TryGetAddress("000000000001_0", out address) && address == 1,
            "Leading zeroes in a valid nonzero remote address are preserved");
        foreach (var invalid in new string?[] { null, "", "reader", "COM3", "AE3B00256A8D", "AE3B00256A8D_",
            "000000000000_C00000000", "AE3B00256A8G_C00000000", " AE3B00256A8D_C00000000", "AE3B00256A8D_C00000000 ",
            "AE3B00256A8D-C00000000", "AE3B00256A8D_C0000000G", "AE3B00256A8D__C00000000", "AE3B00256A8_C00000000" })
            check(!WindowsBluetoothConnectionProbe.TryGetAddress(invalid, out _), "Invalid/incoming reader identity never selects an unrelated Bluetooth device");
        check(new WindowsBluetoothConnectionProbe().Check(new("not-a-bluetooth-device", "Reader", "COM3")) == BluetoothLinkState.Unknown,
            "An unresolved identity is unknown, not a lost reader connection");

        // Win32 ABI regression checks: a wrong BOOL width, address alignment or WCHAR
        // count makes Windows reject the query or overwrite the marshalled buffer.
        const BindingFlags nested = BindingFlags.NonPublic;
        var info = typeof(WindowsBluetoothConnectionProbe).GetNestedType("DeviceInfo", nested)!;
        check(Marshal.SizeOf(info) == 560 && Marshal.OffsetOf(info, "Address").ToInt32() == 8
            && Marshal.OffsetOf(info, "Connected").ToInt32() == 20 && Marshal.OffsetOf(info, "Name").ToInt32() == 64,
            "BLUETOOTH_DEVICE_INFO matches the Win32 ABI");
        var search = typeof(WindowsBluetoothConnectionProbe).GetNestedType("DeviceSearchParameters", nested)!;
        check(Marshal.SizeOf(search) == (IntPtr.Size == 8 ? 40 : 32)
            && Marshal.OffsetOf(search, "Radio").ToInt32() == (IntPtr.Size == 8 ? 32 : 28),
            "BLUETOOTH_DEVICE_SEARCH_PARAMS preserves platform HANDLE alignment");
    }
}
