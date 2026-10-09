using System.Collections.Concurrent;
using System.Text.Json;

namespace LootLogger.Core.Data;

/// <summary>
/// Remembers the estimated market value the game reports for each item, in silver per unit.
/// The game only sends values for items the player sees, so values are kept between sessions.
/// Items the game never priced can get a reserve price from the Albion Data Project, kept apart and marked as such.
/// </summary>
public sealed class MarketValueCache
{
    /// <summary>A reserve price (or the lack of one) is asked again after this long.</summary>
    public static readonly TimeSpan ReserveLifetime = TimeSpan.FromDays(1);

    private readonly ConcurrentDictionary<int, long> _values = new();
    private readonly ConcurrentDictionary<int, ReservePrice> _reserve = new();

    public long Get(int itemIndex) => _values.GetValueOrDefault(itemIndex);

    /// <summary>The game's value if known, else the reserve price; 0 when there is neither.</summary>
    public (long Value, bool IsReserve) GetWithReserve(int itemIndex)
    {
        if (Get(itemIndex) is > 0 and var value)
        {
            return (value, false);
        }

        return _reserve.TryGetValue(itemIndex, out var reserve) && reserve.Value > 0 ? (reserve.Value, true) : (0, false);
    }

    /// <summary>True when the item has no game value and no fresh reserve price (or fresh "no sales") yet.</summary>
    public bool NeedsReserve(int itemIndex, DateTime utcNow) =>
        Get(itemIndex) <= 0 && (!_reserve.TryGetValue(itemIndex, out var reserve) || utcNow - reserve.CheckedUtc > ReserveLifetime);

    /// <param name="value">Silver per unit; 0 records that the price list had no sales, so it is not asked again right away.</param>
    public void SetReserve(int itemIndex, long value, DateTime utcNow) => _reserve[itemIndex] = new ReservePrice(Math.Max(0, value), utcNow);

    /// <param name="internalValue">Value as sent by the game, in units of 1/10000 silver.</param>
    public void SetFromGame(int itemIndex, long internalValue)
    {
        if (internalValue > 0)
        {
            _values[itemIndex] = internalValue / 10_000;
        }
    }

    public static MarketValueCache Load(string path, string? reservePath = null)
    {
        var cache = new MarketValueCache();
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<Dictionary<int, long>>(File.ReadAllText(path));
                foreach (var (k, v) in data ?? [])
                {
                    cache._values[k] = v;
                }
            }

            if (reservePath is not null && File.Exists(reservePath))
            {
                var data = JsonSerializer.Deserialize<Dictionary<int, ReservePrice>>(File.ReadAllText(reservePath));
                foreach (var (k, v) in data ?? [])
                {
                    cache._reserve[k] = v;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }

        return cache;
    }

    public void Save(string path, string? reservePath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<int, long>(_values)));
        if (reservePath is not null)
        {
            File.WriteAllText(reservePath, JsonSerializer.Serialize(new Dictionary<int, ReservePrice>(_reserve)));
        }
    }

    public sealed record ReservePrice(long Value, DateTime CheckedUtc);
}
