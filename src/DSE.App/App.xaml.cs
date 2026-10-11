using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DSE.App.Tray;
using DSE.App.Ui;
using DSE.App.Views;
using DSE.Core.Controllers;
using DSE.Core.Diagnostics;
using DSE.Core.Profiles;
using DSE.Core.Setup;

namespace DSE.App;

public partial class App : Application
{
    private const string NomeMutex = "DSE.App.SingleInstance.Mutex";
    private const string NomeSinalMostrar = "DSE.App.Mostrar";

    private Mutex? _singleInstanceMutex;
    private const string NomeSinalSair = "DSE.App.Sair";
    private EventWaitHandle? _sinalMostrar;
    private EventWaitHandle? _sinalSair;
    private ControllerWatcher? _watcher;
    private TrayIconService? _tray;
    private AppSettingsService? _settings;
    private AtalhosGlobais? _atalhos;
    private MainWindow? _janela;
    private bool _driversFaltando;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Autoteste do .exe (ferramentas\empacotar.ps1): monta a janela fora da
        // tela, fotografa as telas e sai. Não abre o ViGEm nem esconde controle.
        int iAuto = Array.IndexOf(e.Args, "--autoteste");
        if (iAuto >= 0)
        {
            Autoteste(iAuto + 1 < e.Args.Length ? e.Args[iAuto + 1] : null);
            return;
        }

        // Bancada do atualizador (com DSE_TESTE_API e DSE_TESTE_SEM_ADMIN): procura,
        // baixa, confere e arma a troca sem janela; os scripts trocam e reabrem.
        if (e.Args.Contains("--autoteste-atualizacao"))
        {
            AutotesteAtualizacao();
            return;
        }

        DseLog.Write("[app] DSE iniciando");
        bool doWindows = e.Args.Contains(InicioWindows.ArgBandeja);

        // Exceção em THREAD DE FUNDO. Sem este handler, ela derruba o processo
        // na hora e não sobra rastro nenhum — é o clássico "o programa fechou
        // sozinho" que nos custou semanas de tentativa e erro.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                DseLog.Write($"[app] EXCEÇÃO FATAL em thread de fundo (encerrando={args.IsTerminating}): {ex}");
            else
                DseLog.Write($"[app] EXCEÇÃO FATAL não identificada (encerrando={args.IsTerminating})");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DseLog.Write($"[app] exceção não observada em task de fundo: {args.Exception}");
            args.SetObserved();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            var ex = args.Exception;
            var msg = ex.ToString();
            DseLog.Write($"[app] exceção na thread de UI: {ex}");

            bool isViGEmTimingError =
                msg.Contains("0x80004005") ||
                msg.Contains("VirtualController") ||
                msg.Contains("ViGEm");

            if (isViGEmTimingError)
            {
                MessageBox.Show(
                    "Não consegui criar o controle virtual.\n\n" +
                    "Isso quase sempre acontece quando o driver ViGEmBus foi " +
                    "instalado há pouco e o Windows ainda não terminou de " +
                    "carregá-lo.\n\n" +
                    "O que fazer:\n" +
                    "1. Reinicie o computador\n" +
                    "2. Abra o DSE de novo\n\n" +
                    "Se continuar acontecendo depois de reiniciar, reinstale o " +
                    "ViGEmBus.",
                    "DSE", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(
                    $"Ocorreu um erro inesperado:\n\n{ex.Message}",
                    "DSE — Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            args.Handled = true; // o app continua vivo — não morre por uma exceção isolada
        };

        // Instância única. A segunda aberta à mão MOSTRA a janela da primeira;
        // a que vem do Windows (--bandeja) sai quieta.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, NomeMutex, out bool isNew);
        if (!isNew)
        {
            if (!doWindows)
            {
                try
                {
                    using var sinal = EventWaitHandle.OpenExisting(NomeSinalMostrar);
                    sinal.Set();
                }
                catch { /* a primeira ainda está subindo */ }
            }
            Shutdown();
            return;
        }
        _sinalMostrar = new EventWaitHandle(false, EventResetMode.AutoReset, NomeSinalMostrar);
        // "Sair" pedido de fora (o empacotar, para refazer o .exe): fecha como o
        // segurar do X, devolvendo os controles ao Windows antes de sair.
        _sinalSair = new EventWaitHandle(false, EventResetMode.AutoReset, NomeSinalSair);
        var esperaSinal = new Thread(() =>
        {
            var sinais = new WaitHandle[] { _sinalMostrar, _sinalSair };
            while (true)
            {
                if (WaitHandle.WaitAny(sinais) == 0) Dispatcher.BeginInvoke(() => _janela?.Mostrar());
                else Dispatcher.BeginInvoke(RequestExit);
            }
        }) { IsBackground = true, Name = "DSE sinais" };
        esperaSinal.Start();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _settings = new AppSettingsService();
        InicioWindows.Atualizar();
        _watcher = new ControllerWatcher
        {
            AutoDisableBluetoothOnUsb = _settings.Current.AutoDisableBluetoothOnUsb
            // Auto power off (10min idle) é FIXO: usa os defaults do watcher.
        };

        _atalhos = new AtalhosGlobais();
        _atalhos.Disparou += ExecutarAtalho;
        _atalhos.Aplicar(_settings.Current.Atalhos);

        _tray = new TrayIconService(onOpen: () => _janela?.Mostrar(), onToggle: clique => _janela?.Alternar(clique),
            preencherMenu: m => _janela?.PreencherMenuBandeja(m));
        _tray.Initialize();

        // A janela nasce já (escondida ou não): assim ela recebe os eventos
        // desde o início, inclusive de controle desativado.
        var installer = new DependencyInstaller();
        _driversFaltando = !installer.CheckAll();
        _janela = new MainWindow(_watcher, _settings, _atalhos, () => _driversFaltando, AbrirAssistente);

        if (!_driversFaltando)
        {
            _watcher.Start();
            IniciarVigiaAtualizacao();
        }
        AvisarAtualizacaoFeita();

        // Iniciar minimizado / vindo do Windows: direto na bandeja.
        bool naBandeja = doWindows || !_settings.Current.OpenWindowOnStartup;
        if (!naBandeja || _driversFaltando) _janela.Mostrar();
        if (_driversFaltando) AbrirAssistente();
    }

    private void ExecutarAtalho(string acao)
    {
        if (acao == "janela") _janela?.Alternar();
        else _janela?.ExecutarAtalho(acao);
    }

    // ===================================================================
    // Atualizador (padrão scrcpy-f): procura no prazo das opções (todo dia,
    // semana, mês ou nunca) e no botão "Procurar agora". A procura automática
    // NÃO abre pergunta (roubaria o foco do jogo): avisa no balão da bandeja e
    // no botão verde da barra; o clique em qualquer um pergunta Sim/Não.
    // "Não" pula AQUELA versão. Sim = baixa, confere, pede administrador,
    // fecha e reabre sozinho na versão nova.
    // ===================================================================

    private readonly SemaphoreSlim _travaProcura = new(1, 1);
    private UpdateInfo? _versaoNova;
    private bool _vigiaLigada;

    private static readonly Dictionary<string, TimeSpan> Prazos = new()
    {
        ["diario"] = TimeSpan.FromDays(1),
        ["semanal"] = TimeSpan.FromDays(7),
        ["mensal"] = TimeSpan.FromDays(30),
    };

    public string? VersaoNovaDisponivel => _versaoNova?.Version;

    public void AvisarNaBandeja(string texto) => _tray?.ShowBalloon("DSE", texto);

    private void IniciarVigiaAtualizacao()
    {
        if (_vigiaLigada) return;
        _vigiaLigada = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60)); // a abertura primeiro; internet sem pressa
            while (true)
            {
                try
                {
                    var s = _settings!.Current;
                    if (Prazos.TryGetValue(s.ProcurarAtualizacao, out var prazo)
                        && (s.UltimaProcura == null || DateTime.Now - s.UltimaProcura.Value >= prazo))
                        await Dispatcher.InvokeAsync(() => ProcurarAtualizacao(manual: false)).Task.Unwrap();
                }
                catch (Exception ex) { DseLog.Write($"[update] vigia: {DseLog.Fmt(ex)}"); }
                await Task.Delay(TimeSpan.FromHours(1)); // confere o prazo de hora em hora
            }
        });
    }

    /// <summary>Procura (na thread da janela). Devolve o texto curto pra tela de opções.</summary>
    public async Task<string> ProcurarAtualizacao(bool manual)
    {
        if (!await _travaProcura.WaitAsync(0)) return "já estou procurando";
        try
        {
            _settings!.Current.UltimaProcura = DateTime.Now;
            _settings.Save();
            var (info, erro) = await Task.Run(UpdateChecker.UltimaAsync);
            if (info == null) return "não deu para procurar (" + erro + ")";
            if (!UpdateChecker.EhMaisNova(info.Version)) return "em dia";

            _versaoNova = info;
            bool pulada = _settings.Current.PularVersao == info.Version;
            if (manual) return await PerguntarEAtualizar(info);
            if (!pulada)
            {
                _janela?.ShowUpdateAvailable(info.Version);
                _tray?.ShowBalloon("DSE", $"Saiu a versão {info.Version}. Clique para atualizar.",
                    aoClicar: () => _ = AtualizarAgora());
            }
            return $"versão {info.Version} disponível";
        }
        finally
        {
            if (_travaProcura.CurrentCount == 0) _travaProcura.Release();
        }
    }

    /// <summary>Clique no botão verde da barra ou no balão.</summary>
    public async Task<string> AtualizarAgora()
    {
        if (_versaoNova == null) return "";
        if (!await _travaProcura.WaitAsync(0)) return "já estou procurando";
        try { return await PerguntarEAtualizar(_versaoNova); }
        finally { if (_travaProcura.CurrentCount == 0) _travaProcura.Release(); }
    }

    private async Task<string> PerguntarEAtualizar(UpdateInfo info)
    {
        var atual = UpdateChecker.VersaoAtual.ToString(3);
        if (!Atualizador.EhExeDeVerdade())
            return $"versão {info.Version} disponível (só atualiza o .exe)";
        if (info.ZipUrl == null)
        {
            AbrirNoNavegador(info.Url);
            return $"versão {info.Version} disponível (baixe pela página)";
        }

        _janela?.Mostrar();
        var dono = _janela is { IsVisible: true } ? _janela : null;
        var texto = $"Saiu o DSE {info.Version} (você tem o {atual}).\n\n" +
                    "Atualizar agora? O DSE fecha, o Windows pede permissão de administrador " +
                    "e ele abre sozinho na versão nova. Seus perfis, luzes, atalhos e opções ficam como estão.";
        var resp = dono != null
            ? MessageBox.Show(dono, texto, "DSE — atualização", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(texto, "DSE — atualização", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (resp != MessageBoxResult.Yes)
        {
            _settings!.Current.PularVersao = info.Version;
            _settings.Save();
            _janela?.EsconderAtualizacao();
            DseLog.Write($"[update] ele disse não à {info.Version}");
            return $"versão {info.Version} disponível";
        }

        _janela?.MostrarAndamentoAtualizacao("baixando…");
        var (pasta, erro) = await Task.Run(() => UpdateChecker.BaixarAsync(info, (b, t) =>
            Dispatcher.BeginInvoke(() => _janela?.MostrarAndamentoAtualizacao(
                t > 0 ? $"baixando… {b * 100 / t}%" : "baixando…"))));
        if (pasta == null)
        {
            MessageBox.Show($"A atualização não deu certo: {erro}.", "DSE", MessageBoxButton.OK, MessageBoxImage.Warning);
            return "falhou: " + erro;
        }

        _janela?.MostrarAndamentoAtualizacao("pedindo permissão…");
        var (ok, erroTroca) = Atualizador.Trocar(pasta);
        if (!ok) return erroTroca.StartsWith("a permissão") ? "atualização cancelada" : "falhou: " + erroTroca;

        _settings!.Current.PularVersao = null;
        _settings.Save();
        RequestExit();
        return "atualizando…";
    }

    private async void AutotesteAtualizacao()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var linhas = new List<string> { $"[autoteste-atualizacao] {DateTime.Now:yyyy-MM-dd HH:mm:ss} atual {UpdateChecker.VersaoAtual}" };
        try
        {
            var (info, erro) = await UpdateChecker.UltimaAsync();
            linhas.Add(info == null ? "procura: " + erro : $"procura: {info.Version} zip {info.ZipName} nova={UpdateChecker.EhMaisNova(info.Version)}");
            if (info != null && UpdateChecker.EhMaisNova(info.Version))
            {
                var (pasta, erroBaixar) = await UpdateChecker.BaixarAsync(info);
                linhas.Add(pasta == null ? "baixar: " + erroBaixar : "baixar: ok");
                if (pasta != null)
                {
                    var (ok, erroTroca) = Atualizador.Trocar(pasta);
                    linhas.Add(ok ? "trocar: armado" : "trocar: " + erroTroca);
                }
            }
        }
        catch (Exception ex) { linhas.Add("ERRO: " + ex.Message); }
        try
        {
            var rel = Path.Combine(UpdateChecker.PastaTrabalho, "autoteste-atualizacao.txt");
            Directory.CreateDirectory(UpdateChecker.PastaTrabalho);
            File.WriteAllLines(rel, linhas);
        }
        catch { /* sem onde gravar */ }
        Shutdown(0);
    }

    /// <summary>Na abertura: avisa uma vez se a atualização da vez passada deu certo.</summary>
    private void AvisarAtualizacaoFeita()
    {
        var r = Atualizador.ResultadoDaUltima();
        if (r == null) return;
        var v = UpdateChecker.VersaoAtual.ToString(3);
        DseLog.Write($"[update] resultado da atualização: {(r.Length == 0 ? "ok" : r)}");
        if (r.Length == 0) _tray?.ShowBalloon("DSE", $"DSE atualizado para a versão {v}.");
        else MessageBox.Show($"A atualização do DSE não terminou: {r}.\n\nO DSE segue na versão {v}.",
            "DSE", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void AbrirNoNavegador(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { /* sem navegador disponível — nada a fazer */ }
    }

    private SetupWizardWindow? _assistente;

    private void AbrirAssistente()
    {
        if (_assistente != null)
        {
            _assistente.Activate();
            return;
        }
        var installer = new DependencyInstaller();
        _assistente = new SetupWizardWindow(installer, _watcher!);
        _assistente.Closed += (_, _) =>
        {
            _assistente = null;
            // Ao fechar o assistente, revalida: se agora está tudo ok, inicia.
            _driversFaltando = !new DependencyInstaller().CheckAll();
            if (!_driversFaltando && !_watcher!.IsRunning)
            {
                _watcher.Start();
                IniciarVigiaAtualizacao();
            }
            _janela?.Repintar();
        };
        _assistente.Show();
    }

    private void OnExitRequested()
    {
        _watcher?.Dispose();
        _tray?.Dispose();
        Shutdown();
    }

    /// <summary>Fecha o DSE de vez (segurar o X 1 s).</summary>
    public void RequestExit() => OnExitRequested();

    protected override void OnExit(ExitEventArgs e)
    {
        if (_settings != null)
        {
            // Marca o encerramento LIMPO. Se o log terminar sem esta linha, o app
            // morreu de repente — e a causa deve estar nas linhas acima.
            DseLog.Write("[app] DSE encerrando normalmente");
        }
        _atalhos?.Dispose();
        _watcher?.Dispose();
        _tray?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* já pode ter sido liberado */ }
        base.OnExit(e);
    }

    // ===================================================================
    // Autoteste: a janela é montada FORA da tela (nunca aparece na tela dele),
    // com controles de mentira, e cada tela vira um PNG.
    // ===================================================================

    private void Autoteste(string? pastaFotos)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string raiz = AppContext.BaseDirectory;
        string relatorio = Path.Combine(raiz, "relatorios", "autoteste.txt");
        var linhas = new List<string> { $"[autoteste] {DateTime.Now:yyyy-MM-dd HH:mm:ss} DSE {typeof(App).Assembly.GetName().Version}" };
        int codigo = 0;
        try
        {
            // Configuração numa pasta temporária: o teste não mexe na dele.
            var cfg = new AppSettingsService(Path.Combine(Path.GetTempPath(), "dse-autoteste", "appsettings.json"));
            cfg.Current.Atalhos.Clear();
            cfg.Current.Atalhos["janela"] = new AtalhoTeclado { Mods = AtalhosGlobais.ModCtrl | AtalhosGlobais.ModAlt, Vk = 0x44 };
            cfg.Current.JanelaX = cfg.Current.JanelaY = null;
            var watcher = new ControllerWatcher(); // não é iniciado: nada de ViGEm/HidHide
            var atalhos = new AtalhosGlobais();
            var janela = new MainWindow(watcher, cfg, atalhos, () => false, () => { })
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000,
            };
            janela.Show();
            janela.Left = -20000;
            janela.Top = -20000;

            void Foto(string nome)
            {
                // Deixa as animações (chave desliza em 0,15 s) terminarem antes da foto.
                var quadro = new System.Windows.Threading.DispatcherFrame();
                var espera = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                espera.Tick += (_, _) => { espera.Stop(); quadro.Continue = false; };
                espera.Start();
                System.Windows.Threading.Dispatcher.PushFrame(quadro);
                janela.UpdateLayout();
                if (pastaFotos == null) return;
                Directory.CreateDirectory(pastaFotos);
                var alvo = (FrameworkElement)janela.Content;
                var dpi = VisualTreeHelper.GetDpi(janela);
                var bmp = new RenderTargetBitmap((int)(alvo.ActualWidth * dpi.DpiScaleX), (int)(alvo.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bmp.Render(alvo);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var f = File.Create(Path.Combine(pastaFotos, nome + ".png"));
                enc.Save(f);
                linhas.Add($"foto {nome}");
            }

            Foto("1-sem-controle");
            janela.InjetarParaTeste(new ActiveControllerInfo
            {
                Serial = "AA", Type = PhysicalControllerType.DualSense, IsBluetooth = true,
                Profile = EmulationProfile.Xbox360, LedMode = LedMode.Preset
            }, desativado: false);
            Foto("2-dualsense-xbox");
            janela.InjetarParaTeste(new ActiveControllerInfo
            {
                Serial = "BB", Type = PhysicalControllerType.DualShock4, IsBluetooth = false,
                Profile = EmulationProfile.DualShock4, LedMode = LedMode.Preset
            }, desativado: false, selecionar: true);
            Foto("3-dois-controles-ds4");

            // Menu da bandeja montado numa janela fora da tela (o real abre em popup).
            // ContextMenu não entra em janela: os itens vão pra um Menu vertical com a mesma moldura.
            var cm = new System.Windows.Controls.ContextMenu();
            janela.PreencherMenuBandeja(cm);
            var itensMenu = cm.Items.Cast<object>().ToList();
            cm.Items.Clear();
            var host = new System.Windows.Controls.Menu
            {
                Background = Brushes.Transparent,
                ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(
                    new FrameworkElementFactory(typeof(System.Windows.Controls.StackPanel))),
            };
            foreach (var it in itensMenu) host.Items.Add(it);
            var menu = new System.Windows.Controls.Border
            {
                Background = (Brush)FindResource("SuperficieBrush"),
                BorderBrush = (Brush)FindResource("SuperficieBordaBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(5), MinWidth = 230, Child = host,
            };
            var jMenu = new Window
            {
                WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight, Left = -20000, Top = -20000,
                Background = Brushes.Black, Content = menu,
            };
            jMenu.Show();
            jMenu.UpdateLayout();
            if (pastaFotos != null)
            {
                var dpiM = VisualTreeHelper.GetDpi(jMenu);
                var bmpM = new RenderTargetBitmap((int)(menu.ActualWidth * dpiM.DpiScaleX), (int)(menu.ActualHeight * dpiM.DpiScaleY),
                    dpiM.PixelsPerInchX, dpiM.PixelsPerInchY, PixelFormats.Pbgra32);
                bmpM.Render(menu);
                var encM = new PngBitmapEncoder();
                encM.Frames.Add(BitmapFrame.Create(bmpM));
                using var fM = File.Create(Path.Combine(pastaFotos, "3b-menu-bandeja.png"));
                encM.Save(fM);
            }
            linhas.Add($"menu da bandeja: {host.Items.Count} itens");
            jMenu.Close();
            host.Items.Clear();

            // Só na bancada (aparece na tela por 1 s): o ContextMenu DE VERDADE, aberto
            // no canto da bandeja, para conferir posição e cantos.
            if (Environment.GetEnvironmentVariable("DSE_TESTE_MENU_REAL") == "1" && pastaFotos != null)
            {
                var real = new System.Windows.Controls.ContextMenu
                {
                    Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint,
                    HorizontalOffset = SystemParameters.PrimaryScreenWidth - 60,
                    VerticalOffset = SystemParameters.PrimaryScreenHeight - 20,
                };
                janela.PreencherMenuBandeja(real);
                real.IsOpen = true;
                if (real.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(m => m.HasItems) is { } sub)
                    sub.IsSubmenuOpen = true;
                var q = new System.Windows.Threading.DispatcherFrame();
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                t.Tick += (_, _) => { t.Stop(); q.Continue = false; };
                t.Start();
                System.Windows.Threading.Dispatcher.PushFrame(q);
                if (PresentationSource.FromVisual(real) is System.Windows.Interop.HwndSource fonte)
                {
                    var pos = real.PointToScreen(new Point(0, 0));
                    linhas.Add($"menu real: pos {pos}, tam {real.ActualWidth}x{real.ActualHeight}, tela {SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}");
                    int x = (int)pos.X - 280, y = (int)pos.Y - 120, w = (int)real.ActualWidth + 300, h = (int)real.ActualHeight + 140;
                    using var bmpR = new System.Drawing.Bitmap(w, h);
                    using (var g = System.Drawing.Graphics.FromImage(bmpR)) g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                    bmpR.Save(Path.Combine(pastaFotos, "3c-menu-real.png"));
                    _ = fonte;
                }
                real.IsOpen = false;
            }
            janela.InjetarParaTeste(null, desativado: true, selecionar: false);
            Foto("4-desativado");
            janela.IrParaTeste("opcoes");
            Foto("5-opcoes");
            janela.RolarOpcoesParaFimTeste();
            Foto("5b-opcoes-atalhos");
            janela.IrParaTeste("ajuda");
            Foto("6-ajuda");
            janela.IrParaTeste("inicio");
            linhas.Add("ok");
            atalhos.Dispose();
        }
        catch (Exception ex)
        {
            linhas.Add("ERRO: " + ex);
            codigo = 1;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(relatorio)!);
            File.WriteAllLines(relatorio, linhas);
        }
        catch { /* sem onde gravar */ }
        Shutdown(codigo);
    }
}
