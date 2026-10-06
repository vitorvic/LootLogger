using System.Globalization;
using LootLogger.Protocol.Photon;

namespace LootLogger.Core.Protocol;

public enum MessageKind
{
    Event,
    Request,
    Response
}

/// <summary>One decoded game message: its Albion code and its parameters.</summary>
public sealed record GameMessage(MessageKind Kind, short Code, IReadOnlyDictionary<byte, object> Parameters);

/// <summary>
/// Turns UDP payloads into game messages. Albion puts the real event code in parameter 252
/// and the real operation code in parameter 253.
/// </summary>
public sealed class AlbionMessageParser : PhotonParser
{
    private const byte EventCodeKey = 252;
    private const byte OperationCodeKey = 253;

    public event Action<GameMessage>? MessageReceived;

    protected override void OnEvent(byte code, Dictionary<byte, object> parameters)
    {
        Raise(MessageKind.Event, parameters, EventCodeKey);
    }

    protected override void OnRequest(byte operationCode, Dictionary<byte, object> parameters)
    {
        Raise(MessageKind.Request, parameters, OperationCodeKey);
    }

    protected override void OnResponse(byte operationCode, short returnCode, string debugMessage, Dictionary<byte, object> parameters)
    {
        Raise(MessageKind.Response, parameters, OperationCodeKey);
    }

    private void Raise(MessageKind kind, Dictionary<byte, object> parameters, byte codeKey)
    {
        if (!parameters.TryGetValue(codeKey, out var raw))
        {
            return;
        }

        short code;
        try
        {
            code = checked((short) Convert.ToInt32(raw, CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return;
        }

        MessageReceived?.Invoke(new GameMessage(kind, code, parameters));
    }
}
