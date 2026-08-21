using DSE.Core.Diagnostics;
using DSE.Core.Profiles;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.ViGEm.Client.Targets.DualShock4;

namespace DSE.Core.Virtual;

/// <summary>
/// Estado de input normalizado, já traduzido do formato bruto do controle
/// físico (DS4/DualSense) para um formato neutro que serve tanto para
/// alimentar um Xbox360Controller quanto um DS4Controller virtual.
/// </summary>
public sealed class NormalizedInputState
{
    public bool Cross, Circle, Square, Triangle;
    public bool L1, R1, L3, R3;
    public bool Share, Options, Ps, TouchpadClick;
    public bool DpadUp, DpadDown, DpadLeft, DpadRight;
    public byte L2, R2; // 0-255 analógico
    public short LX, LY, RX, RY; // -32768 a 32767

    // Campos de movimento/touch — só usados no caminho de decodificação
    // explícita (DualSense). Pro DS4, o passthrough usa cópia direta de
    // bytes brutos (mais simples e já validado), então esses campos ficam
    // em 0/false nesse caminho sem problema.
    public short GyroX, GyroY, GyroZ;
    public short AccelX, AccelY, AccelZ;
    public bool TouchActive;
    public int TouchX, TouchY;

    // Bateria: nível 0-100% e se está carregando. -1 = desconhecido/não lido.
    public int BatteryPercent = -1;
    public bool IsCharging;

    /// <summary>
    /// Volta todos os campos ao estado inicial. Existe porque o objeto é
    /// REUTILIZADO a cada frame (o read loop roda ~250x por segundo e alocar
    /// um estado novo a cada volta era lixo constante pro coletor). Vários
    /// campos são preenchidos só condicionalmente pelos parsers — bateria,
    /// giroscópio, touch — então, sem zerar antes, sobraria valor do frame
    /// anterior. Precisa reproduzir EXATAMENTE os padrões da classe, e o
    /// único diferente de zero/false é o BatteryPercent.
    /// </summary>
    public void Reset()
    {
        Cross = Circle = Square = Triangle = false;
        L1 = R1 = L3 = R3 = false;
        Share = Options = Ps = TouchpadClick = false;
        DpadUp = DpadDown = DpadLeft = DpadRight = false;
        L2 = R2 = 0;
        LX = LY = RX = RY = 0;
        GyroX = GyroY = GyroZ = 0;
        AccelX = AccelY = AccelZ = 0;
        TouchActive = false;
        TouchX = TouchY = 0;
        BatteryPercent = -1;
        IsCharging = false;
    }
}

/// <summary>
/// Cria e alimenta um controle virtual (Xbox 360 ou DS4) via ViGEmBus,
/// conforme o perfil de emulação ativo. Cada instância representa UM
/// controle virtual — um por controle físico conectado.
/// </summary>
public sealed class VirtualController : IDisposable
{
    // Cliente ViGEm PRÓPRIO desta sessão (não mais compartilhado pelo
    // watcher). Motivo: o log de hardware mostrou fantasma mesmo com TODOS
    // os Connect OK e todos os cleanups rodando — ou seja, Disconnect()
    // não remove o dispositivo do bus de forma confiável. O único mecanismo
    // comprovadamente eficaz é FECHAR O HANDLE do cliente (é por isso que o
    // fantasma sumia ao fechar o app). Então o cliente é reciclado a cada
    // troca de perfil e no teardown — ver RecycleClientNoLock.
    // Buffer do report DS4 reutilizado entre frames. Antes era um
    // "new byte[63]" por frame, ~250 vezes por segundo por controle. O acesso
    // é sempre de dentro do _vigemLock, então uma instância só é segura.
    private readonly byte[] _ds4ReportBuffer = new byte[63];


    private ViGEmClient _client;
    private IXbox360Controller? _xbox360;
    private IDualShock4Controller? _ds4;
    private EmulationProfile _activeProfile;

    // A lib do ViGEm NÃO é thread-safe (confirmado na doc oficial do SDK).
    // A thread de leitura chama Submit* ~250x/s enquanto a troca de perfil
    // (create/disconnect) pode vir de outra thread. Sem serializar, a
    // remoção do controle virtual pode falhar no meio de um submit e deixar
    // um dispositivo fantasma. Este lock garante que só uma operação toque
    // a lib por vez.
    private readonly object _vigemLock = new();
    private bool _disposed;

    // Rastreia TODOS os alvos virtuais já criados nesta sessão (Xbox e DS4),
    // inclusive os que falharam com 0x80004005 e foram mantidos como "sucesso
    // provável". No teardown, removemos todos — se algum ficou meio-criado no
    // Windows, é aqui que ele é destruído, em vez de virar controle fantasma.
    // (Causa observada do fantasma reaparecer com DualSense: um alvo Xbox
    // criado no boot da sessão que não foi removido no caminho de falha.)
    private readonly List<object> _allCreatedTargets = new();

    /// <summary>
    /// True se algum alvo deste controller nasceu de um Connect ambíguo
    /// (0x80004005) — esses alvos podem ser zumbis irremovíveis pela lib.
    /// O watcher usa isso pra recriar o cliente ViGEm quando a última
    /// sessão fecha (fechar o handle do cliente mata os zumbis).
    /// </summary>
    public bool HasAmbiguousTargets { get; private set; }

    /// <summary>
    /// Vibração pedida pelo JOGO (motor esquerdo/forte, motor direito/fraco).
    /// Só existe no perfil Xbox 360: no perfil DS4 o pedido chega junto do
    /// report de saída que o passthrough de LED já lê.
    /// </summary>
    public event Action<byte, byte>? RumbleReceived;

    private void OnXbox360Feedback(object sender, Xbox360FeedbackReceivedEventArgs e) =>
        RumbleReceived?.Invoke(e.LargeMotor, e.SmallMotor);

    public EmulationProfile ActiveProfile => _activeProfile;

    public VirtualController(EmulationProfile initialProfile)
    {
        _client = new ViGEmClient();
        _activeProfile = initialProfile;
        CreateTarget(initialProfile, recycleClient: false); // cliente acabou de nascer
    }

    private void CreateTarget(EmulationProfile profile, bool recycleClient = true)
    {
        lock (_vigemLock)
        {
            if (_disposed) return;
            DseLog.Write($"[vigem] CreateTarget({profile}) — limpando alvos anteriores");
            DisconnectCurrentNoLock();

            // Fecha o handle antigo e abre um novo ANTES de criar o alvo. É o
            // que garante que nada do perfil anterior sobreviva no bus, mesmo
            // que o Disconnect acima não tenha surtido efeito.
            if (recycleClient) RecycleClientNoLock();

            switch (profile)
            {
                case EmulationProfile.Xbox360:
                    _xbox360 = ConnectXbox360WithRetry();
                    break;

                case EmulationProfile.DualShock4:
                    _ds4 = ConnectDs4WithRetry();
                    break;
            }

            _activeProfile = profile;
            DseLog.Write($"[vigem] CreateTarget({profile}) concluído; alvos rastreados: {_allCreatedTargets.Count}");
        }
    }

    /// <summary>
    /// Fecha o handle do cliente ViGEm atual e abre um novo. Fechar o handle
    /// é o ÚNICO jeito comprovado de fazer o driver soltar os dispositivos
    /// virtuais desta sessão (inclusive os que o Disconnect não removeu e os
    /// zumbis de Connect ambíguo). Sempre chamado dentro do _vigemLock, com
    /// os alvos já desconectados/descartados.
    /// </summary>
    private void RecycleClientNoLock()
    {
        try { _client.Dispose(); }
        catch (Exception ex) { DseLog.Write($"[vigem] recycle: Dispose do cliente lançou: {DseLog.Fmt(ex)}"); }

        _client = new ViGEmClient();
        HasAmbiguousTargets = false; // handle novo: nada de zumbi herdado
        DseLog.Write("[vigem] recycle: cliente ViGEm recriado (handle antigo fechado)");
    }

    /// <summary>
    /// O Connect() do ViGEm pode retornar 0x80004005 ("operação concluída com
    /// êxito" — um erro ambíguo) quando o driver ViGEmBus ainda não está 100%
    /// pronto. O ponto CRÍTICO: nesse erro o controle virtual FREQUENTEMENTE
    /// já foi criado no Windows mesmo assim. Se tratássemos como falha e
    /// criássemos outro alvo, cada tentativa empilharia um controle Xbox real
    /// e visível (a causa dos "vários controles fantasma").
    ///
    /// Estratégia:
    ///  - 0x80004005 é tratado como SUCESSO PROVÁVEL: mantemos ESSE alvo e não
    ///    criamos outro (evita empilhar).
    ///  - Só retentamos em erros claramente diferentes, e SEMPRE destruindo o
    ///    alvo anterior antes de criar o próximo.
    ///  - No máximo UM alvo existe por vez — nunca criamos um segundo sem ter
    ///    destruído o primeiro.
    /// </summary>
    private IXbox360Controller ConnectXbox360WithRetry()
    {
        const int maxAttempts = 5;
        IXbox360Controller? target = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Garante que nenhum alvo anterior sobreviva antes de criar outro.
            // Ao descartar AQUI, remove da lista de rastreamento — senão o
            // teardown descartaria o mesmo alvo de novo (Dispose duplo sobre
            // recurso nativo = crash do processo, não capturável).
            if (target != null)
            {
                _allCreatedTargets.Remove(target);
                try { target.Disconnect(); } catch (Exception ex) { DseLog.Write($"[vigem] xbox retry: Disconnect do alvo falho lançou: {DseLog.Fmt(ex)}"); }
                try { (target as IDisposable)?.Dispose(); } catch (Exception ex) { DseLog.Write($"[vigem] xbox retry: Dispose do alvo falho lançou: {DseLog.Fmt(ex)}"); }
                target = null;
            }

            target = _client.CreateXbox360Controller();
            _allCreatedTargets.Add(target); // rastreia pra limpeza garantida no teardown
            target.FeedbackReceived += OnXbox360Feedback;
            try
            {
                target.Connect();
                DseLog.Write($"[vigem] xbox Connect OK (tentativa {attempt})");
                return target; // sucesso limpo
            }
            catch (Exception ex) when (IsAmbiguousSuccess(ex))
            {
                // 0x80004005: o controle provavelmente FOI criado. Mantém
                // este alvo (não cria outro) — empilhar é justamente o bug.
                DseLog.Write($"[vigem] xbox Connect AMBÍGUO (tentativa {attempt}), mantendo alvo: {DseLog.Fmt(ex)}");
                HasAmbiguousTargets = true;
                return target;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                // Erro diferente e ainda temos tentativas: o laço vai
                // destruir este alvo no topo da próxima iteração antes de
                // criar o próximo.
                DseLog.Write($"[vigem] xbox Connect FALHOU (tentativa {attempt}): {DseLog.Fmt(ex)}");
                Thread.Sleep(100 * attempt);
            }
        }

        // Esgotou as tentativas: devolve o último alvo criado (melhor do que
        // lançar e derrubar a sessão; se for fantasma, é no máximo UM).
        return target!;
    }

    private IDualShock4Controller ConnectDs4WithRetry()
    {
        const int maxAttempts = 5;
        IDualShock4Controller? target = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (target != null)
            {
                // Mesmo cuidado do retry do Xbox: sai da lista antes de
                // descartar, pra o teardown não dar Dispose duplo.
                _allCreatedTargets.Remove(target);
                try { target.Disconnect(); } catch (Exception ex) { DseLog.Write($"[vigem] ds4 retry: Disconnect do alvo falho lançou: {DseLog.Fmt(ex)}"); }
                try { (target as IDisposable)?.Dispose(); } catch (Exception ex) { DseLog.Write($"[vigem] ds4 retry: Dispose do alvo falho lançou: {DseLog.Fmt(ex)}"); }
                target = null;
            }

            target = _client.CreateDualShock4Controller();
            _allCreatedTargets.Add(target); // rastreia pra limpeza garantida no teardown
            try
            {
                target.Connect();
                DseLog.Write($"[vigem] ds4 Connect OK (tentativa {attempt})");
                return target;
            }
            catch (Exception ex) when (IsAmbiguousSuccess(ex))
            {
                DseLog.Write($"[vigem] ds4 Connect AMBÍGUO (tentativa {attempt}), mantendo alvo: {DseLog.Fmt(ex)}");
                HasAmbiguousTargets = true;
                return target;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                DseLog.Write($"[vigem] ds4 Connect FALHOU (tentativa {attempt}): {DseLog.Fmt(ex)}");
                Thread.Sleep(100 * attempt);
            }
        }

        return target!;
    }

    /// <summary>
    /// Detecta o erro 0x80004005 ("operação concluída com êxito"), que na
    /// prática significa que a operação provavelmente teve efeito apesar do
    /// código de erro. HRESULT E_FAIL = 0x80004005.
    /// </summary>
    private static bool IsAmbiguousSuccess(Exception ex)
    {
        const int E_FAIL = unchecked((int)0x80004005);
        return ex.HResult == E_FAIL
            || ex.Message.Contains("0x80004005")
            || ex.Message.Contains("concluída com êxito")
            || ex.Message.Contains("completed successfully");
    }

    /// <summary>
    /// Troca o perfil ativo em runtime (chamado pelo hotkey Share+Options).
    /// Desconecta o controle virtual atual e cria um novo do tipo correspondente —
    /// o Windows/jogo enxerga isso como "trocar de controle", o que é esperado.
    /// </summary>
    public void SwitchProfile(EmulationProfile newProfile)
    {
        if (newProfile == _activeProfile) return;
        CreateTarget(newProfile);
    }

    /// <summary>
    /// Envia o estado atual de input para o controle virtual ativo.
    /// Chamado a cada ciclo de leitura do controle físico (~250Hz).
    /// </summary>
    public void SubmitState(NormalizedInputState state)
    {
        lock (_vigemLock)
        {
            if (_disposed) return;
            switch (_activeProfile)
            {
                case EmulationProfile.Xbox360 when _xbox360 != null:
                    SubmitXbox360(state);
                    break;

                case EmulationProfile.DualShock4 when _ds4 != null:
                    SubmitDs4(state);
                    break;
            }
        }
    }

    private void SubmitXbox360(NormalizedInputState s)
    {
        var pad = _xbox360!;

        pad.SetButtonState(Xbox360Button.A, s.Cross);
        pad.SetButtonState(Xbox360Button.B, s.Circle);
        pad.SetButtonState(Xbox360Button.X, s.Square);
        pad.SetButtonState(Xbox360Button.Y, s.Triangle);
        pad.SetButtonState(Xbox360Button.LeftShoulder, s.L1);
        pad.SetButtonState(Xbox360Button.RightShoulder, s.R1);
        pad.SetButtonState(Xbox360Button.LeftThumb, s.L3);
        pad.SetButtonState(Xbox360Button.RightThumb, s.R3);
        pad.SetButtonState(Xbox360Button.Back, s.Share);
        pad.SetButtonState(Xbox360Button.Start, s.Options);
        pad.SetButtonState(Xbox360Button.Guide, s.Ps);
        pad.SetButtonState(Xbox360Button.Up, s.DpadUp);
        pad.SetButtonState(Xbox360Button.Down, s.DpadDown);
        pad.SetButtonState(Xbox360Button.Left, s.DpadLeft);
        pad.SetButtonState(Xbox360Button.Right, s.DpadRight);

        pad.SetSliderValue(Xbox360Slider.LeftTrigger, s.L2);
        pad.SetSliderValue(Xbox360Slider.RightTrigger, s.R2);

        pad.SetAxisValue(Xbox360Axis.LeftThumbX, s.LX);
        pad.SetAxisValue(Xbox360Axis.LeftThumbY, InvertAxis(s.LY));
        pad.SetAxisValue(Xbox360Axis.RightThumbX, s.RX);
        pad.SetAxisValue(Xbox360Axis.RightThumbY, InvertAxis(s.RY));

        pad.SubmitReport();
    }

    // Convenção HID (usada pelo report bruto do DS4/DualSense): eixo Y cresce
    // para BAIXO (cima = valor negativo). Convenção XInput/Xbox: eixo Y cresce
    // para CIMA (cima = valor positivo) — o oposto. Só o perfil Xbox precisa
    // dessa inversão; o perfil DS4 mantém a mesma convenção do report original.
    private static short InvertAxis(short value) =>
        value == short.MinValue ? short.MaxValue : (short)-value;

    private void SubmitDs4(NormalizedInputState s)
    {
        var pad = _ds4!;

        pad.SetButtonState(DualShock4Button.Cross, s.Cross);
        pad.SetButtonState(DualShock4Button.Circle, s.Circle);
        pad.SetButtonState(DualShock4Button.Square, s.Square);
        pad.SetButtonState(DualShock4Button.Triangle, s.Triangle);
        pad.SetButtonState(DualShock4Button.ShoulderLeft, s.L1);
        pad.SetButtonState(DualShock4Button.ShoulderRight, s.R1);
        pad.SetButtonState(DualShock4Button.ThumbLeft, s.L3);
        pad.SetButtonState(DualShock4Button.ThumbRight, s.R3);
        pad.SetButtonState(DualShock4Button.Share, s.Share);
        pad.SetButtonState(DualShock4Button.Options, s.Options);
        pad.SetButtonState(DualShock4SpecialButton.Ps, s.Ps);
        pad.SetButtonState(DualShock4SpecialButton.Touchpad, s.TouchpadClick);

        pad.SetDPadDirection(ToDpadDirection(s));

        pad.SetSliderValue(DualShock4Slider.LeftTrigger, s.L2);
        pad.SetSliderValue(DualShock4Slider.RightTrigger, s.R2);

        pad.SetAxisValue(DualShock4Axis.LeftThumbX, ToByteAxis(s.LX));
        pad.SetAxisValue(DualShock4Axis.LeftThumbY, ToByteAxis(s.LY));
        pad.SetAxisValue(DualShock4Axis.RightThumbX, ToByteAxis(s.RX));
        pad.SetAxisValue(DualShock4Axis.RightThumbY, ToByteAxis(s.RY));

        pad.SubmitReport();
    }

    // DS4 usa eixos de 0-255 (centro em 128), diferente do short de -32768/32767 do Xbox.
    private static byte ToByteAxis(short axisValue) => (byte)((axisValue / 256) + 128);

    private static DualShock4DPadDirection ToDpadDirection(NormalizedInputState s)
    {
        if (s.DpadUp && s.DpadRight) return DualShock4DPadDirection.Northeast;
        if (s.DpadDown && s.DpadRight) return DualShock4DPadDirection.Southeast;
        if (s.DpadDown && s.DpadLeft) return DualShock4DPadDirection.Southwest;
        if (s.DpadUp && s.DpadLeft) return DualShock4DPadDirection.Northwest;
        if (s.DpadUp) return DualShock4DPadDirection.North;
        if (s.DpadRight) return DualShock4DPadDirection.East;
        if (s.DpadDown) return DualShock4DPadDirection.South;
        if (s.DpadLeft) return DualShock4DPadDirection.West;
        return DualShock4DPadDirection.None;
    }

    /// <summary>
    /// Envia o estado no perfil DS4 usando o report ESTENDIDO (DS4_REPORT_EX),
    /// que inclui giroscópio/acelerômetro/touchpad. Como essa struct espelha
    /// 1:1 o formato do hardware real, copiamos os bytes 9..59 diretamente do
    /// report físico (timestamp, bateria, giro, acelerômetro e touch) — sem
    /// decodificar/recodificar, o que elimina a maior fonte de risco de erro.
    /// Só os primeiros 9 bytes (sticks/botões/gatilhos) são tratados à parte
    /// porque vêm do estado já processado (deadzone, etc. se aplicável).
    ///
    /// Layout de DS4_REPORT_EX (63 bytes, validado contra o header oficial
    /// ViGEmClient/include/ViGEm/Common.h):
    ///   [0-3]   sticks (LX,LY,RX,RY)
    ///   [4-5]   wButtons (dpad + botões, mesmo bit layout do report físico)
    ///   [6]     bSpecial (PS=bit0, Touchpad=bit1 — mascarado, sem o contador)
    ///   [7-8]   gatilhos analógicos (L2,R2)
    ///   [9-59]  timestamp+bateria+giro+acelerômetro+touch — cópia direta
    ///   [60-62] não usado
    /// </summary>
    /// <param name="rawPhysicalBuffer">Buffer bruto lido do controle físico.</param>
    /// <param name="physicalBaseOffset">
    /// Offset onde os sticks começam nesse buffer (3 para Bluetooth, validado
    /// empiricamente; 1 para USB, não validado ainda).
    /// </param>
    public void SubmitDs4ExtendedState(NormalizedInputState s, byte[] rawPhysicalBuffer, int physicalBaseOffset)
    {
        lock (_vigemLock)
        {
            if (_disposed) return;
            if (_activeProfile != EmulationProfile.DualShock4 || _ds4 == null)
                return;

            var buffer = _ds4ReportBuffer;
            Array.Clear(buffer); // frame anterior não pode vazar pros bytes não escritos

            // Sticks — cópia direta do byte bruto (sem deadzone: o perfil DS4
            // mantém a resposta "nativa" do stick, igual ao hardware real).
            int off = physicalBaseOffset;
            Array.Copy(rawPhysicalBuffer, off, buffer, 0, 4);

            // Botões + dpad (wButtons) — mesmo bit layout validado no parser.
            buffer[4] = rawPhysicalBuffer[off + 4];
            buffer[5] = rawPhysicalBuffer[off + 5];

            // bSpecial: mantém só PS (bit0) e Touchpad click (bit1); descarta o
            // contador de frame que o hardware usa nos bits superiores.
            buffer[6] = (byte)(rawPhysicalBuffer[off + 6] & 0x03);

            // Gatilhos analógicos.
            buffer[7] = rawPhysicalBuffer[off + 7];
            buffer[8] = rawPhysicalBuffer[off + 8];

            // Timestamp, bateria, giroscópio, acelerômetro e touchpad — cópia
            // direta de 51 bytes, formato idêntico entre o report físico e o
            // DS4_REPORT_EX (esse é o cerne do passthrough).
            int copyLength = Math.Min(51, rawPhysicalBuffer.Length - (off + 9));
            if (copyLength > 0)
                Array.Copy(rawPhysicalBuffer, off + 9, buffer, 9, copyLength);

            _ds4.SubmitRawReport(buffer);
        }
    }

    /// <summary>
    /// Aguarda o próximo output report que um jogo/app mandou pro controle
    /// virtual DS4 (usado pra passthrough de LED — repassar a cor que o jogo
    /// pediu pro controle físico, em vez da cor fixa do perfil).
    /// Bloqueia até chegar um report ou o timeout expirar.
    /// NÃO usa o lock geral: essa chamada BLOQUEIA por até timeoutMs, e
    /// segurar o lock aqui congelaria a thread de leitura. O AwaitRawOutputReport
    /// da lib é seguro pra chamar em paralelo aos submits; o problema de
    /// thread-safety é só com create/remove, que são serializados à parte.
    /// </summary>
    public byte[]? AwaitDs4OutputReport(int timeoutMs, out bool timedOut)
    {
        timedOut = true;
        var ds4 = _ds4; // snapshot: pode ser trocado por outra thread
        if (_disposed || _activeProfile != EmulationProfile.DualShock4 || ds4 == null)
            return null;

        try
        {
            var result = ds4.AwaitRawOutputReport(timeoutMs, out timedOut);
            return timedOut ? null : result.ToArray();
        }
        catch
        {
            timedOut = true;
            return null;
        }
    }

    /// <summary>
    /// Igual a <see cref="SubmitDs4ExtendedState(NormalizedInputState, byte[], int)"/>,
    /// mas monta o report campo a campo a partir do estado JÁ DECODIFICADO
    /// em vez de copiar bytes brutos. Necessário pra controles cujo formato
    /// físico não é idêntico ao DS4_REPORT_EX (ex: DualSense).
    /// </summary>
    public void SubmitDs4ExtendedStateDecoded(NormalizedInputState s)
    {
        lock (_vigemLock)
        {
            if (_disposed) return;
            if (_activeProfile != EmulationProfile.DualShock4 || _ds4 == null)
                return;

            var buffer = _ds4ReportBuffer;
            Array.Clear(buffer); // frame anterior não pode vazar pros bytes não escritos

            buffer[0] = ToByteAxis(s.LX);
            buffer[1] = ToByteAxis(s.LY);
            buffer[2] = ToByteAxis(s.RX);
            buffer[3] = ToByteAxis(s.RY);

            ushort wButtons = 0;
            if (s.DpadUp && s.DpadRight) wButtons |= 1;
            else if (s.DpadRight && s.DpadDown) wButtons |= 3;
            else if (s.DpadDown && s.DpadLeft) wButtons |= 5;
            else if (s.DpadLeft && s.DpadUp) wButtons |= 7;
            else if (s.DpadUp) wButtons |= 0;
            else if (s.DpadRight) wButtons |= 2;
            else if (s.DpadDown) wButtons |= 4;
            else if (s.DpadLeft) wButtons |= 6;
            else wButtons |= 8; // nenhum = centro (hat neutro)

            if (s.Square) wButtons |= 0x10;
            if (s.Cross) wButtons |= 0x20;
            if (s.Circle) wButtons |= 0x40;
            if (s.Triangle) wButtons |= 0x80;
            if (s.L1) wButtons |= 0x0100;
            if (s.R1) wButtons |= 0x0200;
            if (s.Share) wButtons |= 0x1000;
            if (s.Options) wButtons |= 0x2000;
            if (s.L3) wButtons |= 0x4000;
            if (s.R3) wButtons |= 0x8000;

            buffer[4] = (byte)(wButtons & 0xFF);
            buffer[5] = (byte)(wButtons >> 8);

            byte bSpecial = 0;
            if (s.Ps) bSpecial |= 0x01;
            if (s.TouchpadClick) bSpecial |= 0x02;
            buffer[6] = bSpecial;

            buffer[7] = s.L2;
            buffer[8] = s.R2;

            // [9-10] timestamp — deixa em 0, não é crítico pra maioria dos jogos.
            buffer[11] = 0; // bateria — TODO: reportar nível real quando disponível

            WriteShortLE(buffer, 12, s.GyroX);
            WriteShortLE(buffer, 14, s.GyroY);
            WriteShortLE(buffer, 16, s.GyroZ);
            WriteShortLE(buffer, 18, s.AccelX);
            WriteShortLE(buffer, 20, s.AccelY);
            WriteShortLE(buffer, 22, s.AccelZ);

            // Touchpad — offsets da struct DS4_REPORT_EX do ViGEm (NÃO do
            // report HID cru do controle físico; as duas diferem por um byte
            // nessa região, e confundir as duas foi exatamente o erro que fez
            // o ponto piscar e trocar de orientação numa tentativa anterior):
            //
            //   34 = bIsUpTrackingNum1 — bit 7 LIGADO significa "dedo NÃO está
            //        encostado"; apagado = tocando
            //   35-37 = coordenadas X e Y do dedo 1, 12 bits cada
            //   38 = bIsUpTrackingNum2 (dedo 2, mesmo formato do 34)
            //
            // O bug original estava SÓ na polaridade: escrevíamos 0x01/0x00
            // aqui, então o bit 7 nunca ligava e o controle virtual anunciava
            // dedo encostado o tempo todo — o ponto seguia o dedo e congelava
            // no último lugar tocado. As coordenadas sempre estiveram certas.
            buffer[34] = (byte)(s.TouchActive ? 0x00 : 0x80);
            buffer[35] = (byte)(s.TouchX & 0xFF);
            buffer[36] = (byte)(((s.TouchX >> 8) & 0x0F) | ((s.TouchY & 0x0F) << 4));
            buffer[37] = (byte)(s.TouchY >> 4);

            // Dedo 2 sempre "sem toque". Sem isso ele fica zerado pelo
            // Array.Clear — e byte zerado quer dizer bit 7 apagado, ou seja,
            // um segundo dedo fantasma encostado em (0,0).
            buffer[38] = 0x80;

            _ds4.SubmitRawReport(buffer);
        }
    }

    private static void WriteShortLE(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    // Sempre chamado já dentro do lock _vigemLock (por CreateTarget ou Dispose).
    private void DisconnectCurrentNoLock()
    {
        // IMPORTANTE: o alvo atual (_xbox360/_ds4) TAMBÉM está em
        // _allCreatedTargets (é adicionado lá na criação). Por isso NÃO
        // desconectamos/descartamos ele separadamente antes do loop — fazer
        // isso descartava o mesmo alvo DUAS vezes, e o Dispose duplo sobre o
        // recurso nativo do driver derrubava o processo inteiro
        // (AccessViolation não é capturável por try/catch). Bug: app fechava
        // ao trocar perfil / desativar emulação / desligar o controle.
        // Aqui só zeramos as referências; o loop abaixo limpa cada alvo
        // exatamente UMA vez.
        _xbox360 = null;
        _ds4 = null;

        // Limpeza garantida: percorre TODOS os alvos já criados por este
        // controller (inclusive os de tentativas de conexão que falharam com
        // 0x80004005 e podem ter virado fantasma). Desconecta e descarta
        // cada um exatamente uma vez.
        //
        // RESSINCRONIZAÇÃO: se o Disconnect lançar, a causa provável é o
        // estado interno da lib dessincronizado pelo Connect ambíguo
        // (0x80004005) — a lib acha que o alvo nunca conectou e se recusa a
        // removê-lo, mas o alvo EXISTE no driver (o fantasma que só some
        // fechando o app). Nesse caso tentamos Connect() pra realinhar o
        // estado e Disconnect() de novo. Tudo logado pra diagnóstico.
        foreach (var t in _allCreatedTargets)
        {
            bool disconnected = false;
            try
            {
                (t as IXbox360Controller)?.Disconnect();
                (t as IDualShock4Controller)?.Disconnect();
                disconnected = true;
            }
            catch (Exception ex)
            {
                DseLog.Write($"[vigem] cleanup: Disconnect lançou ({t.GetType().Name}): {DseLog.Fmt(ex)} — tentando ressincronizar");
            }

            if (!disconnected)
            {
                // Realinha o estado da lib com o driver e tenta remover de novo.
                try
                {
                    (t as IXbox360Controller)?.Connect();
                    (t as IDualShock4Controller)?.Connect();
                    DseLog.Write($"[vigem] cleanup: Connect de ressincronização OK ({t.GetType().Name})");
                }
                catch (Exception ex)
                {
                    DseLog.Write($"[vigem] cleanup: Connect de ressincronização lançou: {DseLog.Fmt(ex)}");
                }

                try
                {
                    (t as IXbox360Controller)?.Disconnect();
                    (t as IDualShock4Controller)?.Disconnect();
                    DseLog.Write($"[vigem] cleanup: Disconnect pós-ressincronização OK ({t.GetType().Name})");
                }
                catch (Exception ex)
                {
                    DseLog.Write($"[vigem] cleanup: Disconnect pós-ressincronização TAMBÉM lançou (alvo pode ficar fantasma até fechar o app): {DseLog.Fmt(ex)}");
                }
            }

            try { (t as IDisposable)?.Dispose(); }
            catch (Exception ex) { DseLog.Write($"[vigem] cleanup: Dispose lançou: {DseLog.Fmt(ex)}"); }
        }
        DseLog.Write($"[vigem] cleanup: {_allCreatedTargets.Count} alvo(s) processado(s)");
        _allCreatedTargets.Clear();
    }

    public void Dispose()
    {
        lock (_vigemLock)
        {
            if (_disposed) return;
            _disposed = true;
            DseLog.Write("[vigem] Dispose do VirtualController — limpeza final dos alvos");
            DisconnectCurrentNoLock();

            // Fecha o handle: é isso que faz o driver soltar de vez qualquer
            // dispositivo virtual desta sessão (o mesmo efeito que fechar o
            // app tinha, agora acontecendo já no desligar do controle).
            try { _client.Dispose(); }
            catch (Exception ex) { DseLog.Write($"[vigem] Dispose do cliente lançou: {DseLog.Fmt(ex)}"); }
            DseLog.Write("[vigem] cliente ViGEm da sessão fechado");
        }
    }
}
