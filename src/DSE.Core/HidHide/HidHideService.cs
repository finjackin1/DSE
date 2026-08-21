using Nefarius.Drivers.HidHide;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace DSE.Core.HidHide;

/// <summary>
/// Encapsula toda a automação do HidHide: whitelist do próprio processo e
/// ocultação/exibição dinâmica de controles físicos conforme conectam/desconectam.
/// O usuário nunca precisa abrir a interface do HidHide manualmente.
///
/// NOTA: a superfície exata da API do pacote Nefarius.Drivers.HidHide pode variar
/// entre versões do NuGet — validar contra a versão instalada localmente
/// (testado como referência: HidHide.Client >= 1.3.x). Se algum nome de
/// membro divergir, o ajuste é mecânico (mesma responsabilidade, nome diferente).
/// </summary>
public sealed class HidHideService
{
    private readonly IHidHideControlService _hidHide;
    private readonly Dictionary<string, string> _currentlyHidden = new(StringComparer.OrdinalIgnoreCase); // interfacePath -> instanceId

    public bool IsAvailable { get; }

    public HidHideService()
    {
        _hidHide = new HidHideControlService();

        try
        {
            IsAvailable = _hidHide.IsInstalled;
        }
        catch
        {
            // Driver não instalado ou serviço inacessível — DSE.App deve seguir
            // funcionando (sem ocultação automática) em vez de travar.
            IsAvailable = false;
        }
    }

    /// <summary>
    /// Garante que o executável do DSE.App está na whitelist do HidHide.
    /// Sem isso, o próprio app perderia acesso ao controle que ele mesmo ocultou.
    /// Deve ser chamado uma vez no Setup Wizard e, por segurança, também no
    /// startup normal (idempotente).
    /// </summary>
    public void EnsureSelfWhitelisted()
    {
        if (!IsAvailable) return;

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Não foi possível resolver o caminho do executável atual.");

        var current = _hidHide.ApplicationPaths;
        if (!current.Contains(exePath, StringComparer.OrdinalIgnoreCase))
        {
            _hidHide.AddApplicationPath(exePath);
        }

        // Garante que o cloaking está ativo globalmente — sem isso, os
        // devices adicionados à blocklist continuariam visíveis para todos.
        _hidHide.IsActive = true;
    }

    /// <summary>
    /// Oculta um controle físico do restante do sistema (jogos, outros apps),
    /// mantendo o DSE.App com acesso via whitelist. Idempotente.
    /// </summary>
    /// <param name="deviceInterfacePath">
    /// Device Interface Path do controle físico, no formato bruto retornado pelo
    /// HidSharp (ex: "\\?\hid#vid_054c&amp;pid_09cc#...#{4d1e55b2-...}"). Esse NÃO é
    /// o formato que o HidHide entende — precisa ser convertido para o Instance ID
    /// real via PnPDevice.GetInstanceIdFromInterfaceId antes de bloquear.
    /// </param>
    public void HideDevice(string deviceInterfacePath)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(deviceInterfacePath))
            return;

        string? instanceId;
        try
        {
            instanceId = PnPDevice.GetInstanceIdFromInterfaceId(deviceInterfacePath);
        }
        catch
        {
            // Path em formato inesperado — não deve travar o app, só não oculta esse device.
            return;
        }

        // Sem Instance ID válido não há o que ocultar. Além de calar os avisos
        // de nulabilidade, evita mandar nulo/vazio pro HidHide (que estouraria
        // exceção lá dentro).
        if (string.IsNullOrWhiteSpace(instanceId))
            return;

        if (_currentlyHidden.ContainsKey(deviceInterfacePath))
            return;

        var blocked = _hidHide.BlockedInstanceIds;
        if (!blocked.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
        {
            _hidHide.AddBlockedInstanceId(instanceId);
        }

        _currentlyHidden[deviceInterfacePath] = instanceId;
    }

    /// <summary>
    /// Remove a ocultação de um controle (chamado ao desconectar), evitando
    /// acúmulo de entradas "fantasma" na configuração do HidHide.
    /// </summary>
    public void UnhideDevice(string deviceInterfacePath)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(deviceInterfacePath))
            return;

        if (!_currentlyHidden.TryGetValue(deviceInterfacePath, out var instanceId))
            return;

        try
        {
            _hidHide.RemoveBlockedInstanceId(instanceId);
        }
        catch
        {
            // Dispositivo pode já ter sido removido da lista (ex: driver reiniciou) —
            // não é uma falha crítica, apenas segue.
        }

        _currentlyHidden.Remove(deviceInterfacePath);
    }

    /// <summary>
    /// Reverte completamente as mudanças feitas pelo DSE.App: remove todos os
    /// devices ocultados por essa sessão e a whitelist do próprio app.
    /// Chamado ao desinstalar ou quando o usuário pede "restaurar tudo"
    /// nas configurações — deixa o HidHide como estava antes do DSE.App.
    /// </summary>
    public void RestoreAll()
    {
        if (!IsAvailable) return;

        foreach (var interfacePath in _currentlyHidden.Keys.ToList())
        {
            UnhideDevice(interfacePath);
        }

        var exePath = Environment.ProcessPath;
        if (exePath != null)
        {
            try { _hidHide.RemoveApplicationPath(exePath); }
            catch { /* já pode não estar mais presente */ }
        }
    }
}
