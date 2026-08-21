using System.Linq;
using System.Runtime.InteropServices;

namespace DSE.Core.Bluetooth;

/// <summary>
/// Desconecta um controle Bluetooth de verdade (força o dispositivo a
/// desligar/dormir), diferente de só derrubar o controle virtual. Usa a
/// IOCTL nativa do Windows pra isso — a mesma técnica documentada pela
/// própria Microsoft pra "forçar desconexão sem despairar":
/// https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/bthioctl/ni-bthioctl-ioctl_bth_disconnect_device
///
/// NÃO TESTADO em hardware real ainda — a ordem dos bytes do endereço BT
/// tem duas convenções possíveis (e cada implementação Windows tende a usar
/// uma diferente sem documentar claramente), então o método tenta as duas.
/// </summary>
public static class BluetoothDisconnector
{
    private const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x41000c;

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public int dwSize;
    }

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        ref ulong lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>
    /// Tenta desconectar o controle Bluetooth a partir do endereço MAC
    /// (formato "bcc74656fccd", 12 caracteres hex, sem separador — o mesmo
    /// formato que costuma vir no Serial Number do dispositivo HID).
    /// Retorna true se algum radio local aceitou o comando com sucesso.
    /// </summary>
    public static bool TryDisconnect(string macHex)
    {
        if (string.IsNullOrWhiteSpace(macHex))
            return false;

        // Normaliza: remove separadores comuns de MAC (dois-pontos, hífen,
        // espaço) e coloca em minúsculo. O serial do DS4 costuma vir como 12
        // hex puros, mas o DualSense (e alguns stacks BT do Windows) pode
        // reportar "bc:c7:46:56:fc:cd" ou "bc-c7-...". Sem normalizar, o
        // comprimento != 12 fazia o disconnect falhar de cara e o controle
        // nunca desligava.
        var normalized = new string(macHex
            .Where(c => Uri.IsHexDigit(c))
            .ToArray());

        if (normalized.Length != 12)
            return false;

        if (!TryParseMac(normalized, out var bytes))
            return false;

        // A ordem dos bytes na struct BLUETOOTH_ADDRESS não é 100% consistente
        // entre implementações — tenta direta e invertida.
        return TryWithRadio(PackAddress(bytes, reversed: false))
            || TryWithRadio(PackAddress(bytes, reversed: true));
    }

    private static bool TryWithRadio(ulong btAddress)
    {
        var findParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        var hFind = BluetoothFindFirstRadio(ref findParams, out var hRadio);

        if (hFind == IntPtr.Zero)
            return false;

        try
        {
            var addr = btAddress;
            bool success = DeviceIoControl(
                hRadio, IOCTL_BTH_DISCONNECT_DEVICE,
                ref addr, sizeof(ulong),
                IntPtr.Zero, 0,
                out _, IntPtr.Zero);

            return success;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { CloseHandle(hRadio); } catch { /* ignora */ }
            try { BluetoothFindRadioClose(hFind); } catch { /* ignora */ }
        }
    }

    private static bool TryParseMac(string macHex, out byte[] bytes)
    {
        bytes = new byte[6];
        try
        {
            for (int i = 0; i < 6; i++)
            {
                bytes[i] = Convert.ToByte(macHex.Substring(i * 2, 2), 16);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ulong PackAddress(byte[] bytes, bool reversed)
    {
        var ordered = reversed ? bytes.Reverse().ToArray() : bytes;
        ulong result = 0;
        for (int i = 0; i < 6; i++)
        {
            result |= (ulong)ordered[i] << (8 * i);
        }
        return result;
    }
}
