using System.IO.Ports;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Scanner.Server.Services;

public sealed record BarcodeReaderChoice(string DeviceId, string Name, string PortName)
{
    public string DisplayName => $"{Name} — {PortName}";
}

public static partial class BluetoothReaderDiscovery
{
    private const string SppService = "{00001101-0000-1000-8000-00805f9b34fb}";

    // The Windows Bluetooth enumerator uses a nonzero remote address for outgoing
    // SPP instances; its incoming/listening instance has the all-zero address.
    // Do not infer direction from a COM number or a localized friendly name.
    public static string? OutgoingAddress(string service, string instance)
    {
        if (!service.StartsWith(SppService + "_", StringComparison.OrdinalIgnoreCase)) return null;
        var match = RemoteAddress().Match(instance);
        return match.Success && match.Groups[1].Value != "000000000000"
            ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    public static IReadOnlyList<BarcodeReaderChoice> Discover()
    {
        var activePorts = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var devices = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM");
        if (devices is null) return [];
        var result = new List<BarcodeReaderChoice>();
        foreach (var serviceName in devices.GetSubKeyNames().Where(n => n.StartsWith(SppService + "_", StringComparison.OrdinalIgnoreCase)))
        {
            using var service = devices.OpenSubKey(serviceName);
            if (service is null) continue;
            foreach (var instanceName in service.GetSubKeyNames())
            {
                var address = OutgoingAddress(serviceName, instanceName);
                if (address is null) continue;
                using var instance = service.OpenSubKey(instanceName);
                using var parameters = instance?.OpenSubKey("Device Parameters");
                if (parameters?.GetValue("PortName") is not string port || !activePorts.Contains(port)) continue;
                var name = DeviceName(devices, address) ?? $"Bluetooth SPP {address}";
                // Stable across COM reassignment. Keep the remote service discriminator
                // in case a device offers more than one serial service.
                var remoteService = instanceName[(instanceName.LastIndexOf('&') + 1)..].ToUpperInvariant();
                result.Add(new(remoteService, name, port));
            }
        }
        return result.DistinctBy(r => r.PortName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.PortName).ToArray();
    }

    private static string? DeviceName(RegistryKey devices, string address)
    {
        using var device = devices.OpenSubKey("Dev_" + address);
        if (device is null) return null;
        foreach (var child in device.GetSubKeyNames())
        {
            using var instance = device.OpenSubKey(child);
            if (instance?.GetValue("FriendlyName") is string name && !string.IsNullOrWhiteSpace(name)) return name;
        }
        return null;
    }

    [GeneratedRegex(@"&([0-9A-F]{12})_[0-9A-F]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteAddress();
}
