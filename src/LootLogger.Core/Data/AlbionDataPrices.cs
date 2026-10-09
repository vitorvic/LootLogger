using System.Globalization;
using System.Text.Json;
using LootLogger.Core.Network;

namespace LootLogger.Core.Data;

/// <summary>
/// Reserve prices from the Albion Data Project, a public price list fed by players' market uploads.
/// Used only when the game never sent its own estimate for an item (it sends one only for items the player sees).
/// </summary>
public static class AlbionDataPrices
{
    /// <summary>Sales from the last week count; older ones only when there is nothing newer.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);

    private static readonly TimeSpan OldestUsable = TimeSpan.FromDays(30);

    // Keeps each address short enough for any server.
    private const int ItemsPerRequest = 50;

    public static string? BaseUrl(ServerRegion region) => region switch
    {
        ServerRegion.Americas => "https://west.albion-online-data.com/api/v2/",
        ServerRegion.Europe => "https://europe.albion-online-data.com/api/v2/",
        ServerRegion.Asia => "https://east.albion-online-data.com/api/v2/",
        _ => null
    };

    /// <summary>Average sale price per unit for each item it found, in silver. Items with no recent sales are left out.</summary>
    public static async Task<Dictionary<string, long>> FetchAsync(HttpClient http, ServerRegion region, IReadOnlyCollection<string> itemIds,
        DateTime utcNow, CancellationToken cancel = default)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (BaseUrl(region) is not { } baseUrl)
        {
            return result;
        }

        foreach (var chunk in itemIds.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(ItemsPerRequest))
        {
            var url = baseUrl + "stats/history/" + string.Join(',', chunk.Select(Uri.EscapeDataString)) + ".json?time-scale=24";
            var json = await http.GetStringAsync(url, cancel);
            foreach (var (id, price) in ParseHistory(json, utcNow))
            {
                result[id] = price;
            }
        }

        return result;
    }

    /// <summary>
    /// Reads a stats/history answer. Each item gets the average price of its sales across every city and quality,
    /// weighted by how many sold: last 7 days, or the latest day with sales within 30 days if the week had none.
    /// </summary>
    public static Dictionary<string, long> ParseHistory(string json, DateTime utcNow)
    {
        var sales = new Dictionary<string, List<(DateTime Day, long Count, long Price)>>(StringComparer.OrdinalIgnoreCase);
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var series in doc.RootElement.EnumerateArray())
            {
                if (series.ValueKind != JsonValueKind.Object
                    || !series.TryGetProperty("item_id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                    || idElement.GetString() is not { Length: > 0 } id
                    || !series.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var point in data.EnumerateArray())
                {
                    if (point.ValueKind == JsonValueKind.Object
                        && point.TryGetProperty("item_count", out var countElement) && countElement.TryGetInt64(out var count) && count > 0
                        && point.TryGetProperty("avg_price", out var priceElement) && priceElement.TryGetInt64(out var price) && price > 0
                        && point.TryGetProperty("timestamp", out var timeElement) && timeElement.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(timeElement.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                        && utcNow - day <= OldestUsable)
                    {
                        if (!sales.TryGetValue(id, out var list))
                        {
                            sales[id] = list = [];
                        }

                        list.Add((day, count, price));
                    }
                }
            }
        }

        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, list) in sales)
        {
            var recent = list.Where(s => utcNow - s.Day <= RecentWindow).ToList();
            if (recent.Count == 0)
            {
                var latest = list.Max(s => s.Day);
                recent = list.Where(s => s.Day == latest).ToList();
            }

            var sold = recent.Sum(s => s.Count);
            result[id] = (long) Math.Round(recent.Sum(s => (decimal) s.Count * s.Price) / sold);
        }

        return result;
    }
}
