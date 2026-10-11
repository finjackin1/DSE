using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DSE.Core.Diagnostics;

namespace DSE.Core.Setup;

/// <summary>A última versão publicada no GitHub e o zip do Windows dela.</summary>
public sealed class UpdateInfo
{
    public required string Version { get; init; }      // "1.2.0" (sem o "v")
    public required string Url { get; init; }          // página da release
    public string? ZipUrl { get; init; }               // null = release sem o zip do DSE
    public string? ZipName { get; init; }
    public long ZipSize { get; init; }
    public string? Sha256 { get; init; }               // o "digest" que a API publica
}

/// <summary>
/// Atualizador pelo GitHub (receita do scrcpy-f): pergunta a última release,
/// baixa o zip `DSE-&lt;versão&gt;-windows.zip` SEM pedir nada, confere tamanho e
/// SHA-256 e extrai numa pasta de trabalho. A troca dos arquivos (que pede
/// administrador) fica no app, em Ui\Atualizador.cs.
///
/// Nunca atrapalha o uso: qualquer falha vira texto curto, sem exceção.
/// DSE_TESTE_API troca o endereço por um arquivo local (file:///…), só pra
/// bancada de teste, sem publicar nada.
/// </summary>
public static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/finjackin1/DSE/releases/latest";
    private const string PaginaReleases = "https://github.com/finjackin1/DSE/releases/latest";
    private static readonly Regex PadraoZip = new(@"^DSE-[\d.]+-windows\.zip$", RegexOptions.IgnoreCase);

    public static string PastaTrabalho => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSE.App", "atualizacao");

    public static Version VersaoAtual
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>"1.2" == "1.2.0"; texto inválido = 0.0.0.</summary>
    public static Version Numeros(string v)
    {
        var n = Regex.Matches(v ?? "", @"\d+").Take(3).Select(m => int.Parse(m.Value)).ToList();
        while (n.Count < 3) n.Add(0);
        return new Version(n[0], n[1], n[2]);
    }

    public static bool EhMaisNova(string versao) => Numeros(versao) > VersaoAtual;

    private static HttpClient NovoHttp(int segundos)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(segundos) };
        http.DefaultRequestHeaders.Add("User-Agent", "DSE-Atualizador"); // a API recusa sem
        return http;
    }

    private static bool EhArquivo(string url) => url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    /// <summary>(info, erro). info null = não deu para perguntar.</summary>
    public static async Task<(UpdateInfo? Info, string Erro)> UltimaAsync()
    {
        try
        {
            var url = Environment.GetEnvironmentVariable("DSE_TESTE_API") ?? ApiUrl;
            string json;
            if (EhArquivo(url)) json = await File.ReadAllTextAsync(new Uri(url).LocalPath);
            else
            {
                using var http = NovoHttp(20);
                http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                json = await http.GetStringAsync(url);
            }

            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;
            var tag = raiz.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (tag.Length == 0) return (null, "resposta do GitHub sem versão");
            var pagina = raiz.TryGetProperty("html_url", out var h) ? h.GetString() ?? PaginaReleases : PaginaReleases;

            string? zipUrl = null, zipNome = null, sha = null;
            long tam = 0;
            if (raiz.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var nome = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!PadraoZip.IsMatch(nome)) continue;
                    zipNome = nome;
                    zipUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    tam = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                    var dg = a.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                    sha = dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? dg[7..] : null;
                    break;
                }
            }

            var info = new UpdateInfo
            {
                Version = tag.TrimStart('v', 'V'),
                Url = pagina,
                ZipUrl = zipUrl,
                ZipName = zipNome,
                ZipSize = tam,
                Sha256 = sha,
            };
            DseLog.Write($"[update] publicada {info.Version} (atual {VersaoAtual}), zip: {zipNome ?? "nenhum"}");
            return (info, "");
        }
        catch (Exception ex)
        {
            DseLog.Write($"[update] procura falhou: {DseLog.Fmt(ex)}");
            return (null, "sem resposta do GitHub");
        }
    }

    /// <summary>
    /// Baixa, confere e extrai. Devolve (pasta com o DSE.App.exe novo, erro).
    /// `progresso(baixado, total)` é chamado de fora da thread da janela.
    /// </summary>
    public static async Task<(string? Pasta, string Erro)> BaixarAsync(UpdateInfo info, Action<long, long>? progresso = null)
    {
        if (info.ZipUrl == null || info.ZipName == null) return (null, "a versão nova não tem o zip do Windows");
        var trabalho = PastaTrabalho;
        var destino = Path.Combine(trabalho, info.ZipName);
        var parcial = destino + ".parcial";
        try
        {
            Directory.CreateDirectory(trabalho);
            long baixado = 0;
            using (var sha = SHA256.Create())
            {
                await using (var saida = File.Create(parcial))
                {
                    Stream entrada;
                    HttpClient? http = null;
                    HttpResponseMessage? resp = null;
                    if (EhArquivo(info.ZipUrl)) entrada = File.OpenRead(new Uri(info.ZipUrl).LocalPath);
                    else
                    {
                        http = NovoHttp(60);
                        resp = await http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead);
                        resp.EnsureSuccessStatusCode();
                        entrada = await resp.Content.ReadAsStreamAsync();
                    }
                    try
                    {
                        var buf = new byte[256 * 1024];
                        int lidos;
                        var ultimo = DateTime.MinValue;
                        while ((lidos = await entrada.ReadAsync(buf)) > 0)
                        {
                            await saida.WriteAsync(buf.AsMemory(0, lidos));
                            sha.TransformBlock(buf, 0, lidos, null, 0);
                            baixado += lidos;
                            if (progresso != null && (DateTime.Now - ultimo).TotalMilliseconds > 200)
                            {
                                ultimo = DateTime.Now;
                                progresso(baixado, info.ZipSize);
                            }
                        }
                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    }
                    finally
                    {
                        entrada.Dispose();
                        resp?.Dispose();
                        http?.Dispose();
                    }
                }

                if (info.ZipSize > 0 && baixado != info.ZipSize)
                {
                    Apagar(parcial);
                    return (null, "o arquivo chegou incompleto");
                }
                var soma = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (!string.IsNullOrEmpty(info.Sha256) && soma != info.Sha256.ToLowerInvariant())
                {
                    Apagar(parcial);
                    return (null, "o arquivo chegou diferente do publicado");
                }
            }
            Apagar(destino);
            File.Move(parcial, destino);

            // Extrai já (sem administrador): a parte elevada só copia.
            var novo = Path.Combine(trabalho, "novo");
            if (Directory.Exists(novo)) Directory.Delete(novo, recursive: true);
            ZipFile.ExtractToDirectory(destino, novo);
            Apagar(destino);
            var pasta = File.Exists(Path.Combine(novo, "DSE.App.exe"))
                ? novo
                : Directory.GetDirectories(novo).FirstOrDefault(d => File.Exists(Path.Combine(d, "DSE.App.exe")));
            if (pasta == null) return (null, "o zip não tinha o DSE dentro");
            DseLog.Write($"[update] {info.ZipName} baixado e conferido ({baixado} bytes)");
            return (pasta, "");
        }
        catch (Exception ex)
        {
            Apagar(parcial);
            DseLog.Write($"[update] download falhou: {DseLog.Fmt(ex)}");
            return (null, "o download falhou");
        }
    }

    private static void Apagar(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* fica pra próxima */ }
    }
}
