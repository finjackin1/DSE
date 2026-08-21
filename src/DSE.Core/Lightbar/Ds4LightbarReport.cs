namespace DSE.Core.Lightbar;

/// <summary>
/// Monta o Output Report HID que controla a lightbar (LED) do DS4 físico.
/// Layout documentado pela comunidade (controllers.fandom.com/wiki/DualShock_4,
/// hid-sony do kernel Linux, e implementações de referência como DS4Windows) —
/// NÃO testado por mim em hardware real, validar empiricamente.
///
/// USB: Report ID 0x05, sem checksum.
/// Bluetooth: Report ID 0x11, com CRC-32 nos últimos 4 bytes (obrigatório —
/// o controle ignora o report sem um CRC válido).
/// </summary>
public static class Ds4LightbarReport
{
    /// <summary>
    /// Monta o report para envio via USB (32 bytes, sem CRC).
    /// </summary>
    public static byte[] BuildUsbReport(byte red, byte green, byte blue,
                                        byte rumbleLeft = 0, byte rumbleRight = 0)
    {
        var report = new byte[32];
        report[0] = 0x05; // Report ID
        report[1] = 0x07; // Flags: habilita rumble (0x01) + LED (0x02) + flash (0x04)
        report[2] = 0x00;
        report[3] = rumbleRight; // motor direito (fraco / high-frequency)
        report[4] = rumbleLeft;  // motor esquerdo (forte / low-frequency)
        report[5] = red;
        report[6] = green;
        report[7] = blue;
        report[8] = 0x00; // flash "on" duration (0 = sem flash)
        report[9] = 0x00; // flash "off" duration
        return report;
    }

    /// <summary>
    /// Monta o report para envio via Bluetooth (78 bytes + CRC-32 nos últimos 4).
    ///
    /// Offsets validados EMPIRICAMENTE em hardware real (DS4 v2 via BT) por
    /// varredura byte a byte — divergem do que a documentação da comunidade
    /// sugere:
    ///   [3]  = flags (bit0 = liga rumble, bit1 = liga atualização do LED)
    ///   [4]  = LED branco separado (indicador próprio, NÃO faz parte do RGB
    ///          da lightbar — deixado em 0 propositalmente)
    ///   [8]  = Vermelho
    ///   [9]  = Verde
    ///   [10] = Azul
    /// </summary>
    /// <summary>
    /// Report de VIBRAÇÃO PURA via Bluetooth: aciona os motores sem tocar na
    /// lightbar. Existe para o pulso de confirmação com a emulação desativada
    /// — nesse momento o controle está visível pro Windows e a Steam pode ter
    /// acendido a luz de jogador. Um report normal de lightbar apagaria essa
    /// luz e causaria um piscar; aqui o bit do LED fica desligado.
    /// </summary>
    public static byte[] BuildBluetoothRumbleReport(byte rumbleLeft, byte rumbleRight)
    {
        var report = new byte[78];
        report[0] = 0x11;
        report[1] = 0xC0;
        report[2] = 0x20;
        report[3] = 0x01;        // SÓ bit0 = aplica motores; bit1 (LED) desligado
        report[4] = 0x00;
        report[5] = rumbleRight;
        report[6] = rumbleLeft;

        var crcInput = new byte[1 + 74];
        crcInput[0] = 0xA2;
        Array.Copy(report, 0, crcInput, 1, 74);

        uint crc = Crc32.Compute(crcInput);
        report[74] = (byte)(crc & 0xFF);
        report[75] = (byte)((crc >> 8) & 0xFF);
        report[76] = (byte)((crc >> 16) & 0xFF);
        report[77] = (byte)((crc >> 24) & 0xFF);

        return report;
    }

    /// <param name="aplicarMotores">
    /// Liga o bit que manda o controle APLICAR os bytes de vibração. Fica
    /// desligado nos reports que só mudam a cor: assim eles saem byte a byte
    /// iguais aos da versão que rodou meses neste hardware, e o controle
    /// simplesmente mantém a vibração que já estava valendo. Precisa vir
    /// ligado em toda mudança de vibração — inclusive na que a zera, senão o
    /// controle ignoraria os zeros e continuaria vibrando.
    /// </param>
    public static byte[] BuildBluetoothReport(byte red, byte green, byte blue,
                                              byte rumbleLeft = 0, byte rumbleRight = 0,
                                              bool aplicarMotores = false)
    {
        var report = new byte[78];
        report[0] = 0x11; // Report ID
        report[1] = 0xC0; // poll rate / enable HID
        report[2] = 0x20;
        // bit1 = atualiza o LED, bit0 = aplica os motores.
        report[3] = (byte)(0x02 | (aplicarMotores ? 0x01 : 0x00));
        report[4] = 0x00;        // LED branco separado — não mexemos aqui
        report[5] = rumbleRight; // motor direito (fraco)
        report[6] = rumbleLeft;  // motor esquerdo (forte)
        report[7] = 0x00; // reservado — sem efeito observado no hardware testado
        report[8] = red;
        report[9] = green;
        report[10] = blue;
        // Bytes 11-73 permanecem zerados (padding / flash, não usado por ora)

        // CRC-32 padrão (mesmo polinômio do zlib/PKZIP), calculado sobre um
        // byte de cabeçalho de transporte (0xA2, "output report" via BT HID)
        // seguido pelos bytes 0..73 do report. Os últimos 4 bytes recebem o
        // CRC em little-endian.
        var crcInput = new byte[1 + 74];
        crcInput[0] = 0xA2;
        Array.Copy(report, 0, crcInput, 1, 74);

        uint crc = Crc32.Compute(crcInput);
        report[74] = (byte)(crc & 0xFF);
        report[75] = (byte)((crc >> 8) & 0xFF);
        report[76] = (byte)((crc >> 16) & 0xFF);
        report[77] = (byte)((crc >> 24) & 0xFF);

        return report;
    }
}

/// <summary>
/// Implementação padrão de CRC-32 (polinômio 0xEDB88320, mesmo do zlib/PKZIP),
/// necessária para os output reports de Bluetooth do DS4.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        const uint poly = 0xEDB88320;

        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }

        return table;
    }

    public static uint Compute(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }
}
