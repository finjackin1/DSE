using System.Diagnostics;
using System.Net.Http;
using System.ServiceProcess;
using DSE.Core.HidHide;

namespace DSE.Core.Setup;

public enum DependencyStatus
{
    NotChecked,
    Installed,
    Missing
}

public sealed class DependencyState
{
    public required string Name { get; init; }
    public required string InstallerUrl { get; init; }   // download direto (auto)
    public required string DownloadPageUrl { get; init; } // página oficial (fallback manual)
    public DependencyStatus Status { get; set; } = DependencyStatus.NotChecked;
}

/// <summary>
/// Verificador + instalador de dependências. Roda a CADA execução: se
/// ViGEmBus e HidHide estiverem instalados, o app inicia normal; se faltar
/// algum, o assistente aparece.
///
/// Modelo HÍBRIDO de instalação: tenta baixar e abrir o instalador oficial
/// automaticamente (URLs fixas das versões atuais); se qualquer coisa
/// falhar (sem internet, antivírus, SmartScreen), o assistente oferece o
/// botão de download manual da página oficial como plano B.
/// </summary>
public sealed class DependencyInstaller
{
    // URLs fixas das versões atuais (releases oficiais da Nefarius).
    private const string ViGEmBusInstallerUrl =
        "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe";
    private const string ViGEmBusDownloadPage = "https://github.com/nefarius/ViGEmBus/releases/latest";

    private const string HidHideInstallerUrl =
        "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe";
    private const string HidHideDownloadPage = "https://github.com/nefarius/HidHide/releases/latest";

    public DependencyState ViGEmBusState { get; } = new()
    {
        Name = "ViGEmBus Driver",
        InstallerUrl = ViGEmBusInstallerUrl,
        DownloadPageUrl = ViGEmBusDownloadPage
    };

    public DependencyState HidHideState { get; } = new()
    {
        Name = "HidHide Driver",
        InstallerUrl = HidHideInstallerUrl,
        DownloadPageUrl = HidHideDownloadPage
    };

    /// <summary>Verifica os dois drivers. Retorna true se TODOS instalados.</summary>
    public bool CheckAll()
    {
        CheckViGEmBus();
        CheckHidHide();
        return ViGEmBusState.Status == DependencyStatus.Installed
            && HidHideState.Status == DependencyStatus.Installed;
    }

    public IEnumerable<DependencyState> GetMissing()
    {
        if (ViGEmBusState.Status == DependencyStatus.Missing) yield return ViGEmBusState;
        if (HidHideState.Status == DependencyStatus.Missing) yield return HidHideState;
    }

    /// <summary>
    /// Baixa o instalador da dependência e o executa (o usuário passa pelo
    /// UAC e pela UI do instalador oficial). Retorna true se conseguiu ao
    /// menos INICIAR o instalador; false se o download/execução falhou (aí o
    /// assistente cai no download manual).
    /// </summary>
    public async Task<bool> TryDownloadAndRunInstallerAsync(DependencyState dep)
    {
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(),
                $"DSE_{dep.Name.Replace(" ", "")}_{Guid.NewGuid():N}.exe");

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromMinutes(3);
                var bytes = await http.GetByteArrayAsync(dep.InstallerUrl);
                await File.WriteAllBytesAsync(tempPath, bytes);
            }

            // Abre o instalador. UseShellExecute=true deixa o Windows lidar
            // com o prompt de elevação (UAC) do instalador assinado.
            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true
            });

            return true;
        }
        catch
        {
            // Qualquer falha (download, antivírus, execução) -> plano B manual.
            return false;
        }
    }

    private void CheckViGEmBus()
    {
        try
        {
            using var sc = new ServiceController("ViGEmBus");
            var _ = sc.Status;
            ViGEmBusState.Status = DependencyStatus.Installed;
        }
        catch
        {
            ViGEmBusState.Status = DependencyStatus.Missing;
        }
    }

    private void CheckHidHide()
    {
        try
        {
            var hidHide = new HidHideService();
            HidHideState.Status = hidHide.IsAvailable ? DependencyStatus.Installed : DependencyStatus.Missing;
        }
        catch
        {
            HidHideState.Status = DependencyStatus.Missing;
        }
    }
}
