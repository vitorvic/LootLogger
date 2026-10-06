// Adapted from AlbionOnline-StatisticsAnalysis by Triky313 (GPL-3.0)
// https://github.com/Triky313/AlbionOnline-StatisticsAnalysis
namespace LootLogger.Protocol.Protocol18;

public class EventData
{
    public byte Code { get; }
    public Dictionary<byte, object> Parameters { get; }

    public EventData(byte code, Dictionary<byte, object> parameters)
    {
        Code = code;
        Parameters = parameters;
    }
}