using LootLogger.Core.Export;
using LootLogger.Core.Import;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Tests;

public class LootFileTests
{
    private static LootEntry Loot(string by, string itemId, int qty, double second, string from = "XAgiota") => new(
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
    public void ParseAll_ReadsDeaths()
    {
        var kill = new KillEntry(new DateTime(2026, 10, 6, 22, 14, 0, DateTimeKind.Utc), "Ana", "PlVAS", "", "faca02", "Outlimits", "", "Sunfang Cliffs");

        var (_, kills) = LootFile.ParseAll(CsvExporter.ToCsv([], [kill]));

        Assert.Equal(kill, Assert.Single(kills));
    }

    [Fact]
    public void Compare_ItemsCarriedWhenDyingAreLostNotMissing()
    {
        var items = LootLogger.Core.Data.ItemDatabase.LoadBuiltIn();
        // Picked a bag, died, then picked a cape and a sword; only the sword reached the chest.
        var loot = new[] { Loot("Ana", "T6_BAG", 1, 0), Loot("Ana", "T5_CAPE", 1, 60), Loot("Ana", "T4_MAIN_SWORD", 1, 61) };
        var death = new KillEntry(loot[0].UtcTime.AddSeconds(30), "Ana", "PlVAS", "", "faca02", "Outlimits", "", "Sunfang Cliffs");
        var sword = items.GetByUniqueName("T4_MAIN_SWORD")!;
        var chest = new[] { new LootLogger.Core.Chest.ChestLogEntry(loot[2].UtcTime.AddMinutes(5), "Ana", sword.EnglishName, 0, 1, 1) };

        var ana = Assert.Single(LootLogger.Core.Chest.ChestComparer.Compare(loot, chest, items, [death]));

        Assert.Equal("T6_BAG", Assert.Single(ana.LostOnDeath).ItemId);
        Assert.Equal("T5_CAPE", Assert.Single(ana.Missing).ItemId);
        Assert.Equal(1, ana.Deposited);
        Assert.Equal(death, ana.Death);
    }

    [Fact]
    public void Compare_SeesADeathWhenSomeoneLootsTheBody()
    {
        var items = LootLogger.Core.Data.ItemDatabase.LoadBuiltIn();
        // No death rows: Ana picked a bag, then Bia looted Ana's body.
        var loot = new[] { Loot("Ana", "T6_BAG", 1, 0), Loot("Bia", "T5_CAPE", 1, 40, from: "Ana") };

        var ana = LootLogger.Core.Chest.ChestComparer.Compare(loot, [], items).Single(p => p.Player == "Ana");

        Assert.Equal("T6_BAG", Assert.Single(ana.LostOnDeath).ItemId);
        Assert.Empty(ana.Missing);
    }

    [Fact]
    public void Compare_ADeathInALaterFightDoesNotCoverEarlierLoot()
    {
        var items = LootLogger.Core.Data.ItemDatabase.LoadBuiltIn();
        // A week compared at once: Ana kept Monday's bag, then died in Wednesday's fight while carrying a cape.
        var monday = Loot("Ana", "T6_BAG", 1, 0);
        var wednesday = Loot("Ana", "T5_CAPE", 1, 0) with { UtcTime = monday.UtcTime.AddDays(2) };
        var death = new KillEntry(wednesday.UtcTime.AddMinutes(10), "Ana", "PlVAS", "", "faca02", "Outlimits", "", "Sunfang Cliffs");

        var ana = Assert.Single(LootLogger.Core.Chest.ChestComparer.Compare([monday, wednesday], [], items, [death]));

        Assert.Equal("T6_BAG", Assert.Single(ana.Missing).ItemId);
        Assert.Equal("T5_CAPE", Assert.Single(ana.LostOnDeath).ItemId);
        Assert.Equal(death, ana.Death);
    }

    [Fact]
    public void Compare_ADeathAfterAPauseInTheSameFightStillCoversTheLoot()
    {
        var items = LootLogger.Core.Data.ItemDatabase.LoadBuiltIn();
        // Ana picked a bag, the fight paused (Bia looted 40 minutes later), and Ana died 35 minutes after that.
        var loot = new[] { Loot("Ana", "T6_BAG", 1, 0), Loot("Bia", "T5_CAPE", 1, 40 * 60) };
        var death = new KillEntry(loot[0].UtcTime.AddMinutes(75), "Ana", "PlVAS", "", "faca02", "Outlimits", "", "Sunfang Cliffs");

        var ana = LootLogger.Core.Chest.ChestComparer.Compare(loot, [], items, [death]).Single(p => p.Player == "Ana");

        Assert.Equal("T6_BAG", Assert.Single(ana.LostOnDeath).ItemId);
        Assert.Empty(ana.Missing);
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
    public void Merge_LinesUpPcsWhoseClocksDiffer()
    {
        var mine = new[] { Loot("Ana", "T6_BAG", 1, 100), Loot("Bia", "T5_CAPE", 1, 103), Loot("Caio", "T4_MAIN_SWORD", 1, 110) };
        // The other PC's clock is 76 s behind.
        var theirs = new[] { Loot("Ana", "T6_BAG", 1, 24), Loot("Bia", "T5_CAPE", 1, 27), Loot("Caio", "T4_MAIN_SWORD", 1, 34), Loot("Duda", "T4_BAG", 1, 40) };

        var merged = LootFile.Merge([mine, theirs]);

        Assert.Equal(4, merged.Count);
        Assert.Equal(mine[0].UtcTime.AddSeconds(16), merged.Single(e => e.LootedByName == "Duda").UtcTime);
    }

    [Fact]
    public void Merge_KeepsRepeatedPickupsInsideOneLog()
    {
        var log = new[] { Loot("Ana", "T6_BAG", 1, 0), Loot("Ana", "T6_BAG", 1, 1) };

        Assert.Equal(2, LootFile.Merge([log]).Count);
    }

    [Fact]
    public void Merge_CountsAPickupTheGameResentOnce()
    {
        // A resend comes 0.2 s later, and each wait doubles (0.2 / 0.4 / 0.8 / 1.6 / 3.2 s).
        var log = new[] { 0, 0.2, 0.6, 1.4, 3, 6.2 }.Select(s => Loot("Ana", "T8_OFF_SHIELD", 1, 10 + s, "ByBlex")).ToList();
        Assert.Single(LootFile.Merge([log]));

        // The first resend can be lost too; the beat of the others still shows they are resends.
        var lostFirst = new[] { 0, 0.75, 1.75, 3.75 }.Select(s => Loot("Ana", "T8_OFF_SHIELD", 1, 10 + s, "ByBlex")).ToList();
        Assert.Single(LootFile.Merge([lostFirst]));
    }

    [Fact]
    public void Merge_KeepsEqualPickupsOffTheResendBeat()
    {
        var ana = new[] { 0, 0.9, 2.5 }.Select(s => Loot("Ana", "T7_POTION_HEAL", 5, 10 + s));
        var bia = new[] { 0, 0.2, 0.4, 0.6 }.Select(s => Loot("Bia", "T7_POTION_HEAL", 5, 10 + s));

        Assert.Equal(7, LootFile.Merge([ana.Concat(bia).ToList()]).Count);
    }

    [Fact]
    public void Merge_DropsAQuickRepeatOnlyOneLogWroteDown()
    {
        var mine = new[] { Loot("Ana", "T7_POTION_HEAL", 4, 10), Loot("Ana", "T7_POTION_HEAL", 4, 10.2) };
        var theirs = new[] { Loot("Ana", "T7_POTION_HEAL", 4, 11) };

        Assert.Single(LootFile.Merge([mine, theirs]));
        Assert.Single(LootFile.Merge([theirs, mine]));
    }

    [Fact]
    public void Merge_KeepsAQuickRepeatBothLogsSaw()
    {
        var mine = new[] { Loot("Ana", "T7_POTION_HEAL", 4, 10), Loot("Ana", "T7_POTION_HEAL", 4, 10.3) };
        var theirs = new[] { Loot("Ana", "T7_POTION_HEAL", 4, 11), Loot("Ana", "T7_POTION_HEAL", 4, 11.3) };

        Assert.Equal(2, LootFile.Merge([mine, theirs]).Count);
    }

    [Fact]
    public void Merge_KeepsAQuickRepeatWhenNoOtherLogWasThere()
    {
        var log = new[] { Loot("Ana", "T7_POTION_HEAL", 4, 10), Loot("Ana", "T7_POTION_HEAL", 4, 10.2) };

        Assert.Equal(2, LootFile.Merge([log]).Count);
    }

    [Fact]
    public void Merge_KeepsEqualStacksTakenAtOnce()
    {
        // The game never resends that fast: it is "take all" on a body with two equal stacks.
        var mine = new[] { Loot("Ana", "T4_RUNE", 2, 10, "Mob"), Loot("Ana", "T4_RUNE", 2, 10, "Mob") };
        var theirs = new[] { Loot("Ana", "T4_RUNE", 2, 11, "Mob") };

        Assert.Equal(2, LootFile.Merge([mine, theirs]).Count);
    }

    [Fact]
    public void ClockOffset_LinesUpALoggerThatWasMostlyOnAnotherMap()
    {
        var mine = Enumerable.Range(0, 40).Select(i => Loot("P" + i, "T8_BAG", 1, i * 10)).ToList();
        // Only 3 pickups in common (less than a tenth of the log), all 75 s behind.
        var theirs = mine.Take(3).Select(e => e with { UtcTime = e.UtcTime.AddSeconds(-75) })
            .Concat(Enumerable.Range(0, 30).Select(i => Loot("Q" + i, "T8_BAG", 1, i * 10))).ToList();

        Assert.Equal(TimeSpan.FromSeconds(75), LootFile.ClockOffset(mine, theirs));
    }

    [Fact]
    public void ClockOffset_LeavesTheClockAloneWhenTheFewMatchesDisagree()
    {
        var mine = Enumerable.Range(0, 50).Select(i => Loot("P" + i, "T8_BAG", 1, i * 10)).ToList();
        var theirs = mine.Take(3).Select(e => e with { UtcTime = e.UtcTime.AddSeconds(30) })
            .Concat(mine.Skip(10).Take(3).Select(e => e with { UtcTime = e.UtcTime.AddSeconds(-40) }))
            .Concat(Enumerable.Range(0, 44).Select(i => Loot("Q" + i, "T8_BAG", 1, i * 10))).ToList();

        Assert.Equal(TimeSpan.Zero, LootFile.ClockOffset(mine, theirs));
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
