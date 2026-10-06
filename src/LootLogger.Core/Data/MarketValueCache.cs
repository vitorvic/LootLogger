using System.Collections.Concurrent;
using System.Text.Json;

namespace LootLogger.Core.Data;

/// <summary>
/// Remembers the estimated market value the game reports for each item, in silver per unit.
/// The game only sends values for items the player sees, so values are kept between sessions.
/// </summary>
public sealed class MarketValueCache
{
    private readonly ConcurrentDictionary<int, long> _values = new();

    public long Get(int itemIndex) => _values.GetValueOrDefault(itemIndex);

    /// <param name="internalValue">Value as sent by the game, in units of 1/10000 silver.</param>
    public void SetFromGame(int itemIndex, long internalValue)
    {
        if (internalValue > 0)
        {
            _values[itemIndex] = internalValue / 10_000;
        }
    }

    public static MarketValueCache Load(string path)
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
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }

        return cache;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<int, long>(_values)));
    }
}
