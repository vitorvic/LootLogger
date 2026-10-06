using System.Text;

namespace LootLogger.Core.Data;

/// <summary>Maps the map index the game sends ("2343") to its name ("Sunfang Cliffs") and tier.</summary>
public sealed class ClusterDatabase
{
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _tiers = new(StringComparer.OrdinalIgnoreCase);

    public static ClusterDatabase LoadBuiltIn()
    {
        using var stream = typeof(ClusterDatabase).Assembly.GetManifestResourceStream("LootLogger.clusters.tsv")
                           ?? throw new InvalidOperationException("Built-in map list is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var db = new ClusterDatabase();
        foreach (var line in reader.ReadToEnd().Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length >= 2)
            {
                db._names[parts[0]] = parts[1];
            }

            if (parts.Length >= 3 && int.TryParse(parts[2], out var tier))
            {
                db._tiers[parts[0]] = tier;
            }
        }

        return db;
    }

    /// <summary>
    /// Returns a readable name. Instanced maps arrive as "index@guid" or "@ISLAND@guid";
    /// those fall back to the base map or to the raw text.
    /// </summary>
    public string DisplayName(string? clusterIndex)
    {
        if (string.IsNullOrWhiteSpace(clusterIndex))
        {
            return string.Empty;
        }

        if (_names.TryGetValue(clusterIndex, out var name))
        {
            return name;
        }

        foreach (var part in clusterIndex.Split('@', StringSplitOptions.RemoveEmptyEntries))
        {
            if (_names.TryGetValue(part, out name))
            {
                return name;
            }
        }

        return clusterIndex;
    }

    /// <summary>Map tier (1 to 8), or 0 when unknown. Same fallback as <see cref="DisplayName"/>.</summary>
    public int Tier(string? clusterIndex)
    {
        if (string.IsNullOrWhiteSpace(clusterIndex))
        {
            return 0;
        }

        if (_tiers.TryGetValue(clusterIndex, out var tier))
        {
            return tier;
        }

        foreach (var part in clusterIndex.Split('@', StringSplitOptions.RemoveEmptyEntries))
        {
            if (_tiers.TryGetValue(part, out tier))
            {
                return tier;
            }
        }

        return 0;
    }
}
