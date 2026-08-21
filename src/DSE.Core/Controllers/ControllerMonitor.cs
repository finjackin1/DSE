using DSE.Core.Bluetooth;
using DSE.Core.Diagnostics;
using DSE.Core.Hotkeys;
using DSE.Core.Lightbar;
using DSE.Core.Virtual;
using HidSharp;

namespace DSE.Core.Controllers;

/// <summary>
/// "Escuta" um controle que está com a emulação DESATIVADA, só pra continuar
/// reconhecendo o atalho de desligar (PS segurado por 1s).
///
/// Com a emulação desativada a sessão normal é desmontada por inteiro — e é
/// dentro do read loop dela que os atalhos são detectados. Sem alguém lendo os
/// reports, o botão PS não chega em ninguém. Este monitor cobre esse vazio.
///
/// O que ele NÃO faz, de propósito: não oculta o controle no HidHide, não cria
/// controle virtual, não escreve LED e ignora o atalho de trocar perfil (não há
/// perfil ativo pra trocar). O controle segue nativo pro Windows e pra Steam;
/// o monitor apenas lê.
///
/// É uma classe separada da sessão de emulação de caso pensado: espalhar
/// condicionais de "modo monitor" pelo caminho crítico da emulação seria pedir
/// regressão justamente onde mais custou pra estabilizar.
///
/// Só faz sentido no Bluetooth: desligar o controle é um comando BT pelo MAC.
/// No cabo não existe desligar (o USB alimenta o aparelho).
/// </summary>
internal sealed class ControllerMonitor : IDisposable
{
    private readonly string _serial;
    private readonly string _devicePath;
    private readonly PhysicalControllerType _type;
    private readonly HidStream _stream;
    private readonly HidDevice _device;
    private readonly HotkeyDetector _hotkeys = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;

    // Um único estado reutilizado: o monitor só alimenta o detector de
    // atalhos, não compara frame anterior com atual.
    private readonly NormalizedInputState _state = new();

    // O report via Bluetooth do DualSense carrega número de sequência; se ele
    // se repetir, o firmware descarta o pacote.
    private byte _ledSeq;
    private bool _disposed;

    /// <summary>Disparado depois de mandar o controle desligar.</summary>
    public event Action<string>? PoweredOff;

    /// <summary>
    /// Disparado quando o atalho de alternar emulação é acionado com ela
    /// desligada — ou seja, o pedido é para LIGAR de volta. Quem trata é o
    /// watcher, que reconstrói a sessão de emulação.
    /// </summary>
    public event Action<string>? ToggleEmulationRequested;

    private ControllerMonitor(string serial, string devicePath, PhysicalControllerType type,
                              HidDevice device, HidStream stream)
    {
        _serial = serial;
        _devicePath = devicePath;
        _type = type;
        _device = device;
        _stream = stream;
    }

    /// <summary>
    /// Tenta abrir o controle em modo monitor. Retorna null se não der — e não
    /// dar é aceitável: o pior caso é o atalho não funcionar com a emulação
    /// desativada, sem prejuízo pro resto.
    /// </summary>
    public static ControllerMonitor? TryStart(string serial, string devicePath,
                                              PhysicalControllerType type, bool isBluetooth)
    {
        if (!isBluetooth)
        {
            // No cabo não há o que desligar; nem abrimos o dispositivo.
            return null;
        }

        try
        {
            var device = DeviceList.Local.GetHidDevices()
                .FirstOrDefault(d => d.DevicePath == devicePath);
            if (device == null) return null;

            if (!device.TryOpen(out var stream))
            {
                DseLog.Write($"[monitor] não consegui abrir {type} pra escutar o atalho (o controle segue funcionando normalmente)");
                return null;
            }

            var monitor = new ControllerMonitor(serial, devicePath, type, device, stream);
            monitor.Start();
            DseLog.Write($"[monitor] escutando {type} com a emulação desativada (só o atalho de desligar)");
            return monitor;
        }
        catch (Exception ex)
        {
            DseLog.Write($"[monitor] falha ao iniciar o monitoramento: {DseLog.Fmt(ex)}");
            return null;
        }
    }

    public string DevicePath => _devicePath;

    private void Start()
    {
        _hotkeys.HotkeyTriggered += OnHotkey;
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    private void OnHotkey(HotkeyEvent evt)
    {
        switch (evt)
        {
            case HotkeyEvent.PowerOffController:
                DseLog.Write($"[monitor] atalho de desligar acionado em {_type}");
                try { BluetoothDisconnector.TryDisconnect(_serial); }
                catch (Exception ex) { DseLog.Write($"[monitor] falha ao desligar o controle: {DseLog.Fmt(ex)}"); }
                PoweredOff?.Invoke(_serial);
                break;

            case HotkeyEvent.ToggleEmulation:
                // Com a emulação desligada, o atalho só pode significar LIGAR.
                // O pulso vem ANTES de avisar o watcher: logo depois o monitor
                // é encerrado e o stream fechado, e não haveria por onde vibrar.
                PulsoDeConfirmacao();
                ToggleEmulationRequested?.Invoke(_serial);
                break;

            // Trocar perfil não faz sentido aqui: não há emulação ativa.
        }
    }

    /// <summary>
    /// Pulso curto confirmando que o atalho foi reconhecido — o mesmo retorno
    /// que a sessão de emulação dá ao desativar, para o usuário saber que pode
    /// soltar os botões antes que o firmware do controle o desligue sozinho
    /// (por volta de 5 segundos de PS segurado).
    ///
    /// Usa o report de vibração PURA: com a emulação desativada o controle
    /// pertence ao Windows, e um report de lightbar apagaria a luz que a Steam
    /// tiver acendido. Roda na própria thread de leitura, que fica parada os
    /// 140ms do pulso — inofensivo, já que aqui só escutamos atalhos.
    /// </summary>
    private void PulsoDeConfirmacao()
    {
        try
        {
            // Assinatura de ATIVAR a emulação: tremor grave SUBINDO em degraus,
            // o espelho exato do decrescendo que a sessão toca ao desativar.
            // Assim os dois sentidos do mesmo atalho se distinguem no tato.
            Escrever(60, 0);
            Thread.Sleep(60);
            Escrever(130, 0);
            Thread.Sleep(60);
            Escrever(200, 0);
            Thread.Sleep(90);
            Escrever(0, 0);
        }
        catch (Exception ex)
        {
            // Retorno tátil não é essencial: se falhar, a reativação segue.
            DseLog.Write($"[monitor] pulso de confirmação falhou: {DseLog.Fmt(ex)}");
        }

        void Escrever(byte esquerdo, byte direito)
        {
            var report = _type == PhysicalControllerType.DualShock4
                ? Ds4LightbarReport.BuildBluetoothRumbleReport(esquerdo, direito)
                : DualSenseLightbarReport.BuildBluetoothRumbleReport(_ledSeq++, esquerdo, direito);
            _stream.Write(report);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        int reportLength;
        try { reportLength = _device.GetMaxInputReportLength(); }
        catch { reportLength = 78; }

        var buffer = new byte[reportLength];
        bool isBluetooth = true; // TryStart só monta monitor pra Bluetooth

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = _stream.Read(buffer, 0, buffer.Length);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; } // desligamento normal
            catch (Exception ex)
            {
                // O controle sumiu ou o stream morreu: encerra o monitor em
                // silêncio se estamos saindo, registra se foi inesperado.
                if (!ct.IsCancellationRequested && !_disposed)
                    DseLog.WriteThrottled($"monitor-read-{_serial}",
                        $"[monitor] leitura de {_type} falhou: {DseLog.Fmt(ex)}");
                break;
            }

            if (read <= 0)
            {
                try { await Task.Delay(10, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                if (_type == PhysicalControllerType.DualShock4)
                    Ds4ReportParser.Parse(buffer, isBluetooth, _state);
                else
                    DualSenseReportParser.Parse(buffer, isBluetooth, _state);

                _hotkeys.Feed(_state, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                DseLog.WriteThrottled($"monitor-parse-{_serial}",
                    $"[monitor] falha ao processar report de {_type}: {DseLog.Fmt(ex)}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _hotkeys.HotkeyTriggered -= OnHotkey;
        _cts.Cancel();
        try { _stream.Dispose(); } catch { /* já pode ter sido fechado */ }

        // Não espera o read loop de dentro dele mesmo (o atalho de desligar
        // dispara justamente daí).
        var loop = _readLoop;
        if (loop != null && Task.CurrentId != loop.Id)
        {
            try { loop.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* ignora */ }
        }

        try { _cts.Dispose(); } catch { /* ignora */ }
        DseLog.Write($"[monitor] monitoramento de {_type} encerrado");
    }
}
