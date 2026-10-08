using LootLogger.Core.Protocol;
using LootLogger.Protocol.Protocol18;

namespace LootLogger.Core.Tests;

/// <summary>
/// Game data that is broken, or made on purpose to hurt, must fail as an ordinary error that the
/// capture skips. It must never overflow the stack or ask for gigabytes, which would close the app.
/// </summary>
public class MalformedPacketTests
{
    private const byte ObjectArray = 23;
    private const byte IntZero = 30;

    [Fact]
    public void DeepNesting_IsRefusedInsteadOfOverflowingTheStack()
    {
        // 20,000 object arrays one inside the other: about 40 KB, a single packet.
        var payload = new List<byte>();
        for (var i = 0; i < 20_000; i++)
        {
            payload.Add(ObjectArray);
            payload.Add(1);
        }

        payload.Add(IntZero);
        Assert.Throws<InvalidDataException>(() => Protocol18Deserializer.Deserialize(payload.ToArray()));
    }

    [Fact]
    public void DeepNesting_InDictionaryTypes_IsRefused()
    {
        // Dictionary of byte to dictionary of byte to dictionary...
        var nestedDictionaries = new List<byte> { 20 };
        for (var i = 0; i < 20_000; i++)
        {
            nestedDictionaries.Add(3);
            nestedDictionaries.Add(20);
        }

        nestedDictionaries.AddRange([3, 3, 0]);
        Assert.Throws<InvalidDataException>(() => Protocol18Deserializer.Deserialize(nestedDictionaries.ToArray()));

        // Dictionary of byte to array of array of array...
        var nestedArrays = new List<byte> { 20, 3 };
        nestedArrays.AddRange(Enumerable.Repeat((byte) 64, 20_000));
        nestedArrays.AddRange([3, 0]);
        Assert.Throws<InvalidDataException>(() => Protocol18Deserializer.Deserialize(nestedArrays.ToArray()));
    }

    [Fact]
    public void ModestNesting_StillReads()
    {
        var payload = new List<byte>();
        for (var i = 0; i < 10; i++)
        {
            payload.Add(ObjectArray);
            payload.Add(1);
        }

        payload.AddRange([7, 2, (byte) 'o', (byte) 'k']);
        var value = Protocol18Deserializer.Deserialize(payload.ToArray());
        for (var i = 0; i < 10; i++)
        {
            value = Assert.Single(Assert.IsType<object[]>(value));
        }

        Assert.Equal("ok", value);
    }

    [Theory]
    [InlineData(new byte[] { 7 })]          // string
    [InlineData(new byte[] { 67 })]         // bytes
    [InlineData(new byte[] { 68 })]         // shorts
    [InlineData(new byte[] { 69 })]         // floats
    [InlineData(new byte[] { 70 })]         // doubles
    [InlineData(new byte[] { 66 })]         // booleans
    [InlineData(new byte[] { 71 })]         // strings
    [InlineData(new byte[] { 73 })]         // ints
    [InlineData(new byte[] { 74 })]         // longs
    [InlineData(new byte[] { 23 })]         // objects
    [InlineData(new byte[] { 21 })]         // hashtable
    [InlineData(new byte[] { 85 })]         // hashtables
    [InlineData(new byte[] { 64 })]         // arrays
    [InlineData(new byte[] { 83 })]         // custom values
    [InlineData(new byte[] { 19, 1 })]      // custom value
    [InlineData(new byte[] { 128 })]        // custom value, short form
    [InlineData(new byte[] { 84, 3, 3 })]   // dictionaries
    public void HugeDeclaredSizes_AreRefusedBeforeAnythingIsAllocated(byte[] header)
    {
        // A few bytes that announce about two billion elements.
        byte[] payload = [.. header, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 1, 2, 3];

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<EndOfStreamException>(() => Protocol18Deserializer.Deserialize(payload));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
    }

    [Fact]
    public void Booleans_PackedEightToAByte_StillFitAtTheEnd()
    {
        var value = Protocol18Deserializer.Deserialize(new byte[] { 66, 16, 0x01, 0x80 });

        var flags = Assert.IsType<bool[]>(value);
        Assert.Equal(16, flags.Length);
        Assert.True(flags[0]);
        Assert.True(flags[15]);
        Assert.Equal(2, flags.Count(f => f));
    }

    [Fact]
    public void RandomData_NeverAsksForMuchMemory()
    {
        var random = new Random(1234);
        var buffer = new byte[64];
        for (var i = 0; i < 50_000; i++)
        {
            random.NextBytes(buffer);
            var length = random.Next(1, buffer.Length + 1);

            var before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                Protocol18Deserializer.DeserializeEventData(buffer.AsSpan(0, length));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Garbage is expected to fail; it only must fail cheaply.
            }

            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000, Convert.ToHexString(buffer, 0, length));
        }
    }

    [Fact]
    public void HostileEvent_FailsAlone_AndTheNextMessageStillArrives()
    {
        var parser = new AlbionMessageParser();
        var received = new List<short>();
        parser.MessageReceived += m => received.Add(m.Code);

        // One parameter (key 0) holding 20,000 nested object arrays.
        var parameters = new List<byte> { 1, 0 };
        for (var i = 0; i < 20_000; i++)
        {
            parameters.Add(ObjectArray);
            parameters.Add(1);
        }

        parameters.Add(IntZero);
        Assert.Throws<InvalidDataException>(() => parser.ReceivePacket(PhotonPackets.RawEvent(parameters.ToArray())));

        parser.ReceivePacket(PhotonPackets.Event(42, new Dictionary<byte, object> { [0] = "ok" }));
        Assert.Equal([(short) 42], received);
    }
}
