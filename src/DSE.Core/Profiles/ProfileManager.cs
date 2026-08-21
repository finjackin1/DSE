using System.Text.Json;

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

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath))
                return;

            var json = File.ReadAllText(_storePath);
            _profiles = JsonSerializer.Deserialize<Dictionary<string, EmulationProfile>>(json)
                        ?? new Dictionary<string, EmulationProfile>();
        }
        catch
        {
            // Arquivo corrompido ou inacessível: segue com dicionário vazio,
            // não deve derrubar o app por causa de persistência de perfil.
            _profiles = new Dictionary<string, EmulationProfile>();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storePath, json);
        }
        catch
        {
            // Falha ao salvar não deve interromper o fluxo de troca de perfil em memória.
        }
    }
}
