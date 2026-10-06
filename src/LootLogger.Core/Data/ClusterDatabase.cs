using System.Text;

namespace LootLogger.Core.Data;

/// <summary>Maps the map index the game sends ("2343") to its name ("Sunfang Cliffs").</summary>
public sealed class ClusterDatabase
{
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public static ClusterDatabase LoadBuiltIn()
    {
        using var stream = typeof(ClusterDatabase).Assembly.GetManifestResourceStream("LootLogger.clusters.tsv")
                           ?? throw new InvalidOperationException("Built-in map list is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var db = new ClusterDatabase();
        foreach (var line in reader.ReadToEnd().Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length == 2)
            {
                db._names[parts[0]] = parts[1];
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
}
