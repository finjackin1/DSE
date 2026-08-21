namespace DSE.Core.Controllers;

/// <summary>
/// Tipo de controle físico suportado.
/// </summary>
public enum PhysicalControllerType
{
    Unknown,
    DualShock4,
    DualSense
}

/// <summary>
/// VID/PID conhecidos para identificar os controles Sony suportados.
/// Sony Vendor ID é sempre 054C.
/// </summary>
public static class ControllerConstants
{
    public const int SonyVendorId = 0x054C;

    // DualShock 4 - primeira revisão (CUH-ZCT1)
    public const int Ds4V1ProductId = 0x05C4;

    // DualShock 4 - segunda revisão / slim (CUH-ZCT2)
    public const int Ds4V2ProductId = 0x09CC;

    // DualSense (CFI-ZCT1)
    public const int DualSenseProductId = 0x0CE6;

    // DualSense Edge
    public const int DualSenseEdgeProductId = 0x0DF2;

    /// <summary>
    /// Tamanho do input report em bytes. O report por Bluetooth é maior que
    /// o por USB, e é essa diferença que a detecção usa pra saber por onde o
    /// controle está conectado — daí só este valor ser necessário.
    /// </summary>
    public static class ReportLength
    {
        public const int Ds4Bluetooth = 78;
    }

    public static PhysicalControllerType IdentifyController(int vendorId, int productId)
    {
        if (vendorId != SonyVendorId)
            return PhysicalControllerType.Unknown;

        return productId switch
        {
            Ds4V1ProductId or Ds4V2ProductId => PhysicalControllerType.DualShock4,
            DualSenseProductId or DualSenseEdgeProductId => PhysicalControllerType.DualSense,
            _ => PhysicalControllerType.Unknown
        };
    }

    public static bool IsSupported(int vendorId, int productId)
        => IdentifyController(vendorId, productId) != PhysicalControllerType.Unknown;

    /// <summary>
    /// Verifica se o Device Interface Path indica um dispositivo FÍSICO real
    /// (conectado via USB ou Bluetooth), em vez de um controle VIRTUAL criado
    /// pelo próprio ViGEmBus. Isso é crítico: o controle virtual DS4 usa o
    /// MESMO VID/PID da Sony (para ser indistinguível em jogos), então sem
    /// esse filtro o próprio app detectaria o controle que ele mesmo criou
    /// como se fosse um controle físico novo, entrando num loop infinito de
    /// criação de controles virtuais.
    ///
    /// Heurística: dispositivos físicos reais têm no Device Interface Path
    /// um segmento de barramento reconhecível — "USB#" para conexão via cabo,
    /// ou o GUID de serviço HID padrão do Bluetooth
    /// "{00001124-0000-1000-8000-00805f9b34fb}" para conexão sem fio.
    /// Qualquer path que não bata em nenhum dos dois é tratado como NÃO
    /// físico (fail-safe: na dúvida, não cria bridge).
    /// </summary>
    public static bool IsLikelyPhysicalDevice(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return false;

        var lower = devicePath.ToLowerInvariant();

        bool looksLikeUsb = lower.Contains("usb#") || lower.Contains(@"usb\");
        bool looksLikeBluetooth = lower.Contains("{00001124-0000-1000-8000-00805f9b34fb}");

        return looksLikeUsb || looksLikeBluetooth;
    }
}
