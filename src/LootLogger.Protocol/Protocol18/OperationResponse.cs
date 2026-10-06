// Adapted from AlbionOnline-StatisticsAnalysis by Triky313 (GPL-3.0)
// https://github.com/Triky313/AlbionOnline-StatisticsAnalysis
namespace LootLogger.Protocol.Protocol18;

public class OperationResponse
{
    public byte OperationCode { get; }
    public short ReturnCode { get; }
    public string DebugMessage { get; }
    public Dictionary<byte, object> Parameters { get; }

    public OperationResponse(byte operationCode, short returnCode, string debugMessage, Dictionary<byte, object> parameters)
    {
        OperationCode = operationCode;
        ReturnCode = returnCode;
        DebugMessage = debugMessage;
        Parameters = parameters;
    }
}