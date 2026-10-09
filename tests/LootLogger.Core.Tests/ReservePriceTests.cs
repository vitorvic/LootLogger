using LootLogger.Core.Data;
using LootLogger.Core.Session;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Tests;

public class ReservePriceTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 23, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void History_AveragesSalesOfTheLastWeekWeightedByCount()
    {
        const string json = """
            [
              {"location":"Caerleon","item_id":"T8_ARMOR_PLATE_SET3@2","quality":1,
               "data":[{"item_count":3,"avg_price":2000000,"timestamp":"2026-10-08T00:00:00"},
                       {"item_count":5,"avg_price":9999999,"timestamp":"2026-09-20T00:00:00"}]},
              {"location":"Lymhurst","item_id":"T8_ARMOR_PLATE_SET3@2","quality":2,
               "data":[{"item_count":1,"avg_price":2400000,"timestamp":"2026-10-07T00:00:00"}]}
            ]
            """;

        var prices = AlbionDataPrices.ParseHistory(json, Now);

        // (3 x 2.000.000 + 1 x 2.400.000) / 4; the September sale is older than a week.
        Assert.Equal(2_100_000, prices["T8_ARMOR_PLATE_SET3@2"]);
    }

    [Fact]
    public void History_UsesTheLatestDayWhenTheWeekHadNoSales_AndIgnoresTooOld()
    {
        const string json = """
            [
              {"location":"Martlock","item_id":"T6_2H_SHAPESHIFTER_KEEPER@2","quality":1,
               "data":[{"item_count":2,"avg_price":800000,"timestamp":"2026-09-25T00:00:00"},
                       {"item_count":4,"avg_price":900000,"timestamp":"2026-09-20T00:00:00"}]},
              {"location":"Martlock","item_id":"T4_BAG","quality":1,
               "data":[{"item_count":9,"avg_price":3000,"timestamp":"2026-07-01T00:00:00"}]},
              {"location":"Martlock","item_id":"T5_BAG","quality":1,
               "data":[{"item_count":0,"avg_price":0,"timestamp":"2026-10-08T00:00:00"}]}
            ]
            """;

        var prices = AlbionDataPrices.ParseHistory(json, Now);

        Assert.Equal(800_000, prices["T6_2H_SHAPESHIFTER_KEEPER@2"]);
        Assert.False(prices.ContainsKey("T4_BAG"));
        Assert.False(prices.ContainsKey("T5_BAG"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[1, {\"item_id\":5}, {\"item_id\":\"X\",\"data\":{}}]")]
    public void History_OddAnswers_GiveNothing(string json)
    {
        if (json.Length == 0)
        {
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => AlbionDataPrices.ParseHistory(json, Now));
            return;
        }

        Assert.Empty(AlbionDataPrices.ParseHistory(json, Now));
    }

    [Fact]
    public void Cache_GameValueWinsOverReserve()
    {
        var cache = new MarketValueCache();
        Assert.True(cache.NeedsReserve(10, Now));

        cache.SetReserve(10, 500, Now);
        Assert.Equal((500L, true), cache.GetWithReserve(10));
        Assert.False(cache.NeedsReserve(10, Now.AddHours(1)));
        Assert.True(cache.NeedsReserve(10, Now.AddDays(2)));

        cache.SetFromGame(10, 7_000_000);
        Assert.Equal((700L, false), cache.GetWithReserve(10));
        Assert.False(cache.NeedsReserve(10, Now.AddDays(2)));
    }

    [Fact]
    public void Cache_NoSalesIsRememberedForADay()
    {
        var cache = new MarketValueCache();
        cache.SetReserve(11, 0, Now);

        Assert.Equal((0L, false), cache.GetWithReserve(11));
        Assert.False(cache.NeedsReserve(11, Now.AddHours(12)));
    }

    [Fact]
    public void Cache_SavesAndLoadsReservePrices()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cache = new MarketValueCache();
            cache.SetFromGame(1, 120_000);
            cache.SetReserve(2, 3_456, Now);
            cache.Save(Path.Combine(folder, "a.json"), Path.Combine(folder, "b.json"));

            var loaded = MarketValueCache.Load(Path.Combine(folder, "a.json"), Path.Combine(folder, "b.json"));
            Assert.Equal((12L, false), loaded.GetWithReserve(1));
            Assert.Equal((3_456L, true), loaded.GetWithReserve(2));
            Assert.False(loaded.NeedsReserve(2, Now));

            // An app that never asked for reserve prices still loads its game values.
            Assert.Equal(12, MarketValueCache.Load(Path.Combine(folder, "a.json")).Get(1));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Session_UpdateLoot_RewritesTheFileWithThePrice()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var session = new LootSession(Now, folder);
            session.Add(new LootEntry(Now, "NillBlack", "PlVAS", "OOPS", 5, "T8_ARMOR_PLATE_SET3@2", "Elder's Guardian Armor", 1,
                "Fulano", "KETA", "", 0, "Willowshade Icemarsh"));
            session.Add(new LootEntry(Now.AddSeconds(1), "NillBlack", "PlVAS", "OOPS", 6, "T4_BAG", "Adept's Bag", 2,
                "Fulano", "KETA", "", 3000, "Willowshade Icemarsh"));

            var changed = session.UpdateLoot(e => e.UnitValue == 0 ? e with { UnitValue = 2_100_000, IsReservePrice = true } : null);

            Assert.Equal(1, changed);
            Assert.Equal(2_106_000, session.TotalValue);
            Assert.True(session.Loot[0].IsReservePrice);
            var lines = File.ReadAllLines(session.AutosavePath!);
            Assert.Equal(3, lines.Length);
            Assert.EndsWith(";2100000;Willowshade Icemarsh", lines[1]);
            Assert.EndsWith(";3000;Willowshade Icemarsh", lines[2]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
