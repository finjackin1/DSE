using DSE.Core.Virtual;

namespace DSE.Core.Hotkeys;

public enum HotkeyEvent
{
    ToggleProfile,      // Share + Options
    PowerOffController, // PS segurado por 1s (sem o Options)
    ToggleEmulation,    // Options + PS segurados por 1s
    ToggleLedMode       // Clique no touchpad + PS segurados por 1s
}

/// <summary>
/// Detecta combos de botões físicos a partir do stream contínuo de
/// NormalizedInputState. Mantém estado interno (timers, flags de "já disparado")
/// para evitar disparo repetido enquanto os botões continuam pressionados.
/// </summary>
public sealed class HotkeyDetector
{
    private const int PsHoldMillisecondsThreshold = 1000;

    private DateTime? _psPressStartedAt;
    private bool _psHoldFired;
    private bool _optionsDuranteSegurada;
    private bool _touchpadDuranteSegurada;

    // O atalho de alternar emulação DESTRÓI a sessão e cria outra (ou o
    // monitor), e cada uma nasce com um detector novo. Sem isto, o detector
    // recém-criado vê o PS ainda pressionado, conta o próprio segundo e
    // dispara de novo — vira cascata: desativa, ativa, desativa, e desliga o
    // controle se o Options for solto no meio. Um detector novo só passa a
    // valer depois que o PS for solto pelo menos uma vez.
    // Começa TRAVADO: um detector novo só passa a reconhecer o PS depois de
    // vê-lo solto pelo menos uma vez. Não basta checar o primeiro estado — se
    // aquele report chegasse com o PS momentaneamente solto (perfeitamente
    // possível na troca de sessão), a proteção não valeria e a contagem
    // recomeçaria, disparando o atalho de novo.
    private bool _aguardandoSoltarPs = true;
    private bool _toggleComboFired;

    public event Action<HotkeyEvent>? HotkeyTriggered;

    /// <summary>
    /// Deve ser chamado a cada novo estado lido do controle físico (mesmo loop
    /// que alimenta o VirtualController). Não bloqueia, apenas inspeciona o estado.
    /// </summary>
    public void Feed(NormalizedInputState state, DateTime timestampUtc)
    {
        HandleToggleCombo(state);
        HandlePsHold(state, timestampUtc);
    }

    private void HandleToggleCombo(NormalizedInputState state)
    {
        bool comboActive = state.Share && state.Options;

        if (comboActive && !_toggleComboFired)
        {
            _toggleComboFired = true;
            HotkeyTriggered?.Invoke(HotkeyEvent.ToggleProfile);
        }
        else if (!comboActive)
        {
            _toggleComboFired = false;
        }
    }

    /// <summary>
    /// O PS segurado por 1s dispara UM dos dois atalhos, decidido pelo Options:
    /// com ele, alterna a emulação; sem ele, desliga o controle.
    ///
    /// A checagem do Options é TOLERANTE de propósito: basta ele ter sido
    /// pressionado em algum momento da segurada, não exatamente no instante em
    /// que o segundo completa. Do contrário, apertar o PS um instante antes do
    /// Options desligaria o controle sem querer — e desligar não tem volta pela
    /// sessão, enquanto alternar a emulação é reversível pelo próprio atalho.
    /// </summary>
    private void HandlePsHold(NormalizedInputState state, DateTime nowUtc)
    {
        // Segurada herdada de um detector anterior: ignora até soltar.
        if (_aguardandoSoltarPs)
        {
            if (!state.Ps) _aguardandoSoltarPs = false;

            _psPressStartedAt = null;
            _psHoldFired = false;
            _optionsDuranteSegurada = false;
            _touchpadDuranteSegurada = false;
            return;
        }

        if (state.Ps)
        {
            _psPressStartedAt ??= nowUtc;

            if (state.Options) _optionsDuranteSegurada = true;
            if (state.TouchpadClick) _touchpadDuranteSegurada = true;

            if (!_psHoldFired &&
                (nowUtc - _psPressStartedAt.Value).TotalMilliseconds >= PsHoldMillisecondsThreshold)
            {
                _psHoldFired = true;
                // Ordem de prioridade no caso de vários botões segurados
                // junto: emulação primeiro, por ser a ação mais consequente.
                HotkeyTriggered?.Invoke(
                    _optionsDuranteSegurada ? HotkeyEvent.ToggleEmulation
                    : _touchpadDuranteSegurada ? HotkeyEvent.ToggleLedMode
                    : HotkeyEvent.PowerOffController);
            }
        }
        else
        {
            _psPressStartedAt = null;
            _psHoldFired = false;
            _optionsDuranteSegurada = false;
            _touchpadDuranteSegurada = false;
        }
    }
}
