using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Posta.Mapi;
using Xunit;

public class AddressBookTests
{
    [Fact]
    public async Task LoadDirectoryBindsThenPagesQueryRowsAndDecodesColumns()
    {
        using var handler = new AbServer { TotalRows = 150 }; // two pages of 1000 requested; server returns 150 then 0
        await using var ab = new MapiAddressBook(new("https://exchange-lab-01/mapi/nspi/"), new HttpClient(handler));
        var rows = await ab.LoadDirectoryAsync(20000);
        Assert.Equal("Bind", handler.Operations[0]);
        Assert.All(handler.Operations.Skip(1), op => Assert.Equal("QueryRows", op));
        Assert.Equal(150, rows.Count);
        Assert.Equal("გელაშვილი 0", rows[0][0x3001001fu]);
        Assert.Equal("user0@example.test", rows[0][0x39fe001fu]);
        Assert.Null(rows[0][0x3003001fu]);              // absent value (HasValue == 0)
        Assert.Equal(6u, rows[0][0x0ffe0003u]);          // PR_OBJECT_TYPE int, no HasValue prefix
        Assert.Equal("გელაშვილი 1", rows[1][0x3001001fu]);
        Assert.Equal("user1@example.test", rows[1][0x39fe001fu]);
        Assert.Null(rows[1][0x3003001fu]);
        Assert.Equal(new MapiCellError(0x8004010f), rows[1][0x3a17001fu]);
        Assert.Equal(6u, rows[1][0x0ffe0003u]);
        Assert.Equal("გელაშვილი 2", rows[2][0x3001001fu]);
    }

    [Fact]
    public async Task QueryRowsRequestCarriesStatColumnsAndRowCap()
    {
        using var handler = new AbServer { TotalRows = 0 };
        await using var ab = new MapiAddressBook(new("https://exchange-lab-01/mapi/nspi/"), new HttpClient(handler));
        await ab.LoadDirectoryAsync(20000);
        var r = new WireReader(handler.LastQueryRows);
        Assert.Equal(0u, r.U32());                       // Flags
        Assert.Equal(1, r.U8());                         // HasState
        r.Read(8); Assert.Equal(0u, r.U32());            // STAT up to CurrentRec (MID_BEGINNING)
        r.Read(12); Assert.Equal(1252u, r.U32());        // CodePage must be 1252, never 1200
        r.Read(8);                                       // locales
        Assert.Equal(0u, r.U32());                       // ExplicitTableCount
        Assert.Equal(1000u, r.U32());                    // RowCount page cap
        Assert.Equal(1, r.U8());                         // HasColumns
        Assert.Equal((uint)MapiAddressBook.PersonColumns.Length, r.U32());
    }

    private sealed class AbServer : HttpMessageHandler
    {
        public int TotalRows;
        public List<string> Operations { get; } = [];
        public byte[] LastQueryRows { get; private set; } = [];
        private int _served;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var op = request.Headers.GetValues("X-RequestType").Single(); Operations.Add(op);
            var reqBody = await request.Content!.ReadAsByteArrayAsync(ct);
            byte[] body;
            if (op == "Bind") body = MapiWire.Write(w => { w.Write(0u); w.Write(0u); w.Write(new byte[16]); w.Write(0u); });
            else if (op == "Unbind") body = new byte[8];
            else // QueryRows
            {
                LastQueryRows = reqBody;
                var remaining = Math.Max(0, TotalRows - _served);
                var pageRows = Math.Min(1000, remaining);
                var start = _served; _served += pageRows;
                body = MapiWire.Write(w =>
                {
                    w.Write(0u); w.Write(0u);                    // StatusCode, ErrorCode
                    w.Write((byte)1); w.Write(NextStat(_served)); // HasState + STAT (advanced position)
                    RowSet(w, start, pageRows);
                });
            }
            var raw = Encoding.ASCII.GetBytes("DONE\r\n\r\n").Concat(body).ToArray();
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) };
            reply.Content.Headers.ContentType = new MediaTypeHeaderValue("application/mapi-http"); reply.Headers.Add("X-ResponseCode", "0");
            return reply;
        }

        private static byte[] NextStat(int pos) => MapiWire.Write(w =>
        { w.Write(0u); w.Write(0u); w.Write((uint)pos); w.Write(0u); w.Write((uint)pos); w.Write(0u); w.Write(1252u); w.Write(0x409u); w.Write(0x409u); });

        private static void RowSet(BinaryWriter w, int start, int pageRows)
        {
            w.Write((byte)1);                                    // HasColsAndRows
            var columns = MapiAddressBook.PersonColumns;
            w.Write((uint)columns.Length); foreach (var t in columns) w.Write(t);
            w.Write((uint)pageRows);
            for (int i = 0; i < pageRows; i++)
            {
                var flagged = (start + i) % 2 == 1;
                w.Write(flagged ? (byte)1 : (byte)0);
                foreach (var tag in columns)
                {
                    if (flagged && tag == 0x3003001fu) { w.Write((byte)1); continue; }
                    if (flagged && tag == 0x3a17001fu) { w.Write((byte)0x0a); w.Write(0x8004010fu); continue; }
                    if (flagged) w.Write((byte)0);
                    switch ((ushort)tag)
                    {
                        case 0x001f when tag == 0x3001001f: w.Write((byte)0xff); Z(w, "გელაშვილი " + (start + i)); break;
                        case 0x001f when tag == 0x39fe001f: w.Write((byte)0xff); Z(w, $"user{start + i}@example.test"); break;
                        case 0x001f: w.Write((byte)0); break;    // other strings absent
                        case 0x0003: w.Write(tag == 0x0ffe0003u ? 6u : 0u); break;
                    }
                }
            }
        }

        private static void Z(BinaryWriter w, string s) { w.Write(Encoding.Unicode.GetBytes(s)); w.Write((ushort)0); }
    }
}
