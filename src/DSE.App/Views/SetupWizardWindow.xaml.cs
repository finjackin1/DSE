using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DSE.Core.Controllers;
using DSE.Core.Setup;

namespace DSE.App.Views;

/// <summary>
/// Assistente de dependências faltantes. Só aparece quando o verificador
/// (a cada execução) encontra ViGEmBus ou HidHide faltando.
///
/// Modelo híbrido: o botão "Instalar" tenta baixar e rodar o instalador
/// oficial automaticamente. Se falhar (sem internet, antivírus), a linha
/// troca pra um botão "Baixar manualmente" que abre a página oficial.
/// Um botão geral "Já instalei — verificar de novo" revalida tudo.
/// </summary>
public partial class SetupWizardWindow : Window
{
    private readonly DependencyInstaller _installer;
    private readonly ControllerWatcher _watcher;

    public SetupWizardWindow(DependencyInstaller installer, ControllerWatcher watcher)
    {
        InitializeComponent();
        _installer = installer;
        _watcher = watcher;

        RenderMissing();
    }

    private void RenderMissing()
    {
        DependencyList.Children.Clear();
        foreach (var dep in _installer.GetMissing())
        {
            DependencyList.Children.Add(BuildDependencyRow(dep));
        }
    }

    private Border BuildDependencyRow(DependencyState dep)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = dep.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = (Brush)FindResource("TextBrush")
        });
        var statusText = new TextBlock
        {
            Text = "não instalado",
            FontSize = 11,
            Foreground = (Brush)FindResource("DisconnectedBrush")
        };
        info.Children.Add(statusText);
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var btn = new Button
        {
            Content = "Instalar",
            Style = (Style)FindResource("PrimaryButton"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 130
        };
        btn.Click += async (_, _) => await OnInstallClick(dep, btn, statusText);
        Grid.SetColumn(btn, 1);
        grid.Children.Add(btn);

        return new Border
        {
            Background = (Brush)FindResource("Surface2Brush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    private async Task OnInstallClick(DependencyState dep, Button btn, TextBlock statusText)
    {
        // Se já estamos em modo "manual" (tentativa automática falhou antes),
        // esse clique abre a página oficial em vez de tentar baixar de novo.
        if (btn.Tag as string == "manual")
        {
            OpenPage(dep.DownloadPageUrl);
            return;
        }

        btn.IsEnabled = false;
        btn.Content = "Baixando...";
        statusText.Text = "baixando o instalador oficial...";
        statusText.Foreground = (Brush)FindResource("AccentBrush");

        bool ok = await _installer.TryDownloadAndRunInstallerAsync(dep);

        if (ok)
        {
            statusText.Text = "instalador aberto — conclua, e REINICIE o PC ao terminar";
            statusText.Foreground = (Brush)FindResource("ConnectedBrush");
            btn.Content = "Instalador aberto";
            // Fica desabilitado; o usuário conclui e usa "verificar de novo".
        }
        else
        {
            // Plano B: próximo clique abre a página oficial (via Tag "manual").
            statusText.Text = "download automático falhou — baixe manualmente";
            statusText.Foreground = (Brush)FindResource("DisconnectedBrush");
            btn.Content = "Baixar manualmente";
            btn.Tag = "manual";
            btn.IsEnabled = true;
        }
    }

    private void OpenPage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show($"Não consegui abrir o navegador. Baixe manualmente em:\n\n{url}",
                "DSE", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnRecheckClick(object sender, RoutedEventArgs e)
    {
        if (_installer.CheckAll())
        {
            if (!_watcher.IsRunning)
                _watcher.Start();
            Close();
        }
        else
        {
            RenderMissing();
            MessageBox.Show("Ainda falta alguma dependência. Conclua a instalação e verifique de novo.",
                "DSE", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
