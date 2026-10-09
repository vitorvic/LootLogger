using System.IO;
using System.Text.Json;

namespace LootLogger.App.Services;

/// <summary>User choices, saved as JSON in %AppData%\LootLogger.</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "pt-BR";
    public bool PartyOnly { get; set; }
    public string ExportFolder { get; set; } = AppPaths.DefaultExportFolder;
    public bool DamageResetOnMap { get; set; } = true;
    public bool DamageResetBeforeCombat { get; set; }
    public bool DamageSaveBeforeReset { get; set; } = true;
    public bool DamageCopyShort { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Options));
    }
}
