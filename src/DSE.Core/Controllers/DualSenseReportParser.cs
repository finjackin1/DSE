using DSE.Core.Virtual;

namespace DSE.Core.Controllers;

/// <summary>
/// Traduz o input report bruto do DualSense pro estado normalizado.
///
/// Layout baseado em PS5StatePacket_t da SDL (Simple DirectMedia Layer,
/// src/joystick/hidapi/SDL_hidapi_ps5.c), a mesma biblioteca de input usada
/// por uma fração enorme dos jogos PC — validado em milhões de instalações
/// e revisões de hardware diferentes, ao contrário de calibração num único
/// controle. Offsets abaixo são relativos ao INÍCIO do payload de dados
/// (após o Report ID); some com baseOffset pra achar a posição real no
/// buffer bruto lido do dispositivo:
///
///   [0-1]   sticks esquerdo (X,Y)      [2-3] sticks direito (X,Y)
///   [4-5]   gatilhos (L2,R2)           [6]   contador
///   [7-10]  botões + dpad (4 bytes!)   [11-14] sequência de pacote
///   [15-16] giroscópio X               [17-18] giroscópio Y
///   [19-20] giroscópio Z               [21-22] acelerômetro X
///   [23-24] acelerômetro Y             [25-26] acelerômetro Z
///   [27-30] timestamp                  [31] temperatura do sensor
///   [32]    contador touch 1           [33-35] dados touch 1 (X/Y 12 bits)
///   [36]    contador touch 2           [37-39] dados touch 2 (X/Y 12 bits)
///
/// IMPORTANTE: o valor de baseOffset pra Bluetooth ainda não foi validado
/// empiricamente em hardware real (só USB segue o padrão direto "Report ID
/// + payload" = offset 1). Ao testar com um DualSense de verdade via BT,
/// usar a mesma metodologia de varredura de bytes que validamos pro DS4 se
/// os valores não baterem.
/// </summary>
public static class DualSenseReportParser
{
    public static void Parse(byte[] report, bool isBluetooth, NormalizedInputState state)
    {
        // USB: Report ID (0x01) ocupa 1 byte, payload começa em offset 1.
        // BT: layout observado em outras implementações sugere offset 2,
        // mas precisa de confirmação empírica — ver aviso na doc acima.
        int off = isBluetooth ? 2 : 1;

        // O objeto vem de fora e é reutilizado a cada frame: zera
        // antes pra não sobrar nada do frame anterior nos campos
        // que são preenchidos só em algumas condições.
        state.Reset();

        state.LX = ToAxisShort(report[off + 0]);
        state.LY = ToAxisShort(report[off + 1]);
        state.RX = ToAxisShort(report[off + 2]);
        state.RY = ToAxisShort(report[off + 3]);

        state.L2 = report[off + 4];
        state.R2 = report[off + 5];
        // report[off + 6] = contador, não usado

        // rgucButtonsAndHat[4] — layout confirmado pela SDL:
        // byte0: dpad (bits 0-3) + Square/Cross/Circle/Triangle (bits 4-7)
        byte buttons0 = report[off + 7];
        byte hat = (byte)(buttons0 & 0x0F);
        state.DpadUp = hat is 0 or 1 or 7;
        state.DpadRight = hat is 1 or 2 or 3;
        state.DpadDown = hat is 3 or 4 or 5;
        state.DpadLeft = hat is 5 or 6 or 7;

        state.Square = (buttons0 & 0x10) != 0;
        state.Cross = (buttons0 & 0x20) != 0;
        state.Circle = (buttons0 & 0x40) != 0;
        state.Triangle = (buttons0 & 0x80) != 0;

        // byte1: L1/R1/L2btn/R2btn/Share(Create)/Options/L3/R3
        byte buttons1 = report[off + 8];
        state.L1 = (buttons1 & 0x01) != 0;
        state.R1 = (buttons1 & 0x02) != 0;
        state.Share = (buttons1 & 0x10) != 0; // "Create" no DualSense
        state.Options = (buttons1 & 0x20) != 0;
        state.L3 = (buttons1 & 0x40) != 0;
        state.R3 = (buttons1 & 0x80) != 0;

        // byte2: PS + Touchpad click nos bits baixos (confirmado pela SDL:
        // "data & 0x01" = Guide/PS, "data & 0x02" = Touchpad)
        byte buttons2 = report[off + 9];
        state.Ps = (buttons2 & 0x01) != 0;
        state.TouchpadClick = (buttons2 & 0x02) != 0;

        // Giroscópio e acelerômetro — offsets 15-26 relativos ao payload.
        state.GyroX = ReadShortLE(report, off + 15);
        state.GyroY = ReadShortLE(report, off + 17);
        state.GyroZ = ReadShortLE(report, off + 19);
        state.AccelX = ReadShortLE(report, off + 21);
        state.AccelY = ReadShortLE(report, off + 23);
        state.AccelZ = ReadShortLE(report, off + 25);

        // Touchpad — mesma técnica de empacotamento 12-bit do DS4, offset 32
        // (contador+ativo) e 33-35 (X/Y), confirmado pela SDL.
        if (report.Length > off + 35)
        {
            byte trackingByte = report[off + 32];
            // SDL documenta esse byte como "high bit clear + counter": bit7
            // limpo (0) = touch válido/ativo. Convenção DIFERENTE do DS4
            // (que usa o bit0) — não copiar a lógica de um pro outro.
            state.TouchActive = (trackingByte & 0x80) == 0;
            byte t0 = report[off + 33];
            byte t1 = report[off + 34];
            byte t2 = report[off + 35];
            state.TouchX = t0 | ((t1 & 0x0F) << 8);
            state.TouchY = (t1 >> 4) | (t2 << 4);
        }

        // Bateria — offset 52 relativo ao payload, conforme o driver
        // hid-playstation.c do kernel Linux. Estrutura DIFERENTE do DS4:
        //   nibble baixo (bits 0-3) = capacidade (0-10)
        //   nibble alto  (bits 4-7) = status de carga
        // Percentual = min(cap * 10 + 5, 100). Status: 0x0=descarregando,
        // 0x1=carregando, 0x2=cheio (100%), 0xA/0xB/0xF = erro.
        // NÃO VALIDADO em hardware real — offset pode variar; confirmar ao
        // testar com um DualSense de verdade.
        int batteryByteIndex = off + 52;
        if (batteryByteIndex < report.Length)
        {
            byte batteryByte = report[batteryByteIndex];
            int capacity = batteryByte & 0x0F;
            int chargeStatus = (batteryByte >> 4) & 0x0F;

            switch (chargeStatus)
            {
                case 0x0: // descarregando
                    state.BatteryPercent = Math.Min(capacity * 10 + 5, 100);
                    state.IsCharging = false;
                    break;
                case 0x1: // carregando
                    state.BatteryPercent = Math.Min(capacity * 10 + 5, 100);
                    state.IsCharging = true;
                    break;
                case 0x2: // cheio
                    state.BatteryPercent = 100;
                    state.IsCharging = true;
                    break;
                default: // erro de tensão/temperatura/carga — desconhecido
                    state.BatteryPercent = -1;
                    state.IsCharging = false;
                    break;
            }
        }

    }

    private static short ReadShortLE(byte[] buffer, int offset)
    {
        if (offset + 1 >= buffer.Length) return 0;
        return (short)(buffer[offset] | (buffer[offset + 1] << 8));
    }

    private static short ToAxisShort(byte raw) => (short)((raw - 128) * 256);
}
