namespace LootLogger.Core.Network;

public enum ServerRegion
{
    Unknown,
    Americas,
    Europe,
    Asia
}

/// <summary>Tells which Albion server a packet came from by its address.</summary>
/// <remarks>Address ranges from AlbionServerRegistry in AlbionOnline-StatisticsAnalysis by Triky313 (GPL-3.0).</remarks>
public static class GameServers
{
    private static readonly (uint Prefix, ServerRegion Region)[] Ranges =
    [
        (Prefix(5, 188, 125), ServerRegion.Americas),
        // Some Americas maps (hideouts included) run here while the main server stays on 5.188.125.x.
        (Prefix(85, 234, 70), ServerRegion.Americas),
        (Prefix(5, 45, 187), ServerRegion.Asia),
        (Prefix(193, 169, 238), ServerRegion.Europe)
    ];

    /// <param name="address">IPv4 address as a big-endian number.</param>
    public static ServerRegion RegionOf(uint address)
    {
        foreach (var (prefix, region) in Ranges)
        {
            if ((address & 0xFFFFFF00) == prefix)
            {
                return region;
            }
        }

        return ServerRegion.Unknown;
    }

    private static uint Prefix(byte a, byte b, byte c) => (uint)(a << 24 | b << 16 | c << 8);
}
