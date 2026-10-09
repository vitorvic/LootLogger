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

    /// <summary>Prices from the Albion Data Project for items the game never priced.</summary>
    public static string ReservePrices => Path.Combine(DataFolder, "precos-reserva.json");

    /// <summary>Fights kept from the Dano page.</summary>
    public static string SavedFightsFile => Path.Combine(DataFolder, "lutas-salvas.json");

    /// <summary>Hidden copy of each session, written as it happens, in case the program closes mid-fight.</summary>
    public static string BackupFolder => Path.Combine(DataFolder, "backup");

    public static string DefaultExportFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LootLogger", "Sessões");
}
