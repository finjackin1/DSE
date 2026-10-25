using HidSharp;

namespace DSE.Core.Controllers;

/// <summary>
/// Identidade do controle físico = o MAC, igual por USB e por Bluetooth.
/// Por BT o Windows já entrega o MAC como serial; por cabo o serial vem vazio
/// (o DualSense nem responde), então o MAC é lido do feature report de
/// pareamento. Com a mesma identidade nos dois caminhos, o "desligar BT ao
/// plugar USB" acha a sessão BT e o controle não ganha duas sessões.
/// </summary>
public static class ControllerSerial
{
    // Feature report com o MAC nos bytes 1..6, do byte menos significativo pro
    // mais (hid-playstation.c: DS_FEATURE_REPORT_PAIRING_INFO; DS4: 0x12).
    private const byte DualSensePairingReport = 0x09;
    private const byte Ds4PairingReport = 0x12;

    /// <summary>MAC em 12 hex minúsculos, ou null se não deu pra ler.</summary>
    public static string? Read(HidDevice device, bool isBluetooth)
    {
        string? serial = null;
        try { serial = device.GetSerialNumber(); } catch { /* nem todo device expõe */ }

        var mac = Normalize(serial);
        if (mac != null || isBluetooth) return mac ?? NullIfEmpty(serial);

        return ReadMacByFeature(device);
    }

    /// <summary>"90:B6:85-d6 0374" → "90b685d60374"; null se não for um MAC.</summary>
    public static string? Normalize(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return null;
        var hex = new string(serial.Where(c => c != ':' && c != '-' && c != ' ').ToArray());
        return hex.Length == 12 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    private static string? ReadMacByFeature(HidDevice device)
    {
        var reportId = ControllerConstants.IdentifyController(device.VendorID, device.ProductID) switch
        {
            PhysicalControllerType.DualSense => DualSensePairingReport,
            PhysicalControllerType.DualShock4 => Ds4PairingReport,
            _ => (byte)0
        };
        if (reportId == 0) return null;

        try
        {
            if (!device.TryOpen(out var stream)) return null;
            using (stream)
            {
                var buf = new byte[Math.Max(device.GetMaxFeatureReportLength(), 16)];
                buf[0] = reportId;
                stream.GetFeature(buf);
                if (buf[0] != reportId) return null;

                var mac = string.Concat(Enumerable.Range(1, 6).Reverse().Select(i => buf[i].ToString("x2")));
                return mac is "000000000000" or "ffffffffffff" ? null : mac;
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
