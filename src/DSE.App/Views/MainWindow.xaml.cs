using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DSE.App.Ui;
using DSE.Core.Bluetooth;
using DSE.Core.Controllers;
using DSE.Core.Diagnostics;
using DSE.Core.Profiles;
using DSE.Core.Setup;

namespace DSE.App.Views;

/// <summary>
/// Janela principal no padrão "app simples" (interface-simples, 10/out/2026):
/// tamanho FIXO 5:4, barra de 34 com botões-célula, duas colunas (esquerda =
/// perfil e luz do controle selecionado, ou a dica quando não há controle;
/// direita = controles conectados + imagem do selecionado), rodapé com
/// indicador. Telas secundárias: Opções e Ajuda (Esc volta).
/// </summary>
public partial class MainWindow : Window
{
    private readonly ControllerWatcher _watcher;
    private readonly AppSettingsService _settings;
    private readonly AtalhosGlobais _atalhos;
    private readonly Func<bool> _driversFaltando;
    private readonly Action _abrirAssistente;

    // Controles conhecidos, na ordem em que chegaram. Desativado continua na
    // lista (dá pra reativar); some só ao desconectar de verdade.
    private readonly Dictionary<string, ActiveControllerInfo> _infos = new();
    private readonly List<string> _ordem = new();
    private readonly HashSet<string> _desativados = new();
    private string? _selecionado;

    private Botao _bXbox = null!, _bDs4 = null!, _bDesativar = null!, _bCorPerfil = null!, _bCorJogo = null!, _bDica = null!;
    private BotaoBarra _bAjuda = null!, _bOpcoes = null!, _bAtualizar = null!;
    private string _estadoAtualizacao = "";
    private TextBlock? _legendaVersao;
    private string _tela = "inicio";

    // Imagem do selecionado (refeita só quando muda o que ela mostra).
    private string? _chaveImagem;
    private System.Windows.Shapes.Rectangle? _touchpad;
    private System.Windows.Shapes.Ellipse? _pontoToque;

    // Relógio do ponto do toque: a janela PUXA o estado 30x/s (chegam ~250
    // reports/s) e só com ela visível — na bandeja o custo é zero.
    private readonly DispatcherTimer _relogioDoToque = new() { Interval = TimeSpan.FromMilliseconds(33) };

    // Captura de atalho do teclado em andamento.
    private (string Acao, Botao Botao)? _capturando;
    private readonly DispatcherTimer _relogioRecusa = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    public MainWindow(ControllerWatcher watcher, AppSettingsService settings, AtalhosGlobais atalhos,
        Func<bool> driversFaltando, Action abrirAssistente)
    {
        _watcher = watcher;
        _settings = settings;
        _atalhos = atalhos;
        _driversFaltando = driversFaltando;
        _abrirAssistente = abrirAssistente;

        InitializeComponent();
        MontarBarra();
        MontarColunaControles();
        Indicador.Content = Icones.Criar("controle", 14);
        RodapeOpcoes.Text = $"DSE {VersaoTexto()} · registro de diagnóstico: crie uma pasta \"log\" ao lado do DSE.App.exe";
        Posicionar();

        _relogioDoToque.Tick += (_, _) => AtualizarToque();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) _relogioDoToque.Start();
            else _relogioDoToque.Stop();
        };
        _relogioRecusa.Tick += (_, _) =>
        {
            _relogioRecusa.Stop();
            if (_capturando is { } c) c.Botao.DefinirConteudo("aperte as teclas…", null);
        };

        PreviewKeyDown += OnTecla;
        Deactivated += (_, _) => _ = CancelarCapturaSeSairAsync();
        SourceInitialized += (_, _) => CantosWindows11();

        _watcher.ControllerConnected += OnControllerConnected;
        _watcher.ControllerDisconnected += OnControllerDisconnected;
        _watcher.DeviceDisabledByUser += OnDeviceDisabledByUser;
        _watcher.ProfileChanged += OnProfileChanged;
        _watcher.LedColorChanged += OnLedColorChanged;
        _watcher.BatteryChanged += OnBatteryChanged;
        _watcher.LedModeChanged += OnLedModeChanged;

        // Controles que já estavam conectados antes da janela existir.
        foreach (var info in _watcher.GetActiveControllers())
            Guardar(info);

        Pintar();
    }

    private static string VersaoTexto()
    {
        var v = typeof(MainWindow).Assembly.GetName().Version;
        return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // ===================================================================
    // Barra
    // ===================================================================

    private void MontarBarra()
    {
        _bAtualizar = new BotaoBarra("baixar", AbrirAtualizacao, dica: "Nova versão disponível")
        {
            Visibility = Visibility.Collapsed
        };
        _bAtualizar.CorFixa((Brush)FindResource("ConnectedBrush"));
        _bAjuda = new BotaoBarra("ajuda", () => AlternarTela("ajuda"), dica: "Ajuda e atalhos do controle");
        _bOpcoes = new BotaoBarra("opcoes", () => AlternarTela("opcoes"), dica: "Opções");
        var bMin = new BotaoBarra("minimizar", () => WindowState = WindowState.Minimized, dica: "Minimizar");
        var bFechar = new BotaoBarra("fechar", EsconderNaBandeja, largura: 40,
            aoSegurar: () => (Application.Current as App)?.RequestExit(),
            dica: "Fechar\nclique: esconde na bandeja\nsegurar 1 s: fecha o DSE");

        foreach (var b in new UIElement[] { _bAtualizar, _bAjuda, _bOpcoes, bMin, bFechar })
            BotoesBarra.Children.Add(b);
    }

    private void OnBarraArrastar(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { /* botão já solto */ }
        GuardarPosicao();
    }

    public void ShowUpdateAvailable(string versao)
    {
        _bAtualizar.ToolTip = $"Saiu a versão {versao}\nclique para atualizar";
        _bAtualizar.Visibility = Visibility.Visible;
        MostrarAndamentoAtualizacao($"versão {versao} disponível");
    }

    public void EsconderAtualizacao() => _bAtualizar.Visibility = Visibility.Collapsed;

    /// <summary>Texto da linha "Versão" nas opções (procura, download, resultado).</summary>
    public void MostrarAndamentoAtualizacao(string texto)
    {
        _estadoAtualizacao = texto;
        if (_legendaVersao != null) _legendaVersao.Text = texto;
    }

    private async void AbrirAtualizacao()
    {
        if (Application.Current is App app) MostrarAndamentoAtualizacao(await app.AtualizarAgora());
    }

    private static void Abrir(string alvo)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = alvo, UseShellExecute = true });
        }
        catch (Exception ex) { DseLog.Write($"[janela] não abriu {alvo}: {DseLog.Fmt(ex)}"); }
    }

    // ===================================================================
    // Telas
    // ===================================================================

    private void AlternarTela(string nome) => Ir(_tela == nome ? "inicio" : nome);

    private void Ir(string nome)
    {
        CancelarCaptura();
        if (nome == "opcoes") MontarOpcoes();
        if (nome == "ajuda" && ConteudoAjuda.Children.Count == 0) MontarAjuda();

        TelaInicio.Visibility = nome == "inicio" ? Visibility.Visible : Visibility.Collapsed;
        TelaOpcoes.Visibility = nome == "opcoes" ? Visibility.Visible : Visibility.Collapsed;
        TelaAjuda.Visibility = nome == "ajuda" ? Visibility.Visible : Visibility.Collapsed;
        _tela = nome;
        _bOpcoes.Acender(nome == "opcoes");
        _bAjuda.Acender(nome == "ajuda");
    }

    private void TituloTela(StackPanel alvo, string texto)
    {
        alvo.Children.Clear();
        var voltar = Botao.Criar(null, "voltar", largura: 32, altura: 32, raio: 8, ladoIcone: 16,
            dica: "Voltar (Esc)", cor: (Brush)FindResource("FundoBrush"));
        voltar.Click += (_, _) => Ir("inicio");
        alvo.Children.Add(voltar);
        alvo.Children.Add(new TextBlock { Text = texto, Style = (Style)FindResource("TitTela"), Margin = new Thickness(6, 0, 0, 0) });
    }

    // ===================================================================
    // Tela principal
    // ===================================================================

    private void MontarColunaControles()
    {
        _bXbox = Botao.Criar("Xbox 360", altura: 56, raio: 12, tam: 14, dica: "Perfil Xbox 360 (1)\no jogo vê um controle de Xbox");
        _bXbox.Margin = new Thickness(0, 0, 3, 0);
        _bXbox.Click += (_, _) => EscolherPerfil(EmulationProfile.Xbox360);
        _bDs4 = Botao.Criar("DualShock 4", altura: 56, raio: 12, tam: 14, dica: "Perfil DualShock 4 (2)\no jogo vê um controle de PS4: touchpad, giroscópio e cor da luz");
        _bDs4.Margin = new Thickness(3, 0, 0, 0);
        _bDs4.Click += (_, _) => EscolherPerfil(EmulationProfile.DualShock4);
        LinhaPerfis.Children.Add(_bXbox);
        LinhaPerfis.Children.Add(_bDs4);

        _bDesativar = Botao.Criar("Desativar emulação", "desligar", altura: 38, raio: 10, tam: 14,
            dica: "Desativar a emulação (0)\no jogo passa a ver o controle original");
        _bDesativar.Click += (_, _) => Desativar();
        HostDesativar.Child = _bDesativar;

        _bCorPerfil = Botao.Criar("Cor do perfil", altura: 34, tam: 13.3, dica: "Luz fixa: verde no Xbox 360, azul no DualShock 4");
        _bCorPerfil.Margin = new Thickness(0, 0, 3, 0);
        _bCorPerfil.Click += (_, _) => EscolherLuz(LedMode.Preset);
        _bCorJogo = Botao.Criar("Cor do jogo", altura: 34, tam: 13.3, dica: "O jogo escolhe a cor da luz (perfil DualShock 4)");
        _bCorJogo.Margin = new Thickness(3, 0, 0, 0);
        _bCorJogo.Click += (_, _) => EscolherLuz(LedMode.Passthrough);
        LinhaLuz.Children.Add(_bCorPerfil);
        LinhaLuz.Children.Add(_bCorJogo);

        _bDica = Botao.Criar("", altura: 34, tam: 13.3);
        HostBotaoDica.Child = _bDica;
    }

    private void Guardar(ActiveControllerInfo info)
    {
        _infos[info.Serial] = info;
        if (!_ordem.Contains(info.Serial)) _ordem.Add(info.Serial);
    }

    private ActiveControllerInfo? Selecionado =>
        _selecionado != null && _infos.TryGetValue(_selecionado, out var i) ? i : null;

    private bool Ativo(string serial) => !_desativados.Contains(serial);

    /// <summary>Atualiza a tela inteira a partir do estado conhecido.</summary>
    private void Pintar()
    {
        if (_selecionado == null || !_infos.ContainsKey(_selecionado))
            _selecionado = _ordem.FirstOrDefault();

        bool drivers = _driversFaltando();
        bool tem = _infos.Count > 0 && !drivers;
        ColunaControles.Visibility = tem ? Visibility.Visible : Visibility.Collapsed;
        PainelDica.Visibility = tem ? Visibility.Collapsed : Visibility.Visible;
        if (!tem) PintarDica(drivers);

        PintarEsquerda();
        PintarLista();
        PintarImagem();
        PintarRodape(drivers);
    }

    private void PintarDica(bool drivers)
    {
        if (drivers)
        {
            DicaTitulo.Text = "Faltam drivers";
            DicaTexto.Text = "O DSE precisa do ViGEmBus (cria o controle virtual) e do HidHide (esconde o controle " +
                             "original do jogo).\n\nInstale os dois e reinicie o computador.";
            _bDica.DefinirConteudo("Instalar", null);
            _bDica.Tag = "drivers";
        }
        else
        {
            DicaTitulo.Text = "Nenhum controle conectado";
            DicaTexto.Text = "Ligue o DualShock 4 ou o DualSense no cabo USB, ou pelo Bluetooth.\n\n" +
                             "Para parear: segure PS + Share (DualShock 4) ou PS + Create (DualSense) até a luz " +
                             "piscar rápido e adicione o controle nas configurações de Bluetooth do Windows.\n\n" +
                             "Ele aparece aqui sozinho.";
            _bDica.DefinirConteudo("Abrir Bluetooth", "bluetooth", 14);
            _bDica.Tag = "bluetooth";
        }
        _bDica.Click -= OnBotaoDica;
        _bDica.Click += OnBotaoDica;
    }

    private void OnBotaoDica(object sender, RoutedEventArgs e)
    {
        if (_bDica.Tag as string == "drivers") _abrirAssistente();
        else Abrir("ms-settings:bluetooth");
    }

    private void PintarEsquerda()
    {
        var info = Selecionado;
        if (info == null)
        {
            foreach (var b in new[] { _bXbox, _bDs4, _bDesativar, _bCorPerfil, _bCorJogo })
            {
                b.Selecionado = false;
                b.IsEnabled = false;
            }
            return;
        }

        bool ativo = Ativo(info.Serial);
        var perfil = ativo ? info.Profile : _watcher.GetSavedProfile(info.Serial);
        _bXbox.IsEnabled = _bDs4.IsEnabled = _bDesativar.IsEnabled = true;
        _bXbox.Selecionado = ativo && perfil == EmulationProfile.Xbox360;
        _bDs4.Selecionado = ativo && perfil == EmulationProfile.DualShock4;
        _bDesativar.Selecionado = !ativo;
        _bDesativar.DefinirConteudo(ativo ? "Desativar emulação" : "Emulação desativada", "desligar");

        var modo = _watcher.GetLedMode(info.Serial);
        _bCorPerfil.IsEnabled = _bCorJogo.IsEnabled = ativo;
        _bCorPerfil.Selecionado = ativo && modo == LedMode.Preset;
        _bCorJogo.Selecionado = ativo && modo == LedMode.Passthrough;
        LegendaLuz.Text = !ativo ? "com a emulação ativa"
            : perfil == EmulationProfile.Xbox360 && modo == LedMode.Passthrough ? "o jogo só manda cor no DS4"
            : "cor da barra de luz";
    }

    private static string NomeTipo(PhysicalControllerType t) =>
        t == PhysicalControllerType.DualShock4 ? "DualShock 4" : "DualSense";

    private string NomeDe(ActiveControllerInfo info)
    {
        // Dois do mesmo tipo: o segundo ganha número.
        var mesmos = _ordem.Where(s => _infos[s].Type == info.Type).ToList();
        int n = mesmos.IndexOf(info.Serial);
        return mesmos.Count > 1 ? $"{NomeTipo(info.Type)} {n + 1}" : NomeTipo(info.Type);
    }

    private string LegendaDe(ActiveControllerInfo info)
    {
        string conexao = info.IsBluetooth ? "Bluetooth" : "USB";
        return Ativo(info.Serial) ? $"{conexao} · {info.Profile.DisplayName()}" : $"{conexao} · emulação desativada";
    }

    private void PintarLista()
    {
        ListaControles.Children.Clear();
        LegendaControles.Text = _infos.Count switch
        {
            0 => "",
            1 => "1 conectado",
            var n => $"{n} conectados"
        };

        if (_infos.Count == 0)
        {
            ListaControles.Children.Add(new TextBlock
            {
                Text = "Nenhum controle conectado.",
                Style = (Style)FindResource("Legenda"),
                FontSize = 12.7,
                Margin = new Thickness(0, 4, 0, 0)
            });
            return;
        }

        foreach (var serial in _ordem)
        {
            var info = _infos[serial];
            bool sel = serial == _selecionado;
            var grade = new Grid();
            grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textos.Children.Add(new TextBlock { Text = NomeDe(info), Style = (Style)FindResource("Corpo"), TextTrimming = TextTrimming.CharacterEllipsis });
            textos.Children.Add(new TextBlock { Text = LegendaDe(info), Style = (Style)FindResource("Legenda") });
            grade.Children.Add(textos);

            var (pct, carregando) = _watcher.GetBattery(serial);
            var bateria = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
            if (Ativo(serial) && pct >= 0)
            {
                bateria.Children.Add(BuildBatteryIcon(pct, carregando));
                bateria.Children.Add(new TextBlock
                {
                    Text = $"{pct}%",
                    FontSize = 12,
                    Foreground = BatteryColor(pct),
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                bateria.ToolTip = carregando ? "Carregando" : "Bateria";
            }
            Grid.SetColumn(bateria, 1);
            grade.Children.Add(bateria);

            if (info.IsBluetooth)
            {
                var desligar = Botao.Criar(null, "desligar", largura: 28, altura: 28, raio: 8, ladoIcone: 14,
                    dica: "Desligar o controle (Bluetooth)", cor: (Brush)FindResource(sel ? "Camada2Brush" : "Camada1Brush"));
                desligar.Click += (_, e) =>
                {
                    e.Handled = true;
                    DesligarControle(serial);
                };
                Grid.SetColumn(desligar, 2);
                grade.Children.Add(desligar);
            }

            var linha = new Border
            {
                Background = (Brush)FindResource(sel ? "Camada2Brush" : "Camada1Brush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 4, 4, 4),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = Cursors.Hand,
                Child = grade
            };
            linha.MouseLeftButtonUp += (_, _) =>
            {
                if (_selecionado == serial) return;
                _selecionado = serial;
                Pintar();
            };
            ListaControles.Children.Add(linha);
        }
    }

    private void PintarImagem()
    {
        var info = Selecionado;
        if (info == null)
        {
            HostImagem.Child = null;
            _chaveImagem = null;
            _touchpad = null;
            _pontoToque = null;
            return;
        }

        bool ativo = Ativo(info.Serial);
        var modo = _watcher.GetLedMode(info.Serial);
        string chave = $"{info.Serial}|{info.Type}|{ativo}|{info.Profile}|{modo}";
        if (chave == _chaveImagem) return;
        _chaveImagem = chave;

        var serial = info.Serial;
        var img = ControllerIconFactory.Build(
            info.Type,
            ativo ? BuildLedBrush(CorDaLuz(info)) : Brushes.White,
            boxWidth: 216,
            boxHeight: 150,
            onBodyClick: () => AlternarEmulacao(serial),
            onTouchpadClick: () => AlternarLuz(serial),
            out var touchpad,
            out var ponto);
        img.HorizontalAlignment = HorizontalAlignment.Center;
        img.VerticalAlignment = VerticalAlignment.Center;
        img.ToolTip = "Clique no controle: liga/desliga a emulação\nClique no touchpad: troca a cor da luz";
        _touchpad = touchpad;
        _pontoToque = ponto;
        AplicarBrilho(touchpad, ativo ? CorDaLuz(info) : null);
        HostImagem.Child = img;
    }

    private void PintarRodape(bool drivers)
    {
        string chaveCor;
        string texto;
        int ativos = _infos.Keys.Count(Ativo);
        if (drivers)
        {
            chaveCor = "DisconnectedBrush";
            texto = "Faltam drivers";
        }
        else if (_infos.Count == 0)
        {
            chaveCor = "ApagadoBrush";
            texto = "Procurando controles";
        }
        else if (_infos.Count == 1)
        {
            var i = _infos.Values.First();
            chaveCor = ativos > 0 ? "ConnectedBrush" : "ApagadoBrush";
            texto = ativos > 0 ? $"{NomeTipo(i.Type)} · emulando como {i.Profile.DisplayName()}" : $"{NomeTipo(i.Type)} · emulação desativada";
        }
        else
        {
            chaveCor = ativos > 0 ? "ConnectedBrush" : "ApagadoBrush";
            texto = $"{_infos.Count} controles · {ativos} emulando";
        }
        Indicador.Foreground = (Brush)FindResource(chaveCor);
        IndicadorTexto.Text = texto;
    }

    // ---- Ações da tela principal ----

    private void EscolherPerfil(EmulationProfile perfil)
    {
        var info = Selecionado;
        if (info == null) return;
        var serial = info.Serial;

        if (!Ativo(serial))
        {
            // Desativado: grava o perfil escolhido e reativa com ele.
            _watcher.SetSavedProfile(serial, perfil);
            info.Profile = perfil;
            _desativados.Remove(serial);
            Pintar();
            _ = Task.Run(() => _watcher.SetDevicePassthroughDisabled(serial, false));
            return;
        }
        if (info.Profile == perfil) return;
        info.Profile = perfil; // muda na hora; o evento do watcher confirma
        Pintar();
        _ = Task.Run(() => _watcher.SetProfile(serial, perfil));
    }

    private void Desativar()
    {
        var info = Selecionado;
        if (info == null || !Ativo(info.Serial)) return;
        var serial = info.Serial;
        _desativados.Add(serial);
        Pintar();
        _ = Task.Run(() => _watcher.SetDevicePassthroughDisabled(serial, true));
    }

    private void AlternarEmulacao(string serial)
    {
        _selecionado = serial;
        if (Ativo(serial)) Desativar();
        else EscolherPerfil(_watcher.GetSavedProfile(serial));
    }

    private void EscolherLuz(LedMode modo)
    {
        var info = Selecionado;
        if (info == null || !Ativo(info.Serial)) return;
        _watcher.SetLedMode(info.Serial, modo);
        info.LedMode = modo;
        Pintar();
    }

    private void AlternarLuz(string serial)
    {
        if (!Ativo(serial))
        {
            AlternarEmulacao(serial);
            return;
        }
        _selecionado = serial;
        EscolherLuz(_watcher.GetLedMode(serial) == LedMode.Preset ? LedMode.Passthrough : LedMode.Preset);
    }

    private void DesligarControle(string serial)
    {
        if (Ativo(serial)) _ = Task.Run(() => _watcher.PowerOffController(serial));
        else _ = Task.Run(() => BluetoothDisconnector.TryDisconnect(serial));
    }

    // ---- Eventos do watcher (chegam de outras threads) ----

    private void OnControllerConnected(ActiveControllerInfo info) =>
        Dispatcher.Invoke(() =>
        {
            Guardar(info);
            _desativados.Remove(info.Serial);
            Pintar();
        });

    private void OnControllerDisconnected(string serial) =>
        Dispatcher.Invoke(() =>
        {
            _infos.Remove(serial);
            _ordem.Remove(serial);
            _desativados.Remove(serial);
            Pintar();
        });

    private void OnDeviceDisabledByUser(string serial) =>
        Dispatcher.Invoke(() =>
        {
            _desativados.Add(serial);
            Pintar();
        });

    private void OnProfileChanged(ActiveControllerInfo info) =>
        Dispatcher.Invoke(() =>
        {
            Guardar(info);
            Pintar();
        });

    private void OnLedModeChanged(string serial) => Dispatcher.Invoke(Pintar);

    private void OnBatteryChanged(string serial, int percent, bool charging) => Dispatcher.Invoke(PintarLista);

    private void OnLedColorChanged(string serial, byte r, byte g, byte b) =>
        Dispatcher.Invoke(() =>
        {
            // Recolore só o touchpad (a cor pode mudar muitas vezes por segundo
            // em jogos que animam a luz). Só no modo "cor do jogo".
            if (serial != _selecionado || _touchpad == null) return;
            if (_watcher.GetLedMode(serial) != LedMode.Passthrough) return;
            var cor = Color.FromRgb(r, g, b);
            _touchpad.Fill = BuildLedBrush(cor);
            AplicarBrilho(_touchpad, cor);
        });

    /// <summary>Move o ponto do toque do selecionado (só no perfil DS4, o único que repassa o touchpad).</summary>
    private void AtualizarToque()
    {
        var info = Selecionado;
        if (info == null || _pontoToque == null) return;
        bool repassa = Ativo(info.Serial) && info.Profile == EmulationProfile.DualShock4;
        var (ativo, x, y) = _watcher.GetTouch(info.Serial);
        ControllerIconFactory.PosicionarToque(_pontoToque, info.Type, ativo && repassa, x, y);
    }

    /// <summary>Refaz a tela (ex.: depois do assistente de drivers).</summary>
    public void Repintar() => Pintar();

    // ---- Só para o autoteste (controles de mentira, sem watcher rodando) ----

    internal void InjetarParaTeste(ActiveControllerInfo? info, bool desativado, bool selecionar = false)
    {
        if (info != null)
        {
            Guardar(info);
            if (selecionar) _selecionado = info.Serial;
        }
        if (_selecionado != null)
        {
            if (desativado) _desativados.Add(_selecionado);
            else _desativados.Remove(_selecionado);
        }
        Pintar();
    }

    internal void IrParaTeste(string tela) => Ir(tela);

    internal void RolarOpcoesParaFimTeste()
    {
        RolOpcoes.UpdateLayout();
        RolOpcoes.ScrollToEnd();
    }

    // ---- Ações vindas dos atalhos do teclado (App) ----

    public void ExecutarAtalho(string acao)
    {
        switch (acao)
        {
            case "perfil":
                foreach (var s in _ordem.Where(Ativo).ToList())
                    _ = Task.Run(() => _watcher.ToggleProfile(s));
                break;
            case "emulacao":
                var ativos = _ordem.Where(Ativo).ToList();
                if (ativos.Count > 0)
                {
                    foreach (var s in ativos)
                    {
                        _desativados.Add(s);
                        _ = Task.Run(() => _watcher.SetDevicePassthroughDisabled(s, true));
                    }
                }
                else
                {
                    foreach (var s in _ordem.ToList())
                    {
                        _desativados.Remove(s);
                        _ = Task.Run(() => _watcher.SetDevicePassthroughDisabled(s, false));
                    }
                }
                Pintar();
                break;
            case "desligar":
                foreach (var s in _ordem.Where(s => _infos[s].IsBluetooth).ToList())
                    DesligarControle(s);
                break;
        }
    }

    // ===================================================================
    // Menu da bandeja (botão direito): montado na hora, com o estado atual
    // ===================================================================

    /// <summary>
    /// Pedido dele (11/out/2026): no topo, SÓ informação (cada controle e a
    /// bateria, sem clique); embaixo Opções, Procurar atualização e Fechar.
    /// </summary>
    public void PreencherMenuBandeja(ContextMenu menu)
    {
        menu.Items.Clear();
        MenuItem Item(string texto, Action acao)
        {
            var mi = new MenuItem { Header = texto };
            mi.Click += (_, _) => acao();
            return mi;
        }
        MenuItem Info(string nome, string? direita = null, Brush? corDireita = null)
        {
            var linha = new DockPanel { LastChildFill = true };
            if (direita != null)
            {
                var d = new TextBlock { Text = direita, Margin = new Thickness(16, 0, 0, 0), Foreground = corDireita ?? (Brush)FindResource("Texto2Brush") };
                DockPanel.SetDock(d, Dock.Right);
                linha.Children.Add(d);
            }
            linha.Children.Add(new TextBlock { Text = nome, TextTrimming = TextTrimming.CharacterEllipsis });
            return new MenuItem { Header = linha, IsHitTestVisible = false, Focusable = false };
        }

        if (_driversFaltando())
            menu.Items.Add(Info("Faltam drivers: abra o DSE"));
        else if (_ordem.Count == 0)
            menu.Items.Add(Info("Nenhum controle conectado"));

        foreach (var serial in _ordem.ToList())
        {
            var info = _infos[serial];
            var (pct, carregando) = _watcher.GetBattery(serial);
            var nome = NomeDe(info) + (info.IsBluetooth ? " · Bluetooth" : " · USB");
            if (!Ativo(serial)) menu.Items.Add(Info(nome, "sem emulação"));
            else if (pct >= 0) menu.Items.Add(Info(nome, carregando ? $"{pct}% · carregando" : $"{pct}%", BatteryColor(pct)));
            else menu.Items.Add(Info(nome));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Opções", () =>
        {
            Mostrar();
            if (_tela != "opcoes") Ir("opcoes");
        }));
        if (Application.Current is App app)
        {
            var nova = app.VersaoNovaDisponivel;
            menu.Items.Add(nova != null
                ? Item($"Atualizar para a versão {nova}", async () => MostrarAndamentoAtualizacao(await app.AtualizarAgora()))
                : Item("Procurar atualização", async () =>
                {
                    var r = await app.ProcurarAtualizacao(manual: true);
                    MostrarAndamentoAtualizacao(r);
                    if (r == "em dia") app.AvisarNaBandeja($"O DSE {VersaoTexto()} é a versão mais nova.");
                }));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Fechar o DSE", () => (Application.Current as App)?.RequestExit()));
    }

    // ===================================================================
    // LED na imagem (modelo "neon", mantido da versão anterior)
    // ===================================================================

    private const double LedSaturacao = 0.15;
    private const double LedBrilho = 0.22;
    private const double LedNucleo = 0.33;
    private const double LedHaloRaio = 26;
    private const double LedHaloOpacidade = 0.50;

    /// <summary>Cor que a luz deve mostrar na tela.</summary>
    private Color CorDaLuz(ActiveControllerInfo info)
    {
        if (_watcher.GetLedMode(info.Serial) == LedMode.Passthrough)
        {
            var (r, g, b) = _watcher.GetCurrentLedColor(info.Serial);
            if (r == 0 && g == 0 && b == 0)
                return (FindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.Gray;
            return Color.FromRgb(r, g, b);
        }

        // Mesma matiz do LED físico, com brilho amplificado só na tela (o
        // controle recebe 20, que na tela ficaria escuro demais).
        const int brilhoNaTela = 126;
        var (pr, pg, pb) = info.Profile.GetPresetColor();
        byte Amplificar(byte canal) => canal == 0 ? (byte)0 : (byte)brilhoNaTela;
        return Color.FromRgb(Amplificar(pr), Amplificar(pg), Amplificar(pb));
    }

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

    // ---- Bateria ----

    private FrameworkElement BuildBatteryIcon(int percent, bool charging)
    {
        var contorno = (Brush)FindResource("Texto2Brush");
        var apagada = (Brush)FindResource("Borda1Brush");
        int acesas = percent < 0 ? 0 : percent > 66 ? 3 : percent > 33 ? 2 : 1;
        Brush cor = acesas switch
        {
            3 => (Brush)FindResource("ConnectedBrush"),
            2 => (Brush)FindResource("AlertaBrush"),
            _ => (Brush)FindResource("DisconnectedBrush")
        };

        var barras = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 3; i++)
        {
            barras.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 3.4,
                Height = 6.5,
                RadiusX = 0.8,
                RadiusY = 0.8,
                Fill = i < acesas ? cor : apagada,
                Margin = new Thickness(0, 0, i < 2 ? 1.2 : 0, 0)
            });
        }

        var caixa = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        caixa.Children.Add(new Border
        {
            BorderBrush = contorno,
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(2.5),
            Padding = new Thickness(1.6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = barras
        });
        caixa.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 1.8,
            Height = 4.5,
            RadiusX = 0.8,
            RadiusY = 0.8,
            Fill = contorno,
            Margin = new Thickness(1, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (charging)
        {
            caixa.Children.Add(new TextBlock
            {
                Text = "⚡",
                FontSize = 9,
                Foreground = (Brush)FindResource("AlertaBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            });
        }
        return caixa;
    }

    private Brush BatteryColor(int percent)
    {
        if (percent <= 15) return (Brush)FindResource("DisconnectedBrush");
        if (percent <= 35) return (Brush)FindResource("AlertaBrush");
        return (Brush)FindResource("Texto2Brush");
    }

    // ===================================================================
    // Opções
    // ===================================================================

    private CheckBox? _chRapido;
    private FrameworkElement? _linhaRapido;

    private void MontarOpcoes()
    {
        TituloTela(TituloOpcoes, "Opções");

        while (PainelPrograma.Children.Count > 1) PainelPrograma.Children.RemoveAt(1);

        bool win = InicioWindows.Ativo();
        bool rapido = win && InicioWindows.Rapido();

        PainelPrograma.Children.Add(Linha("Iniciar com o Windows", "abre escondido na bandeja",
            Chave(win, async v =>
            {
                bool ok = await Task.Run(() => InicioWindows.Definir(v));
                if (ok)
                {
                    _settings.Current.StartWithWindows = v;
                    _settings.Save();
                    if (_linhaRapido != null) _linhaRapido.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
                    if (!v && _chRapido != null) _chRapido.IsChecked = false;
                }
                return ok;
            })));

        // Sub-opção: só aparece com a de origem ligada, recuada.
        _chRapido = Chave(rapido, v => Task.Run(() => InicioWindows.DefinirRapido(v)));
        _linhaRapido = Linha("Iniciar mais rápido", "junto com o login; pede o administrador uma vez", _chRapido, recuo: 16);
        _linhaRapido.Visibility = win ? Visibility.Visible : Visibility.Collapsed;
        PainelPrograma.Children.Add(_linhaRapido);

        PainelPrograma.Children.Add(Linha("Iniciar minimizado", "abre direto na bandeja",
            Chave(!_settings.Current.OpenWindowOnStartup, v =>
            {
                _settings.Current.OpenWindowOnStartup = !v;
                _settings.Save();
                return Task.FromResult(true);
            })));

        PainelPrograma.Children.Add(Linha("Desligar o Bluetooth ao plugar o cabo", "o controle segue só pelo USB, sem conexão dupla",
            Chave(_settings.Current.AutoDisableBluetoothOnUsb, v =>
            {
                _settings.Current.AutoDisableBluetoothOnUsb = v;
                _watcher.AutoDisableBluetoothOnUsb = v;
                _settings.Save();
                return Task.FromResult(true);
            })));

        // Atualizador (padrão scrcpy-f): prazo da procura + versão com "Procurar agora".
        var prazos = new (string Valor, string Rotulo)[]
        {
            ("diario", "todo dia"), ("semanal", "toda semana"), ("mensal", "todo mês"), ("nunca", "nunca"),
        };
        string RotuloPrazo() => prazos.FirstOrDefault(p => p.Valor == _settings.Current.ProcurarAtualizacao).Rotulo ?? "toda semana";
        var bPrazo = Botao.Criar(RotuloPrazo(), "seta_baixo", largura: 130, altura: 30, tam: 12.7, semi: false, ladoIcone: 12);
        bPrazo.Click += (_, _) => AbrirMenu(bPrazo, prazos.Select(p => (p.Rotulo, (Action)(() =>
        {
            _settings.Current.ProcurarAtualizacao = p.Valor;
            _settings.Save();
            bPrazo.DefinirConteudo(p.Rotulo, "seta_baixo", 12);
        }))));
        PainelPrograma.Children.Add(Linha("Procurar atualização", "no GitHub do DSE; avisa na bandeja", bPrazo));

        var bProcurar = Botao.Criar("Procurar agora", largura: 130, altura: 30, tam: 12.7, semi: false);
        bProcurar.Click += async (_, _) =>
        {
            if (Application.Current is not App app) return;
            bProcurar.IsEnabled = false;
            MostrarAndamentoAtualizacao("procurando…");
            MostrarAndamentoAtualizacao(await app.ProcurarAtualizacao(manual: true));
            bProcurar.IsEnabled = true;
        };
        var linhaVersao = Linha($"Versão {VersaoTexto()}", _estadoAtualizacao.Length > 0 ? _estadoAtualizacao : " ", bProcurar);
        _legendaVersao = ((StackPanel)((Grid)linhaVersao).Children[0]).Children.OfType<TextBlock>().Last();
        PainelPrograma.Children.Add(linhaVersao);

        var mais = Botao.Criar(null, "mais", largura: 28, altura: 28, raio: 8, ladoIcone: 14,
            dica: "Novo atalho", cor: (Brush)FindResource("Camada1Brush"));
        mais.Click += (_, _) => MenuNovoAtalho(mais);
        HostNovoAtalho.Child = mais;
        MontarAtalhos();
    }

    private FrameworkElement Linha(string nome, string? legenda, FrameworkElement controle, double recuo = 0)
    {
        var g = new Grid { Margin = new Thickness(recuo, 10, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        tx.Children.Add(new TextBlock { Text = nome, Style = (Style)FindResource("Corpo") });
        if (legenda != null)
            tx.Children.Add(new TextBlock { Text = legenda, Style = (Style)FindResource("Legenda"), TextWrapping = TextWrapping.Wrap });
        g.Children.Add(tx);
        controle.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(controle, 1);
        g.Children.Add(controle);
        return g;
    }

    /// <summary>Interruptor que muda na hora e ESCUTA a função (false = volta).</summary>
    private CheckBox Chave(bool ligado, Func<bool, Task<bool>> aoMudar)
    {
        var ch = new CheckBox { IsChecked = ligado, Style = (Style)FindResource("Chave") };
        ch.Click += async (_, _) =>
        {
            bool v = ch.IsChecked == true;
            ch.IsEnabled = false;
            bool ok;
            try { ok = await aoMudar(v); }
            catch (Exception ex)
            {
                DseLog.Write($"[opcoes] falha: {DseLog.Fmt(ex)}");
                ok = false;
            }
            ch.IsEnabled = true;
            if (!ok) ch.IsChecked = !v;
        };
        return ch;
    }

    private void MontarAtalhos()
    {
        ListaAtalhos.Children.Clear();
        var lista = AtalhosGlobais.Acoes.Where(a => _settings.Current.Atalhos.ContainsKey(a.Acao)).ToList();
        if (lista.Count == 0)
        {
            ListaAtalhos.Children.Add(new TextBlock
            {
                Text = "Nenhum atalho. Use o + para criar.",
                Style = (Style)FindResource("Legenda"),
                FontSize = 12.7,
                Margin = new Thickness(0, 8, 0, 0)
            });
            return;
        }

        foreach (var (acao, rotulo) in lista)
        {
            var t = _settings.Current.Atalhos[acao];
            bool falhou = _atalhos.Falhas.Contains(acao);
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = rotulo, Style = (Style)FindResource("Corpo"), FontSize = 13.3, TextTrimming = TextTrimming.CharacterEllipsis });

            var teclas = Botao.Criar(t.Vk == 0 ? "definir" : AtalhosGlobais.Texto(t), largura: 150, altura: 30, tam: 12.7, semi: false,
                dica: falhou ? "O Windows recusou: outro programa já usa" : "Clique e aperte as teclas");
            teclas.Foreground = (Brush)FindResource(falhou ? "DisconnectedBrush" : t.Vk == 0 ? "Texto2Brush" : "TextBrush");
            teclas.Margin = new Thickness(8, 0, 4, 0);
            teclas.Click += (_, _) => Capturar(acao, teclas);
            Grid.SetColumn(teclas, 1);
            g.Children.Add(teclas);

            var tirar = Botao.Criar(null, "lixo", largura: 28, altura: 30, raio: 8, ladoIcone: 13,
                dica: "Tirar", cor: (Brush)FindResource("Camada1Brush"));
            tirar.Click += (_, _) =>
            {
                CancelarCaptura();
                _settings.Current.Atalhos.Remove(acao);
                _settings.Save();
                _atalhos.Aplicar(_settings.Current.Atalhos);
                MontarAtalhos();
            };
            Grid.SetColumn(tirar, 2);
            g.Children.Add(tirar);
            ListaAtalhos.Children.Add(g);
        }
    }

    /// <summary>Menu da casa sob o "+": só as ações que ainda não têm atalho.</summary>
    private void MenuNovoAtalho(FrameworkElement ancora)
    {
        var livres = AtalhosGlobais.Acoes.Where(a => !_settings.Current.Atalhos.ContainsKey(a.Acao)).ToList();
        if (livres.Count == 0) return;

        AbrirMenu(ancora, livres.Select(l => (l.Rotulo, (Action)(() =>
        {
            var (acao, rotulo) = l;
            _settings.Current.Atalhos[acao] = new AtalhoTeclado();
            MontarAtalhos();
            // Atalho novo já entra pedindo as teclas.
            var linha = ListaAtalhos.Children.OfType<Grid>()
                .FirstOrDefault(g => g.Children.OfType<TextBlock>().FirstOrDefault()?.Text == rotulo);
            var botao = linha?.Children.OfType<Botao>().FirstOrDefault(b => Grid.GetColumn(b) == 1);
            if (botao != null)
            {
                botao.BringIntoView();
                Capturar(acao, botao);
            }
        }))));
    }

    /// <summary>Menu da casa, alinhado à direita da âncora; some com clique fora.</summary>
    private void AbrirMenu(FrameworkElement ancora, IEnumerable<(string Rotulo, Action Acao)> opcoes)
    {
        var itens = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = ancora,
            Placement = PlacementMode.Custom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
        };
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[]
        {
            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height + 4), PopupPrimaryAxis.Horizontal)
        };

        foreach (var (rotulo, aoEscolher) in opcoes)
        {
            var item = new Border
            {
                Padding = new Thickness(12, 6, 12, 6),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = rotulo, FontSize = 13.3 }
            };
            item.MouseEnter += (_, _) => item.Background = (Brush)FindResource("RealceBrush");
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, _) =>
            {
                popup.IsOpen = false;
                aoEscolher();
            };
            itens.Children.Add(item);
        }

        popup.Child = new Border
        {
            Background = (Brush)FindResource("SuperficieBrush"),
            BorderBrush = (Brush)FindResource("SuperficieBordaBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Child = itens,
            LayoutTransform = new ScaleTransform(1.3, 1.3), // o popup não herda a escala da janela
        };
        popup.IsOpen = true;
    }

    // ---- Captura de teclas ----

    private void Capturar(string acao, Botao botao)
    {
        CancelarCaptura();
        _capturando = (acao, botao);
        _atalhos.Aplicar(new Dictionary<string, AtalhoTeclado>()); // a combinação já registrada não pode roubar a tecla
        botao.Selecionado = true;
        botao.DefinirConteudo("aperte as teclas…", null);
        Ativar();
        botao.Focus();
    }

    private void CancelarCaptura()
    {
        if (_capturando is not { } c) return;
        _capturando = null;
        _relogioRecusa.Stop();
        if (_settings.Current.Atalhos.TryGetValue(c.Acao, out var t) && t.Vk == 0)
            _settings.Current.Atalhos.Remove(c.Acao); // novo sem teclas não fica
        _settings.Save();
        _atalhos.Aplicar(_settings.Current.Atalhos);
        if (_tela == "opcoes") MontarAtalhos();
    }

    private async Task CancelarCapturaSeSairAsync()
    {
        if (_capturando == null) return;
        await Task.Delay(300); // "perdeu o foco" só cancela depois de 300 ms
        if (!IsActive) CancelarCaptura();
    }

    private void OnTecla(object sender, KeyEventArgs e)
    {
        if (_capturando is { } c)
        {
            e.Handled = true;
            var k = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            if (k == Key.Escape)
            {
                CancelarCaptura();
                return;
            }
            if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
                or Key.RightShift or Key.LWin or Key.RWin or Key.None)
                return;

            var m = Keyboard.Modifiers;
            int mods = 0;
            if (m.HasFlag(ModifierKeys.Control)) mods |= AtalhosGlobais.ModCtrl;
            if (m.HasFlag(ModifierKeys.Alt)) mods |= AtalhosGlobais.ModAlt;
            if (m.HasFlag(ModifierKeys.Shift)) mods |= AtalhosGlobais.ModShift;
            if (m.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= AtalhosGlobais.ModWin;

            if ((mods & (AtalhosGlobais.ModCtrl | AtalhosGlobais.ModAlt | AtalhosGlobais.ModWin)) == 0)
            {
                Recusar(c.Botao, "use Ctrl/Alt/Win + tecla");
                return;
            }
            int vk = KeyInterop.VirtualKeyFromKey(k);
            var outro = _settings.Current.Atalhos.FirstOrDefault(p => p.Key != c.Acao && p.Value.Mods == mods && p.Value.Vk == vk);
            if (outro.Key != null)
            {
                Recusar(c.Botao, "já usado: " + AtalhosGlobais.Rotulo(outro.Key));
                return;
            }

            _settings.Current.Atalhos[c.Acao] = new AtalhoTeclado { Mods = mods, Vk = vk };
            _capturando = null;
            _relogioRecusa.Stop();
            _settings.Save();
            _atalhos.Aplicar(_settings.Current.Atalhos);
            MontarAtalhos();
            return;
        }

        if (e.Key == Key.Escape && _tela != "inicio")
        {
            Ir("inicio");
            e.Handled = true;
            return;
        }
        if (_tela != "inicio" || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.D1 or Key.NumPad1: EscolherPerfil(EmulationProfile.Xbox360); e.Handled = true; break;
            case Key.D2 or Key.NumPad2: EscolherPerfil(EmulationProfile.DualShock4); e.Handled = true; break;
            case Key.D0 or Key.NumPad0: Desativar(); e.Handled = true; break;
        }
    }

    private void Recusar(Botao botao, string motivo)
    {
        botao.DefinirConteudo(motivo, null);
        _relogioRecusa.Stop();
        _relogioRecusa.Start();
    }

    // ===================================================================
    // Ajuda
    // ===================================================================

    private void MontarAjuda()
    {
        TituloTela(TituloAjuda, "Ajuda");

        ConteudoAjuda.Children.Add(PainelAjuda("No controle", new[]
        {
            ("Share + Options", "alterna o perfil: Xbox 360 ou DualShock 4"),
            ("PS por 1 s", "desliga o controle (só Bluetooth)"),
            ("Options + PS por 1 s", "liga ou desliga a emulação"),
            ("Touchpad + PS por 1 s", "troca a luz: cor do perfil ou cor do jogo"),
        }, "Os atalhos com PS funcionam mesmo com a emulação desativada, com o DSE aberto. Solte assim que " +
           "sentir a vibração: segurar o PS uns 10 s desliga o controle (isso é do próprio controle)."));

        ConteudoAjuda.Children.Add(PainelAjuda("Na janela", new[]
        {
            ("1  /  2", "perfil Xbox 360 / DualShock 4"),
            ("0", "desativa a emulação"),
            ("Clique no controle", "liga ou desliga a emulação"),
            ("Clique no touchpad", "troca a cor da luz"),
            ("Fechar (X)", "clique esconde na bandeja; segurar 1 s fecha o DSE"),
            ("Bandeja", "clique mostra ou esconde; duplo clique abre"),
        }, null));

        ConteudoAjuda.Children.Add(PainelAjuda("Bom saber", Array.Empty<(string, string)>(),
            "Mantenha o firmware do controle atualizado: melhora a compatibilidade e corrige falhas. No DualSense, " +
            "pelo aplicativo PlayStation Accessories (Microsoft Store) ou por um PS5; no DualShock 4, por um PS4.\n\n" +
            "O controle parado por 10 minutos no Bluetooth desliga sozinho, pra poupar a bateria."));
    }

    private Border PainelAjuda(string titulo, (string Tecla, string Faz)[] linhas, string? nota)
    {
        var pilha = new StackPanel();
        pilha.Children.Add(new TextBlock { Text = titulo, Style = (Style)FindResource("TitPainel"), HorizontalAlignment = HorizontalAlignment.Left });
        foreach (var (tecla, faz) in linhas)
        {
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new Border
            {
                Background = (Brush)FindResource("Camada2Brush"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = tecla, FontFamily = (FontFamily)FindResource("LetraSemi"), FontSize = 12.7 }
            });
            var faz2 = new TextBlock { Text = faz, Style = (Style)FindResource("Corpo"), FontSize = 13.3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(faz2, 1);
            g.Children.Add(faz2);
            pilha.Children.Add(g);
        }
        if (nota != null)
            pilha.Children.Add(new TextBlock { Text = nota, Style = (Style)FindResource("Legenda"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 10, 0, 0), LineHeight = 17 });

        return new Border { Style = (Style)FindResource("Painel"), Margin = new Thickness(0, 0, 0, 10), Child = pilha };
    }

    // ===================================================================
    // Janela: posição, mostrar/esconder, cantos
    // ===================================================================

    private void Posicionar()
    {
        double w = Width, h = Height;
        double x, y;
        if (_settings.Current.JanelaX is double sx && _settings.Current.JanelaY is double sy)
        {
            x = sx;
            y = sy;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            x = area.Left + (area.Width - w) / 2;
            y = area.Top + (area.Height - h) / 2;
        }
        // Limitada à área de TODOS os monitores (monitor desligado não esconde a janela).
        double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        Left = Math.Clamp(x, vx, Math.Max(vx, vx + vw - w));
        Top = Math.Clamp(y, vy, Math.Max(vy, vy + vh - h));
    }

    private void GuardarPosicao()
    {
        _settings.Current.JanelaX = Left;
        _settings.Current.JanelaY = Top;
        _settings.Save();
    }

    /// <summary>Mostra, restaura e traz pra frente.</summary>
    public void Mostrar()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Ativar();
    }

    /// <summary>
    /// Mostra ou esconde. `clique` = quando foi o clique na bandeja: clicar no
    /// ícone já tira o foco da janela (a barra de tarefas vira a ativa), então
    /// "estava na frente" vale também se ela perdeu o foco nesse clique.
    /// </summary>
    public void Alternar(DateTime? clique = null)
    {
        bool naFrente = IsActive || (clique.HasValue && _desativadaEm >= clique.Value - TimeSpan.FromMilliseconds(700));
        if (IsVisible && WindowState != WindowState.Minimized && naFrente) EsconderNaBandeja();
        else Mostrar();
    }

    private DateTime _desativadaEm = DateTime.MinValue;
    protected override void OnDeactivated(EventArgs e)
    {
        _desativadaEm = DateTime.Now;
        base.OnDeactivated(e);
    }

    private void Ativar()
    {
        Activate();
        try { SetForegroundWindow(new WindowInteropHelper(this).Handle); } catch { /* ignora */ }
    }

    private void EsconderNaBandeja()
    {
        CancelarCaptura();
        Hide();
    }

    private void CantosWindows11()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int redondo = 2; // DWMWCP_ROUND
        int r = DwmSetWindowAttribute(hwnd, 33, ref redondo, sizeof(int));
        if (r == 0)
        {
            int cor = 0x00574C4C; // SUPERFICIE_BORDA #4C4C57 em COLORREF (0x00BBGGRR)
            DwmSetWindowAttribute(hwnd, 34, ref cor, sizeof(int));
        }
        else
        {
            Moldura.BorderThickness = new Thickness(1); // Windows 10: borda desenhada pela própria janela
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        EsconderNaBandeja();
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher.ControllerConnected -= OnControllerConnected;
        _watcher.ControllerDisconnected -= OnControllerDisconnected;
        _watcher.DeviceDisabledByUser -= OnDeviceDisabledByUser;
        _watcher.ProfileChanged -= OnProfileChanged;
        _watcher.LedColorChanged -= OnLedColorChanged;
        _watcher.BatteryChanged -= OnBatteryChanged;
        _watcher.LedModeChanged -= OnLedModeChanged;
        base.OnClosed(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
