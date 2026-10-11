using System.Text.Json;
using DSE.Core.Controllers;

namespace DSE.Core.Profiles;

/// <summary>
/// Persiste e recupera o perfil de emulação ativo por controle físico,
/// usando o serial/MAC do dispositivo como chave. Sobrevive a reconexões
/// e a reinícios do aplicativo.
/// </summary>
public sealed class ProfileManager
{
    private readonly string _storePath;
    private readonly object _lock = new();
    private Dictionary<string, EmulationProfile> _profiles = new();
    private Dictionary<string, LedMode> _leds = new();

    public EmulationProfile DefaultProfile { get; set; } = EmulationProfile.Xbox360;

    public ProfileManager(string? storePath = null)
    {
        _storePath = storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DSE.App",
            "profiles.json");

        Load();
    }

    /// <summary>
    /// Retorna o perfil salvo para o controle (por serial), ou o perfil
    /// padrão caso seja a primeira vez que esse controle é visto.
    /// </summary>
    public EmulationProfile GetProfile(string controllerSerial)
    {
        lock (_lock)
        {
            return _profiles.TryGetValue(controllerSerial, out var profile)
                ? profile
                : DefaultProfile;
        }
    }

    public void SetProfile(string controllerSerial, EmulationProfile profile)
    {
        lock (_lock)
        {
            _profiles[controllerSerial] = profile;
            Save();
        }
    }

    public EmulationProfile ToggleProfile(string controllerSerial)
    {
        var next = GetProfile(controllerSerial).Toggle();
        SetProfile(controllerSerial, next);
        return next;
    }

    /// <summary>
    /// Modo de luz salvo pro controle (Preset na primeira vez). Fica em
    /// leds.json, ao lado do profiles.json.
    /// </summary>
    public LedMode GetLedMode(string controllerSerial)
    {
        lock (_lock)
        {
            return _leds.TryGetValue(controllerSerial, out var mode) ? mode : LedMode.Preset;
        }
    }

    public void SetLedMode(string controllerSerial, LedMode mode)
    {
        lock (_lock)
        {
            if (_leds.TryGetValue(controllerSerial, out var atual) && atual == mode) return;
            _leds[controllerSerial] = mode;
            SaveFile(LedsPath, _leds);
        }
    }

    private string LedsPath => Path.Combine(Path.GetDirectoryName(_storePath)!, "leds.json");

    private void Load()
    {
        _profiles = LoadFile<EmulationProfile>(_storePath);
        _leds = LoadFile<LedMode>(LedsPath);
    }

    private static Dictionary<string, T> LoadFile<T>(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, T>();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, T>>(json)
                   ?? new Dictionary<string, T>();
        }
        catch
        {
            // Arquivo corrompido ou inacessível: segue com dicionário vazio,
            // não deve derrubar o app por causa de persistência.
            return new Dictionary<string, T>();
        }
    }

    private void Save() => SaveFile(_storePath, _profiles);

    private static void SaveFile<T>(string path, Dictionary<string, T> data)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Falha ao salvar não deve interromper o fluxo de troca de perfil em memória.
        }
    }
}
