using DSE.Core.Virtual;

namespace DSE.Core.Controllers;

/// <summary>
/// Traduz o input report bruto do DualShock 4 (USB ou Bluetooth) para o
/// estado normalizado usado pelo VirtualController.
///
/// Layout de referência (offsets a partir do início do buffer, INCLUINDO
/// o report ID): documentado pela comunidade em controllers.fandom.com/wiki/DualShock_4.
/// USB: report ID 0x01 no byte 0, dados começam no byte 1.
/// Bluetooth: report ID 0x11 no byte 0, dados começam no byte 3 (2 bytes extras
/// de cabeçalho antes do payload equivalente ao USB) — validar offset exato
/// contra captura real ao integrar, variações de firmware podem deslocar 1 byte.
/// </summary>
public static class Ds4ReportParser
{
    public static void Parse(byte[] report, bool isBluetooth, NormalizedInputState state)
    {
        int off = isBluetooth ? 3 : 1;

        // O objeto vem de fora e é reutilizado a cada frame: zera
        // antes pra não sobrar nada do frame anterior nos campos
        // que são preenchidos só em algumas condições.
        state.Reset();

        state.LX = ToAxisShort(report[off + 0]);
        state.LY = ToAxisShort(report[off + 1]);
        state.RX = ToAxisShort(report[off + 2]);
        state.RY = ToAxisShort(report[off + 3]);

        byte buttons1 = report[off + 4];
        byte hat = (byte)(buttons1 & 0x0F); // 0=N,1=NE,...7=NW,8=none
        ApplyDpadFromHat(state, hat);

        state.Square = (buttons1 & 0x10) != 0;
        state.Cross = (buttons1 & 0x20) != 0;
        state.Circle = (buttons1 & 0x40) != 0;
        state.Triangle = (buttons1 & 0x80) != 0;

        byte buttons2 = report[off + 5];
        state.L1 = (buttons2 & 0x01) != 0;
        state.R1 = (buttons2 & 0x02) != 0;
        // bits 2,3 = L2/R2 digital (ignorado; usamos o analógico abaixo)
        state.Share = (buttons2 & 0x10) != 0;
        state.Options = (buttons2 & 0x20) != 0;
        state.L3 = (buttons2 & 0x40) != 0;
        state.R3 = (buttons2 & 0x80) != 0;

        byte buttons3 = report[off + 6];
        state.Ps = (buttons3 & 0x01) != 0;
        state.TouchpadClick = (buttons3 & 0x02) != 0;

        state.L2 = report[off + 7];
        state.R2 = report[off + 8];

        // Bateria — byte 30 (USB) / 32 (BT) contados desde o INÍCIO do
        // report (incluindo o report ID), conforme o driver hid-sony do
        // kernel Linux. ATENÇÃO: esses valores são ABSOLUTOS, não relativos
        // ao payload — por isso NÃO somamos o 'off' aqui (somar daria byte
        // 31/35, deslocado, e leria lixo → percentual errado).
        // Os 4 bits baixos = nível; o 5º bit (0x10) = cabo USB conectado.
        // Nível: 0-10 com cabo, 0-9 na bateria.
        int batteryByteIndex = isBluetooth ? 32 : 30;
        if (batteryByteIndex < report.Length)
        {
            byte batteryByte = report[batteryByteIndex];
            int rawLevel = batteryByte & 0x0F;
            bool cableConnected = (batteryByte & 0x10) != 0;

            state.IsCharging = cableConnected && rawLevel <= 10;

            // Normaliza pra 0-100%. Escala 0-10 (cabo) ou 0-9 (bateria).
            int max = cableConnected ? 10 : 9;
            int clamped = Math.Min(rawLevel, max);
            state.BatteryPercent = (int)Math.Round(clamped / (double)max * 100);
        }

    }

    private static void ApplyDpadFromHat(NormalizedInputState s, byte hat)
    {
        s.DpadUp = hat is 0 or 1 or 7;
        s.DpadRight = hat is 1 or 2 or 3;
        s.DpadDown = hat is 3 or 4 or 5;
        s.DpadLeft = hat is 5 or 6 or 7;
    }

    // DS4 reporta eixos analógicos em byte (0-255, centro 128).
    // Convertemos para short (-32768/32767) para casar com NormalizedInputState.
    private static short ToAxisShort(byte raw) => (short)((raw - 128) * 256);
}
