using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DSE.App.Ui;

/// <summary>
/// Célula da barra de título: só o ícone acende com o mouse (TEXTO_2 → TEXTO),
/// tela aberta = ícone laranja. Com <c>aoSegurar</c> (o fechar): clique
/// esconde, segurar 1 s sai — a cor vermelha nasce do centro.
/// </summary>
public sealed class BotaoBarra : Grid
{
    private const double SegurarS = 1.0;

    private readonly Action _aoClicar;
    private readonly Action? _aoSegurar;
    private readonly Rectangle _enchido;
    private readonly ScaleTransform _escala = new(0, 0);
    private readonly ContentControl _icone;
    private readonly DispatcherTimer _relogio = new() { Interval = TimeSpan.FromMilliseconds(12) };
    private readonly Stopwatch _t = new();
    private bool _apertado, _saiu, _aceso;
    private Brush? _corFixa;

    public BotaoBarra(string icone, Action aoClicar, double largura = 34, Action? aoSegurar = null, string? dica = null)
    {
        _aoClicar = aoClicar;
        _aoSegurar = aoSegurar;
        Width = largura;
        Height = 34;
        Background = Brushes.Transparent; // recebe o mouse na célula inteira
        if (dica != null) ToolTip = dica;

        _enchido = new Rectangle
        {
            Fill = (Brush)Application.Current.FindResource("FecharSegurarBrush"),
            RadiusX = 6,
            RadiusY = 6,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _escala,
            IsHitTestVisible = false,
        };
        Children.Add(_enchido);

        _icone = new ContentControl
        {
            Content = Icones.Criar(icone, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        Children.Add(_icone);

        MouseEnter += (_, _) => Pintar();
        MouseLeave += (_, _) => Pintar();
        MouseLeftButtonDown += Apertou;
        MouseLeftButtonUp += Soltou;
        LostMouseCapture += (_, _) => Cancelar();
        _relogio.Tick += (_, _) => Passo();
        Pintar();
    }

    /// <summary>Tela aberta = ícone laranja.</summary>
    public void Acender(bool sim)
    {
        _aceso = sim;
        Pintar();
    }

    /// <summary>Cor fixa do ícone (ex.: verde do "nova versão").</summary>
    public void CorFixa(Brush? cor)
    {
        _corFixa = cor;
        Pintar();
    }

    private void Pintar(double k = 0)
    {
        Brush cor;
        if (k > 0) cor = Brushes.White;
        else if (_corFixa != null) cor = _corFixa;
        else if (_aceso) cor = (Brush)Application.Current.FindResource("AccentBrush");
        else cor = (Brush)Application.Current.FindResource(IsMouseOver ? "TextBrush" : "Texto2Brush");
        _icone.Foreground = cor;
        _escala.ScaleX = _escala.ScaleY = k;
        _enchido.RadiusX = _enchido.RadiusY = k < 1 ? 6 : 0;
    }

    private void Apertou(object sender, MouseButtonEventArgs e)
    {
        _apertado = true;
        _saiu = false;
        CaptureMouse();
        e.Handled = true; // não deixa a barra começar a arrastar a janela
        if (_aoSegurar == null) return;
        _t.Restart();
        _relogio.Start();
    }

    private void Passo()
    {
        double s = _t.Elapsed.TotalSeconds;
        if (s < 0.15) return; // clique rápido não pisca
        double k = s / SegurarS;
        if (k >= 1)
        {
            _relogio.Stop();
            _saiu = true;
            Pintar(1);
            _aoSegurar?.Invoke();
            return;
        }
        Pintar(Math.Max(0.05, k));
    }

    private void Soltou(object sender, MouseButtonEventArgs e)
    {
        bool estava = _apertado;
        _apertado = false;
        _relogio.Stop();
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_saiu) return; // já saiu pelo segurar
        Pintar();
        var p = e.GetPosition(this);
        if (estava && p.X >= 0 && p.Y >= 0 && p.X < ActualWidth && p.Y < ActualHeight)
            _aoClicar();
        e.Handled = true;
    }

    private void Cancelar()
    {
        if (_saiu) return;
        _apertado = false;
        _relogio.Stop();
        Pintar();
    }
}
