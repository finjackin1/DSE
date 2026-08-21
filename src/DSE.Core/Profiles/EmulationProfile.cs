namespace DSE.Core.Profiles;

/// <summary>
/// Perfil de emulação: define qual controle virtual é apresentado ao sistema/jogos.
/// </summary>
public enum EmulationProfile
{
    Xbox360,
    DualShock4
}

public static class EmulationProfileExtensions
{
    public static EmulationProfile Toggle(this EmulationProfile current) =>
        current == EmulationProfile.Xbox360
            ? EmulationProfile.DualShock4
            : EmulationProfile.Xbox360;

    public static string DisplayName(this EmulationProfile profile) => profile switch
    {
        EmulationProfile.Xbox360 => "Xbox 360",
        EmulationProfile.DualShock4 => "DualShock 4",
        _ => profile.ToString()
    };

    /// <summary>
    /// Cor fixa da lightbar por perfil, usada no modo "preset" (padrão) —
    /// mesmos valores validados no teste de LED: verde escuro pro perfil
    /// Xbox, azul escuro pro perfil DS4.
    /// </summary>
    public static (byte r, byte g, byte b) GetPresetColor(this EmulationProfile profile) => profile switch
    {
        EmulationProfile.Xbox360 => (0, 20, 0),
        EmulationProfile.DualShock4 => (0, 0, 20),
        _ => (0, 0, 0)
    };
}
