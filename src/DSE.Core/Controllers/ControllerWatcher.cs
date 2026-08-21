using System.IO;
using DSE.Core.Bluetooth;
using DSE.Core.Diagnostics;
using DSE.Core.HidHide;
using DSE.Core.Hotkeys;
using DSE.Core.Identification;
using DSE.Core.Lightbar;
using DSE.Core.Profiles;
using DSE.Core.Virtual;
using HidSharp;

namespace DSE.Core.Controllers;

public sealed class ActiveControllerInfo
{
    public required string Serial { get; init; }
    public required PhysicalControllerType Type { get; init; }
    public required bool IsBluetooth { get; init; }
    public required EmulationProfile Profile { get; set; }
    public required LedMode LedMode { get; set; }
}

public enum LedMode
{
    /// <summary>Cor fixa por perfil (padrão) — verde escuro no Xbox, azul escuro no DS4.</summary>
    Preset,
    /// <summary>Deixa o jogo controlar a cor da lightbar (só afeta o perfil DS4).</summary>
    Passthrough
}

/// <summary>
/// Serviço central: detecta DS4/DualSense, oculta o físico via HidHide,
/// cria e alimenta o controle virtual (Xbox 360 ou DS4, com passthrough de
/// giroscópio/touchpad/LED quando em DS4), reage aos atalhos físicos.
///
/// Passthrough total e modo de LED são configuráveis POR CONTROLE (via
/// serial) — dá pra desativar a emulação só no DualSense e deixar ativa no
/// DS4 conectado ao mesmo tempo, por exemplo.
/// </summary>
public sealed class ControllerWatcher : IDisposable
{
    private readonly ControllerIdentificationService _identification = new();
    private readonly HidHideService _hidHide = new();
    private readonly ProfileManager _profileManager = new();
    // O cliente ViGEm NÃO é mais compartilhado: cada VirtualController abre e
    // fecha o seu (ver VirtualController.RecycleClientNoLock). Fechar o handle
    // é o único jeito comprovado de o driver soltar os dispositivos virtuais,
    // então isso passa a acontecer a cada troca de perfil e a cada teardown.
    private bool _shuttingDown;

    private readonly Dictionary<string, ControllerSession> _sessions = new();
    private readonly HashSet<string> _disabledSerials = new();

    /// <summary>
    /// Protege _sessions, _disabledSerials e _pendingSerials. Essas coleções
    /// são tocadas por CINCO threads: a UI, o timer de idle, a thread de
    /// detecção de dispositivos, o read loop de cada controle (via hotkey) e
    /// as tasks de LED. Dictionary/HashSet não são thread-safe — sem isso, uma
    /// sessão podia sumir do dicionário sem passar pelo teardown e deixar o
    /// controle virtual dela pra sempre no Windows (fantasma que só some ao
    /// fechar o app).
    ///
    /// REGRA: nada de operação demorada (Wait, Dispose, Write, HidHide) com
    /// este lock na mão — só leitura/escrita das coleções.
    /// </summary>
    private readonly object _sync = new();

    /// <summary>
    /// Seriais cuja sessão está sendo montada AGORA (fora do lock, porque
    /// abrir o device e criar o controle virtual demora). Impede que dois
    /// eventos de chegada quase simultâneos criem duas sessões — e dois
    /// controles virtuais — pro mesmo controle físico.
    /// </summary>
    private readonly HashSet<string> _pendingSerials = new();

    /// <summary>
    /// Caminhos de dispositivo com unhide AGENDADO (aguardando o atraso do
    /// teardown). Guardado sob _sync. Serve pra duas garantias: no
    /// encerramento do app, todo unhide pendente é executado na hora
    /// (senão o controle ficaria escondido do Windows pra sempre); e se o
    /// mesmo dispositivo voltar a ser ocultado antes do prazo, o unhide
    /// agendado é cancelado (senão ele desocultaria a sessão NOVA).
    /// </summary>
    private readonly HashSet<string> _pendingUnhides = new();

    /// <summary>
    /// Monitores ativos (serial -> monitor), para controles com a emulação
    /// desativada. Servem só pra continuar reconhecendo o atalho de desligar.
    /// Guardado sob _sync, como as demais coleções.
    /// </summary>
    private readonly Dictionary<string, ControllerMonitor> _monitors = new();

    private readonly CancellationTokenSource _cts = new();

    /// <summary>Controle conectado e emulação ativa (recém-conectado ou reativado).</summary>
    public event Action<ActiveControllerInfo>? ControllerConnected;
    /// <summary>Controle desconectado de verdade (fisicamente) — some da UI.</summary>
    public event Action<string>? ControllerDisconnected;
    /// <summary>Usuário desativou a emulação desse controle manualmente — continua na UI, só para de emular.</summary>
    public event Action<string>? DeviceDisabledByUser;
    public event Action<ActiveControllerInfo>? ProfileChanged;
    /// <summary>Cor atual do LED mudou (preset aplicado ou o jogo mandou uma cor nova via passthrough).</summary>
    public event Action<string, byte, byte, byte>? LedColorChanged;
    /// <summary>Disparado quando o nível de bateria ou o estado de carga muda. (serial, percent 0-100, carregando)</summary>
    public event Action<string, int, bool>? BatteryChanged;

    /// <summary>
    /// Modo do LED trocado PELO ATALHO do controle. A interface precisa saber
    /// pra atualizar o card — quando a troca parte de um clique na janela, é a
    /// própria janela que se atualiza e este evento não é usado.
    /// </summary>
    public event Action<string>? LedModeChanged;

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Quando true, se um controle já conectado por Bluetooth também for
    /// plugado via USB, o Bluetooth dele é desligado automaticamente. O App
    /// sincroniza isso com a configuração do usuário.
    /// </summary>
    public bool AutoDisableBluetoothOnUsb { get; set; } = true;

    /// <summary>
    /// Quando true, desliga o controle automaticamente após ficar ocioso
    /// (sem entrada do usuário nem feedback do jogo) pelo tempo de
    /// AutoPowerOffIdle. Só afeta controles por Bluetooth (USB não desliga).
    /// </summary>
    public bool AutoPowerOffEnabled { get; set; } = true;

    /// <summary>Tempo de ociosidade até desligar o controle. Padrão: 10 min.</summary>
    public TimeSpan AutoPowerOffIdle { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Lista os controles atualmente ativos (já conectados). A UI chama isso
    /// ao abrir, pra mostrar controles que já estavam conectados ANTES da
    /// janela existir — senão dependeria só de eventos futuros e o controle
    /// só apareceria depois de religar.
    /// </summary>
    public IReadOnlyList<ActiveControllerInfo> GetActiveControllers()
    {
        lock (_sync)
        {
            return _sessions.Values.Select(s => new ActiveControllerInfo
            {
                Serial = s.Serial,
                Type = s.Type,
                IsBluetooth = s.IsBluetooth,
                Profile = s.VirtualController.ActiveProfile,
                LedMode = s.LedMode
            }).ToList();
        }
    }

    public LedMode GetLedMode(string serial)
    {
        lock (_sync)
            return _sessions.TryGetValue(serial, out var session) ? session.LedMode : LedMode.Preset;
    }

    public (byte r, byte g, byte b) GetCurrentLedColor(string serial)
    {
        lock (_sync)
            return _sessions.TryGetValue(serial, out var session) ? session.CurrentColor : ((byte)0, (byte)0, (byte)0);
    }

    /// <summary>
    /// Posição atual do dedo no touchpad. Lê o último estado recebido — a
    /// janela consulta isso num relógio próprio em vez de receber um evento
    /// por report, porque chegam ~250 por segundo e isso travaria a interface.
    /// </summary>
    public (bool ativo, int x, int y) GetTouch(string serial)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(serial, out var sessao)) return (false, 0, 0);
            var estado = sessao.LastInputSnapshot;
            return estado == null ? (false, 0, 0) : (estado.TouchActive, estado.TouchX, estado.TouchY);
        }
    }

    /// <summary>Bateria atual: (percent 0-100, carregando). percent -1 = desconhecido.</summary>
    public (int percent, bool charging) GetBattery(string serial)
    {
        lock (_sync)
            return _sessions.TryGetValue(serial, out var session) ? (session.BatteryPercent, session.IsCharging) : (-1, false);
    }

    /// <summary>
    /// Troca o modo de LED só desse controle específico. Ao voltar pra
    /// Preset, já reaplica a cor certa na hora.
    /// </summary>
    public void SetLedMode(string serial, LedMode mode)
    {
        ControllerSession? session;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(serial, out session)) return;
            session.LedMode = mode;
        }

        // Fora do lock: escreve no controle.
        if (mode == LedMode.Preset)
        {
            ApplyPresetColor(session, session.VirtualController.ActiveProfile);
        }
    }

    /// <summary>
    /// Ativa/desativa a emulação SÓ desse controle (identificado pelo
    /// serial). Desativado: passthrough total pra esse controle específico
    /// (sem virtual, sem HidHide) — outros controles conectados continuam
    /// funcionando normalmente. A preferência persiste até o usuário
    /// reativar, mesmo que o controle desconecte e reconecte nesse meio-tempo.
    /// </summary>
    public void SetDevicePassthroughDisabled(string serial, bool disabled)
    {
        if (disabled)
        {
            ControllerSession? session;
            lock (_sync)
            {
                if (!_disabledSerials.Add(serial)) return;
                _sessions.TryGetValue(serial, out session);
            }

            // Fora do lock: o teardown escreve no controle e descarta recursos.
            if (session != null)
            {
                var devicePath = session.DevicePath;
                var type = session.Type;
                var isBluetooth = session.IsBluetooth;

                TeardownSession(session, isManualDisable: true);

                // Passa a só escutar o controle, pra o atalho de desligar
                // continuar funcionando mesmo sem emulação. Espera o unhide
                // agendado do teardown vencer antes de reabrir o dispositivo.
                StartMonitorAfterTeardown(serial, devicePath, type, isBluetooth);
            }
        }
        else
        {
            lock (_sync)
            {
                if (!_disabledSerials.Remove(serial)) return;
            }

            // Solta o dispositivo antes de montar a sessão de emulação.
            StopMonitor(serial);

            // Recria a sessão na hora se o físico ainda estiver conectado.
            var device = FindPhysicalDeviceBySerial(serial);
            if (device != null)
            {
                var type = ControllerConstants.IdentifyController(device.VendorID, device.ProductID);
                OnControllerArrived(new DetectedDeviceInfo
                {
                    Type = type,
                    VendorId = device.VendorID,
                    ProductId = device.ProductID,
                    DevicePath = device.DevicePath,
                    IsBluetooth = IsLikelyBluetooth(device),
                    SerialNumber = serial
                });
            }
        }
    }

    private static HidDevice? FindPhysicalDeviceBySerial(string serial)
    {
        foreach (var device in DeviceList.Local.GetHidDevices()
                     .Where(d => ControllerConstants.IsSupported(d.VendorID, d.ProductID))
                     .Where(d => ControllerConstants.IsLikelyPhysicalDevice(d.DevicePath)))
        {
            string? s = null;
            try { s = device.GetSerialNumber(); } catch { /* nem todo device expõe */ }
            if ((s ?? device.DevicePath) == serial) return device;
        }
        return null;
    }

    private void ApplyPresetColor(ControllerSession session, EmulationProfile profile)
    {
        var (r, g, b) = profile.GetPresetColor();

        // Tenta aplicar a cor algumas vezes: logo após conectar, o controle
        // às vezes ainda não aceita output reports, e a 1ª escrita falha.
        // O report é RECONSTRUÍDO a cada tentativa: no DualSense via BT, cada
        // report precisa de um número de sequência novo (senão o firmware
        // descarta como duplicata) — reaproveitar o mesmo buffer repetiria o
        // seq. Roda em background pra não travar a criação da sessão.
        Task.Run(async () =>
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    // Monta e escreve sob o mesmo lock: garante que o número
                    // de sequência do report bata com a ordem de escrita.
                    lock (session.WriteLock)
                    {
                        session.Stream.Write(BuildLightbarReport(session, r, g, b));
                    }
                    session.CurrentColor = (r, g, b);
                    LedColorChanged?.Invoke(session.Serial, r, g, b);
                    return; // sucesso
                }
                catch (Exception ex)
                {
                    if (attempt == 5)
                        DseLog.Write($"[watcher] desisti de aplicar a cor do perfil em {session.Type} após 5 tentativas: {DseLog.Fmt(ex)}");
                    await Task.Delay(150 * attempt); // 150, 300, 450, 600ms
                }
            }
        });
    }

    /// <summary>
    /// Monta o output report de LED certo pro tipo/transporte do controle.
    /// Centralizado aqui pra TODOS os envios passarem pelo contador de
    /// sequência da sessão (exigência do DualSense via BT).
    /// </summary>
    /// <summary>
    /// Monta o report de saída com a cor pedida E a vibração atual da sessão.
    /// Toda escrita passa por aqui de propósito: cor e motores compartilham o
    /// mesmo report, então mandar um sem o outro apagaria o que estava valendo.
    /// </summary>
    private static byte[] BuildLightbarReport(ControllerSession session, byte r, byte g, byte b,
                                              bool aplicarMotores = false)
    {
        byte rl = session.RumbleLeft, rr = session.RumbleRight;

        return session.Type == PhysicalControllerType.DualShock4
            ? (session.IsBluetooth
                ? Ds4LightbarReport.BuildBluetoothReport(r, g, b, rl, rr, aplicarMotores)
                : Ds4LightbarReport.BuildUsbReport(r, g, b, rl, rr))
            : (session.IsBluetooth
                ? DualSenseLightbarReport.BuildBluetoothReport(r, g, b, session.NextLedSeq(), rl, rr)
                : DualSenseLightbarReport.BuildUsbReport(r, g, b, rl, rr));
    }

    /// <summary>
    /// Pulso curto de vibração confirmando que o atalho foi reconhecido.
    ///
    /// Existe por um motivo prático: o firmware do próprio controle desliga o
    /// aparelho quando o PS fica segurado por volta de 5 segundos — coisa que
    /// acontece com o DSE aberto ou fechado, e que nenhum software impede. Sem
    /// um retorno imediato, o usuário tende a segurar "esperando alguma
    /// coisa" e acaba desligando o controle sem querer. O pulso diz: reconheci,
    /// pode soltar.
    ///
    /// Usa o motor direito (o de alta frequência), que dá um toque seco e
    /// curto em vez de um tremor pesado. Roda ANTES do teardown de propósito:
    /// o report de despedida zera motores e luzes, e cortaria o pulso no meio.
    /// </summary>
    /// <summary>
    /// Assinatura de DESATIVAR a emulação: tremor grave descendo em degraus,
    /// como algo desligando. Usa o motor esquerdo (baixa frequência).
    /// </summary>
    private async Task PulsoDesativarAsync(ControllerSession session) =>
        await VibrarAsync(session, (200, 0, 90), (130, 0, 60), (60, 0, 60));

    /// <summary>
    /// Assinatura da troca de modo do LED: dois toques secos e agudos no motor
    /// direito (alta frequência). Contrasta com o grave da emulação, então dá
    /// pra reconhecer qual atalho pegou só pelo tato.
    /// </summary>
    private async Task PulsoDuploAsync(ControllerSession session) =>
        await VibrarAsync(session, (0, 190, 60), (0, 0, 70), (0, 190, 60));

    /// <summary>
    /// Toca uma sequência de vibração e garante os motores parados no fim.
    ///
    /// Cada passo é (esquerdo, direito, duração). Os dois motores existem e
    /// soam diferente: o ESQUERDO é de baixa frequência e dá peso, tremor
    /// grave; o DIREITO é de alta frequência e dá toque seco. É essa diferença
    /// que torna as assinaturas reconhecíveis no tato — só variar o ritmo de um
    /// mesmo motor produzia vibrações parecidas demais entre si.
    /// </summary>
    private async Task VibrarAsync(ControllerSession session,
                                   params (byte esquerdo, byte direito, int ms)[] passos)
    {
        try
        {
            foreach (var (esquerdo, direito, ms) in passos)
            {
                SetRumble(session, left: esquerdo, right: direito);
                await Task.Delay(ms);
            }
            SetRumble(session, left: 0, right: 0);
        }
        catch
        {
            // Retorno tátil não é essencial: se falhar, o atalho segue seu curso.
        }
    }

    /// <summary>
    /// Aplica no controle físico a vibração pedida pelo jogo. Só escreve
    /// quando o valor muda — jogos mandam o mesmo valor repetidas vezes, e
    /// reenviar tudo isso entupiria o canal (sobretudo no Bluetooth).
    /// </summary>
    private void SetRumble(ControllerSession session, byte left, byte right)
    {
        if (session.RumbleLeft == left && session.RumbleRight == right) return;

        session.RumbleLeft = left;
        session.RumbleRight = right;

        // Vibração é o jogo reagindo ao jogador: conta como atividade, senão o
        // controle poderia desligar sozinho no meio de uma partida parada.
        if (AutoPowerOffEnabled && (left != 0 || right != 0))
            session.LastActivityUtc = DateTime.UtcNow;

        try
        {
            var (r, g, b) = session.CurrentColor;
            lock (session.WriteLock)
            {
                // aplicarMotores: este é o único report que MUDA a vibração,
                // então é ele que precisa mandar o controle aplicar os bytes.
                session.Stream.Write(BuildLightbarReport(session, r, g, b, aplicarMotores: true));
            }
        }
        catch (Exception ex)
        {
            DseLog.WriteThrottled($"rumble-{session.Serial}",
                $"[watcher] falha ao aplicar vibração em {session.Type}: {DseLog.Fmt(ex)}");
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;

        _hidHide.EnsureSelfWhitelisted();

        // NÃO existe mais warm-up do ViGEm aqui. Ele criava conexões ambíguas
        // (0x80004005) de propósito pra "esquentar" o driver, apostando que
        // fechar o cliente temporário limparia os alvos que sobrassem. Como
        // essa premissa nunca foi confirmada, o warm-up podia estar fabricando
        // 2-3 controles fantasma a cada abertura — em troca de evitar um
        // fantasma eventual no primeiro connect real, que o retry do
        // VirtualController já trata mantendo o controle funcionando.

        _identification.ControllerArrived += OnControllerArrived;
        _identification.ControllerRemoved += OnControllerRemoved;
        _identification.Start();

        // Timer de idle: verifica a cada 30s se algum controle passou do
        // tempo ocioso e, se sim, desliga. Intervalo folgado porque o alvo
        // é de minutos.
        _idleTimer = new System.Threading.Timer(_ => CheckIdleControllers(),
            null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private System.Threading.Timer? _idleTimer;

    private void CheckIdleControllers()
    {
        if (!AutoPowerOffEnabled) return;

        var now = DateTime.UtcNow;
        // Snapshot sob lock; o teardown (demorado) roda fora dele.
        List<ControllerSession> snapshot;
        lock (_sync) snapshot = _sessions.Values.ToList();

        foreach (var session in snapshot)
        {
            // USB não dá pra desligar por software — só Bluetooth.
            if (!session.IsBluetooth) continue;

            if (now - session.LastActivityUtc >= AutoPowerOffIdle)
            {
                try
                {
                    BluetoothDisconnector.TryDisconnect(session.Serial);
                }
                catch { /* se falhar, o controle segue conectado — sem crash */ }

                TeardownSession(session, isManualDisable: false);
            }
        }
    }

    private void OnControllerArrived(IdentifiedController info) =>
        OnControllerArrived(new DetectedDeviceInfo
        {
            Type = info.Type,
            VendorId = info.VendorId,
            ProductId = info.ProductId,
            DevicePath = info.DevicePath,
            IsBluetooth = info.IsBluetooth,
            SerialNumber = info.SerialNumber ?? info.DevicePath
        });

    private void OnControllerArrived(DetectedDeviceInfo info)
    {
        var serial = info.SerialNumber;

        ControllerSession? btSessionToDrop = null;
        bool monitorarDesativado = false;
        lock (_sync)
        {
            if (_disabledSerials.Contains(serial))
            {
                // Emulação desativada pra este controle: não monta sessão, mas
                // passa a escutar pra o atalho de desligar continuar valendo
                // (inclusive quando ele reconecta já desativado).
                monitorarDesativado = !_monitors.ContainsKey(serial);
            }
        }

        if (monitorarDesativado)
        {
            StartMonitorAfterTeardown(serial, info.DevicePath, info.Type, info.IsBluetooth);
            return;
        }
        lock (_sync)
        {
            if (_disabledSerials.Contains(serial)) return;

            // Auto-Disable BT: se este dispositivo chegou por USB e o MESMO
            // controle (mesmo serial/MAC) já tem uma sessão ativa por
            // Bluetooth, ela precisa cair pro USB assumir.
            if (AutoDisableBluetoothOnUsb && !info.IsBluetooth
                && _sessions.TryGetValue(serial, out var existingBtSession)
                && existingBtSession.IsBluetooth)
            {
                btSessionToDrop = existingBtSession;
            }
            else if (_sessions.ContainsKey(serial))
            {
                return; // já tem sessão viva pra esse controle
            }

            // Reserva: impede que outro evento de chegada quase simultâneo
            // monte uma segunda sessão (e um segundo controle virtual) pro
            // mesmo controle enquanto esta ainda está sendo montada.
            if (!_pendingSerials.Add(serial)) return;
        }

        DseLog.Write($"[watcher] controle chegou: {info.Type} via {(info.IsBluetooth ? "BT" : "USB")}");

        try
        {
            // Fora do lock (operações demoradas).
            if (btSessionToDrop != null)
            {
                // Desliga o rádio BT do controle de verdade.
                try { BluetoothDisconnector.TryDisconnect(serial); }
                catch { /* se falhar, segue — o pior caso é ter as duas conexões */ }

                // Derruba a sessão BT antiga JÁ (síncrono), senão a nova sessão
                // USB dividiria o mesmo serial/MAC. suppressNotify evita o
                // flicker de "desconectou/conectou" na UI.
                TeardownSession(btSessionToDrop, isManualDisable: false, suppressNotify: true);
            }

            BuildSession(info, serial);
        }
        finally
        {
            lock (_sync) _pendingSerials.Remove(serial);
        }
    }

    /// <summary>
    /// Monta a sessão (abre o device, oculta no HidHide, cria o controle
    /// virtual e inicia os loops). Só é chamado com o serial reservado em
    /// _pendingSerials — ver OnControllerArrived.
    /// </summary>
    private void BuildSession(DetectedDeviceInfo info, string serial)
    {
        var device = DeviceList.Local.GetHidDevices()
            .FirstOrDefault(d => d.DevicePath == info.DevicePath);
        if (device == null) return;

        // Se havia um unhide agendado pra este dispositivo (teardown recente),
        // cancela: ele desocultaria ESTA sessão nova quando o prazo vencesse.
        lock (_sync) _pendingUnhides.Remove(info.DevicePath);

        _hidHide.HideDevice(info.DevicePath);

        if (!device.TryOpen(out var stream))
        {
            _hidHide.UnhideDevice(info.DevicePath);
            return;
        }
        stream.ReadTimeout = 2000;

        var profile = _profileManager.GetProfile(serial);

        VirtualController virtualController;
        try
        {
            // O construtor do VirtualController conecta o alvo virtual no
            // ViGEm, que pode falhar por timing (0x80004005) na inicialização.
            // Se falhar mesmo após os retries, isola a falha: limpa o que já
            // foi aberto e desiste DESTA sessão, sem derrubar o app inteiro.
            // O controle vai reaparecer no próximo scan/religar.
            virtualController = new VirtualController(profile);
        }
        catch (Exception ex)
        {
            // A sessão nem chegou a nascer. Sem este log, o controle
            // simplesmente não aparecia no app e não havia como saber o motivo.
            DseLog.Write($"[watcher] FALHA ao montar a sessão de {info.Type}: {DseLog.Fmt(ex)}");
            try { stream.Dispose(); } catch { /* ignora */ }
            _hidHide.UnhideDevice(info.DevicePath);
            return;
        }

        var hotkeys = new HotkeyDetector();
        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        var session = new ControllerSession
        {
            Serial = serial,
            DevicePath = info.DevicePath,
            Type = info.Type,
            IsBluetooth = info.IsBluetooth,
            Device = device,
            Stream = stream,
            VirtualController = virtualController,
            Hotkeys = hotkeys,
            Cts = sessionCts,
            LedMode = LedMode.Preset
        };

        hotkeys.HotkeyTriggered += evt => OnHotkeyTriggered(session, evt);
        virtualController.RumbleReceived += (esquerdo, direito) => SetRumble(session, esquerdo, direito);

        lock (_sync) _sessions[serial] = session;

        session.ReadLoopTask = Task.Run(() => ReadLoopAsync(session, sessionCts.Token));

        if (profile == EmulationProfile.DualShock4)
        {
            session.LedLoopCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
            session.LedLoopTask = Task.Run(() => LedPassthroughLoopAsync(session, session.LedLoopCts.Token));
        }

        // Inicialização do DualSense: UMA vez por sessão, antes da primeira
        // cor. Mata a animação de boot do firmware e entrega a lightbar ao
        // host — é o que o driver do kernel faz ao assumir o controle. Os
        // reports de cor seguintes mandam só a cor (mandar o "apagar" junto
        // com a cor fazia a luz acender e sumir 1s depois).
        if (session.Type == PhysicalControllerType.DualSense)
        {
            try
            {
                lock (session.WriteLock)
                {
                    var init = session.IsBluetooth
                        ? DualSenseLightbarReport.BuildBluetoothInitReport(session.NextLedSeq())
                        : DualSenseLightbarReport.BuildUsbInitReport();
                    session.Stream.Write(init);
                }
            }
            catch (Exception ex)
            {
                // Não é crítico: sem isso o pior caso é a animação de boot do
                // firmware disputar a lightbar nos primeiros instantes.
                DseLog.Write($"[watcher] init da lightbar do DualSense falhou: {DseLog.Fmt(ex)}");
            }
        }

        ApplyPresetColor(session, profile);

        ControllerConnected?.Invoke(new ActiveControllerInfo
        {
            Serial = serial,
            Type = info.Type,
            IsBluetooth = info.IsBluetooth,
            Profile = profile,
            LedMode = session.LedMode
        });
    }

    private void OnControllerRemoved(string devicePath)
    {
        ControllerSession? session;
        string? monitorSerial = null;
        lock (_sync)
        {
            session = _sessions.Values.FirstOrDefault(s => s.DevicePath == devicePath);

            // O controle pode estar apenas sendo escutado (emulação desativada).
            foreach (var (serial, monitor) in _monitors)
            {
                if (monitor.DevicePath == devicePath) { monitorSerial = serial; break; }
            }
        }

        if (monitorSerial != null)
        {
            StopMonitor(monitorSerial);

            // O controle sumiu enquanto estava apenas sendo monitorado (com a
            // emulação desativada). Como não há sessão pra desmontar, ninguém
            // avisaria a interface — e o card ficava na tela pra sempre, como
            // se o controle seguisse conectado.
            ControllerDisconnected?.Invoke(monitorSerial);
        }

        if (session != null) TeardownSession(session, isManualDisable: false);
    }

    private void TeardownSession(ControllerSession session, bool isManualDisable, bool suppressNotify = false)
    {
        // Idempotência: os eventos de remoção chegam duplicados às vezes
        // (dois teardowns da mesma sessão no log). O teste-e-marca vai sob o
        // lock pra que duas threads não passem as duas por aqui.
        lock (_sync)
        {
            if (session.TornDown) return;
            session.TornDown = true;
        }

        DseLog.Write($"[watcher] teardown da sessão {session.Type} (manual={isManualDisable})");
        // Para o loop de piscar de bateria baixa, se estiver ativo.
        if (session.BlinkLoopCts != null)
        {
            session.BlinkLoopCts.Cancel();
            try { session.BlinkLoopTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* ignora */ }
            session.BlinkLoopCts = null;
            session.BlinkLoopTask = null;
            session.LowBatteryBlinking = false;
        }

        // Para o loop de LED e espera ele sair de verdade antes de
        // desmontar o controle virtual — senão corre risco de derrubar o
        // processo inteiro (ViGEm não é thread-safe).
        if (session.LedLoopTask != null && session.LedLoopCts != null)
        {
            session.LedLoopCts.Cancel();
            try { session.LedLoopTask.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* ignora */ }
            session.LedLoopTask = null;
            session.LedLoopCts = null;
        }

        // "Report de despedida" pro DualSense: ao soltar o controle (emulação
        // desativada ou app fechando), apaga a lightbar e os player lights —
        // sem isso, os LEDs brancos de baixo ficavam acesos até religar o
        // controle (bug relatado). Não se aplica à transição BT->USB
        // (suppressNotify), onde o mesmo controle continua emulado. Se o
        // teardown veio de desconexão física, o Write falha e é ignorado.
        if (!suppressNotify && session.Type == PhysicalControllerType.DualSense)
        {
            try
            {
                lock (session.WriteLock)
                {
                    var goodbye = session.IsBluetooth
                        ? DualSenseLightbarReport.BuildBluetoothGoodbyeReport(session.NextLedSeq())
                        : DualSenseLightbarReport.BuildUsbGoodbyeReport();
                    session.Stream.Write(goodbye);
                }
                DseLog.Write("[watcher] goodbye report enviado ao DualSense (LEDs limpos)");
            }
            catch { /* controle já desconectado fisicamente — sem pra quem mandar */ }
        }

        session.Cts.Cancel();
        try { session.Stream.Dispose(); } catch { /* já pode ter sido fechado */ }
        // Espera o read loop sair ANTES de descartar o controle virtual: o
        // Dispose fecha o handle do cliente ViGEm inteiro, e fechar um handle
        // com chamada nativa em voo é receita de crash. O stream já foi
        // descartado acima, então o loop cai em exceção e sai rápido.
        //
        // GUARDA: o teardown pode ser chamado DE DENTRO do próprio read loop
        // (atalho PS = desligar controle). Esperar a si mesmo travaria pra
        // sempre — por isso só espera quando estamos em outra thread.
        var readLoop = session.ReadLoopTask;
        if (readLoop != null && Task.CurrentId != readLoop.Id)
        {
            try { readLoop.Wait(TimeSpan.FromMilliseconds(700)); } catch { /* ignora */ }
        }

        // O Dispose da sessão fecha o handle do cliente ViGEm dela, o que faz
        // o driver soltar todos os dispositivos virtuais daquela sessão —
        // inclusive zumbis de Connect ambíguo.
        session.VirtualController.Dispose();

        // O unhide vem SÓ AGORA, depois de o controle virtual já ter sumido —
        // e ainda com um atraso. A ordem importa: se devolvermos o controle
        // físico ao sistema antes, o Windows enxerga o DualSense real E o pad
        // virtual ao mesmo tempo; a Steam enumera o DualSense nessa janela,
        // com o pad virtual ainda ocupando um slot, e dá a ele um número de
        // jogador mais alto (o número de luzes brancas é o número do slot, e
        // a cor da lightbar segue a mesma convenção: azul=P1, vermelho=P2).
        // O Dispose acima retorna antes de o Windows terminar de propagar a
        // remoção do pad virtual, então o atraso dá esse respiro.
        ScheduleUnhide(session.DevicePath);

        lock (_sync) _sessions.Remove(session.Serial);

        // suppressNotify: usado na transição BT->USB do mesmo controle, onde
        // não queremos que a UI pisque um "desconectou/conectou" — é o mesmo
        // aparelho continuando pelo cabo.
        if (suppressNotify) return;

        if (isManualDisable)
            DeviceDisabledByUser?.Invoke(session.Serial);
        else
            ControllerDisconnected?.Invoke(session.Serial);
    }

    private async Task ReadLoopAsync(ControllerSession session, CancellationToken ct)
    {
        var reportLength = SafeGetMaxLen(session);
        var buffer = new byte[reportLength];
        int consecutiveEmptyReads = 0;

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = session.Stream.Read(buffer, 0, buffer.Length);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (IsTransientReadError(ex))
            {
                try { await Task.Delay(50, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            catch (Exception ex)
            {
                // Desligamento NORMAL: o teardown descarta o stream de
                // propósito justamente pra tirar este loop do bloqueio de
                // leitura — ele acorda com "stream fechado" e sai. Isso não é
                // erro, e registrar como se fosse enchia o log de alarme falso
                // a cada desativação de emulação (log com falso positivo é
                // como diagnóstico deixa de servir pra alguma coisa).
                bool desligamentoNormal =
                    ct.IsCancellationRequested
                    || session.TornDown
                    || ex is ObjectDisposedException;

                if (!desligamentoNormal)
                {
                    // Aqui sim: o controle parou de funcionar por um motivo
                    // que a gente não previu.
                    DseLog.Write($"[watcher] read loop de {session.Type} encerrado por erro inesperado: {DseLog.Fmt(ex)}");
                }
                break;
            }

            if (read <= 0)
            {
                consecutiveEmptyReads++;
                try { await Task.Delay(consecutiveEmptyReads > 50 ? 500 : 10, ct); }
                catch (OperationCanceledException) { break; }
                if (consecutiveEmptyReads > 50) consecutiveEmptyReads = 0;
                continue;
            }
            consecutiveEmptyReads = 0;

            NormalizedInputState? parsedState = null;
            try
            {
                // Alterna entre dois objetos de estado por sessão. O anterior
                // segue intacto em LastInputSnapshot, que é o que a detecção de
                // inatividade compara — por isso DOIS, e não um só: com um
                // único objeto, atual e anterior seriam o mesmo e o controle
                // nunca seria considerado parado.
                var state = session.NextStateBuffer();

                if (session.Type == PhysicalControllerType.DualShock4)
                {
                    Ds4ReportParser.Parse(buffer, session.IsBluetooth, state);

                    if (session.VirtualController.ActiveProfile == EmulationProfile.DualShock4)
                    {
                        int baseOffset = session.IsBluetooth ? 3 : 1;
                        session.VirtualController.SubmitDs4ExtendedState(state, buffer, baseOffset);
                    }
                    else
                    {
                        session.VirtualController.SubmitState(state);
                    }

                    parsedState = state;
                }
                else // DualSense
                {
                    DualSenseReportParser.Parse(buffer, session.IsBluetooth, state);

                    if (session.VirtualController.ActiveProfile == EmulationProfile.DualShock4)
                    {
                        session.VirtualController.SubmitDs4ExtendedStateDecoded(state);
                    }
                    else
                    {
                        session.VirtualController.SubmitState(state);
                    }

                    parsedState = state;
                }
            }
            catch (Exception ex)
            {
                // Falha ao processar/enviar um report isolado não derruba a
                // sessão. Limitado por tempo: aqui passam ~250 reports por
                // segundo, e um log por report entupiria o arquivo.
                DseLog.WriteThrottled($"parse-{session.Serial}",
                    $"[watcher] falha ao processar/enviar report de {session.Type}: {DseLog.Fmt(ex)}");
            }

            // Hotkeys FORA do try acima: a troca de perfil que isso pode
            // disparar (com desconexão/reconexão de controle virtual) não pode
            // ter suas exceções engolidas pelo catch de "report isolado" —
            // era isso que deixava o controle Xbox virtual fantasma quando a
            // remoção falhava silenciosamente.
            if (parsedState != null)
            {
                session.Hotkeys.Feed(parsedState, DateTime.UtcNow);

                // Idle inteligente: se houve atividade real neste frame,
                // marca o instante. O timer de idle usa isso pra decidir se
                // o controle ficou parado tempo demais.
                if (AutoPowerOffEnabled && HasActivity(parsedState, session.LastInputSnapshot))
                {
                    session.LastActivityUtc = DateTime.UtcNow;
                }
                session.LastInputSnapshot = parsedState;

                // Atualiza bateria só quando muda (evita spam de evento a
                // ~250Hz; o nível muda de minuto em minuto).
                if (parsedState.BatteryPercent >= 0 &&
                    (parsedState.BatteryPercent != session.BatteryPercent ||
                     parsedState.IsCharging != session.IsCharging))
                {
                    session.BatteryPercent = parsedState.BatteryPercent;
                    session.IsCharging = parsedState.IsCharging;
                    BatteryChanged?.Invoke(session.Serial, session.BatteryPercent, session.IsCharging);

                    UpdateLowBatteryBlink(session);
                }
            }
        }
    }

    private const int LowBatteryThreshold = 10;

    /// <summary>
    /// Liga ou desliga o piscar de bateria crítica conforme o nível atual.
    /// Pisca quando a bateria está <=10% e NÃO está carregando (se está no
    /// cabo carregando, não faz sentido alarmar). Ao sair dessa condição,
    /// para de piscar e restaura a cor normal do modo ativo.
    /// </summary>
    private void UpdateLowBatteryBlink(ControllerSession session)
    {
        bool shouldBlink = session.BatteryPercent >= 0
            && session.BatteryPercent <= LowBatteryThreshold
            && !session.IsCharging;

        if (shouldBlink && !session.LowBatteryBlinking)
        {
            // Começa a piscar. Para o loop de passthrough antes, pra não
            // brigarem pelo controle do LED.
            session.LowBatteryBlinking = true;
            session.BlinkLoopCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
            session.BlinkLoopTask = Task.Run(() => LowBatteryBlinkLoopAsync(session, session.BlinkLoopCts.Token));
        }
        else if (!shouldBlink && session.LowBatteryBlinking)
        {
            // Para de piscar e restaura a cor do modo atual.
            session.LowBatteryBlinking = false;
            if (session.BlinkLoopCts != null)
            {
                session.BlinkLoopCts.Cancel();
                try { session.BlinkLoopTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* ignora */ }
                session.BlinkLoopCts = null;
                session.BlinkLoopTask = null;
            }
            // Restaura a cor do preset (se estiver em passthrough, o próprio
            // jogo volta a mandar a cor no próximo output report).
            if (session.LedMode == LedMode.Preset)
            {
                ApplyPresetColor(session, session.VirtualController.ActiveProfile);
            }
        }
    }

    /// <summary>
    /// Loop que faz o LED piscar vermelho continuamente (vermelho / apagado)
    /// enquanto a bateria estiver crítica. Roda até ser cancelado (bateria
    /// subiu, carregando, ou controle desconectou).
    /// </summary>
    private async Task LowBatteryBlinkLoopAsync(ControllerSession session, CancellationToken ct)
    {
        bool on = false;
        while (!ct.IsCancellationRequested)
        {
            on = !on;
            byte r = on ? (byte)64 : (byte)0; // vermelho aceso / apagado
            try
            {
                lock (session.WriteLock)
                {
                    session.Stream.Write(BuildLightbarReport(session, r, 0, 0));
                }
            }
            catch { /* falha ao escrever não derruba o loop */ }

            try { await Task.Delay(500, ct); } catch { break; } // pisca a cada 500ms
        }
    }

    private async Task LedPassthroughLoopAsync(ControllerSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[]? report;
            bool timedOut;
            try
            {
                report = session.VirtualController.AwaitDs4OutputReport(250, out timedOut);
            }
            catch
            {
                try { await Task.Delay(500, ct); } catch { break; }
                continue;
            }

            if (timedOut || report == null || report.Length < 9) continue;

            // VIBRAÇÃO — vem no mesmo report que a cor. No formato do DS4 a
            // ordem é sempre motor direito, motor esquerdo, R, G, B em bytes
            // seguidos; como a cor está em 6/7/8, os motores estão em 4 e 5.
            //
            // Isto fica ANTES das checagens de modo de LED de propósito: a
            // vibração precisa funcionar mesmo no modo de cor fixa e mesmo
            // enquanto o LED está piscando por bateria crítica.
            SetRumble(session, left: report[5], right: report[4]);

            // Modo Preset: drena a fila (evita acúmulo no ViGEmBus) mas
            // ignora o pedido de COR do jogo — a cor fixa do perfil continua
            // valendo. A vibração acima já foi aplicada.
            if (session.LedMode != LedMode.Passthrough) continue;

            // Se está piscando por bateria crítica, o piscar tem prioridade —
            // não escreve a cor do passthrough por cima (senão brigam pelo LED).
            if (session.LowBatteryBlinking) continue;

            byte r = report[6];
            byte g = report[7];
            byte b = report[8];

            // Feedback do jogo conta como atividade pro idle — MAS só quando a
            // cor realmente muda. Jogos que mandam a mesma cor repetida (mesmo
            // parados) não devem impedir o desligamento pra sempre.
            if (AutoPowerOffEnabled && session.CurrentColor != (r, g, b))
            {
                session.LastActivityUtc = DateTime.UtcNow;
            }

            try
            {
                lock (session.WriteLock)
                {
                    session.Stream.Write(BuildLightbarReport(session, r, g, b));
                }
                session.CurrentColor = (r, g, b);
                LedColorChanged?.Invoke(session.Serial, r, g, b);
            }
            catch (Exception ex)
            {
                // Escrita de LED falhando não deve derrubar a sessão.
                DseLog.WriteThrottled($"led-{session.Serial}",
                    $"[watcher] falha ao escrever LED em {session.Type}: {DseLog.Fmt(ex)}");
            }
        }
    }

    private void OnHotkeyTriggered(ControllerSession session, HotkeyEvent evt)
    {
        switch (evt)
        {
            case HotkeyEvent.ToggleProfile:
                if (session.LedLoopTask != null && session.LedLoopCts != null)
                {
                    session.LedLoopCts.Cancel();
                    try { session.LedLoopTask.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* ignora */ }
                    session.LedLoopTask = null;
                    session.LedLoopCts = null;
                }

                var newProfile = _profileManager.ToggleProfile(session.Serial);
                session.VirtualController.SwitchProfile(newProfile);

                if (newProfile == EmulationProfile.DualShock4 && session.LedLoopTask == null)
                {
                    session.LedLoopCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
                    session.LedLoopTask = Task.Run(() => LedPassthroughLoopAsync(session, session.LedLoopCts.Token));
                }

                if (session.LedMode == LedMode.Preset)
                {
                    ApplyPresetColor(session, newProfile);
                }

                ProfileChanged?.Invoke(new ActiveControllerInfo
                {
                    Serial = session.Serial,
                    Type = session.Type,
                    IsBluetooth = session.IsBluetooth,
                    Profile = newProfile,
                    LedMode = session.LedMode
                });
                break;

            case HotkeyEvent.PowerOffController:
                if (session.IsBluetooth)
                {
                    BluetoothDisconnector.TryDisconnect(session.Serial);
                }
                TeardownSession(session, isManualDisable: false);
                break;

            case HotkeyEvent.ToggleLedMode:
            {
                var novo = session.LedMode == LedMode.Preset
                    ? LedMode.Passthrough
                    : LedMode.Preset;
                DseLog.Write($"[watcher] atalho de modo do LED: {session.LedMode} -> {novo}");
                SetLedMode(session.Serial, novo);
                LedModeChanged?.Invoke(session.Serial);
                _ = PulsoDuploAsync(session);
                break;
            }

            case HotkeyEvent.ToggleEmulation:
                // Chegou aqui com a emulação ATIVA (é o read loop da sessão que
                // alimenta este detector), então o atalho desativa. O caminho de
                // volta é o monitor: ele assume a escuta e é quem reconhece o
                // mesmo atalho pra reativar. Sai da thread de leitura porque o
                // teardown descarta justamente o stream que ela está usando.
                DseLog.Write($"[watcher] atalho de alternar emulação: desativando {session.Type}");
                var serialParaDesativar = session.Serial;
                _ = Task.Run(async () =>
                {
                    await PulsoDesativarAsync(session);
                    SetDevicePassthroughDisabled(serialParaDesativar, true);
                });
                break;
        }
    }

    /// <summary>
    /// Reativa a emulação a pedido do monitor (atalho Options+PS com a emulação
    /// desligada). Roda fora da thread de leitura do monitor e espera um pouco
    /// antes de reconstruir a sessão: reabrir o dispositivo enquanto o handle
    /// antigo ainda está vivo faria o TryOpen falhar, e o resultado seria o
    /// atalho não fazer nada — nem emulação, nem monitor.
    /// </summary>
    private void RequestReenableEmulation(string serial)
    {
        _ = Task.Run(async () =>
        {
            DseLog.Write("[monitor] atalho de alternar emulação: reativando");
            StopMonitor(serial);          // fecha o stream e espera o loop sair
            await Task.Delay(300);        // margem pro handle fechar de fato
            SetDevicePassthroughDisabled(serial, false);
        });
    }

    private static int SafeGetMaxLen(ControllerSession session)
    {
        try { return session.Device.GetMaxInputReportLength(); }
        catch { return 78; }
    }

    /// <summary>
    /// Idle inteligente: decide se houve ATIVIDADE neste frame comparado ao
    /// anterior. Atividade = qualquer botão pressionado, gatilho apertado,
    /// analógico fora do centro, movimento de giroscópio/acelerômetro, touch,
    /// OU o jogo mandando feedback (LED em passthrough / cor mudando).
    /// Se nada disso está acontecendo, o frame conta como "parado".
    /// </summary>
    private static bool HasActivity(NormalizedInputState cur, NormalizedInputState? prev)
    {
        // Qualquer botão digital pressionado.
        if (cur.Cross || cur.Circle || cur.Square || cur.Triangle ||
            cur.L1 || cur.R1 || cur.L3 || cur.R3 ||
            cur.Share || cur.Options || cur.Ps || cur.TouchpadClick ||
            cur.DpadUp || cur.DpadDown || cur.DpadLeft || cur.DpadRight)
            return true;

        // Gatilhos analógicos apertados (margem pra ruído).
        if (cur.L2 > 8 || cur.R2 > 8) return true;

        // Touch ativo.
        if (cur.TouchActive) return true;

        // Analógicos: usamos a VARIAÇÃO entre frames, não o valor absoluto.
        // Isso é de propósito: um stick com drift (desgaste) repousa parado
        // num valor deslocado (ex: fora da zona morta), e se olhássemos o
        // valor absoluto ele marcaria "ativo" pra sempre e o controle nunca
        // desligaria (bug relatado no DualSense). Já a variação de um stick
        // parado — mesmo com drift — é pequena; só movê-lo de fato gera
        // variação grande. Assim, drift estático não conta como uso.
        if (prev != null)
        {
            const int stickMoveThreshold = 3200;
            if (Math.Abs((int)cur.LX - prev.LX) > stickMoveThreshold ||
                Math.Abs((int)cur.LY - prev.LY) > stickMoveThreshold ||
                Math.Abs((int)cur.RX - prev.RX) > stickMoveThreshold ||
                Math.Abs((int)cur.RY - prev.RY) > stickMoveThreshold)
                return true;
        }

        // Movimento (giroscópio) — compara com o frame anterior, já que os
        // valores absolutos variam. IMPORTANTE: o giroscópio (sobretudo o do
        // DualSense) tem drift/ruído mesmo parado numa mesa — micro-variações
        // constantes. Um threshold baixo faz o controle "parecer" sempre em
        // uso e NUNCA desligar (bug relatado). O valor precisa ser alto o
        // bastante pra ignorar esse ruído de sensor parado, mas baixo o
        // bastante pra pegar o controle sendo segurado na mão (que oscila
        // muito mais). 6000 dá boa margem nos dois sentidos.
        if (prev != null)
        {
            const int motionThreshold = 6000;
            if (Math.Abs(cur.GyroX - prev.GyroX) > motionThreshold ||
                Math.Abs(cur.GyroY - prev.GyroY) > motionThreshold ||
                Math.Abs(cur.GyroZ - prev.GyroZ) > motionThreshold)
                return true;
        }

        return false;
    }

    private static bool IsLikelyBluetooth(HidDevice device)
    {
        try { return device.GetMaxInputReportLength() >= ControllerConstants.ReportLength.Ds4Bluetooth; }
        catch { return false; }
    }

    private static bool IsTransientReadError(Exception ex) =>
        ex is TimeoutException or IOException or System.ComponentModel.Win32Exception;

    /// <summary>
    /// Sobe o monitor depois que o teardown terminou. O teardown agenda o
    /// unhide pra daqui a pouco; reabrir o dispositivo antes disso não faz
    /// sentido, então esperamos um pouco mais que esse prazo.
    /// </summary>
    private void StartMonitorAfterTeardown(string serial, string devicePath,
                                           PhysicalControllerType type, bool isBluetooth)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(700);

            // Se a emulação já voltou nesse meio-tempo, não abre nada.
            lock (_sync)
            {
                if (!_disabledSerials.Contains(serial)) return;
                if (_monitors.ContainsKey(serial)) return;
            }

            var monitor = ControllerMonitor.TryStart(serial, devicePath, type, isBluetooth);
            if (monitor == null) return;

            // Depois de desligar o controle, o monitor não tem mais o que
            // escutar — ele se encerra sozinho.
            monitor.PoweredOff += s => StopMonitor(s);
            monitor.ToggleEmulationRequested += RequestReenableEmulation;

            bool descartar = false;
            lock (_sync)
            {
                if (!_disabledSerials.Contains(serial) || !_monitors.TryAdd(serial, monitor))
                    descartar = true;
            }
            if (descartar) monitor.Dispose();
        });
    }

    private void StopMonitor(string serial)
    {
        ControllerMonitor? monitor;
        lock (_sync)
        {
            if (!_monitors.Remove(serial, out monitor)) return;
        }
        monitor.Dispose();
    }

    /// <summary>
    /// Devolve o controle físico ao Windows depois de um pequeno atraso, pra
    /// dar tempo do sistema propagar a remoção do controle virtual antes de o
    /// físico reaparecer (senão a Steam conta os dois e trata o DualSense como
    /// player 2/3, acendendo luzes brancas e mudando a cor da lightbar).
    ///
    /// O unhide é OBRIGATÓRIO: se ele não acontecer, o controle continua
    /// escondido do Windows mesmo depois de fechar o DSE, e o usuário fica sem
    /// controle nenhum. Por isso as três travas:
    ///  - no encerramento do app, roda SÍNCRONO (não dá pra confiar num
    ///    atraso quando o processo está morrendo);
    ///  - fica registrado em _pendingUnhides, e o Dispose do watcher executa
    ///    na hora tudo que ainda estiver pendente;
    ///  - se o mesmo dispositivo for ocultado de novo antes do prazo (o
    ///    usuário reativou a emulação), o agendamento é cancelado.
    /// </summary>
    private void ScheduleUnhide(string devicePath)
    {
        if (_shuttingDown)
        {
            _hidHide.UnhideDevice(devicePath);
            return;
        }

        lock (_sync) _pendingUnhides.Add(devicePath);

        _ = Task.Run(async () =>
        {
            await Task.Delay(400);

            // Só desoculta se o agendamento ainda vale: pode ter sido
            // cancelado por um re-hide, ou já executado pelo shutdown.
            lock (_sync)
            {
                if (!_pendingUnhides.Remove(devicePath)) return;
            }

            try { _hidHide.UnhideDevice(devicePath); }
            catch (Exception ex) { DseLog.Write($"[watcher] unhide agendado falhou: {DseLog.Fmt(ex)}"); }
        });
    }

    public void Dispose()
    {
        _shuttingDown = true;
        _cts.Cancel();
        _idleTimer?.Dispose();
        _identification.ControllerArrived -= OnControllerArrived;
        _identification.ControllerRemoved -= OnControllerRemoved;
        _identification.Dispose();

        // Cada sessão fecha o próprio cliente ViGEm no seu Dispose (dentro do
        // TeardownSession), então não há mais cliente compartilhado pra fechar.
        List<ControllerSession> snapshot;
        lock (_sync) snapshot = _sessions.Values.ToList();

        foreach (var session in snapshot)
        {
            TeardownSession(session, isManualDisable: false);
        }

        // Encerra os monitores (controles com emulação desativada que estavam
        // só sendo escutados pelo atalho de desligar).
        List<ControllerMonitor> monitores;
        lock (_sync)
        {
            monitores = _monitors.Values.ToList();
            _monitors.Clear();
        }
        foreach (var monitor in monitores) monitor.Dispose();

        // Rede de segurança: qualquer unhide que tenha sido agendado antes do
        // encerramento (e cujo atraso não venceu) é executado AGORA, síncrono.
        // Sem isso, o controle poderia continuar escondido do Windows depois
        // que o DSE fecha — o usuário ficaria sem controle nenhum.
        List<string> pendentes;
        lock (_sync)
        {
            pendentes = _pendingUnhides.ToList();
            _pendingUnhides.Clear();
        }
        foreach (var path in pendentes)
        {
            try { _hidHide.UnhideDevice(path); }
            catch (Exception ex) { DseLog.Write($"[watcher] unhide pendente no shutdown falhou: {DseLog.Fmt(ex)}"); }
        }

        // Última rede de segurança: manda o HidHide devolver TUDO que ainda
        // estiver oculto e tira o DSE da lista de aplicativos autorizados. Se
        // algum unhide individual falhou em qualquer ponto, é aqui que o
        // controle é devolvido ao Windows. O pior cenário deste projeto é o
        // usuário fechar o DSE e ficar sem controle nenhum — esta chamada
        // existe pra isso não acontecer.
        try { _hidHide.RestoreAll(); }
        catch (Exception ex) { DseLog.Write($"[watcher] RestoreAll no shutdown falhou: {DseLog.Fmt(ex)}"); }
    }

    private sealed class DetectedDeviceInfo
    {
        public required PhysicalControllerType Type { get; init; }
        public required int VendorId { get; init; }
        public required int ProductId { get; init; }
        public required string DevicePath { get; init; }
        public required bool IsBluetooth { get; init; }
        public required string SerialNumber { get; init; }
    }

    private sealed class ControllerSession
    {
        public required string Serial { get; init; }
        public required string DevicePath { get; init; }
        public required PhysicalControllerType Type { get; init; }
        public required bool IsBluetooth { get; init; }
        public required HidDevice Device { get; init; }
        public required HidStream Stream { get; init; }
        public required VirtualController VirtualController { get; init; }

        /// <summary>
        /// True depois do primeiro TeardownSession — os eventos de remoção
        /// do dispositivo chegam duplicados às vezes (visto no log), e o
        /// segundo teardown vira no-op em vez de desmontar de novo.
        /// </summary>
        public bool TornDown;
        public required HotkeyDetector Hotkeys { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required LedMode LedMode { get; set; }
        public (byte r, byte g, byte b) CurrentColor { get; set; }
        public int BatteryPercent { get; set; } = -1;
        public bool IsCharging { get; set; }
        public CancellationTokenSource? LedLoopCts { get; set; }
        public Task? ReadLoopTask { get; set; }
        public Task? LedLoopTask { get; set; }

        // Idle inteligente: marca o último instante em que houve atividade
        // real (entrada do usuário OU feedback do jogo). Usado pra desligar o
        // controle após um tempo sem NADA acontecendo.
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
        // Snapshot do último estado, pra detectar movimento (giroscópio, sticks)
        // comparando frames consecutivos.
        public NormalizedInputState? LastInputSnapshot { get; set; }

        // Dois estados reutilizados, alternando a cada frame: o read loop roda
        // ~250x por segundo e alocar um objeto novo por frame era lixo
        // constante pro coletor. São dois porque a detecção de inatividade
        // compara o frame atual com o anterior — se fosse um só, os dois
        // seriam o mesmo objeto e nunca haveria diferença.
        private readonly NormalizedInputState _stateA = new();
        private readonly NormalizedInputState _stateB = new();
        private bool _usandoA;

        public NormalizedInputState NextStateBuffer()
        {
            _usandoA = !_usandoA;
            return _usandoA ? _stateA : _stateB;
        }

        // Piscar por bateria crítica: quando o nível cai pra <=10%, um loop
        // faz o LED piscar vermelho continuamente, sobrepondo o modo normal.
        // O loop roda enquanto LowBatteryBlinking for true.
        public CancellationTokenSource? BlinkLoopCts { get; set; }
        public Task? BlinkLoopTask { get; set; }
        public bool LowBatteryBlinking { get; set; }

        // Sequência dos output reports do LED via Bluetooth (DualSense): o
        // firmware exige um número de sequência DIFERENTE a cada report
        // (nibble alto do seq_tag), senão descarta como duplicata. Um
        // contador por controle, incrementado a cada envio.
        private byte _dualSenseLedSeq;
        public byte NextLedSeq() => _dualSenseLedSeq++;

        /// <summary>
        /// Serializa as escritas no controle físico. Quatro pontos escrevem
        /// no mesmo stream: a cor do preset (em background), o piscar de
        /// bateria crítica, o passthrough de LED e o report de despedida.
        /// Sem isso, dois reports podiam se intercalar — e no DualSense via
        /// BT, onde cada report leva número de sequência e CRC, o firmware
        /// descarta o que chega fora de ordem (LED "travado"). Montar o
        /// report e escrever tem que acontecer DENTRO deste lock, pra a
        /// ordem dos seq bater com a ordem das escritas.
        /// </summary>
        public readonly object WriteLock = new();

        // Força ATUAL dos motores. Fica na sessão porque cor e vibração
        // viajam no mesmo report de saída: qualquer escrita precisa carregar
        // os dois, senão uma atualização de cor cortaria a vibração (e
        // vice-versa).
        public byte RumbleLeft;
        public byte RumbleRight;
    }
}
