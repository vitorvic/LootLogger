using LootLogger.Core.Export;
using LootLogger.Core.Import;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Tests;

public class LootFileTests
{
    private static LootEntry Loot(string by, string itemId, int qty, int second, string from = "XAgiota") => new(
        new DateTime(2026, 10, 6, 21, 56, 0, DateTimeKind.Utc).AddSeconds(second),
        by, "PlVAS", "", 0, itemId, "Item", qty, from, "Inimigos", "", 1000, "Sunfang Cliffs");

    [Fact]
    public void Parse_ReadsWhatTheExporterWrites_AndSkipsDeaths()
    {
        var loot = new[] { Loot("NillBlack", "T6_HEAD_PLATE_SET1@2", 2, 5) };
        var kills = new[] { new KillEntry(loot[0].UtcTime, "XAgiota", "Inimigos", "", "NillBlack", "PlVAS", "", "Sunfang Cliffs") };

        var read = LootFile.Parse("﻿" + CsvExporter.ToCsv(loot, kills));

        var entry = Assert.Single(read);
        Assert.Equal(loot[0] with { ItemNameEnglish = "Item" }, entry);
        Assert.Equal(DateTimeKind.Utc, entry.UtcTime.Kind);
    }

    [Fact]
    public void Parse_ReturnsNothingForOtherText()
    {
        Assert.Empty(LootFile.Parse("Date,Player,Item,Enchantment,Quality,Amount\n\"10/06/2026 21:58:00\",\"NillBlack\",\"Elmo\",\"2\",\"1\",\"1\""));
    }

    [Fact]
    public void Merge_CountsAPickupSeenByTwoLogsOnce()
    {
        var mine = new[] { Loot("Ana", "T6_BAG", 1, 0), Loot("Ana", "T6_BAG", 1, 2), Loot("Bia", "T5_CAPE", 1, 3) };
        // The other PC's clock is 4 s ahead; it also saw Caio's pickup that mine missed.
        var theirs = new[] { Loot("Ana", "T6_BAG", 1, 4), Loot("Bia", "T5_CAPE", 1, 7), Loot("Caio", "T4_MAIN_SWORD", 1, 9) };

        var merged = LootFile.Merge([mine, theirs]);

        Assert.Equal(4, merged.Count);
        Assert.Equal(2, merged.Count(e => e.LootedByName == "Ana"));
        Assert.Single(merged, e => e.LootedByName == "Caio");
    }

    [Fact]
    public void Merge_KeepsRepeatedPickupsInsideOneLog()
    {
        var log = new[] { Loot("Ana", "T6_BAG", 1, 0), Loot("Ana", "T6_BAG", 1, 1) };

        Assert.Equal(2, LootFile.Merge([log]).Count);
    }
}

public class ItemKindTests
{
    [Theory]
    [InlineData("T6_2H_DUALMACE_AVALON@2", LootLogger.Core.Data.ItemKind.Gear)]
    [InlineData("T5_HEAD_PLATE_SET1", LootLogger.Core.Data.ItemKind.Gear)]
    [InlineData("T4_BAG", LootLogger.Core.Data.ItemKind.Bag)]
    [InlineData("T4_CAPEITEM_FW_BRIDGEWATCH", LootLogger.Core.Data.ItemKind.Cape)]
    [InlineData("T5_MOUNT_ARMORED_HORSE", LootLogger.Core.Data.ItemKind.Mount)]
    [InlineData("T6_POTION_HEAL", LootLogger.Core.Data.ItemKind.Consumable)]
    [InlineData("T7_MEAL_STEW@1", LootLogger.Core.Data.ItemKind.Consumable)]
    [InlineData("T4_RUNE", LootLogger.Core.Data.ItemKind.Other)]
    [InlineData("UNIQUE_HIDEOUT", LootLogger.Core.Data.ItemKind.Other)]
    public void Of_GroupsItems(string id, LootLogger.Core.Data.ItemKind kind) => Assert.Equal(kind, LootLogger.Core.Data.ItemKinds.Of(id));

    [Fact]
    public void TierLabel_ShowsTierAndEnchantment()
    {
        Assert.Equal("6.2", LootLogger.Core.Data.ItemKinds.TierLabel("T6_2H_DUALMACE_AVALON@2"));
        Assert.Equal("4.0", LootLogger.Core.Data.ItemKinds.TierLabel("T4_BAG"));
        Assert.Equal("", LootLogger.Core.Data.ItemKinds.TierLabel("UNIQUE_HIDEOUT"));
    }
}
