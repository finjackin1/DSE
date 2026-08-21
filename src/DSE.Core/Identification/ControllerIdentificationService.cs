using DSE.Core.Controllers;
using HidSharp;

namespace DSE.Core.Identification;

public sealed class IdentifiedController
{
    public required PhysicalControllerType Type { get; init; }
    public required int VendorId { get; init; }
    public required int ProductId { get; init; }
    public required string DevicePath { get; init; }
    public required bool IsBluetooth { get; init; }
    public string? SerialNumber { get; init; }
}

/// <summary>
/// FASE 1 — Identificação apenas. Não abre stream, não lê reports, não cria
/// controle virtual, não toca no HidHide. Só detecta conectar/desconectar.
///
/// Usa o evento DeviceList.Changed do HidSharp (orientado a evento) em vez de
/// um loop de polling manual — deliberado, para eliminar qualquer risco de
/// busy-loop nessa fase inicial enquanto validamos a base.
/// </summary>
public sealed class ControllerIdentificationService : IDisposable
{
    private readonly DeviceList _deviceList = DeviceList.Local;
    private readonly HashSet<string> _knownPaths = new();
    private readonly object _lock = new();

    public event Action<IdentifiedController>? ControllerArrived;
    public event Action<string>? ControllerRemoved; // device path

    public void Start()
    {
        // Snapshot inicial — pega controles já conectados antes do serviço iniciar.
        ScanOnce();
        _deviceList.Changed += OnDeviceListChanged;
    }

    private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
    {
        // O evento Changed não diz especificamente O QUE mudou, então revarremos
        // o snapshot inteiro e comparamos — mas isso roda só quando o Windows
        // sinaliza uma mudança real de dispositivo, não em loop.
        ScanOnce();
    }

    private void ScanOnce()
    {
        lock (_lock)
        {
            var current = _deviceList.GetHidDevices()
                .Where(d => ControllerConstants.IsSupported(d.VendorID, d.ProductID))
                .Where(d => ControllerConstants.IsLikelyPhysicalDevice(d.DevicePath))
                .ToList();

            var currentPaths = new HashSet<string>(current.Select(d => d.DevicePath));

            foreach (var device in current)
            {
                if (_knownPaths.Contains(device.DevicePath))
                    continue;

                _knownPaths.Add(device.DevicePath);

                string? serial = null;
                try { serial = device.GetSerialNumber(); } catch { /* nem todo device expõe */ }

                bool isBluetooth = SafeGetMaxLen(device) >= ControllerConstants.ReportLength.Ds4Bluetooth;

                ControllerArrived?.Invoke(new IdentifiedController
                {
                    Type = ControllerConstants.IdentifyController(device.VendorID, device.ProductID),
                    VendorId = device.VendorID,
                    ProductId = device.ProductID,
                    DevicePath = device.DevicePath,
                    IsBluetooth = isBluetooth,
                    SerialNumber = serial
                });
            }

            var removed = _knownPaths.Where(p => !currentPaths.Contains(p)).ToList();
            foreach (var path in removed)
            {
                _knownPaths.Remove(path);
                ControllerRemoved?.Invoke(path);
            }
        }
    }

    private static int SafeGetMaxLen(HidDevice d)
    {
        try { return d.GetMaxInputReportLength(); }
        catch { return 0; }
    }

    public void Dispose()
    {
        _deviceList.Changed -= OnDeviceListChanged;
    }
}
