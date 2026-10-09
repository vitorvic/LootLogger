using System.Collections;
using System.Globalization;

namespace LootLogger.Core.Protocol;

/// <summary>Converts loosely typed Photon parameters into the values the tracker needs.</summary>
public static class ParameterExtensions
{
    public static string? GetString(this IReadOnlyDictionary<byte, object> p, byte key)
    {
        return p.TryGetValue(key, out var value) && value is not null ? value.ToString() : null;
    }

    public static long? GetLong(this IReadOnlyDictionary<byte, object> p, byte key)
    {
        return p.TryGetValue(key, out var value) ? ToLong(value) : null;
    }

    public static int? GetInt(this IReadOnlyDictionary<byte, object> p, byte key)
    {
        var value = p.GetLong(key);
        return value is >= int.MinValue and <= int.MaxValue ? (int) value.Value : null;
    }

    public static double? GetDouble(this IReadOnlyDictionary<byte, object> p, byte key)
    {
        return p.TryGetValue(key, out var value) ? ToDouble(value) : null;
    }

    public static bool GetBool(this IReadOnlyDictionary<byte, object> p, byte key)
    {
        if (!p.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool b => b,
            _ => ToLong(value) is > 0
        };
    }

    public static Guid? GetGuid(this IReadOnlyDictionary<byte, object> p, byte key)
    {
        return p.TryGetValue(key, out var value) ? ToGuid(value) : null;
    }

    public static List<long> GetLongList(this IReadOnlyDictionary<byte, object> p, byte key)
    {
        var result = new List<long>();
        if (!p.TryGetValue(key, out var value) || value is null or string)
        {
            return result;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (ToLong(item) is { } number)
                {
                    result.Add(number);
                }
            }
        }

        return result;
    }

    /// <summary>A list of numbers; an entry that is not a number becomes null so positions still line up.</summary>
    public static List<double?> GetDoubleList(this IReadOnlyDictionary<byte, object> p, byte key)
    {
        var result = new List<double?>();
        if (!p.TryGetValue(key, out var value) || value is null or string || value is not IEnumerable enumerable)
        {
            return result;
        }

        foreach (var item in enumerable)
        {
            result.Add(ToDouble(item));
        }

        return result;
    }

    public static List<Guid> GetGuidList(this IReadOnlyDictionary<byte, object> p, byte key)
    {
        var result = new List<Guid>();
        if (!p.TryGetValue(key, out var value) || value is byte[] || value is not IEnumerable enumerable)
        {
            return result;
        }

        foreach (var item in enumerable)
        {
            if (ToGuid(item) is { } guid)
            {
                result.Add(guid);
            }
        }

        return result;
    }

    public static List<string> GetStringList(this IReadOnlyDictionary<byte, object> p, byte key)
    {
        var result = new List<string>();
        if (!p.TryGetValue(key, out var value) || value is string || value is not IEnumerable enumerable)
        {
            return result;
        }

        foreach (var item in enumerable)
        {
            result.Add(item?.ToString() ?? string.Empty);
        }

        return result;
    }

    public static long? ToLong(object? value)
    {
        return value switch
        {
            null => null,
            byte b => b,
            sbyte sb => sb,
            short s => s,
            ushort us => us,
            int i => i,
            uint ui => ui,
            long l => l,
            ulong ul when ul <= long.MaxValue => (long) ul,
            float f => (long) f,
            double d => (long) d,
            string str when long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    public static double? ToDouble(object? value)
    {
        return value switch
        {
            float f when float.IsFinite(f) => f,
            double d when double.IsFinite(d) => d,
            float or double => null,
            _ => ToLong(value)
        };
    }

    public static Guid? ToGuid(object? value)
    {
        if (value is byte[] { Length: 16 } bytes)
        {
            return new Guid(bytes);
        }

        if (value is IEnumerable enumerable and not string)
        {
            var list = new List<byte>(16);
            foreach (var item in enumerable)
            {
                if (ToLong(item) is not { } number)
                {
                    return null;
                }

                list.Add(unchecked((byte) number));
            }

            return list.Count == 16 ? new Guid(list.ToArray()) : null;
        }

        return null;
    }
}
