using Posta.Mapi;
using Xunit;

public class MessageTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "items-en-us", name));

    [Fact]
    public void CapturedOpenMessageAndPropertiesDecodeIncludingRecipientRows()
    {
        var request = new WireReader(Fixture("16-execute-message-properties.request.bin")); request.U32();
        var q = new WireReader(MapiWire.ReadPacket(request.Read((int)request.U32())).Operations);
        q.Read(23); // RopOpenMessage request
        Assert.Equal(7, q.U8()); q.Read(6);
        var tags = Enumerable.Range(0, q.U16()).Select(_ => q.U32()).ToArray();
        var response = MapiWire.ReadExecute(MapiWire.Unwrap(Fixture("16-execute-message-properties.response.bin")).Body);
        var r = new WireReader(response.Operations);
        MapiWire.RopHeader(r, 3, 1); MapiWire.ReadOpenMessageMetadata(r);
        MapiWire.RopHeader(r, 7, 1);
        var properties = MapiWire.ReadRow(r, tags);
        Assert.NotEmpty((string)properties[0x0037001fu]!);
        Assert.Equal("IPM.Note", properties[0x001a001fu]);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void CapturedBodySpansMultipleChunksAndEndsWithAShortRead()
    {
        var first = MapiWire.ReadExecute(MapiWire.Unwrap(Fixture("22-execute-body-stream.response.bin")).Body);
        var r = new WireReader(first.Operations);
        MapiWire.RopHeader(r, 3, 1); MapiWire.ReadOpenMessageMetadata(r);
        MapiWire.RopHeader(r, 0x2b, 2); var advertisedSize = r.U32();
        MapiWire.RopHeader(r, 0x2c, 2);
        var collected = new List<byte>(); collected.AddRange(r.Read(r.U16())); Assert.Equal(0, r.Remaining);
        ushort lastCount = 1;
        foreach (var number in new[] { 23, 24, 25 })
        {
            var packet = MapiWire.ReadExecute(MapiWire.Unwrap(Fixture($"{number}-execute-body-stream.response.bin")).Body);
            r = new WireReader(packet.Operations); MapiWire.RopHeader(r, 0x2c, 0);
            lastCount = r.U16(); collected.AddRange(r.Read(lastCount)); Assert.Equal(0, r.Remaining);
        }
        Assert.True(collected.Count > 32768);
        Assert.Equal(advertisedSize, (uint)collected.Count);
        Assert.InRange(lastCount, (ushort)1, (ushort)16383);
    }

    [Fact]
    public void EveryTruncationOfOpenMessageMetadataIsRejected()
    {
        var packet = MapiWire.ReadExecute(MapiWire.Unwrap(Fixture("22-execute-body-stream.response.bin")).Body);
        var r = new WireReader(packet.Operations); MapiWire.RopHeader(r, 3, 1);
        var start = r.Position; MapiWire.ReadOpenMessageMetadata(r);
        var data = packet.Operations[start..r.Position];
        for (int n = 0; n < data.Length; n++)
            Assert.ThrowsAny<IOException>(() => MapiWire.ReadOpenMessageMetadata(new WireReader(data[..n])));
    }
}
