using System.Text.Json;

namespace LootLogger.Core.Protocol;

/// <summary>
/// Albion event and operation numbers. The game renumbers them in some patches,
/// so they can be overridden by a codes.json file next to the settings without rebuilding.
/// Defaults match AlbionOnline-StatisticsAnalysis as of September 2026.
/// </summary>
public sealed class GameCodes
{
    // Events (parameter 252)
    public short Leave { get; set; } = 1;
    public short HealthUpdate { get; set; } = 6;
    public short HealthUpdates { get; set; } = 7;
    public short NewCharacter { get; set; } = 29;
    public short NewEquipmentItem { get; set; } = 30;
    public short NewSimpleItem { get; set; } = 32;
    public short InventoryPutItem { get; set; } = 26;
    public short InventoryDeleteItem { get; set; } = 27;
    public short CharacterEquipmentChanged { get; set; } = 90;
    public short NewLoot { get; set; } = 98;
    public short AttachItemContainer { get; set; } = 99;
    public short DetachItemContainer { get; set; } = 100;
    public short KilledPlayer { get; set; } = 164;
    public short Died { get; set; } = 165;
    public short PartyJoined { get; set; } = 231;
    public short PartyDisbanded { get; set; } = 232;
    public short PartyPlayerJoined { get; set; } = 233;
    public short PartyPlayerLeft { get; set; } = 235;
    public short InCombatStateUpdate { get; set; } = 278;
    public short OtherGrabbedLoot { get; set; } = 279;
    public short NewLootChest { get; set; } = 393;

    // Operations (parameter 253)
    public short Join { get; set; } = 2;
    public short InventoryMoveItem { get; set; } = 30;
    public short InventoryMoveGivenItems { get; set; } = 39;
    public short ChangeCluster { get; set; } = 41;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GameCodes LoadOrDefault(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<GameCodes>(File.ReadAllText(path)) ?? new GameCodes();
            }
        }
        catch (JsonException)
        {
            // A broken override file falls back to the built-in numbers.
        }

        return new GameCodes();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
