namespace LootLogger.Core.Data;

/// <summary>Rough item groups used to filter the chest check.</summary>
public enum ItemKind
{
    Gear,
    Bag,
    Cape,
    Mount,
    Consumable,
    Other
}

public static class ItemKinds
{
    /// <summary>The group of an item id like "T6_2H_DUALMACE_AVALON@2".</summary>
    public static ItemKind Of(string itemId)
    {
        var name = WithoutTier(itemId);
        if (name.StartsWith("BAG", StringComparison.OrdinalIgnoreCase))
        {
            return ItemKind.Bag;
        }

        if (name.StartsWith("CAPE", StringComparison.OrdinalIgnoreCase))
        {
            return ItemKind.Cape;
        }

        if (name.StartsWith("MOUNT", StringComparison.OrdinalIgnoreCase))
        {
            return ItemKind.Mount;
        }

        if (name.StartsWith("POTION", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MEAL", StringComparison.OrdinalIgnoreCase))
        {
            return ItemKind.Consumable;
        }

        string[] gear = ["MAIN_", "2H_", "OFF_", "HEAD_", "ARMOR_", "SHOES_"];
        return gear.Any(g => name.StartsWith(g, StringComparison.OrdinalIgnoreCase)) ? ItemKind.Gear : ItemKind.Other;
    }

    /// <summary>The tier number (4 for "T4_BAG"), or 0 when the item has none.</summary>
    public static int Tier(string itemId) =>
        itemId.Length >= 3 && itemId[0] == 'T' && char.IsDigit(itemId[1]) && itemId[2] == '_' ? itemId[1] - '0' : 0;

    /// <summary>"6.2" for "T6_..@2", empty for items without a tier.</summary>
    public static string TierLabel(string itemId)
    {
        var tier = Tier(itemId);
        if (tier == 0)
        {
            return string.Empty;
        }

        var at = itemId.IndexOf('@');
        return $"{tier}.{(at >= 0 ? itemId[(at + 1)..] : "0")}";
    }

    private static string WithoutTier(string itemId) => Tier(itemId) > 0 ? itemId[3..] : itemId;
}
