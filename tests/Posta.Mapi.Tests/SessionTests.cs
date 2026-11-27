using System.Net;
using System.Net.Http.Headers;
using Posta.Mapi;
using Xunit;

public class SessionTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static readonly Uri Endpoint = new("https://exchange-lab-01/mapi/emsmdb/?MailboxId=00000000-0000-0000-0000-000000000001@lab.local");

    [Fact]
    public async Task ConnectLogonAndInboxUseTheExactIndependentlyCapturedWireRequests()
    {
        var connectRequest = new WireReader(Fixture("02-connect.request.bin")); var dn = connectRequest.Text(false);
        var executeRequest = new WireReader(Fixture("18-execute-contents.request.bin")); executeRequest.U32();
        var tableRequest = new WireReader(MapiWire.ReadPacket(executeRequest.Read((int)executeRequest.U32())).Operations);
        tableRequest.Read(13 + 5 + 4);
        var columns = Enumerable.Range(0, tableRequest.U16()).Select(_ => tableRequest.U32()).ToArray();
        tableRequest.Read(5); var rowLimit = tableRequest.U16();
        using var handler = new ReplayHandler("02-connect", "03-execute-logon", "18-execute-contents");
        await using var session = new MapiHttpSession(Endpoint, new HttpClient(handler));
        var connection = await session.ConnectAsync(dn);
        Assert.NotEmpty(connection.DisplayName);
        var logon = await session.LogonAsync(dn);
        var rows = await session.ReadTableAsync(logon, logon.FolderIds[4], false, columns, rowLimit);
        Assert.NotEmpty(rows.Rows);
        Assert.Equal(3, handler.CapturedRequests);
    }

    [Fact]
    public async Task DelegatorLogonUsesDocumentedFlagsWithoutChangingPrimaryLogon()
    {
        var request = new WireReader(Fixture("02-connect.request.bin"));
        var distinguishedName = request.Text(false);
        using var handler = new ReplayHandler("02-connect", "03-execute-logon") { ExpectDelegatorLogon = true };
        await using var session = new MapiHttpSession(Endpoint, new HttpClient(handler));
        await session.ConnectAsync(distinguishedName);
        var logon = await session.LogonDelegatorAsync(distinguishedName, default);
        Assert.NotEqual(0ul, logon.FolderIds[0]);
        Assert.NotEqual(Guid.Empty, logon.MailboxGuid);
    }

    [Fact]
    public async Task DeniedFolderDoesNotInvalidateTheSession()
    {
        var dn = new WireReader(Fixture("02-connect.request.bin")).Text(false);
        var request = new WireReader(Fixture("18-execute-contents.request.bin")); request.U32();
        var table = new WireReader(MapiWire.ReadPacket(request.Read((int)request.U32())).Operations);
        table.Read(13 + 5 + 4);
        var columns = Enumerable.Range(0, table.U16()).Select(_ => table.U32()).ToArray();
        table.Read(5); var limit = table.U16();
        using var handler = new ReplayHandler("02-connect", "03-execute-logon", "18-execute-contents", "18-execute-contents")
            { DenyFirstContents = true };
        await using var session = new MapiHttpSession(Endpoint, new HttpClient(handler));
        await session.ConnectAsync(dn);
        var logon = await session.LogonAsync(dn);
        var error = await Assert.ThrowsAsync<MapiException>(() => session.ReadTableAsync(logon, logon.FolderIds[4], false, columns, limit));
        Assert.Equal(0x80070005u, error.OperationCode);
        Assert.True(session.IsUsable);
        var result = await session.ReadTableAsync(logon, logon.FolderIds[4], false, columns, limit);
        Assert.NotEmpty(result.Rows);
    }

    [Fact]
    public async Task AuthenticationFailureIsNotRetriedWithOtherUsernamesOrSchemes()
    {
        using var handler = new RejectHandler();
        await using var session = new MapiHttpSession(Endpoint, new HttpClient(handler));
        var error = await Assert.ThrowsAsync<MapiException>(() => session.ConnectAsync("/o=Lab/cn=Alice"));
        Assert.Contains("401", error.Message);
        await Assert.ThrowsAsync<MapiException>(() => session.ConnectAsync("/o=Lab/cn=Alice"));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task ExecuteBeforeConnectNeverMakesANetworkRequest()
    {
        using var handler = new RejectHandler();
        await using var session = new MapiHttpSession(Endpoint, new HttpClient(handler));
        await Assert.ThrowsAsync<MapiException>(() => session.ExecuteAsync([], []));
        Assert.Equal(0, handler.Count);
    }

    private sealed class RejectHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Count++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    private sealed class ReplayHandler(params string[] prefixes) : HttpMessageHandler
    {
        public int CapturedRequests { get; private set; }
        public bool ExpectDelegatorLogon { get; init; }
        public bool DenyFirstContents { get; init; }
        private readonly HashSet<string> _requestIds = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(Endpoint, request.RequestUri);
            Assert.Equal(HttpVersion.Version11, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.Equal("application/mapi-http", request.Content!.Headers.ContentType!.MediaType);
            Assert.True(_requestIds.Add(request.Headers.GetValues("X-RequestId").Single()));
            byte[] response;
            if (CapturedRequests < prefixes.Length)
            {
                var prefix = prefixes[CapturedRequests++];
                var expected = Fixture(prefix + ".request.bin");
                if (ExpectDelegatorLogon && prefix == "03-execute-logon")
                {
                    // Execute header (8), extended header (8), ROP length (2), first four logon fields.
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(22), 0x0100040c);
                }
                Assert.Equal(expected, await request.Content.ReadAsByteArrayAsync(ct));
                response = Fixture(prefix + ".response.bin");
                if (DenyFirstContents && CapturedRequests == 3)
                {
                    var framed = MapiWire.Frame([2, 1, 5, 0, 7, 128], [1, uint.MaxValue, uint.MaxValue]);
                    response = System.Text.Encoding.ASCII.GetBytes("DONE\r\n\r\n").Concat(MapiWire.Write(w =>
                    { w.Write(0u); w.Write(0u); w.Write(0u); w.Write((uint)framed.Length); w.Write(framed); w.Write(0u); })).ToArray();
                }
            }
            else
            {
                var operation = request.Headers.GetValues("X-RequestType").Single();
                if (operation == "Disconnect") response = System.Text.Encoding.ASCII.GetBytes("DONE\r\n\r\n").Concat(new byte[12]).ToArray();
                else
                {
                    var body = await request.Content.ReadAsByteArrayAsync(ct);
                    var reader = new WireReader(body); reader.U32();
                    var released = MapiWire.ReadPacket(reader.Read((int)reader.U32()));
                    Assert.Equal(new byte[] { 1, 0, 2 }, released.Operations);
                    var framed = MapiWire.Frame([], released.Handles);
                    response = System.Text.Encoding.ASCII.GetBytes("DONE\r\n\r\n").Concat(MapiWire.Write(w =>
                    { w.Write(0u); w.Write(0u); w.Write(0u); w.Write((uint)framed.Length); w.Write(framed); w.Write(0u); })).ToArray();
                }
            }
            var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response) };
            result.Headers.Add("X-ResponseCode", "0");
            result.Content.Headers.ContentType = new MediaTypeHeaderValue("application/mapi-http");
            return result;
        }
    }
}
