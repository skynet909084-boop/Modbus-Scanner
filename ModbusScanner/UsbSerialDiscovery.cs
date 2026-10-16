using System.IO.Ports;
using Microsoft.Win32;

namespace ModbusScanner;

/// <summary>Read-only discovery of Windows USB serial adapters. No driver installation or elevation.</summary>
internal static class UsbSerialDiscovery
{
    internal sealed record Adapter(string Port, string Chip, string HardwareId, string DriverState);

    internal static IReadOnlyList<Adapter> Find()
    {
        var ports = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, Adapter>(StringComparer.OrdinalIgnoreCase);
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null) return Array.Empty<Adapter>();
        foreach (string deviceId in usb.GetSubKeyNames())
        {
            if (!deviceId.Contains("VID_", StringComparison.OrdinalIgnoreCase) ||
                !deviceId.Contains("PID_", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var device = usb.OpenSubKey(deviceId);
                if (device is null) continue;
                foreach (string instanceId in device.GetSubKeyNames())
                {
                    try
                    {
                        using var instance = device.OpenSubKey(instanceId);
                        using var parameters = instance?.OpenSubKey("Device Parameters");
                        string? port = parameters?.GetValue("PortName") as string;
                        if (string.IsNullOrWhiteSpace(port) || !port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) continue;
                        string friendly = instance?.GetValue("FriendlyName") as string ?? "";
                        string chip = Identify(deviceId, friendly);
                        bool active = ports.Contains(port);
                        found[port] = new Adapter(port, chip, deviceId,
                            active ? "COM ready" : "COM unavailable (check Device Manager)");
                    }
                    catch (Exception) { /* Some registry device instances are inaccessible. */ }
                }
            }
            catch (Exception) { /* Ignore inaccessible registry branches. */ }
        }
        foreach (string port in ports)
            if (!found.ContainsKey(port))
                found[port] = new Adapter(port, "Serial port (chip not identified)", "—", "COM ready");
        return found.Values.OrderBy(x => x.Port, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Identify(string id, string friendly)
    {
        if (id.Contains("VID_0403", StringComparison.OrdinalIgnoreCase)) return "FTDI (FT232 / FT2232 family)";
        if (id.Contains("VID_1A86", StringComparison.OrdinalIgnoreCase)) return "WCH (CH340 / CH341 family)";
        if (id.Contains("VID_10C4", StringComparison.OrdinalIgnoreCase)) return "Silicon Labs (CP210x family)";
        if (id.Contains("VID_067B", StringComparison.OrdinalIgnoreCase)) return "Prolific (PL2303 family)";
        if (friendly.Contains("USB Serial", StringComparison.OrdinalIgnoreCase)) return friendly;
        return "USB serial adapter (unidentified)";
    }
}
