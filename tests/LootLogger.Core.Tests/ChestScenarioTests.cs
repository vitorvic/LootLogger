using System.Globalization;
using LootLogger.Core.Chest;
using LootLogger.Core.Data;
using LootLogger.Core.Export;
using LootLogger.Core.Import;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Tests;

/// <summary>
/// Comparar Baú as the guild uses it: each test is one situation an officer can run into,
/// with the chest log written the way the game copies it (Portuguese client).
/// </summary>
public class ChestScenarioTests
{
    private static readonly ItemDatabase Items = ItemDatabase.LoadBuiltIn();
    private static readonly DateTime Fight = new(2026, 10, 7, 20, 30, 0, DateTimeKind.Utc);
    private const string Header = "\"Data\"\t\"Jogador\"\t\"Item\"\t\"Encantamento\"\t\"Qualidade\"\t\"Quantidade\"";

    private static LootEntry Pick(string player, string itemId, int quantity = 1, double minutes = 0, string from = "Inimigo") =>
        new(Fight.AddMinutes(minutes), player, "PlVAS", "OOPS", 0, itemId, Items.GetByUniqueName(itemId)!.EnglishName, quantity,
            from, "Inimigos", "", 1000, "Dryvein Steppe");

    private static KillEntry Death(string player, double minutes) =>
        new(Fight.AddMinutes(minutes), player, "PlVAS", "OOPS", "Inimigo", "Inimigos", "", "Dryvein Steppe");

    /// <summary>One chest log line as the game copies it.</summary>
    private static string Line(DateTime time, string player, string itemId, int amount, int quality = 1, bool english = false)
    {
        var item = Items.GetByUniqueName(itemId)!;
        var enchantment = itemId.Contains('@') ? itemId[(itemId.IndexOf('@') + 1)..] : "0";
        return $"\"{time.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture)}\"\t\"{player}\"\t" +
               $"\"{(english ? item.EnglishName : item.PortugueseName)}\"\t\"{enchantment}\"\t\"{quality}\"\t\"{amount}\"";
    }

    /// <summary>Pastes like the app: each paste adds only the lines not loaded yet.</summary>
    private static List<ChestLogEntry> Paste(params string[][] pastes)
    {
        var loaded = new List<ChestLogEntry>();
        foreach (var lines in pastes)
        {
            var entries = ChestLogParser.Parse(Header + "\n" + string.Join("\n", lines), Fight.AddDays(10));
            loaded.AddRange(ChestLogParser.NotYetPasted(loaded, entries));
        }

        return loaded;
    }

    private static PlayerComparison Result(string player, IEnumerable<LootEntry> loot, IEnumerable<ChestLogEntry> chest, IEnumerable<KillEntry>? kills = null) =>
        ChestComparer.Compare(loot, chest, Items, kills).Single(p => string.Equals(p.Player, player, StringComparison.OrdinalIgnoreCase));

    // ---------- Deposits ----------

    [Fact]
    public void Deposit_AfterTheFight_Counts()
    {
        var ana = Result("Ana", [Pick("Ana", "T6_BAG")], Paste([Line(Fight.AddMinutes(45), "Ana", "T6_BAG", 1)]));

        Assert.Equal(1, ana.Deposited);
        Assert.Empty(ana.Missing);
    }

    [Fact]
    public void Deposit_DaysLater_StillCounts()
    {
        var ana = Result("Ana", [Pick("Ana", "T6_BAG")], Paste([Line(Fight.AddDays(3), "Ana", "T6_BAG", 1)]));

        Assert.Equal(1, ana.Deposited);
    }

    [Fact]
    public void Deposit_MadeBeforeTheFight_DoesNotCount()
    {
        var chest = Paste([Line(Fight.AddDays(-1), "Ana", "T6_BAG", 1), Line(Fight.AddHours(-2), "Ana", "T6_BAG", 1)]);

        var ana = Result("Ana", [Pick("Ana", "T6_BAG")], chest);

        Assert.Equal(0, ana.Deposited);
        Assert.Equal(1, Assert.Single(ana.Missing).Quantity);
    }

    [Fact]
    public void Deposit_UpTo30MinutesBeforeThePickup_CountsForAWrongPcClock()
    {
        var loot = new[] { Pick("Ana", "T6_BAG"), Pick("Bia", "T6_BAG") };
        var chest = Paste([Line(Fight.AddMinutes(-30), "Ana", "T6_BAG", 1), Line(Fight.AddMinutes(-31), "Bia", "T6_BAG", 1)]);

        Assert.Equal(1, Result("Ana", loot, chest).Deposited);
        Assert.Equal(0, Result("Bia", loot, chest).Deposited);
    }

    [Fact]
    public void Deposit_OfPartOfTheLoot_LeavesTheRestMissing()
    {
        var loot = new[] { Pick("Ana", "T6_BAG"), Pick("Ana", "T6_BAG", minutes: 1), Pick("Ana", "T6_BAG", minutes: 2) };

        var ana = Result("Ana", loot, Paste([Line(Fight.AddHours(1), "Ana", "T6_BAG", 2)]));

        Assert.Equal(2, ana.Deposited);
        Assert.Equal(1, Assert.Single(ana.Missing).Quantity);
    }

    [Fact]
    public void Deposit_OfMoreThanWasPicked_CoversOnlyWhatWasPicked()
    {
        var ana = Result("Ana", [Pick("Ana", "T6_BAG")], Paste([Line(Fight.AddHours(1), "Ana", "T6_BAG", 3)]));

        Assert.Equal(1, ana.Looted);
        Assert.Equal(1, ana.Deposited);
        Assert.Empty(ana.Missing);
    }

    [Fact]
    public void Deposit_ByAnotherPlayer_DoesNotCoverTheLooter()
    {
        var loot = new[] { Pick("Ana", "T6_BAG") };
        var result = ChestComparer.Compare(loot, Paste([Line(Fight.AddHours(1), "Bia", "T6_BAG", 1)]), Items);

        Assert.Equal(1, Assert.Single(result).MissingCount);
        Assert.DoesNotContain(result, p => p.Player == "Bia");
    }

    [Fact]
    public void Deposit_WithAnotherEnchantment_DoesNotCount()
    {
        var ana = Result("Ana", [Pick("Ana", "T4_MAIN_SWORD@1")], Paste([Line(Fight.AddHours(1), "Ana", "T4_MAIN_SWORD", 1)]));

        Assert.Equal(0, ana.Deposited);
    }

    [Fact]
    public void Deposit_WithAnotherQuality_Counts_BecauseTheLootLogHasNoQuality()
    {
        var ana = Result("Ana", [Pick("Ana", "T6_BAG")], Paste([Line(Fight.AddHours(1), "Ana", "T6_BAG", 1, quality: 5)]));

        Assert.Equal(1, ana.Deposited);
    }

    [Fact]
    public void Deposit_OfAStackPickedUpInPieces_Counts()
    {
        var loot = new[] { Pick("Ana", "T6_POTION_HEAL", 5), Pick("Ana", "T6_POTION_HEAL", 3, 1), Pick("Ana", "T6_POTION_HEAL", 2, 2) };

        var ana = Result("Ana", loot, Paste([Line(Fight.AddHours(1), "Ana", "T6_POTION_HEAL", 10)]));

        Assert.Equal(10, ana.Deposited);
        Assert.Empty(ana.Missing);
    }

    [Fact]
    public void Deposit_WithThePlayerNameInOtherCase_Counts()
    {
        var ana = Result("Ana", [Pick("ana", "T6_BAG")], Paste([Line(Fight.AddHours(1), "Ana", "T6_BAG", 1)]));

        Assert.Equal(1, ana.Deposited);
    }

    [Fact]
    public void Deposit_CopiedFromAnEnglishClient_Counts()
    {
        var ana = Result("Ana", [Pick("Ana", "T6_BAG")], Paste([Line(Fight.AddHours(1), "Ana", "T6_BAG", 1, english: true)]));

        Assert.Equal(1, ana.Deposited);
    }

    [Fact]
    public void Withdrawal_ByAnAdmin_DoesNotUndoTheDeposit()
    {
        var chest = Paste([Line(Fight.AddHours(1), "Ana", "T6_BAG", 1), Line(Fight.AddHours(5), "Gabi89", "T6_BAG", -1)]);
        var result = ChestComparer.Compare([Pick("Ana", "T6_BAG")], chest, Items);

        Assert.Equal(1, Assert.Single(result).Deposited);
    }

    [Fact]
    public void FourWeekLog_OldDepositsOfTheSameItem_DoNotCount()
    {
        var ana = Result("Ana", [Pick("Ana", "T6_BAG")], Paste([Line(Fight.AddDays(-14), "Ana", "T6_BAG", 1)]));

        Assert.Equal(0, ana.Deposited);
    }

    [Fact]
    public void Week_SameItemInTwoFights_OneDepositInBetween_LeavesOneMissing()
    {
        var loot = new[] { Pick("Ana", "T4_MAIN_SWORD"), Pick("Ana", "T4_MAIN_SWORD", minutes: 2 * 24 * 60) };

        var ana = Result("Ana", loot, Paste([Line(Fight.AddHours(1), "Ana", "T4_MAIN_SWORD", 1)]));

        Assert.Equal(1, ana.Deposited);
        Assert.Equal(1, Assert.Single(ana.Missing).Quantity);
    }

    [Fact]
    public void Week_DeathInTheSecondFight_TheDepositCoversTheFirst()
    {
        // Monday's sword Ana kept and deposited on Thursday; Wednesday's sword she dropped when she died.
        var loot = new[] { Pick("Ana", "T4_MAIN_SWORD"), Pick("Ana", "T4_MAIN_SWORD", minutes: 2 * 24 * 60) };

        var ana = Result("Ana", loot, Paste([Line(Fight.AddDays(3), "Ana", "T4_MAIN_SWORD", 1)]), [Death("Ana", 2 * 24 * 60 + 10)]);

        Assert.Equal((1, 0, 1), (ana.Deposited, ana.MissingCount, ana.LostCount));
    }

    [Fact]
    public void Deposit_ByThePlayerWhoDied_GoesFirstToWhatTheyStillHad()
    {
        // Three potions picked up before dying were dropped; the two picked up after were deposited.
        var loot = new[] { Pick("Ana", "T6_POTION_HEAL", 3, 1), Pick("Ana", "T6_POTION_HEAL", 2, 15) };

        var ana = Result("Ana", loot, Paste([Line(Fight.AddHours(1), "Ana", "T6_POTION_HEAL", 2)]), [Death("Ana", 10)]);

        Assert.Equal((2, 0, 3), (ana.Deposited, ana.MissingCount, ana.LostCount));
    }

    // ---------- Pasting the chest log ----------

    [Fact]
    public void Paste_PagesThatOverlap_CountEachDepositOnce()
    {
        var lines = Enumerable.Range(0, 5).Select(i => Line(Fight.AddMinutes(40 + i), "Ana", "T6_POTION_HEAL", 1)).ToArray();

        var chest = Paste(lines[..3], lines[2..]);

        Assert.Equal(5, chest.Count);
    }

    [Fact]
    public void Paste_TabsTogetherSeparatelyOrTwice_GiveTheSameResult()
    {
        var tab1 = new[] { Line(Fight.AddHours(1), "Ana", "T6_BAG", 1), Line(Fight.AddHours(1), "Ana", "T5_CAPE", 1) };
        var tab2 = new[] { Line(Fight.AddHours(2), "Ana", "T6_POTION_HEAL", 4) };
        var loot = new[] { Pick("Ana", "T6_BAG"), Pick("Ana", "T5_CAPE"), Pick("Ana", "T6_POTION_HEAL", 6) };

        var together = Result("Ana", loot, Paste([.. tab1, .. tab2]));
        var separately = Result("Ana", loot, Paste(tab1, tab2));
        var twice = Result("Ana", loot, Paste(tab1, tab2, [.. tab1, .. tab2]));

        Assert.All(new[] { together, separately, twice }, a => Assert.Equal((6, 2), (a.Deposited, a.MissingCount)));
    }

    [Fact]
    public void Paste_TwoEqualDepositsInTheSameSecond_BothCount()
    {
        var line = Line(Fight.AddHours(1), "Ana", "T6_BAG", 1);

        var ana = Result("Ana", [Pick("Ana", "T6_BAG"), Pick("Ana", "T6_BAG", minutes: 1)], Paste([line, line]));

        Assert.Equal(2, ana.Deposited);
    }

    [Fact]
    public void Paste_OfSomethingElse_ReadsNothing()
    {
        Assert.Empty(ChestLogParser.Parse("bom dia\nisso não é log do baú"));
        Assert.Empty(ChestLogParser.Parse(CsvExporter.ToCsv([Pick("Ana", "T6_BAG")], [])));
    }

    // ---------- Deaths ----------

    [Fact]
    public void Death_BeforeThePickup_DoesNotExcuseIt()
    {
        var ana = Result("Ana", [Pick("Ana", "T6_BAG", minutes: 10)], [], [Death("Ana", 5)]);

        Assert.Equal(1, ana.MissingCount);
        Assert.Equal(0, ana.LostCount);
    }

    [Fact]
    public void Death_TwiceInOneFight_TheLastOneCovers()
    {
        var ana = Result("Ana", [Pick("Ana", "T6_BAG", minutes: 10)], [], [Death("Ana", 5), Death("Ana", 20)]);

        Assert.Equal(1, ana.LostCount);
        Assert.Equal(Fight.AddMinutes(20), ana.Death!.UtcTime);
    }

    [Fact]
    public void Death_AfterAnHourWithNothingHappening_IsAnotherFight()
    {
        var ana = Result("Ana", [Pick("Ana", "T6_BAG")], [], [Death("Ana", 61)]);

        Assert.Equal(1, ana.MissingCount);
        Assert.Equal(0, ana.LostCount);
    }

    [Fact]
    public void FightPastMidnight_IsStillOneFight()
    {
        var late = Fight.Date.AddHours(23).AddMinutes(55);
        var loot = new[] { Pick("Ana", "T6_BAG") with { UtcTime = late }, Pick("Bia", "T5_CAPE") with { UtcTime = late.AddMinutes(10) } };
        var chest = Paste([Line(late.AddMinutes(40), "Bia", "T5_CAPE", 1)]);

        var result = ChestComparer.Compare(loot, chest, Items, [Death("Ana", 0) with { UtcTime = late.AddMinutes(15) }]);

        Assert.Equal(1, result.Single(p => p.Player == "Ana").LostCount);
        Assert.Equal(1, result.Single(p => p.Player == "Bia").Deposited);
    }

    // ---------- Loot files ----------

    [Fact]
    public void LootFile_LoadedTwice_CountsOnce()
    {
        var log = LootFile.Parse(CsvExporter.ToCsv([Pick("Ana", "T6_BAG"), Pick("Ana", "T5_CAPE", minutes: 1)], []));

        var (loot, _) = LootFile.MergeAll([(log, []), (log, [])]);

        Assert.Equal(2, loot.Count);
    }

    // ---------- Everything together ----------

    [Fact]
    public void FullFlow_TwoRecordersAndAChestCopiedInPages()
    {
        // Two people recorded the fight; the second PC's clock is 40 s ahead and it also saw Duda die.
        var seenByBoth = new[] { Pick("Ana", "T6_BAG", minutes: 1), Pick("Ana", "T5_CAPE", minutes: 2), Pick("Bia", "T6_POTION_HEAL", 5, 3), Pick("Bia", "T6_POTION_HEAL", 3, 4) };
        var first = seenByBoth.Concat([Pick("Bia", "T6_POTION_HEAL", 2, 5), Pick("Caio", "T4_MAIN_SWORD", minutes: 6)]).ToList();
        var second = seenByBoth.Concat([Pick("Duda", "T6_BAG", minutes: 7), Pick("Edu", "T5_CAPE", minutes: 8), Pick("Edu", "T4_MAIN_SWORD", minutes: 9)])
            .Select(l => l with { UtcTime = l.UtcTime.AddSeconds(40) }).ToList();
        var secondDeaths = new[] { Death("Duda", 12) with { UtcTime = Fight.AddMinutes(12).AddSeconds(40) } };
        var logs = new[] { CsvExporter.ToCsv(first, []), CsvExporter.ToCsv(second, secondDeaths) }
            .Select(LootFile.ParseAll).Select(l => ((IReadOnlyList<LootEntry>) l.Loot, (IReadOnlyList<KillEntry>) l.Kills));
        var (loot, kills) = LootFile.MergeAll(logs);

        // The officer copies two pages; the second page starts with the last line of the first.
        var page1 = new[]
        {
            Line(Fight.AddHours(1), "Ana", "T6_BAG", 1), Line(Fight.AddHours(1), "Ana", "T5_CAPE", 1),
            Line(Fight.AddHours(2), "Gabi89", "T6_BAG", -1)
        };
        var page2 = new[]
        {
            page1[^1], Line(Fight.AddDays(1), "Bia", "T6_POTION_HEAL", 10),
            Line(Fight.AddDays(-1), "Caio", "T4_MAIN_SWORD", 1), Line(Fight.AddMinutes(-20), "Edu", "T5_CAPE", 1)
        };
        var result = ChestComparer.Compare(loot, Paste(page1, page2), Items, kills);

        Assert.Equal(16, loot.Sum(l => l.Quantity));
        Assert.Equal((2, 0), Status("Ana"));
        Assert.Equal((10, 0), Status("Bia"));
        Assert.Equal((0, 1), Status("Caio"));
        Assert.Equal((1, 1), Status("Edu"));
        var duda = result.Single(p => p.Player == "Duda");
        Assert.Equal((0, 0, 1), (duda.Deposited, duda.MissingCount, duda.LostCount));

        (int Deposited, int Missing) Status(string player)
        {
            var p = result.Single(r => r.Player == player);
            return (p.Deposited, p.MissingCount);
        }
    }
}
