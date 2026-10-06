// Adapted from AlbionOnline-StatisticsAnalysis by Triky313 (GPL-3.0)
// https://github.com/Triky313/AlbionOnline-StatisticsAnalysis
namespace LootLogger.Protocol.Protocol18;

public class OperationRequest
{
    public byte OperationCode { get; }
    public Dictionary<byte, object> Parameters { get; }

    public OperationRequest(byte operationCode, Dictionary<byte, object> parameters)
    {
        OperationCode = operationCode;
        Parameters = parameters;
    }
}