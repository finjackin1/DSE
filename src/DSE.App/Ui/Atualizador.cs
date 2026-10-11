using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using DSE.Core.Diagnostics;
using DSE.Core.Setup;

namespace DSE.App.Ui;

/// <summary>
/// Troca o próprio DSE pela versão já baixada e extraída (receita do scrcpy-f).
/// Trocar executável pede ADMINISTRADOR (regra dele do antivírus): um script
/// elevado espera este processo fechar e copia os arquivos novos POR CIMA
/// (robocopy sem apagar nada; a config mora em %AppData%, não é tocada).
/// Processo elevado não reabre o app normal, então um segundo script, SEM
/// administrador e escondido, espera o resultado e abre o DSE de novo.
/// Os scripts são .ps1 em UTF-8 com BOM: caminho com acento não quebra (no
/// .bat quebraria). O resultado ("ok" / "erro motivo") é lido na abertura.
/// </summary>
public static class Atualizador
{
    private static string Trabalho => UpdateChecker.PastaTrabalho;
    private static string ArqResultado => Path.Combine(Trabalho, "resultado.txt");

    private static readonly Dictionary<string, string> Motivos = new()
    {
        ["nao-fechou"] = "o DSE demorou a fechar",
        ["copiar"] = "não consegui copiar todos os arquivos novos",
    };

    /// <summary>Rodando de dentro de bin\ (dotnet run): não troca nada.</summary>
    public static bool EhExeDeVerdade()
    {
        var exe = Environment.ProcessPath ?? "";
        return exe.EndsWith("DSE.App.exe", StringComparison.OrdinalIgnoreCase)
               && !exe.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// Pede o administrador e deixa tudo armado. true = o DSE deve fechar já;
    /// false = cancelado/falhou (erro em texto curto).
    /// </summary>
    public static (bool Ok, string Erro) Trocar(string pastaNova)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            var prog = Path.GetDirectoryName(exe)!;
            var log = Path.Combine(Trabalho, "atualizar.txt");
            int pid = Environment.ProcessId;
            File.Delete(ArqResultado);

            var trocar = Path.Combine(Trabalho, "trocar.ps1");
            File.WriteAllText(trocar, string.Join("\r\n",
                "# Gerado pelo DSE: troca o programa pela versao nova. Pode apagar.",
                $"$res = {Q(ArqResultado)}; $log = {Q(log)}",
                $"$novo = {Q(pastaNova)}; $prog = {Q(prog)}",
                "Add-Content $log \"=== $(Get-Date -Format s) atualizar DSE ===\"",
                "$n = 0",
                $"while (Get-Process -Id {pid} -ErrorAction SilentlyContinue) {{",
                "    $n++; if ($n -ge 60) { Set-Content $res 'erro nao-fechou' -Encoding ascii; exit }",
                "    Start-Sleep -Milliseconds 500",
                "}",
                "robocopy $novo $prog /E /R:3 /W:1 /NP /NFL /NDL | Out-File -Append -Encoding utf8 $log",
                "if ($LASTEXITCODE -ge 8) { Set-Content $res 'erro copiar' -Encoding ascii; exit }",
                $"Remove-Item -Recurse -Force {Q(Path.Combine(Trabalho, "novo"))} -ErrorAction SilentlyContinue",
                "Set-Content $res 'ok' -Encoding ascii",
                ""), new UTF8Encoding(true));

            var reabrir = Path.Combine(Trabalho, "reabrir.ps1");
            File.WriteAllText(reabrir, string.Join("\r\n",
                "# Gerado pelo DSE: reabre o programa depois da atualizacao. Pode apagar.",
                $"$res = {Q(ArqResultado)}",
                "$n = 0",
                "while (-not (Test-Path $res) -and $n -lt 360) { $n++; Start-Sleep -Milliseconds 500 }",
                "Start-Sleep -Seconds 1",
                $"Start-Process -FilePath {Q(exe)}",
                ""), new UTF8Encoding(true));

            bool semAdmin = Environment.GetEnvironmentVariable("DSE_TESTE_SEM_ADMIN") == "1"; // só a bancada
            var elevado = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{trocar}\"")
            {
                UseShellExecute = true,
                Verb = semAdmin ? "" : "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try { Process.Start(elevado); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                DseLog.Write("[update] administrador negado");
                return (false, "a permissão de administrador foi negada");
            }

            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{reabrir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            DseLog.Write("[update] troca armada; fechando o DSE");
            return (true, "");
        }
        catch (Exception ex)
        {
            DseLog.Write($"[update] não consegui armar a troca: {DseLog.Fmt(ex)}");
            return (false, "não consegui preparar a atualização");
        }
    }

    /// <summary>
    /// Na abertura: null = não houve atualização; "" = deu certo; texto = o
    /// motivo da falha. Apaga o arquivo (o aviso é dado uma vez só).
    /// </summary>
    public static string? ResultadoDaUltima()
    {
        try
        {
            if (!File.Exists(ArqResultado)) return null;
            var t = File.ReadAllText(ArqResultado).Trim();
            File.Delete(ArqResultado);
            if (t == "ok") return "";
            var motivo = t.StartsWith("erro") ? t[4..].Trim() : t;
            return Motivos.TryGetValue(motivo, out var frase) ? frase : "motivo desconhecido";
        }
        catch
        {
            return null;
        }
    }
}
