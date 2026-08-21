using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using DSE.Core.Diagnostics;

namespace DSE.Core.Setup;

/// <summary>Resultado da checagem: versão encontrada e onde baixá-la.</summary>
public sealed class UpdateInfo
{
    public required string Version { get; init; }
    public required string Url { get; init; }
}

/// <summary>
/// Verifica se saiu versão nova, consultando a página de releases do projeto
/// no GitHub. Como o DSE é distribuído em zip, sem instalador, quem baixou não
/// tem como saber que atualizou — é o que isto resolve.
///
/// Falha SEMPRE em silêncio: sem internet, com o GitHub fora do ar ou com
/// resposta inesperada, o programa segue como se nada tivesse acontecido.
/// Aviso de atualização nunca pode atrapalhar o uso.
/// </summary>
public static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/finjackin1/DSE/releases/latest";
    private const string PaginaReleases = "https://github.com/finjackin1/DSE/releases/latest";

    /// <summary>
    /// Retorna a versão nova se houver uma maior que a atual; null caso
    /// contrário (inclusive em qualquer erro).
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var atual = Assembly.GetEntryAssembly()?.GetName().Version;
            if (atual == null) return null;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // A API do GitHub recusa requisições sem User-Agent.
            http.DefaultRequestHeaders.Add("User-Agent", "DSE-UpdateChecker");

            var json = await http.GetStringAsync(ApiUrl);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)) return null;
            var tag = tagEl.GetString();
            if (string.IsNullOrWhiteSpace(tag)) return null;

            // As tags são no formato "v1.0.0"; o "v" não faz parte do número.
            var numero = tag.TrimStart('v', 'V');
            if (!Version.TryParse(numero, out var publicada)) return null;

            // Compara só os três primeiros componentes: o Assembly sempre tem
            // quatro (1.0.0.0) e a tag costuma ter três, o que faria uma
            // comparação direta acusar diferença onde não há.
            var a = new Version(atual.Major, atual.Minor, Math.Max(atual.Build, 0));
            var b = new Version(publicada.Major, publicada.Minor, Math.Max(publicada.Build, 0));
            if (b <= a) return null;

            var url = doc.RootElement.TryGetProperty("html_url", out var urlEl)
                ? urlEl.GetString() ?? PaginaReleases
                : PaginaReleases;

            DseLog.Write($"[update] versão nova disponível: {tag} (atual: {a})");
            return new UpdateInfo { Version = tag, Url = url };
        }
        catch (Exception ex)
        {
            DseLog.Write($"[update] checagem falhou (ignorado): {DseLog.Fmt(ex)}");
            return null;
        }
    }
}
