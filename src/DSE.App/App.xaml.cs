using System.Windows;
using DSE.Core.Controllers;
using DSE.Core.Diagnostics;
using DSE.Core.Setup;
using DSE.App.Tray;
using DSE.App.Views;

namespace DSE.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private ControllerWatcher? _watcher;
    private TrayIconService? _tray;
    private AppSettingsService? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DseLog.Write("[app] DSE iniciando");

        // Exceção em THREAD DE FUNDO. Sem este handler, ela derruba o processo
        // na hora e não sobra rastro nenhum — é o clássico "o programa fechou
        // sozinho" que nos custou semanas de tentativa e erro. Não dá pra
        // impedir o encerramento aqui (o .NET já decidiu morrer), mas dá pra
        // registrar o motivo antes.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                DseLog.Write($"[app] EXCEÇÃO FATAL em thread de fundo (encerrando={args.IsTerminating}): {ex}");
            else
                DseLog.Write($"[app] EXCEÇÃO FATAL não identificada (encerrando={args.IsTerminating})");
        };

        // Task de fundo que falhou e cuja exceção ninguém observou. Hoje isso
        // some sem deixar rastro: o loop de LED, o piscar de bateria e o
        // unhide agendado rodam assim.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DseLog.Write($"[app] exceção não observada em task de fundo: {args.Exception}");
            args.SetObserved(); // já registramos; não deixa derrubar o processo
        };

        DispatcherUnhandledException += (_, args) =>
        {
            var ex = args.Exception;
            var msg = ex.ToString();

            DseLog.Write($"[app] exceção na thread de UI: {ex}");

            // Erro conhecido do ViGEmBus: driver recém-instalado ou não
            // totalmente inicializado (0x80004005). Mensagem amigável e
            // acionável em vez do stack trace técnico.
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

        _singleInstanceMutex = new Mutex(initiallyOwned: true, "DSE.App.SingleInstance.Mutex", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("O DSE já está em execução (verifique a bandeja do sistema).",
                "DSE", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _settings = new AppSettingsService();
        _watcher = new ControllerWatcher
        {
            AutoDisableBluetoothOnUsb = _settings.Current.AutoDisableBluetoothOnUsb
            // Auto power off (10min idle) é FIXO: usa os defaults do watcher.
        };

        _tray = new TrayIconService(OnOpenRequested);
        _tray.Initialize();

        // Verifica as dependências A CADA execução. Se faltar alguma, mostra
        // o assistente informando qual falta (com link pro download). Se
        // estiver tudo instalado, inicia normalmente.
        var installer = new DependencyInstaller();
        if (installer.CheckAll())
        {
            _watcher.Start();

            // Abre a janela em primeiro plano, se o usuário deixou ligado
            // (toggle na barra de título). Desligado = fica só na bandeja.
            if (_settings.Current.OpenWindowOnStartup)
                OnOpenRequested();

            CheckForUpdateAsync();
        }
        else
        {
            ShowDependencyWizard(installer);
        }
    }

    /// <summary>
    /// Consulta a página de releases uma vez, na abertura, e avisa se saiu
    /// versão nova. Avisa nos DOIS lugares de propósito: a faixa na janela
    /// alcança quem abre o programa, o balão da bandeja alcança quem deixa o
    /// DSE minimizado e nunca olharia a janela.
    ///
    /// Nada aqui pode atrapalhar o uso: se não houver internet, se o GitHub
    /// estiver fora ou se a resposta vier estranha, a checagem simplesmente
    /// não produz aviso nenhum.
    /// </summary>
    private void CheckForUpdateAsync()
    {
        _ = Task.Run(async () =>
        {
            var info = await UpdateChecker.CheckAsync();
            if (info == null) return;

            Dispatcher.Invoke(() =>
            {
                _tray?.ShowBalloon("DSE", $"Nova versão disponível: {info.Version}",
                    aoClicar: () => AbrirNoNavegador(info.Url));

                var janela = Windows.OfType<MainWindow>().FirstOrDefault();
                janela?.ShowUpdateAvailable(info.Version, info.Url);
            });
        });
    }

    private static void AbrirNoNavegador(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { /* sem navegador disponível — nada a fazer */ }
    }

    private void ShowDependencyWizard(DependencyInstaller installer)
    {
        var wizard = new SetupWizardWindow(installer, _watcher!);
        wizard.Closed += (_, _) =>
        {
            // Ao fechar o assistente, revalida: se agora está tudo ok, inicia.
            if (!_watcher!.IsRunning && new DependencyInstaller().CheckAll())
                _watcher.Start();
        };
        wizard.Show();
    }

    private void OnOpenRequested()
    {
        var existing = Windows.OfType<MainWindow>().FirstOrDefault();
        if (existing != null)
        {
            existing.Show();
            existing.Activate();
            existing.WindowState = WindowState.Normal;
            return;
        }

        var main = new MainWindow(_watcher!, _settings!);
        main.Show();
    }

    private void OnExitRequested()
    {
        _watcher?.Dispose();
        _tray?.Dispose();
        Shutdown();
    }

    /// <summary>
    /// Fecha o DSE de vez (não minimiza). Usado pelo botão "Fechar o
    /// programa" na janela principal, além do item "Sair" da bandeja.
    /// </summary>
    public void RequestExit() => OnExitRequested();

    protected override void OnExit(ExitEventArgs e)
    {
        // Marca o encerramento LIMPO. Se o log terminar sem esta linha, o app
        // morreu de repente — e a causa deve estar nas linhas acima.
        DseLog.Write("[app] DSE encerrando normalmente");
        _watcher?.Dispose();
        _tray?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* já pode ter sido liberado */ }
        base.OnExit(e);
    }
}
