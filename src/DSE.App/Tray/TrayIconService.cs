using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;

namespace DSE.App.Tray;

/// <summary>
/// Ícone da bandeja. Clique = alterna a janela (abre/esconde); duplo clique =
/// SÓ abre (pedido dele, 11/out/2026: o duplo não pode virar abre-fecha).
/// Botão direito = menu montado na hora com o estado atual.
///
/// Detecção do duplo: o clique simples espera o tempo de duplo clique do
/// Windows antes de agir. Se chegar o segundo clique (pelo evento de duplo
/// clique ou por um segundo "soltar"), cancela o simples, abre UMA vez e
/// ignora o que vier até o fim desse tempo (clique triplo não fecha).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Action _onOpen;
    private readonly Action<DateTime> _onToggle;
    private DateTime _momentoClique;
    private readonly Action<ContextMenu> _preencherMenu;
    private readonly DispatcherTimer _esperaClique;
    private readonly TimeSpan _tempoDuplo;
    private DateTime _ignorarAte = DateTime.MinValue;
    private Action? _onBalloonClick;

    private ContextMenu _menu = null!;

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct PONTO { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PONTO p);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    public TrayIconService(Action onOpen, Action<DateTime> onToggle, Action<ContextMenu> preencherMenu)
    {
        _onOpen = onOpen;
        _onToggle = onToggle;
        _preencherMenu = preencherMenu;
        _tempoDuplo = TimeSpan.FromMilliseconds(Math.Clamp(GetDoubleClickTime(), 200u, 900u));
        _esperaClique = new DispatcherTimer { Interval = _tempoDuplo };
        _esperaClique.Tick += (_, _) =>
        {
            _esperaClique.Stop();
            _onToggle(_momentoClique);
        };

        _icon = new TaskbarIcon
        {
            ToolTipText = "DSE",
            Icon = LoadTrayIcon(),
            MenuActivation = PopupActivationMode.None,
        };
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/DSE.App;component/Assets/dse_icon.ico");
            var stream = Application.GetResourceStream(uri)?.Stream;
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
        _icon.TrayLeftMouseUp += (_, _) => CliqueSimples();
        _icon.TrayMouseDoubleClick += (_, _) => CliqueDuplo();

        // O menu é aberto por NÓS, não pelo Hardcodet: ele posiciona pelo cursor em
        // pixels físicos e o WPF lê como DIP, então com escala do Windows acima de
        // 100% o menu ia parar no canto da tela (bug dele, 11/out/2026).
        // Posição: o cursor de verdade (GetCursorPos, pixels) convertido para DIP
        // pela escala do sistema. MousePoint não serve: o WPF usa a última posição
        // do mouse sobre uma janela DELE, e a bandeja é do Windows. O WPF já vira o
        // menu pra cima/esquerda quando não cabe (encosta na barra de tarefas).
        _menu = new ContextMenu { Placement = PlacementMode.AbsolutePoint, StaysOpen = false };
        _menu.Opened += (_, _) =>
        {
            // Sem ser a janela da frente, o menu não fecharia ao clicar fora.
            if (PresentationSource.FromVisual(_menu) is HwndSource fonte)
                SetForegroundWindow(fonte.Handle);
        };
        _icon.TrayRightMouseUp += (_, _) =>
        {
            _esperaClique.Stop();
            _menu.IsOpen = false;
            _preencherMenu(_menu);
            if (GetCursorPos(out var p))
            {
                double escala = 96.0 / Math.Max(96u, GetDpiForSystem());
                _menu.HorizontalOffset = p.X * escala;
                _menu.VerticalOffset = p.Y * escala;
            }
            _menu.IsOpen = true;
        };
        _icon.TrayBalloonTipClicked += (_, _) => _onBalloonClick?.Invoke();
    }

    private void CliqueSimples()
    {
        if (DateTime.Now < _ignorarAte) return;
        if (_esperaClique.IsEnabled) CliqueDuplo(); // segundo "soltar" dentro do tempo = duplo
        else
        {
            _momentoClique = DateTime.Now;
            _esperaClique.Start();
        }
    }

    private void CliqueDuplo()
    {
        if (DateTime.Now < _ignorarAte) return;
        _esperaClique.Stop();
        _ignorarAte = DateTime.Now + _tempoDuplo;
        _onOpen();
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

    public void Dispose()
    {
        _esperaClique.Stop();
        _icon.Dispose();
    }
}
