// Adapted from AlbionOnline-StatisticsAnalysis by Triky313 (GPL-3.0)
// https://github.com/Triky313/AlbionOnline-StatisticsAnalysis
namespace LootLogger.Protocol.Protocol18;

public class NumberDeserializer
{
    public static bool Deserialize(out int value, byte[] source, ref int offset)
    {
        return Deserialize(out value, source.AsSpan(), ref offset);
    }

    public static bool Deserialize(out int value, ReadOnlySpan<byte> source, ref int offset)
    {
        value = 0;

        if (offset < 0 || offset > source.Length - sizeof(int))
        {
            return false;
        }

        ReadOnlySpan<byte> valueBytes = source.Slice(offset, sizeof(int));
        value = (valueBytes[0] << 24)
            | (valueBytes[1] << 16)
            | (valueBytes[2] << 8)
            | valueBytes[3];
        offset += sizeof(int);

        return true;
    }

    public static bool Deserialize(out short value, byte[] source, ref int offset)
    {
        return Deserialize(out value, source.AsSpan(), ref offset);
    }

    public static bool Deserialize(out short value, ReadOnlySpan<byte> source, ref int offset)
    {
        value = 0;

        if (offset < 0 || offset > source.Length - sizeof(short))
        {
            return false;
        }

        ReadOnlySpan<byte> valueBytes = source.Slice(offset, sizeof(short));
        value = (short) ((valueBytes[0] << 8) | valueBytes[1]);
        offset += sizeof(short);

        return true;
    }
}
