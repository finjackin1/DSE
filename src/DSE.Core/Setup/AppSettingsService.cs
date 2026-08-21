using Microsoft.Win32;
using System.Text.Json;

namespace DSE.Core.Setup;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; } = true;

    // Quando um controle já conectado por Bluetooth também é plugado via USB,
    // desliga o Bluetooth automaticamente (evita conexão duplicada e gasto de
    // bateria à toa). Equivalente ao "Auto-Disable BT when connecting to USB"
    // do DS4Windows.
    public bool AutoDisableBluetoothOnUsb { get; set; } = true;

    // Abrir a janela principal em primeiro plano ao iniciar o programa
    // (desligado = inicia só na bandeja). Toggle na barra de título.
    public bool OpenWindowOnStartup { get; set; } = true;

    // OBS: o desligamento por inatividade (10min) é FIXO no watcher, por
    // decisão de design — sem opção de configuração pro usuário.
}

/// <summary>
/// Persiste as configurações gerais do app (flag de primeira execução, etc.)
/// e gerencia o registro de início automático com o Windows via
/// HKCU\...\Run (não exige elevação de administrador).
/// </summary>
public sealed class AppSettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "DSE.App";

    private readonly string _settingsPath;
    public AppSettings Current { get; private set; } = new();

    public AppSettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DSE.App",
            "appsettings.json");

        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Falha ao persistir não deve derrubar o app.
        }
    }

    public void SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (exePath != null)
                    key.SetValue(RunValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            Current.StartWithWindows = enabled;
            Save();
        }
        catch
        {
            // Falha ao registrar não é crítica — usuário pode habilitar manualmente depois.
        }
    }
}
