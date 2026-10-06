using System.IO;

namespace LootLogger.App.Services;

public static class AppPaths
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LootLogger");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    /// <summary>Optional override for event numbers after a game patch.</summary>
    public static string CodesFile => Path.Combine(DataFolder, "codes.json");

    public static string ItemsCache => Path.Combine(DataFolder, "items.tsv");

    public static string MarketValues => Path.Combine(DataFolder, "market-values.json");

    public static string DefaultExportFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LootLogger", "Sessões");
}
