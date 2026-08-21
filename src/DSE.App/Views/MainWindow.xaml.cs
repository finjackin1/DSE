using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DSE.Core.Controllers;
using DSE.Core.Profiles;
using DSE.Core.Setup;

namespace DSE.App.Views;

public partial class MainWindow : Window
{
    private readonly ControllerWatcher _watcher;
    private readonly AppSettingsService _settings;

    // Guarda o último estado conhecido de cada controle — mesmo quando o
    // usuário desativa manualmente (o card continua na tela pra poder
    // reativar depois, só a sessão de emulação é que para).
    private readonly Dictionary<string, ActiveControllerInfo> _controllerInfos = new();
    private readonly Dictionary<string, Border> _controllerCards = new();
    private readonly Dictionary<string, System.Windows.Shapes.Rectangle> _touchpadRects = new();
    private readonly Dictionary<string, System.Windows.Shapes.Ellipse> _touchDots = new();

    // Relógio que anima o ponto do toque. A leitura NÃO empurra cada report
    // pra interface (chegam ~250 por segundo); a janela puxa o estado 30 vezes
    // por segundo. E só corre com a janela visível: com o DSE na bandeja, que
    // é o uso normal, o custo é zero.
    private readonly System.Windows.Threading.DispatcherTimer _relogioDoToque = new()
    {
        Interval = TimeSpan.FromMilliseconds(33)
    };
    private readonly Dictionary<string, TextBlock> _batteryTexts = new();
    private readonly Dictionary<string, Border> _batteryIcons = new();
    private readonly HashSet<string> _suppressToggleFor = new();

    public MainWindow(ControllerWatcher watcher, AppSettingsService settings)
    {
        _watcher = watcher;
        _settings = settings;

        InitializeComponent();

        // Estado inicial do ícone "Iniciar com o Windows" na barra de título.
        UpdateWindowsIconState(_settings.Current.StartWithWindows);
        UpdateOpenOnStartupIconState(_settings.Current.OpenWindowOnStartup);

        _relogioDoToque.Tick += (_, _) => AtualizarToques();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) _relogioDoToque.Start();
            else _relogioDoToque.Stop();
        };

        _watcher.ControllerConnected += OnControllerConnected;
        _watcher.ControllerDisconnected += OnControllerDisconnected;
        _watcher.DeviceDisabledByUser += OnDeviceDisabledByUser;
        _watcher.ProfileChanged += OnProfileChanged;
        _watcher.LedColorChanged += OnLedColorChanged;
        _watcher.BatteryChanged += OnBatteryChanged;
        _watcher.LedModeChanged += OnLedModeChanged;

        // Carrega controles que JÁ estavam conectados antes desta janela
        // existir (o watcher inicia junto com o app, muito antes da janela).
        foreach (var info in _watcher.GetActiveControllers())
        {
            _controllerInfos[info.Serial] = info;
            RebuildCard(info.Serial, isDisabled: false);
        }

        UpdateStatusText();
    }

    private void OnControllerConnected(ActiveControllerInfo info) =>
        Dispatcher.Invoke(() =>
        {
            _controllerInfos[info.Serial] = info;
            RebuildCard(info.Serial, isDisabled: false);
            UpdateStatusText();
        });

    private void OnControllerDisconnected(string serial) =>
        Dispatcher.Invoke(() =>
        {
            _controllerInfos.Remove(serial);
            if (_controllerCards.TryGetValue(serial, out var card))
            {
                ControllersPanel.Children.Remove(card);
                _controllerCards.Remove(serial);
            }
            // Limpa também os elementos do card que ficavam indexados por
            // serial — senão seguravam referências de controles que já saíram.
            _touchpadRects.Remove(serial);
            _touchDots.Remove(serial);
            _batteryTexts.Remove(serial);
            _batteryIcons.Remove(serial);
            UpdateStatusText();
        });

    private void OnDeviceDisabledByUser(string serial) =>
        Dispatcher.Invoke(() => RebuildCard(serial, isDisabled: true));

    private void OnProfileChanged(ActiveControllerInfo info) =>
        Dispatcher.Invoke(() =>
        {
            _controllerInfos[info.Serial] = info;
            RebuildCard(info.Serial, isDisabled: false);
        });

    private void OnLedColorChanged(string serial, byte r, byte g, byte b) =>
        Dispatcher.Invoke(() =>
        {
            // Recolore só o touchpad (leve — a cor pode mudar muitas vezes por
            // segundo em jogos que animam o LED). Só faz sentido em passthrough;
            // nos outros modos a cor é fixa e ignoramos.
            if (_watcher.GetLedMode(serial) != LedMode.Passthrough) return;
            if (_touchpadRects.TryGetValue(serial, out var rect))
            {
                var cor = Color.FromRgb(r, g, b);
                rect.Fill = BuildLedBrush(cor);
                AplicarBrilho(rect, cor);
            }
        });

    private void OnBatteryChanged(string serial, int percent, bool charging) =>
        Dispatcher.Invoke(() =>
        {
            if (_batteryTexts.TryGetValue(serial, out var tb))
            {
                tb.Text = FormatBattery(percent);
                tb.Foreground = BatteryColor(percent);
            }
            if (_batteryIcons.TryGetValue(serial, out var host))
            {
                host.Child = BuildBatteryIcon(percent, charging);
            }
        });

    private static string FormatBattery(int percent) => percent < 0 ? "—" : $"{percent}%";

    /// <summary>
    /// Desenha o ícone de bateria: corpo com terminal e 3 barras internas
    /// que mostram o nível. 3 acesas = verde (alto), 2 = amarelo (médio),
    /// 1 = vermelho (baixo). Carregando: um raio ⚡ ao lado.
    /// </summary>
    private FrameworkElement BuildBatteryIcon(int percent, bool charging)
    {
        var muted = (Brush)FindResource("MutedBrush");
        var offBrush = (Brush)FindResource("BorderBrush2");

        int lit = percent < 0 ? 0
            : percent > 66 ? 3
            : percent > 33 ? 2
            : 1;

        Brush litBrush = lit switch
        {
            3 => (Brush)FindResource("ConnectedBrush"),                       // verde
            2 => new SolidColorBrush(Color.FromRgb(0xE2, 0xC5, 0x5D)),        // amarelo
            _ => (Brush)FindResource("DisconnectedBrush")                     // vermelho
        };

        var bars = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 3; i++)
        {
            bars.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 3.4,
                Height = 6.5,
                RadiusX = 0.8,
                RadiusY = 0.8,
                Fill = i < lit ? litBrush : offBrush,
                Margin = new Thickness(0, 0, i < 2 ? 1.2 : 0, 0)
            });
        }

        var container = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Corpo da bateria (borda) com as barras dentro.
        container.Children.Add(new Border
        {
            BorderBrush = muted,
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(2.5),
            Padding = new Thickness(1.6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = bars
        });

        // Terminal (o "bico" da bateria).
        container.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 1.8,
            Height = 4.5,
            RadiusX = 0.8,
            RadiusY = 0.8,
            Fill = muted,
            Margin = new Thickness(1, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        if (charging)
        {
            container.Children.Add(new TextBlock
            {
                Text = "⚡",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xC5, 0x5D)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            });
        }

        return container;
    }

    private Brush BatteryColor(int percent)
    {
        if (percent < 0) return (Brush)FindResource("MutedBrush");
        if (percent <= 15) return (Brush)FindResource("DisconnectedBrush"); // vermelho
        if (percent <= 35) return new SolidColorBrush(Color.FromRgb(0xE2, 0xC5, 0x5D)); // amarelo
        return (Brush)FindResource("TextBrush");
    }

    private void UpdateStatusText()
    {
        StatusText.Text = _controllerInfos.Count switch
        {
            0 => "Nenhum controle conectado",
            1 => "Ativo — 1 controle",
            var n => $"Ativo — {n} controles"
        };
    }

    private void RebuildCard(string serial, bool isDisabled)
    {
        if (!_controllerInfos.TryGetValue(serial, out var info)) return;

        if (_controllerCards.TryGetValue(serial, out var existing))
        {
            ControllersPanel.Children.Remove(existing);
        }

        var card = BuildControllerCard(info, isDisabled);
        _controllerCards[serial] = card;
        ControllersPanel.Children.Add(card);
    }

    private Border BuildControllerCard(ActiveControllerInfo info, bool isDisabled)
    {
        var typeName = info.Type == PhysicalControllerType.DualShock4 ? "DualShock 4" : "DualSense";
        var connType = info.IsBluetooth ? "Bluetooth" : "USB";
        var muted = (Brush)FindResource("MutedBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var text = (Brush)FindResource("TextBrush");

        var root = new Grid();
        // Coluna esquerda com largura FIXA pra imagem (contém ela inteira, sem
        // transbordar pro card nem empurrar o texto). Coluna direita (star)
        // fica com todo o resto garantido pras informações.
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Lado esquerdo: a imagem do controle, ajustada dentro da coluna fixa.
        // boxWidth grande deixa a imagem grande; boxHeight controla a altura
        // pra não esticar o card (o card é definido pela altura do texto).
        var touchpadColor = GetTouchpadColorBrush(info, isDisabled);
        var iconElement = ControllerIconFactory.Build(
            info.Type,
            touchpadColor,
            boxWidth: 224,
            boxHeight: 132,
            onBodyClick: () => OnDeviceEnableToggled(info.Serial, isDisabled), // clique = liga se tava desligado, desliga se tava ligado
            onTouchpadClick: () => OnTouchpadClicked(info.Serial, isDisabled),
            out var touchpadRect,
            out var touchDot);
        _touchpadRects[info.Serial] = touchpadRect;
        _touchDots[info.Serial] = touchDot;
        AplicarBrilho(touchpadRect, isDisabled ? null : GetTouchpadColor(info));
        iconElement.VerticalAlignment = VerticalAlignment.Center;
        iconElement.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(iconElement, 0);
        root.Children.Add(iconElement);

        // Lado direito: nome, status, perfil e modo do LED em texto — pra
        // nunca depender só da cor da imagem pra saber o que tá rolando.
        // Alinhado totalmente à direita, deixando o lado esquerdo só pra imagem.
        var infoStack = new StackPanel
        {
            Margin = new Thickness(12, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        infoStack.Children.Add(new TextBlock
        {
            Text = typeName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 17,
            TextAlignment = TextAlignment.Right,
            Foreground = isDisabled ? muted : text
        });

        infoStack.Children.Add(new TextBlock
        {
            Text = $"{connType}",
            FontSize = 12,
            Foreground = muted,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 5, 0, 10)
        });

        infoStack.Children.Add(BuildStatusLine(
            "Emulação", isDisabled ? "desativada" : "ativa",
            isDisabled ? muted : (Brush)FindResource("ConnectedBrush")));

        if (!isDisabled)
        {
            infoStack.Children.Add(BuildStatusLine("Perfil", info.Profile.DisplayName(), text));

            var ledModeText = _watcher.GetLedMode(info.Serial) == LedMode.Preset ? "cor fixa" : "jogo controla";
            infoStack.Children.Add(BuildStatusLine("LED", ledModeText, text));

            // Bateria: ícone + porcentagem. Guarda referência ao TextBlock pra
            // atualizar via evento sem reconstruir o card.
            var (batPercent, batCharging) = _watcher.GetBattery(info.Serial);
            var batLine = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var batLabel = new TextBlock
            {
                Text = "Bateria: ",
                FontSize = 12,
                Foreground = muted
            };
            var batIconHost = new Border
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
                Child = BuildBatteryIcon(batPercent, batCharging)
            };
            var batValue = new TextBlock
            {
                Text = FormatBattery(batPercent),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = BatteryColor(batPercent)
            };
            batLine.Children.Add(batLabel);
            batLine.Children.Add(batIconHost);
            batLine.Children.Add(batValue);
            infoStack.Children.Add(batLine);
            _batteryTexts[info.Serial] = batValue;
            _batteryIcons[info.Serial] = batIconHost;
        }

        Grid.SetColumn(infoStack, 1);
        root.Children.Add(infoStack);

        return new Border
        {
            Background = (Brush)FindResource("Surface2Brush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = isDisabled ? (Brush)FindResource("BorderBrush2") : accent,
            Child = root
        };
    }

    private static StackPanel BuildStatusLine(string label, string value, Brush valueColor)
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        line.Children.Add(new TextBlock
        {
            Text = $"{label}:  ",
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("MutedBrush")
        });
        line.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = valueColor
        });
        return line;
    }

    /// <summary>
    /// Cor do touchpad na imagem: branco (igual o resto do corpo) quando
    /// desativado — sinaliza "nada de especial rolando" — ou a cor do modo
    /// de LED ativo quando ligado (cor do perfil se for preset, destaque
    /// se for passthrough).
    /// </summary>
    // ---- Aparência do LED no card ----
    // Modelo "neon": a cor fica viva subindo SATURAÇÃO e BRILHO, nunca
    // misturando com branco — misturar com branco lava a cor e deixa o
    // resultado leitoso, que foi o erro da primeira tentativa. As bordas
    // também não escurecem: toda a luz vai pra fora, no halo.
    private const double LedSaturacao = 0.15;   // quanto a cor ganha de saturação
    private const double LedBrilho = 0.22;      // quanto a cor ganha de brilho
    private const double LedNucleo = 0.33;      // força do núcleo mais luminoso
    private const double LedHaloRaio = 26;      // alcance do halo externo
    private const double LedHaloOpacidade = 0.50;

    private Brush GetTouchpadColorBrush(ActiveControllerInfo info, bool isDisabled)
    {
        // O touchpad é um buraco transparente no corpo, então SEMPRE precisa
        // ser preenchido — senão vira um buraco vazado mostrando o fundo do
        // card. Desativado = branco, como as outras áreas brancas do controle.
        if (isDisabled) return Brushes.White;
        return BuildLedBrush(GetTouchpadColor(info));
    }

    /// <summary>Cor que o LED deve mostrar na tela, sem o tratamento visual.</summary>
    private Color GetTouchpadColor(ActiveControllerInfo info)
    {
        if (_watcher.GetLedMode(info.Serial) == LedMode.Passthrough)
        {
            // Acompanha a cor real que o jogo está mandando pro LED. Enquanto
            // nenhum jogo mandou cor (tudo 0), usa a cor de destaque.
            var (r, g, b) = _watcher.GetCurrentLedColor(info.Serial);
            if (r == 0 && g == 0 && b == 0)
                return (FindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.Gray;
            return Color.FromRgb(r, g, b);
        }

        // Modo preset: MESMA matiz que vai pro LED físico (GetPresetColor),
        // com o brilho amplificado só na exibição. O LED do controle é mandado
        // escuro (valor 20) e esse mesmo 20 ficaria escuro demais na tela.
        // NÃO altera a cor enviada ao controle.
        const int brilhoNaTela = 126;
        var (pr, pg, pb) = info.Profile.GetPresetColor();
        byte Amplify(byte canal) => canal == 0 ? (byte)0 : (byte)brilhoNaTela;
        return Color.FromRgb(Amplify(pr), Amplify(pg), Amplify(pb));
    }

    /// <summary>Move uma cor no espaço HSV, preservando a matiz.</summary>
    private static Color AjustarHsv(Color c, double dSaturacao, double dBrilho)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double v = max, delta = max - min;
        double sat = max <= 0 ? 0 : delta / max;

        double h = 0;
        if (delta > 0)
        {
            if (max == r) h = 60 * (((g - b) / delta) % 6);
            else if (max == g) h = 60 * (((b - r) / delta) + 2);
            else h = 60 * (((r - g) / delta) + 4);
        }
        if (h < 0) h += 360;

        sat = Math.Clamp(sat + dSaturacao, 0, 1);
        v = Math.Clamp(v + dBrilho, 0, 1);

        double cc = v * sat, x = cc * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - cc;
        (double r2, double g2, double b2) = h switch
        {
            < 60 => (cc, x, 0d),
            < 120 => (x, cc, 0d),
            < 180 => (0d, cc, x),
            < 240 => (0d, x, cc),
            < 300 => (x, 0d, cc),
            _ => (cc, 0d, x)
        };
        return Color.FromRgb((byte)((r2 + m) * 255), (byte)((g2 + m) * 255), (byte)((b2 + m) * 255));
    }

    private static Color Misturar(Color a, Color b, double quanto) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * quanto),
        (byte)(a.G + (b.G - a.G) * quanto),
        (byte)(a.B + (b.B - a.B) * quanto));

    /// <summary>
    /// Preenchimento "neon": a cor viva por toda a superfície, com um núcleo
    /// levemente mais luminoso no meio. Sem escurecer as bordas e sem branco
    /// na mistura — o núcleo é a MESMA cor com mais brilho, o que lê como luz
    /// em vez de tinta desbotada.
    /// </summary>
    private static Brush BuildLedBrush(Color cor)
    {
        var vivo = AjustarHsv(cor, LedSaturacao, LedBrilho);
        var nucleo = Misturar(vivo, AjustarHsv(cor, -0.10, 0.38), LedNucleo);

        var pincel = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.6,
            RadiusY = 0.6
        };
        pincel.GradientStops.Add(new GradientStop(nucleo, 0.0));
        pincel.GradientStops.Add(new GradientStop(vivo, 0.65));
        pincel.GradientStops.Add(new GradientStop(vivo, 1.0));
        pincel.Freeze();
        return pincel;
    }

    /// <summary>
    /// Halo externo na cor viva. A superfície do touchpad fica nítida; toda a
    /// difusão acontece pra fora, que é como luz se comporta e como as
    /// interfaces escuras fazem glow sem sujar o elemento. Passar null tira.
    /// </summary>
    private static void AplicarBrilho(System.Windows.Shapes.Rectangle rect, Color? cor)
    {
        rect.Effect = cor == null
            ? null
            : new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = AjustarHsv(cor.Value, LedSaturacao, LedBrilho),
                ShadowDepth = 0,
                BlurRadius = LedHaloRaio,
                Opacity = LedHaloOpacidade
            };
    }


    /// <summary>Modo do LED trocado pelo atalho do controle: refaz o card.</summary>
    private void OnLedModeChanged(string serial) =>
        Dispatcher.Invoke(() =>
        {
            if (_controllerInfos.ContainsKey(serial))
                RebuildCard(serial, isDisabled: false);
        });

    private void OnTouchpadClicked(string serial, bool isDisabled)
    {
        if (isDisabled)
        {
            // Controle desativado: clicar em qualquer parte da imagem (mesmo
            // na área do touchpad) só reativa, não mexe no modo do LED ainda.
            OnDeviceEnableToggled(serial, true);
            return;
        }

        var current = _watcher.GetLedMode(serial);
        var next = current == LedMode.Preset ? LedMode.Passthrough : LedMode.Preset;
        _watcher.SetLedMode(serial, next);

        RebuildCard(serial, isDisabled: false);
    }

    private void OnDeviceEnableToggled(string serial, bool enabled)
    {
        if (_suppressToggleFor.Contains(serial)) return;
        _watcher.SetDevicePassthroughDisabled(serial, !enabled);
    }

    // Arrastar a janela pela barra de título customizada.
    private void OnTitleBarDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void OnStartWithWindowsToggle(object sender, RoutedEventArgs e)
    {
        bool enabled = !_settings.Current.StartWithWindows;
        _settings.SetStartWithWindows(enabled);
        UpdateWindowsIconState(enabled);
    }

    // Ícone do Windows: apagado (opacidade baixa) quando desativado,
    // brilhante (opaco, azul vivo) quando ativado. Mantém as cores do
    // Windows nos dois estados, só muda o brilho.
    private void UpdateWindowsIconState(bool enabled)
    {
        if (WindowsIcon == null) return;
        WindowsIcon.Opacity = enabled ? 1.0 : 0.35;
    }

    private void OnOpenOnStartupToggle(object sender, RoutedEventArgs e)
    {
        bool enabled = !_settings.Current.OpenWindowOnStartup;
        _settings.Current.OpenWindowOnStartup = enabled;
        _settings.Save();
        UpdateOpenOnStartupIconState(enabled);
    }

    // Ícone de janela (abrir ao iniciar): mesma linguagem visual do botão
    // do Windows — brilhante quando ativado, apagado quando desativado.
    private void UpdateOpenOnStartupIconState(bool enabled)
    {
        if (OpenOnStartupIcon == null) return;
        OpenOnStartupIcon.Opacity = enabled ? 1.0 : 0.35;
    }

    /// <summary>
    /// Move o ponto de cada controle conectado. Roda 30 vezes por segundo
    /// enquanto a janela está aberta. Sem sessão ativa (emulação desativada),
    /// a leitura devolve "sem toque" e o ponto some sozinho.
    /// </summary>
    private void AtualizarToques()
    {
        foreach (var (serial, dot) in _touchDots)
        {
            if (!_controllerInfos.TryGetValue(serial, out var info)) continue;

            // O ponto só aparece no perfil DualShock 4, que é o único que
            // repassa o touchpad ao controle virtual. No perfil Xbox 360 o
            // toque não chega a lugar nenhum — mostrar o ponto ali daria a
            // entender que o touchpad está funcionando no jogo, e não está.
            bool repassaTouchpad = info.Profile == EmulationProfile.DualShock4;

            var (ativo, x, y) = _watcher.GetTouch(serial);
            ControllerIconFactory.PosicionarToque(dot, info.Type, ativo && repassaTouchpad, x, y);
        }
    }

    // ---- Aviso de nova versão ----

    private string? _updateUrl;

    /// <summary>
    /// Liga o indicador de nova versão na barra de título. Chamado pelo App
    /// depois da checagem.
    /// </summary>
    public void ShowUpdateAvailable(string versao, string url)
    {
        _updateUrl = url;
        UpdateTip.Content = $"Nova versão disponível: {versao}";
        UpdateBtn.Visibility = Visibility.Visible;
    }

    private void OnUpdateDownloadClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_updateUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _updateUrl,
                UseShellExecute = true
            });
        }
        catch { /* sem navegador disponível — nada a fazer */ }
    }

    // ---- Painel de atalhos ----

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        // O painel cobre só o conteúdo, então este botão continua clicável com
        // ele aberto — daí funcionar como alternador em vez de só abrir.
        HelpOverlay.Visibility = HelpOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnHelpCloseClick(object sender, RoutedEventArgs e) =>
        HelpOverlay.Visibility = Visibility.Collapsed;

    private void OnHelpBackdropClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Só fecha quando o clique é no fundo escurecido. Cliques dentro do
        // cartão não chegam aqui (o Border os consome), então rolar a lista ou
        // selecionar texto não fecha o painel sem querer.
        if (ReferenceEquals(e.OriginalSource, HelpOverlay))
            HelpOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => Hide();

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.RequestExit();
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher.ControllerConnected -= OnControllerConnected;
        _watcher.ControllerDisconnected -= OnControllerDisconnected;
        _watcher.DeviceDisabledByUser -= OnDeviceDisabledByUser;
        _watcher.ProfileChanged -= OnProfileChanged;
        _watcher.LedColorChanged -= OnLedColorChanged;
        _watcher.BatteryChanged -= OnBatteryChanged;
        base.OnClosed(e);
    }
}
