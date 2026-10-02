using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace LQserial;

/// <summary>A COM port plus whatever Windows knows about the device behind it.</summary>
public sealed class PortInfo
{
    public string Name { get; init; } = "";
    /// <summary>Device description, manufacturer and USB serial number / Bluetooth address, when known.</summary>
    public string Details { get; init; } = "";

    public override string ToString() => Details.Length > 0 ? $"{Name}  {Details}" : Name;

    public static List<PortInfo> Enumerate()
    {
        var wmi = QueryDevices();
        return SerialPort.GetPortNames()
            .OrderBy(n => int.TryParse(n.AsSpan(3), out var i) ? i : 999)
            .Select(n => new PortInfo { Name = n, Details = wmi.TryGetValue(n, out var d) ? d : "" })
            .ToList();
    }

    static readonly Regex ComSuffix = new(@"\s*\((COM\d+)\)\s*$", RegexOptions.IgnoreCase);
    static readonly Regex BtAddress = new(@"&([0-9A-F]{12})_", RegexOptions.IgnoreCase);

    static Dictionary<string, string> QueryDevices()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Manufacturer, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%)'");
            foreach (ManagementBaseObject dev in searcher.Get())
            {
                var name = dev["Name"] as string ?? "";
                var m = ComSuffix.Match(name);
                if (!m.Success) continue;

                var description = name[..m.Index].Trim();
                var manufacturer = dev["Manufacturer"] as string ?? "";
                var pnpId = dev["PNPDeviceID"] as string ?? "";

                var parts = new List<string> { description };
                if (manufacturer.Length > 0 && !manufacturer.StartsWith('(') && manufacturer != "Microsoft"
                    && !description.Contains(manufacturer, StringComparison.OrdinalIgnoreCase))
                    parts.Add(manufacturer);

                var serial = UsbSerial(pnpId);
                if (serial != null) parts.Add($"S/N {serial}");
                else if (BluetoothAddress(pnpId) is { } bt) parts.Add($"BT {bt}");

                result[m.Groups[1].Value] = string.Join(" · ", parts.Where(p => p.Length > 0));
            }
        }
        catch
        {
            // WMI unavailable or blocked: fall back to plain port names
        }
        return result;
    }

    static string? BluetoothAddress(string pnpId)
    {
        if (!pnpId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)) return null;
        var m = BtAddress.Match(pnpId);
        if (!m.Success || m.Groups[1].Value.Trim('0').Length == 0) return null;   // all zeros = the local radio
        var h = m.Groups[1].Value.ToUpperInvariant();
        return string.Join(':', Enumerable.Range(0, 6).Select(i => h.Substring(i * 2, 2)));
    }

    /// <summary>
    /// The USB serial number lives on the device's USB node, which for composite devices
    /// (J-Link / nRF DKs) and FTDI bridges is a parent of the node that owns the COM port.
    /// </summary>
    static string? UsbSerial(string pnpId)
    {
        var id = pnpId;
        for (var step = 0; step < 4 && id.Length > 0; step++)
        {
            var isUsb = id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
            if (isUsb && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
            {
                // USB\VID_xxxx&PID_xxxx\<serial>; generated instance IDs contain '&' and are not serial numbers
                var seg = id.Split('\\');
                return seg.Length >= 3 && seg[2].Length > 0 && !seg[2].Contains('&') ? seg[2] : null;
            }
            id = ParentId(id) ?? "";
        }
        return null;
    }

    static string? ParentId(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var dev, instanceId, 0) != 0) return null;
        if (CM_Get_Parent(out var parent, dev, 0) != 0) return null;
        var sb = new StringBuilder(200);
        return CM_Get_Device_IDW(parent, sb, sb.Capacity, 0) == 0 ? sb.ToString() : null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int bufferLen, uint flags);
}
