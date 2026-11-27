using System.Text;
using Posta.Mapi;
using Xunit;

public class WireTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ConnectRequestMatchesIndependentExchangeCapture()
    {
        var original = Fixture("02-connect.request.bin");
        var r = new WireReader(original);
        Assert.Equal(original, MapiWire.Connect(r.Text(false)));
    }

    [Fact]
    public void ConnectResponseDecodesIndependentExchangeCapture()
    {
        var raw = Fixture("02-connect.response.bin");
        var response = MapiHttpSession.ParseConnection(MapiWire.Unwrap(raw).Body);
        Assert.False(string.IsNullOrWhiteSpace(response.DisplayName));
    }

    [Fact]
    public void PrivateMailboxLogonDecodesThirteenFolderIds()
    {
        var raw = Fixture("03-execute-logon.response.bin");
        var packet = MapiWire.ReadExecute(MapiWire.Unwrap(raw).Body);
        var logon = MapiHttpSession.ParseLogon(packet);
        Assert.Equal(13, logon.FolderIds.Count);
        Assert.NotEqual(0ul, logon.FolderIds[4]);
        Assert.NotEqual(uint.MaxValue, logon.Handle);
    }

    [Theory]
    [InlineData("03-execute-logon.request.bin")]
    [InlineData("09-execute-hierarchy.request.bin")]
    [InlineData("18-execute-contents.request.bin")]
    public void ExecuteAndRopFramingRoundTripCapturedRequests(string name)
    {
        var raw = Fixture(name);
        var r = new WireReader(raw); Assert.Equal(3u, r.U32());
        var framed = r.Read((int)r.U32());
        var packet = MapiWire.ReadPacket(framed);
        Assert.Equal(raw, MapiWire.Execute(MapiWire.Frame(packet.Operations, packet.Handles)));
    }

    [Theory]
    [InlineData("09-execute-hierarchy", 4)]
    [InlineData("18-execute-contents", 5)]
    public void CapturedTableRowsDecodeAgainstCapturedColumnSet(string prefix, byte tableRop)
    {
        // Extract the column set from the independently captured request, never our encoder.
        var request = new WireReader(Fixture(prefix + ".request.bin")); request.U32();
        var requested = MapiWire.ReadPacket(request.Read((int)request.U32()));
        var q = new WireReader(requested.Operations);
        Assert.Equal(2, q.U8()); q.Read(12);
        Assert.Equal(tableRop, q.U8()); q.Read(4);
        Assert.Equal(0x12, q.U8()); q.Read(3);
        var columns = Enumerable.Range(0, q.U16()).Select(_ => q.U32()).ToArray();
        var response = MapiWire.ReadExecute(MapiWire.Unwrap(Fixture(prefix + ".response.bin")).Body);
        var r = new WireReader(response.Operations);
        MapiWire.RopHeader(r, 2, 1); r.Read(2);
        MapiWire.RopHeader(r, tableRop, 2); r.U32();
        MapiWire.RopHeader(r, 0x12, 2); r.U8();
        MapiWire.RopHeader(r, 0x15, 2); r.U8();
        var count = r.U16(); Assert.True(count > 0);
        for (int i = 0; i < count; i++) Assert.Equal(columns.Length, MapiWire.ReadRow(r, columns).Count);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void UnicodeAndAbsentPropertiesKeepTheFollowingColumnAligned()
    {
        var bytes = MapiWire.Write(w =>
        {
            w.Write(new byte[] { 1, 1, 0 }); w.Write(Encoding.Unicode.GetBytes("შეხვედრა\0"));
            w.Write((byte)0x0a); w.Write(0x8004010fu);
            w.Write((byte)0); w.Write(42ul);
        });
        uint[] columns = [0x00010003, 0x0037001f, 0x00020003, 0x674a0014];
        var r = new WireReader(bytes); var values = MapiWire.ReadRow(r, columns);
        Assert.Null(values[columns[0]]); Assert.Equal("შეხვედრა", values[columns[1]]);
        Assert.Equal(new MapiCellError(0x8004010f), values[columns[2]]);
        Assert.Equal(42ul, values[columns[3]]); Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void EveryTruncatedLogonResponseIsRejected()
    {
        var body = MapiWire.Unwrap(Fixture("03-execute-logon.response.bin")).Body;
        for (int length = 0; length < body.Length; length++)
            Assert.ThrowsAny<IOException>(() => MapiWire.ReadExecute(body[..length]));
    }

    [Fact]
    public void CompressionAndMalformedHandleTableAreRejected()
    {
        var framed = MapiWire.Frame([1, 0, 0], 0u); framed[2] |= 1;
        Assert.Throws<MapiException>(() => MapiWire.ReadPacket(framed));
        Assert.Throws<MapiException>(() => MapiWire.ReadPacket([0, 0, 4, 0, 3, 0, 3, 0, 2, 0, 0]));
    }

    [Theory]
    [InlineData("<html>bad gateway</html>\r\n")]
    [InlineData("PROCESSING\r\nPENDING\r\n")]
    [InlineData("DONE\r\nX-ResponseCode: 0\r\nX-ResponseCode: 1\r\n\r\n")]
    public void MalformedPreambleCannotLookLikeSuccessfulMapi(string text) =>
        Assert.Throws<MapiException>(() => MapiWire.Unwrap(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void DiscoveryUsesExactMailboxUrlAndRejectsXmlEntities()
    {
        const string xml = "<Autodiscover><Response><User><LegacyDN>/o=Lab/cn=Test</LegacyDN></User><Account><Protocol Type='mapiHttp'><MailStore><ExternalUrl>https://mail.example/mapi/emsmdb/?MailboxId=123@example</ExternalUrl></MailStore></Protocol></Account></Response></Autodiscover>";
        var endpoint = MapiHttpSession.ParseAutodiscover(xml);
        Assert.Equal("?MailboxId=123@example", endpoint.MailStore.Query);
        Assert.Equal("/o=Lab/cn=Test", endpoint.LegacyDn);
        Assert.ThrowsAny<Exception>(() => MapiHttpSession.ParseAutodiscover("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///c:/secret'>]><x>&e;</x>"));
    }

    [Theory]
    [InlineData("http://mail.example/mapi/emsmdb/")]
    [InlineData("https://user:secret@mail.example/mapi/emsmdb/")]
    public void CredentialsRequireAnExplicitHttpsOrigin(string url) =>
        Assert.Throws<ArgumentException>(() => MapiHttpSession.ValidateEndpoint(new Uri(url)));
}
