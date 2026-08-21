using Hardcodet.Wpf.TaskbarNotification;

namespace DSE.App.Tray;

/// <summary>
/// Ícone da bandeja: clique duplo abre a janela principal. Sem menu de
/// contexto (o menu do Hardcodet dava problema de tema/posicionamento, e as
/// ações já têm outros acessos: duplo-clique abre, o X da janela fecha).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Action _onOpen;
    private Action? _onBalloonClick;

    public TrayIconService(Action onOpen)
    {
        _onOpen = onOpen;

        _icon = new TaskbarIcon
        {
            ToolTipText = "DSE — em execução",
            Icon = LoadTrayIcon(),
        };
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/DSE.App;component/Assets/dse_icon.ico");
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream != null)
                return new System.Drawing.Icon(stream);
        }
        catch
        {
            // fallback pro ícone genérico se algo falhar
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void Initialize()
    {
        // Sem menu de contexto: duplo-clique abre a janela (e o X da janela
        // fecha o app). O menu do Hardcodet dava problema visual de tema e
        // posicionamento, e as duas ações já têm acesso por outros caminhos.
        _icon.TrayMouseDoubleClick += (_, _) => _onOpen();
        _icon.TrayBalloonTipClicked += (_, _) => _onBalloonClick?.Invoke();
    }

    /// <summary>
    /// Balão de notificação do Windows. Existe para o aviso de nova versão
    /// alcançar quem deixa o DSE minimizado na bandeja e nunca vê a janela.
    /// </summary>
    public void ShowBalloon(string titulo, string mensagem, Action? aoClicar = null)
    {
        _onBalloonClick = aoClicar;
        try { _icon.ShowBalloonTip(titulo, mensagem, BalloonIcon.Info); }
        catch { /* balão bloqueado pelo Windows — não é motivo pra falhar */ }
    }

    public void Dispose() => _icon.Dispose();
}
