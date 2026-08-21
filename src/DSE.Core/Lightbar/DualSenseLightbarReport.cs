namespace DSE.Core.Lightbar;

/// <summary>
/// Monta o output report que controla a lightbar do DualSense.
///
/// Offsets e flags vêm da struct dualsense_output_report_common do driver
/// hid-playstation.c do kernel Linux (fonte definitiva).
///
/// Layout do payload comum (dualsense_output_report_common, 47 bytes):
///   [0]  valid_flag0
///   [1]  valid_flag1   — 0x04 = habilita controle da COR da lightbar
///   [2]  motor_right   [3] motor_left
///   [4..7] reserved    [8] mute_button_led   [9] power_save_control
///   [10..37] reserved2
///   [38] valid_flag2   — 0x02 = habilita o lightbar_setup
///   [39..40] reserved3
///   [41] lightbar_setup — 0x02 (LIGHT_OUT) = desliga a animação própria do
///        firmware e entrega o controle da lightbar ao host. Sem isso, via
///        Bluetooth o DualSense pode continuar rodando a luz azul default e
///        IGNORAR as cores enviadas (bug observado: LED preso em azul).
///   [42] led_brightness
///   [43] player_leds
///   [44] lightbar_red   [45] lightbar_green   [46] lightbar_blue
///
/// USB (report 0x02, 63 bytes): report_id + payload comum + reserved.
/// BT  (report 0x31, 78 bytes): report_id + seq_tag + tag(0x10) + payload
///     comum + reserved + CRC32.
///
/// IMPORTANTE (BT): o nibble ALTO do seq_tag é um número de sequência que
/// PRECISA mudar a cada report — reports consecutivos com o mesmo seq podem
/// ser descartados pelo controle como duplicados (explica "só a primeira cor
/// pega"). O chamador mantém um contador por controle e passa aqui.
/// </summary>
public static class DualSenseLightbarReport
{
    private const byte UsbReportId = 0x02;
    private const byte BtReportId = 0x31;
    private const byte BtTag = 0x10; // "magic number must be set to 0x10" (kernel)

    // valid_flag1: habilita alterar a COR da lightbar.
    private const byte FLAG1_LIGHTBAR_CONTROL_ENABLE = 0x04;
    // valid_flag1: habilita controlar os player lights (LEDs brancos de baixo).
    private const byte FLAG1_PLAYER_LEDS_CONTROL_ENABLE = 0x10;
    // valid_flag2: habilita o campo lightbar_setup.
    private const byte FLAG2_LIGHTBAR_SETUP_CONTROL_ENABLE = 0x02;
    // lightbar_setup: desliga a luz default do firmware; host assume.
    private const byte LIGHTBar_SETUP_LIGHT_OUT = 0x02;

    // Offsets DENTRO do payload comum.
    // valid_flag0: habilita os motores de vibração (COMPATIBLE_VIBRATION |
    // HAPTICS_SELECT, conforme o driver do kernel).
    private const byte FLAG0_VIBRATION_ENABLE = 0x03;

    private const int OFF_VALID_FLAG0 = 0;
    private const int OFF_MOTOR_RIGHT = 2;
    private const int OFF_MOTOR_LEFT = 3;
    private const int OFF_VALID_FLAG1 = 1;
    private const int OFF_VALID_FLAG2 = 38;
    private const int OFF_LIGHTBAR_SETUP = 41;
    private const int OFF_PLAYER_LEDS = 43;
    private const int OFF_LIGHTBAR_RED = 44;
    private const int OFF_LIGHTBAR_GREEN = 45;
    private const int OFF_LIGHTBAR_BLUE = 46;

    public static byte[] BuildUsbReport(byte red, byte green, byte blue,
                                       byte rumbleLeft = 0, byte rumbleRight = 0)
    {
        // DS_OUTPUT_REPORT_USB_SIZE = 63 (report_id + 47 comum + 15 reserved).
        var report = new byte[63];
        report[0] = UsbReportId;

        int p = 1; // payload logo após o report id
        WriteCommon(report, p, red, green, blue, rumbleLeft: rumbleLeft, rumbleRight: rumbleRight);
        return report;
    }

    /// <summary>
    /// Report via Bluetooth (0x31, 78 bytes). seqTag: contador 0-15 mantido
    /// pelo chamador POR CONTROLE, incrementado a cada envio — vai no nibble
    /// alto do byte de sequência. NÃO VALIDADO em hardware.
    /// </summary>
    public static byte[] BuildBluetoothReport(byte red, byte green, byte blue, byte seqTag,
                                             byte rumbleLeft = 0, byte rumbleRight = 0)
    {
        var report = new byte[78];
        report[0] = BtReportId;
        report[1] = (byte)((seqTag & 0x0F) << 4); // sequência no nibble ALTO
        report[2] = BtTag;

        int p = 3; // payload após report_id + seq_tag + tag
        WriteCommon(report, p, red, green, blue, rumbleLeft: rumbleLeft, rumbleRight: rumbleRight);

        // CRC-32 nos últimos 4 bytes (LE), sobre 0xA2 + primeiros 74 bytes.
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

    /// <summary>
    /// "Report de despedida": enviado quando o DSE solta o controle (emulação
    /// desativada ou app fechando com o controle ligado). Apaga a lightbar e
    /// os player lights (os LEDs brancos de baixo que ficavam acesos), deixando
    /// o controle no mesmo estado limpo de quando acabou de ligar.
    /// </summary>
    public static byte[] BuildUsbGoodbyeReport()
    {
        var report = new byte[63];
        report[0] = UsbReportId;
        WriteCommon(report, 1, 0, 0, 0, clearPlayerLeds: true, lightOut: true);
        return report;
    }

    public static byte[] BuildBluetoothGoodbyeReport(byte seqTag)
    {
        var report = new byte[78];
        report[0] = BtReportId;
        report[1] = (byte)((seqTag & 0x0F) << 4);
        report[2] = BtTag;
        WriteCommon(report, 3, 0, 0, 0, clearPlayerLeds: true, lightOut: true);

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

    /// <summary>
    /// Report de INICIALIZAÇÃO, enviado UMA vez por sessão: mata a animação
    /// de boot do firmware, entrega a lightbar ao host (é o que o driver do
    /// kernel faz ao assumir o controle) e APAGA os player LEDs — aqueles
    /// LEDs brancos que o Windows/Steam acende enquanto o controle está livre.
    /// Sem isso, ao reativar a emulação a luz branca continuava acesa, porque
    /// o firmware guarda o último estado e ninguém mandava apagar.
    /// Depois dele, os reports de cor mandam só a cor.
    /// </summary>
    public static byte[] BuildUsbInitReport()
    {
        var report = new byte[63];
        report[0] = UsbReportId;
        WriteCommon(report, 1, 0, 0, 0, clearPlayerLeds: true, lightOut: true);
        return report;
    }

    public static byte[] BuildBluetoothInitReport(byte seqTag)
    {
        var report = new byte[78];
        report[0] = BtReportId;
        report[1] = (byte)((seqTag & 0x0F) << 4);
        report[2] = BtTag;
        WriteCommon(report, 3, 0, 0, 0, clearPlayerLeds: true, lightOut: true);

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

    /// <summary>
    /// Report de VIBRAÇÃO PURA via Bluetooth: liga os motores sem habilitar o
    /// controle da lightbar (valid_flag1 fica zerado). Usado no pulso de
    /// confirmação com a emulação desativada, quando quem manda na luz é o
    /// Windows/Steam e não queremos disputar com eles.
    /// </summary>
    public static byte[] BuildBluetoothRumbleReport(byte seqTag, byte rumbleLeft, byte rumbleRight)
    {
        var report = new byte[78];
        report[0] = BtReportId;
        report[1] = (byte)((seqTag & 0x0F) << 4);
        report[2] = BtTag;

        int p = 3;
        report[p + OFF_VALID_FLAG0] = FLAG0_VIBRATION_ENABLE;
        report[p + OFF_MOTOR_RIGHT] = rumbleRight;
        report[p + OFF_MOTOR_LEFT] = rumbleLeft;
        // valid_flag1 e valid_flag2 ficam ZERADOS: nada de lightbar aqui.

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

    private static void WriteCommon(byte[] report, int p, byte red, byte green, byte blue,
                                    bool clearPlayerLeds = false, bool lightOut = false,
                                    byte rumbleLeft = 0, byte rumbleRight = 0)
    {
        // Vibração. Vai no MESMO report da cor, então o valor precisa ser
        // sempre o estado atual dos motores — mandar zero aqui numa
        // atualização de cor cortaria a vibração no meio do efeito.
        report[p + OFF_VALID_FLAG0] = FLAG0_VIBRATION_ENABLE;
        report[p + OFF_MOTOR_RIGHT] = rumbleRight;
        report[p + OFF_MOTOR_LEFT] = rumbleLeft;

        report[p + OFF_VALID_FLAG1] = clearPlayerLeds
            ? (byte)(FLAG1_LIGHTBAR_CONTROL_ENABLE | FLAG1_PLAYER_LEDS_CONTROL_ENABLE)
            : FLAG1_LIGHTBAR_CONTROL_ENABLE;

        // ATENÇÃO ao nome: LIGHT_OUT quer dizer "apagar a lightbar", NÃO
        // "assumir o controle dela". O kernel manda isso UMA vez, na
        // inicialização, só pra matar a animação de boot do firmware — e os
        // reports de cor seguintes NÃO o incluem.
        //
        // Mandar em todo report de cor (era assim) faz o pacote dizer "essa é
        // a cor" e "apague a luz" ao mesmo tempo: a lightbar acendia e sumia
        // ~1s depois, e não voltava ao reativar a emulação.
        if (lightOut)
        {
            report[p + OFF_VALID_FLAG2] = FLAG2_LIGHTBAR_SETUP_CONTROL_ENABLE;
            report[p + OFF_LIGHTBAR_SETUP] = LIGHTBar_SETUP_LIGHT_OUT;
        }

        if (clearPlayerLeds)
            report[p + OFF_PLAYER_LEDS] = 0x00; // todos os LEDs brancos apagados

        report[p + OFF_LIGHTBAR_RED] = red;
        report[p + OFF_LIGHTBAR_GREEN] = green;
        report[p + OFF_LIGHTBAR_BLUE] = blue;
    }
}
