using System.Runtime.InteropServices;
using System.Text;

namespace DSE.Core.Controllers;

/// <summary>
/// Sobe a árvore de dispositivos do Windows (Configuration Manager) a partir
/// do caminho de interface HID, pra saber se o controle é físico ou um
/// virtual do ViGEmBus. O caminho sozinho não basta: o DualSense/DS4 por cabo
/// é um dispositivo composto (áudio + HID) e o caminho dele é
/// "\\?\hid#vid_054c&pid_0ce6&mi_03#...", sem nenhum "usb" — quem é USB é o pai.
/// </summary>
internal static class DeviceTree
{
    private const int CrSuccess = 0;
    private const uint CmDrpService = 0x00000005;
    private const int MaxNiveis = 8;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, int bufferLen, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(
        uint devInst, uint property, out uint regType, byte[]? buffer, ref uint length, uint flags);

    /// <summary>
    /// true = físico (algum ancestral é USB\ ou BTHENUM\ e nenhum é o
    /// ViGEmBus); false = virtual ou desconhecido; null = não deu pra ler a
    /// árvore (quem chama decide pelo caminho).
    /// </summary>
    public static bool? IsPhysical(string devicePath)
    {
        try
        {
            var instanceId = InterfacePathToInstanceId(devicePath);
            if (instanceId == null) return null;
            if (CM_Locate_DevNodeW(out var node, instanceId, 0) != CrSuccess) return null;

            bool barramentoFisico = false;
            for (int i = 0; i < MaxNiveis; i++)
            {
                if (CM_Get_Parent(out var parent, node, 0) != CrSuccess) break;
                node = parent;

                if (string.Equals(GetService(node), "ViGEmBus", StringComparison.OrdinalIgnoreCase))
                    return false;

                var id = GetDeviceId(node);
                if (id == null) continue;
                if (id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
                    id.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase))
                    barramentoFisico = true;
            }
            return barramentoFisico;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"\\?\hid#a#b#{guid}" → "HID\A\B".</summary>
    internal static string? InterfacePathToInstanceId(string devicePath)
    {
        var p = devicePath;
        if (p.StartsWith(@"\\?\")) p = p.Substring(4);
        var fimGuid = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (fimGuid <= 0) return null;
        return p.Substring(0, fimGuid).Replace('#', '\\').ToUpperInvariant();
    }

    private static string? GetDeviceId(uint node)
    {
        var buf = new char[512];
        return CM_Get_Device_IDW(node, buf, buf.Length, 0) == CrSuccess
            ? new string(buf).TrimEnd('\0')
            : null;
    }

    private static string? GetService(uint node)
    {
        uint len = 512;
        var buf = new byte[len];
        if (CM_Get_DevNode_Registry_PropertyW(node, CmDrpService, out _, buf, ref len, 0) != CrSuccess)
            return null;
        return Encoding.Unicode.GetString(buf, 0, (int)len).TrimEnd('\0');
    }
}
