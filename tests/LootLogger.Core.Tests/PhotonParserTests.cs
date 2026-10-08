using LootLogger.Core.Protocol;

namespace LootLogger.Core.Tests;

/// <summary>
/// When a confirmation is late, the game sends the same reliable command again with the same number.
/// The game reads it once; so must the capture, or a pickup that came again counts twice.
/// </summary>
public class PhotonParserTests
{
    private readonly AlbionMessageParser _parser = new();
    private readonly List<GameMessage> _messages = [];

    public PhotonParserTests()
    {
        _parser.MessageReceived += _messages.Add;
    }

    [Fact]
    public void ACommandSentAgain_IsReadOnce()
    {
        var pickup = PhotonPackets.Numbered(PhotonPackets.Event(275, new() { [1] = "XAgiota", [2] = "Valniaa", [4] = 3787, [5] = 1 }), 7);

        // The first copy, then the resends 0.2 s, 0.6 s and 1.4 s later.
        for (var i = 0; i < 4; i++)
        {
            _parser.ReceivePacket(pickup, fromServer: true);
        }

        Assert.Single(_messages);
    }

    [Fact]
    public void TheNextCommand_IsRead()
    {
        var pickup = PhotonPackets.Event(275, new() { [1] = "XAgiota", [2] = "Valniaa", [4] = 3787, [5] = 1 });

        _parser.ReceivePacket(PhotonPackets.Numbered(pickup, 7), fromServer: true);
        _parser.ReceivePacket(PhotonPackets.Numbered(pickup, 8), fromServer: true);

        Assert.Equal(2, _messages.Count);
    }

    [Fact]
    public void TheSameNumber_FromTheOtherSideOrAnotherChannelOrConnection_IsAnotherCommand()
    {
        // Each side counts its own commands, per channel, and a new map is a new connection counting from 1 again.
        var request = PhotonPackets.Request(21, new() { [0] = 0 });
        var pickup = PhotonPackets.Event(275, new() { [1] = "XAgiota", [2] = "Valniaa", [4] = 3787, [5] = 1 });

        _parser.ReceivePacket(PhotonPackets.Numbered(request, 1), fromServer: false);
        _parser.ReceivePacket(PhotonPackets.Numbered(pickup, 1), fromServer: true);
        _parser.ReceivePacket(PhotonPackets.Numbered(pickup, 1, channel: 1), fromServer: true);
        _parser.ReceivePacket(PhotonPackets.Numbered(pickup, 1, challenge: 43), fromServer: true);

        Assert.Equal(4, _messages.Count);
    }

    [Fact]
    public void FragmentsSentAgain_MakeTheMessageOnce()
    {
        var parameters = new Dictionary<byte, object> { [1] = "XAgiota", [2] = "Valniaa", [4] = 3787, [5] = 1, [9] = new string('x', 300) };
        var fragments = PhotonPackets.FragmentedEvent(275, parameters, pieces: 3, firstSequence: 20);

        foreach (var fragment in fragments.Concat(fragments))
        {
            _parser.ReceivePacket(fragment, fromServer: true);
        }

        Assert.Equal("Valniaa", Assert.Single(_messages).Parameters[2]);
    }

    [Fact]
    public void AFragmentMissedTheFirstTime_IsTakenFromTheResend()
    {
        var parameters = new Dictionary<byte, object> { [1] = "XAgiota", [2] = "Valniaa", [4] = 3787, [5] = 1, [9] = new string('x', 300) };
        var fragments = PhotonPackets.FragmentedEvent(275, parameters, pieces: 3, firstSequence: 20);

        _parser.ReceivePacket(fragments[0], fromServer: true);
        _parser.ReceivePacket(fragments[2], fromServer: true);
        Assert.Empty(_messages);

        foreach (var fragment in fragments)
        {
            _parser.ReceivePacket(fragment, fromServer: true);
        }

        Assert.Single(_messages);
    }

    [Fact]
    public void WithoutKnowingTheSender_NothingIsDropped()
    {
        var pickup = PhotonPackets.Numbered(PhotonPackets.Event(275, new() { [1] = "XAgiota", [2] = "Valniaa", [4] = 3787, [5] = 1 }), 7);

        _parser.ReceivePacket(pickup);
        _parser.ReceivePacket(pickup);

        Assert.Equal(2, _messages.Count);
    }
}
