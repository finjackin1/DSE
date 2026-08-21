namespace DSE.Core.Diagnostics;

/// <summary>
/// Log de diagnóstico, DESLIGADO por padrão.
///
/// Só grava alguma coisa se existir um MARCADOR na pasta do executável: uma
/// entrada chamada "log", com ou sem extensão, arquivo ou pasta — `log`,
/// `log.txt`, `log.log` ou uma pasta `log` servem igualmente. Sem marcador,
/// nada é escrito e nenhum arquivo é criado.
///
/// A ideia é que quem só usa o programa não fique com arquivo de log
/// sobrando; quando precisar diagnosticar, basta pedir pra pessoa criar um
/// arquivo vazio chamado "log" ao lado do DSE.App.exe e reproduzir o problema.
///
/// Onde grava: em `dse.log`, ao lado do executável. Exceção: se o próprio
/// marcador for um arquivo terminado em `.log` (ou seja, `log.log`), ele vira
/// o arquivo de log e é sobrescrito.
///
/// O log começa VAZIO a cada execução — misturar sessões diferentes no mesmo
/// arquivo já nos custou tempo de análise no passado.
///
/// Best-effort: jamais lança (logging nunca pode derrubar o app).
/// </summary>
public static class DseLog
{
    private static readonly object _lock = new();
    private static readonly bool _enabled;
    private static readonly string? _path;
    private const long MaxBytes = 1_000_000; // ~1MB: recomeça pra não crescer sem fim

    static DseLog()
    {
        try
        {
            var dir = AppContext.BaseDirectory;

            string? marcador = null;
            bool marcadorEhArquivoLog = false;

            foreach (var entrada in Directory.EnumerateFileSystemEntries(dir))
            {
                // "com ou sem extensão": o que vale é o nome sem a extensão
                // ser exatamente "log". Isso deixa `dse.log` de fora (o nome
                // dele é "dse"), então o log gerado não se auto-habilita.
                if (!string.Equals(Path.GetFileNameWithoutExtension(entrada), "log",
                                   StringComparison.OrdinalIgnoreCase))
                    continue;

                marcador = entrada;
                marcadorEhArquivoLog =
                    File.Exists(entrada) &&
                    string.Equals(Path.GetExtension(entrada), ".log", StringComparison.OrdinalIgnoreCase);

                // Se achamos um `log.log`, ele tem preferência: é nele que
                // vamos escrever. Senão seguimos procurando por um.
                if (marcadorEhArquivoLog) break;
            }

            if (marcador == null)
            {
                _enabled = false;
                return;
            }

            _path = marcadorEhArquivoLog ? marcador : Path.Combine(dir, "dse.log");
            _enabled = true;

            // Começa limpo a cada execução.
            try { File.WriteAllText(_path, string.Empty); } catch { /* best-effort */ }
        }
        catch
        {
            // Qualquer problema ao inspecionar a pasta: log fica desligado.
            _enabled = false;
        }
    }

    public static void Write(string message)
    {
        if (!_enabled || _path == null) return;

        lock (_lock)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                    File.Delete(_path);

                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // best-effort: sem log é melhor que sem app
            }
        }
    }

    /// <summary>Formata uma exceção numa linha (tipo, HResult, mensagem).</summary>
    public static string Fmt(Exception ex) =>
        $"{ex.GetType().Name} (HResult=0x{ex.HResult:X8}): {ex.Message}";

    private static readonly Dictionary<string, (DateTime last, int suprimidas)> _throttle = new();

    /// <summary>
    /// Log para caminhos QUENTES (o read loop roda ~250x por segundo). Grava
    /// no máximo uma vez a cada <paramref name="segundos"/> por chave, e
    /// informa quantas ocorrências foram suprimidas nesse intervalo — assim
    /// dá pra distinguir "aconteceu uma vez" de "está acontecendo direto",
    /// sem encher o arquivo.
    /// </summary>
    public static void WriteThrottled(string chave, string message, int segundos = 10)
    {
        if (!_enabled) return;

        int suprimidas;
        lock (_lock)
        {
            var agora = DateTime.UtcNow;
            if (_throttle.TryGetValue(chave, out var estado))
            {
                if ((agora - estado.last).TotalSeconds < segundos)
                {
                    _throttle[chave] = (estado.last, estado.suprimidas + 1);
                    return;
                }
                suprimidas = estado.suprimidas;
            }
            else
            {
                suprimidas = 0;
            }
            _throttle[chave] = (agora, 0);
        }

        Write(suprimidas > 0
            ? $"{message} [+{suprimidas} ocorrência(s) suprimida(s) desde o último registro]"
            : message);
    }
}
