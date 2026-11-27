using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Posta.Mapi;
using Xunit;

public class FolderTests
{
    [Fact]
    public async Task CreateFolderOpensParentThenSendsGenericUnicodeNameAndComment()
    {
        using var handler = new FolderServer();
        await using var session = await Connect(handler);
        var id = await session.CreateFolderAsync(new(1, new ulong[13]), 0x42, "საქაღალდე", "შენიშვნა");
        Assert.Equal(0x1122334455667788ul, id);
        var r = new WireReader(handler.MainRops);
        Assert.Equal(new byte[] { 2, 0, 0, 1 }, r.Read(4)); Assert.Equal(0x42ul, r.U64()); r.U8();           // OpenFolder parent
        Assert.Equal(new byte[] { 0x1c, 0, 1, 2, 1, 1, 0, 0 }, r.Read(8));                                    // CreateFolder: Generic, Unicode, no-open-existing
        Assert.Equal("საქაღალდე", r.Text(true)); Assert.Equal("შენიშვნა", r.Text(true)); Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public async Task DuplicateFolderNameIsReportedNotSilentlyAccepted()
    {
        using var handler = new FolderServer { FolderExists = true };
        await using var session = await Connect(handler);
        await Assert.ThrowsAsync<MapiException>(() => session.CreateFolderAsync(new(1, new ulong[13]), 0x42, "dup", null));
    }

    [Theory]
    [InlineData(false, 0x05)]
    [InlineData(true, 0x15)]
    public async Task DeleteFolderChoosesSoftOrHardFlagsAndCarriesChildId(bool hard, int expectedFlags)
    {
        using var handler = new FolderServer();
        await using var session = await Connect(handler);
        await session.DeleteFolderAsync(new(1, new ulong[13]), 0x42, 0x99, hard);
        var r = new WireReader(handler.MainRops); r.Read(13);                                                 // skip OpenFolder(parent)
        Assert.Equal(new byte[] { 0x1d, 0, 1, (byte)expectedFlags }, r.Read(4)); Assert.Equal(0x99ul, r.U64());
    }

    [Fact]
    public async Task PartialFolderDeletionSurfacesAsError()
    {
        using var handler = new FolderServer { Partial = true };
        await using var session = await Connect(handler);
        await Assert.ThrowsAsync<MapiException>(() => session.DeleteFolderAsync(new(1, new ulong[13]), 0x42, 0x99, false));
    }

    [Theory]
    [InlineData(false, 0x35)]
    [InlineData(true, 0x36)]
    public async Task MoveAndCopyFolderUseDistinctRopsAndTwoOpenParents(bool copy, int op)
    {
        using var handler = new FolderServer();
        await using var session = await Connect(handler);
        await session.MoveCopyFolderAsync(new(1, new ulong[13]), 0x10, 0x20, 0x99, "name", copy);
        var r = new WireReader(handler.MainRops); r.Read(13); r.Read(13);                                     // two OpenFolder (source, dest)
        Assert.Equal((byte)op, r.U8());
    }

    [Fact]
    public async Task EmptyFolderIsSynchronousAndKeepsAssociatedMessages()
    {
        using var handler = new FolderServer();
        await using var session = await Connect(handler);
        await session.EmptyFolderAsync(new(1, new ulong[13]), 0x42);
        var r = new WireReader(handler.MainRops); r.Read(13);
        Assert.Equal(new byte[] { 0x58, 0, 1, 0, 0 }, r.Read(5));
    }

    [Fact]
    public async Task DeleteMessagesBatchesAndReportsPartialCompletion()
    {
        using var handler = new FolderServer { Partial = true };
        await using var session = await Connect(handler);
        await Assert.ThrowsAsync<MapiException>(() => session.DeleteMessagesAsync(new(1, new ulong[13]), 0x42, [1, 2, 3], false));
        Assert.Equal((byte)0x1e, handler.BatchOp);
    }

    [Fact]
    public void MultiValueBinaryPropertyDecodesEachEntry()
    {
        var bytes = MapiWire.Write(w =>
        {
            w.Write((byte)0);                                 // row flag: all values present
            w.Write(3u);                                      // PtypMultipleBinary, 3 values
            w.Write((ushort)1); w.Write((byte)0xaa);
            w.Write((ushort)0);
            w.Write((ushort)2); w.Write(new byte[] { 0xbe, 0xef });
        });
        var row = MapiWire.ReadRow(new WireReader(bytes), [0x36d81102]);
        var values = Assert.IsType<byte[][]>(row[0x36d81102u]);
        Assert.Equal(3, values.Length);
        Assert.Equal(new byte[] { 0xaa }, values[0]); Assert.Empty(values[1]); Assert.Equal(new byte[] { 0xbe, 0xef }, values[2]);
    }

    private static async Task<MapiHttpSession> Connect(FolderServer handler)
    {
        var session = new MapiHttpSession(new("https://exchange-lab-01/mapi/emsmdb/"), new HttpClient(handler));
        await session.ConnectAsync("/o=Lab/cn=Alice"); return session;
    }

    private sealed class FolderServer : HttpMessageHandler
    {
        public bool Partial, FolderExists;
        public byte[] MainRops { get; private set; } = [];
        public byte BatchOp { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var type = request.Headers.GetValues("X-RequestType").Single();
            byte[] raw;
            if (type == "Connect") raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/02-connect.response.bin"));
            else if (type == "Disconnect") raw = Preamble(new byte[12]);
            else
            {
                var b = new WireReader(await request.Content!.ReadAsByteArrayAsync(ct)); b.U32();
                var packet = MapiWire.ReadPacket(b.Read((int)b.U32())); var rops = packet.Operations;
                if (rops[0] == 1) { raw = Preamble(Envelope([], packet.Handles)); } // RopRelease: no response ROP
                else
                {
                    int opens = 0; while (opens * 13 + 13 <= rops.Length && rops[opens * 13] == 2) opens++;
                    var target = opens * 13 < rops.Length ? rops[opens * 13] : (byte)0;
                    if (opens > 0 && target is not (0x1c or 0x1d or 0x58 or 0x35 or 0x36 or 0x0a)) MainRops = rops;
                    if (target is 0x1e or 0x91) BatchOp = target;
                    if (target is 0x1c or 0x1d or 0x58 or 0x35 or 0x36 or 0x0a) MainRops = rops;
                    var result = MapiWire.Write(w =>
                    {
                        for (byte i = 1; i <= opens; i++) { w.Write((byte)2); w.Write(i); w.Write(0u); w.Write((byte)0); w.Write((byte)0); }
                        switch (target)
                        {
                            case 0x1c: w.Write((byte)0x1c); w.Write((byte)2); w.Write(0u); w.Write(0x1122334455667788ul); w.Write((byte)(FolderExists ? 1 : 0)); break;
                            case 0x1d: w.Write((byte)0x1d); w.Write((byte)1); w.Write(0u); w.Write((byte)(Partial ? 1 : 0)); break;
                            case 0x58: w.Write((byte)0x58); w.Write((byte)1); w.Write(0u); w.Write((byte)(Partial ? 1 : 0)); break;
                            case 0x35: case 0x36: w.Write(target); w.Write((byte)1); w.Write(0u); w.Write((byte)(Partial ? 1 : 0)); break;
                            case 0x1e: case 0x91: w.Write(target); w.Write((byte)1); w.Write(0u); w.Write((byte)(Partial ? 1 : 0)); break;
                            case 0x0a: w.Write((byte)0x0a); w.Write((byte)1); w.Write((ushort)0); break;
                        }
                    });
                    var handles = packet.Handles.Select(h => h == uint.MaxValue ? 123u : h).ToArray();
                    raw = Preamble(Envelope(result, handles));
                }
            }
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) };
            reply.Content.Headers.ContentType = new MediaTypeHeaderValue("application/mapi-http"); reply.Headers.Add("X-ResponseCode", "0");
            return reply;
        }

        private static byte[] Envelope(byte[] result, uint[] handles)
        {
            var frame = MapiWire.Frame(result, handles);
            return MapiWire.Write(w => { w.Write(0u); w.Write(0u); w.Write(0u); w.Write((uint)frame.Length); w.Write(frame); w.Write(0u); });
        }
        private static byte[] Preamble(byte[] body) => Encoding.ASCII.GetBytes("DONE\r\n\r\n").Concat(body).ToArray();
    }
}
