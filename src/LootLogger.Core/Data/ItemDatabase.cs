using System.Text;
using System.Text.Json;

namespace LootLogger.Core.Data;

public sealed record ItemInfo(int Index, string UniqueName, string EnglishName, string PortugueseName)
{
    /// <summary>Tier and enchantment as shown in game, like "6.3". Empty for items without a tier.</summary>
    public string TierLabel
    {
        get
        {
            if (UniqueName.Length < 2 || UniqueName[0] != 'T' || !char.IsDigit(UniqueName[1]))
            {
                return string.Empty;
            }

            var at = UniqueName.IndexOf('@');
            var enchantment = at >= 0 ? UniqueName[(at + 1)..] : "0";
            return $"{UniqueName[1]}.{enchantment}";
        }
    }

    public int Enchantment
    {
        get
        {
            var at = UniqueName.IndexOf('@');
            return at >= 0 && int.TryParse(UniqueName[(at + 1)..], out var e) ? e : 0;
        }
    }

    public string NameFor(string language) => language.StartsWith("pt", StringComparison.OrdinalIgnoreCase) && PortugueseName.Length > 0
        ? PortugueseName
        : EnglishName;
}

/// <summary>
/// Maps the item number the game sends to the item's id and names.
/// Ships with a built-in copy and refreshes from the public ao-bin-dumps list when online.
/// </summary>
public sealed class ItemDatabase
{
    public const string RemoteUrl = "https://raw.githubusercontent.com/ao-data/ao-bin-dumps/master/formatted/items.json";

    private Dictionary<int, ItemInfo> _byIndex = new();
    private Dictionary<string, List<ItemInfo>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ItemInfo> _byUniqueName = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byIndex.Count;

    public ItemInfo? Get(int index) => _byIndex.GetValueOrDefault(index);

    public ItemInfo? GetByUniqueName(string uniqueName) => _byUniqueName.GetValueOrDefault(uniqueName);

    /// <summary>Finds an item by the name the game shows (English or Portuguese) and its enchantment.</summary>
    public ItemInfo? FindByName(string localizedName, int enchantment)
    {
        if (!_byName.TryGetValue(localizedName.Trim(), out var candidates))
        {
            return null;
        }

        return candidates.FirstOrDefault(c => c.Enchantment == enchantment) ?? candidates[0];
    }

    public static ItemDatabase LoadBuiltIn()
    {
        using var stream = typeof(ItemDatabase).Assembly.GetManifestResourceStream("LootLogger.items.tsv")
                           ?? throw new InvalidOperationException("Built-in item list is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var db = new ItemDatabase();
        db.Load(ParseTsv(reader.ReadToEnd()));
        return db;
    }

    /// <summary>Uses the cached list if present, otherwise the built-in one.</summary>
    public static ItemDatabase LoadCachedOrBuiltIn(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                var db = new ItemDatabase();
                db.Load(ParseTsv(File.ReadAllText(cachePath, Encoding.UTF8)));
                if (db.Count > 0)
                {
                    return db;
                }
            }
        }
        catch (IOException)
        {
        }

        return LoadBuiltIn();
    }

    /// <summary>Downloads the newest list, saves it as a compact cache and swaps it in.</summary>
    public async Task<bool> RefreshAsync(HttpClient http, string cachePath, CancellationToken ct = default)
    {
        await using var stream = await http.GetStreamAsync(RemoteUrl, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var items = new List<ItemInfo>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("Index", out var indexProp)
                || !int.TryParse(indexProp.GetString(), out var index)
                || !element.TryGetProperty("UniqueName", out var uniqueProp))
            {
                continue;
            }

            string en = string.Empty, pt = string.Empty;
            if (element.TryGetProperty("LocalizedNames", out var names) && names.ValueKind == JsonValueKind.Object)
            {
                en = names.TryGetProperty("EN-US", out var e) ? e.GetString() ?? string.Empty : string.Empty;
                pt = names.TryGetProperty("PT-BR", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            }

            items.Add(new ItemInfo(index, uniqueProp.GetString() ?? string.Empty, en, pt));
        }

        if (items.Count < 1000)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllTextAsync(cachePath, ToTsv(items), Encoding.UTF8, ct);
        Load(items);
        return true;
    }

    internal static IEnumerable<ItemInfo> ParseTsv(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length >= 3 && int.TryParse(parts[0], out var index))
            {
                yield return new ItemInfo(index, parts[1], parts[2], parts.Length > 3 ? parts[3] : string.Empty);
            }
        }
    }

    private static string ToTsv(IEnumerable<ItemInfo> items)
    {
        var sb = new StringBuilder();
        foreach (var i in items)
        {
            sb.Append(i.Index).Append('\t').Append(i.UniqueName).Append('\t')
              .Append(Clean(i.EnglishName)).Append('\t').Append(Clean(i.PortugueseName)).Append('\n');
        }

        return sb.ToString();

        static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ');
    }

    private void Load(IEnumerable<ItemInfo> items)
    {
        var byIndex = new Dictionary<int, ItemInfo>();
        var byName = new Dictionary<string, List<ItemInfo>>(StringComparer.OrdinalIgnoreCase);
        var byUniqueName = new Dictionary<string, ItemInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            byIndex[item.Index] = item;
            byUniqueName.TryAdd(item.UniqueName, item);
            AddName(byName, item.EnglishName, item);
            AddName(byName, item.PortugueseName, item);
        }

        _byIndex = byIndex;
        _byName = byName;
        _byUniqueName = byUniqueName;
    }

    private static void AddName(Dictionary<string, List<ItemInfo>> map, string name, ItemInfo item)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!map.TryGetValue(name, out var list))
        {
            map[name] = list = [];
        }

        list.Add(item);
    }
}
