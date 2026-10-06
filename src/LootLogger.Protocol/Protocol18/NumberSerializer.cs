// Adapted from AlbionOnline-StatisticsAnalysis by Triky313 (GPL-3.0)
// https://github.com/Triky313/AlbionOnline-StatisticsAnalysis
namespace LootLogger.Protocol.Protocol18;

public class NumberSerializer
{
    public static void Serialize(int value, byte[] target, ref int offset)
    {
        target[offset] = (byte) (value >> 24);
        offset++;
        target[offset] = (byte) (value >> 16);
        offset++;
        target[offset] = (byte) (value >> 8);
        offset++;
        target[offset] = (byte) value;
        offset++;
    }
}