using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;
using DSE.Core.Diagnostics;

namespace DSE.App.Ui;

/// <summary>
/// "Iniciar com o Windows" (padrão da casa): Run do usuário (HKCU, sem admin),
/// abrindo com <c>--bandeja</c>. Sub-opção "Iniciar mais rápido" = tarefa de
/// logon no Agendador (sobe junto com o login, sem o atraso do Run); pede o
/// administrador uma vez. Os dois nunca ficam ligados juntos.
/// </summary>
public static class InicioWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunNome = "DSE.App";
    private const string Tarefa = "DSE - iniciar com o Windows";
    public const string ArgBandeja = "--bandeja";

    private static string Exe => Environment.ProcessPath ?? "";
    private static string ComandoRun => $"\"{Exe}\" {ArgBandeja}";

    public static bool Ativo() => RunValor() != null || Rapido();

    private static string? RunValor()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunNome) as string;
        }
        catch { return null; }
    }

    private static bool EscreverRun(bool ligar)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k == null) return false;
            if (ligar) k.SetValue(RunNome, ComandoRun);
            else k.DeleteValue(RunNome, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            DseLog.Write($"[inicio] falha ao gravar o Run: {DseLog.Fmt(ex)}");
            return false;
        }
    }

    /// <summary>Na abertura: o Run aponta pro .exe atual e com --bandeja (versão antiga gravava sem).</summary>
    public static void Atualizar()
    {
        var v = RunValor();
        if (v != null && !string.Equals(v, ComandoRun, StringComparison.OrdinalIgnoreCase))
            EscreverRun(true);
    }

    public static bool Definir(bool ligar)
    {
        if (ligar) return EscreverRun(true);
        bool ok = EscreverRun(false);
        if (Rapido()) ok &= ApagarTarefa();
        return ok;
    }

    public static bool Rapido()
    {
        var (codigo, _) = Rodar("schtasks.exe", $"/Query /TN \"{Tarefa}\"", elevado: false);
        return codigo == 0;
    }

    public static bool DefinirRapido(bool ligar)
    {
        if (ligar)
        {
            if (!CriarTarefa()) return false;
            EscreverRun(false); // nunca os dois juntos
            return true;
        }
        if (!ApagarTarefa()) return false;
        return EscreverRun(true);
    }

    private static bool CriarTarefa()
    {
        // XML: dispara SÓ no logon deste usuário, sem pedir senha
        // (InteractiveToken), sem limite de tempo e mesmo na bateria.
        string usuario = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(usuario)}</UserId></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(usuario)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(Exe)}</Command>
                  <Arguments>{ArgBandeja}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        string arq = Path.Combine(Path.GetTempPath(), "dse-tarefa.xml");
        try
        {
            File.WriteAllText(arq, xml, System.Text.Encoding.Unicode);
            var (codigo, erro) = Rodar("schtasks.exe", $"/Create /F /TN \"{Tarefa}\" /XML \"{arq}\"", elevado: true);
            if (codigo != 0) DseLog.Write($"[inicio] criar tarefa falhou ({codigo}) {erro}");
            return codigo == 0 && Rapido();
        }
        finally
        {
            try { File.Delete(arq); } catch { /* ignora */ }
        }
    }

    private static bool ApagarTarefa()
    {
        var (codigo, erro) = Rodar("schtasks.exe", $"/Delete /F /TN \"{Tarefa}\"", elevado: true);
        if (codigo != 0) DseLog.Write($"[inicio] apagar tarefa falhou ({codigo}) {erro}");
        return !Rapido();
    }

    private static (int codigo, string erro) Rodar(string exe, string args, bool elevado)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevado)
            {
                psi.UseShellExecute = true;
                psi.Verb = "runas";
            }
            else
            {
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
            }
            using var p = Process.Start(psi);
            if (p == null) return (-1, "não iniciou");
            string erro = elevado ? "" : p.StandardError.ReadToEnd();
            if (!elevado) p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return (p.HasExited ? p.ExitCode : -1, erro.Trim());
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (-2, "administrador recusado");
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
