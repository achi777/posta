using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Posta.Core.Models;
using Posta.Core.Services;
using Posta.Mapi;
using Xunit;

public class WriteTests
{
    [Theory]
    [InlineData(0u, true, false)]
    [InlineData(0u, false, false)]
    [InlineData(1u, true, true)]
    [InlineData(1u, false, false)]
    [InlineData(2u, true, true)]
    [InlineData(2u, false, true)]
    public void MissingOrZeroFollowUpPropertyDoesNotTurnOrdinaryMailIntoTasks(uint flag, bool completed, bool expected)
        => Assert.Equal(expected, NativeMapiMailService.IsFlaggedTask(flag, completed));
    [Fact]
    public void TaggedPropertiesMatchIndependentExchangeDraftCapture()
    {
        var body = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/write-create-draft.request.bin"));
        var reader = new WireReader(body); reader.U32();
        var packet = MapiWire.ReadPacket(reader.Read((int)reader.U32()));
        var r = new WireReader(packet.Operations); Assert.Equal(6, r.U8()); r.Read(14);
        var offset = r.Position; Assert.Equal(10, r.U8()); r.U8(); var index = r.U8();
        var size = r.U16(); var count = r.U16(); var properties = new Dictionary<uint, object>();
        for (int i = 0; i < count; i++) { var tag = r.U32(); properties[tag] = MapiWire.ReadValue(r, (ushort)tag)!; }
        Assert.Equal(offset + 5 + size, r.Position);
        Assert.Equal(packet.Operations[offset..r.Position], MapiWriteWire.Properties(index, properties));
    }

    [Fact]
    public void RecipientHasExplicitSmtpAndUnicodeWithMandatoryEmptyPropertyRow()
    {
        var bytes = MapiWriteWire.Recipients(1, [new(2, "არჩილი", "archil@example.test")]);
        var r = new WireReader(bytes);
        Assert.Equal(new byte[] { 0x0e, 0, 1 }, r.Read(3)); Assert.Equal(0, r.U16()); Assert.Equal(1, r.U16());
        Assert.Equal(0u, r.U32()); Assert.Equal(2, r.U8()); var row = new WireReader(r.Read(r.U16()));
        Assert.Equal(0x8258, row.U16()); Assert.Equal("SMTP", row.Text(false));
        Assert.Equal("archil@example.test", row.Text(true)); Assert.Equal("არჩილი", row.Text(true));
        Assert.Equal(new byte[] { 0, 0, 0 }, row.Read(3)); Assert.Equal(0, row.Remaining); Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void MeetingRecipientWritesDedicatedOrganizerAndTrackingColumns()
    {
        var r = new WireReader(MapiWriteWire.Recipients(1, [new(1, "ორგანიზატორი", "owner@example.test", 3, 1)]));
        Assert.Equal(new byte[] { 0x0e, 0, 1 }, r.Read(3));
        Assert.Equal(2, r.U16());
        Assert.Equal(0x5ffd0003u, r.U32());
        Assert.Equal(0x5fff0003u, r.U32());
        Assert.Equal(1, r.U16());
        Assert.Equal(0u, r.U32());
        Assert.Equal(1, r.U8());
        var row = new WireReader(r.Read(r.U16()));
        Assert.Equal(0x8258, row.U16());
        Assert.Equal("SMTP", row.Text(false));
        Assert.Equal("owner@example.test", row.Text(true));
        Assert.Equal("ორგანიზატორი", row.Text(true));
        Assert.Equal(2, row.U16());
        Assert.Equal(0, row.U8());
        Assert.Equal(3u, row.U32());
        Assert.Equal(1u, row.U32());
        Assert.Equal(0, row.Remaining);
        Assert.Equal(0, r.Remaining);
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("Someone <a@example.test>")]
    public void BadRecipientFailsBeforeAnyNetworkWrite(string address)
        => Assert.ThrowsAny<ArgumentException>(() => MapiWriteWire.Recipients(1, [new(1, "", address)]));

    [Fact]
    public void UnsupportedDeliveryOptionsAreNotSilentlyIgnored()
    {
        Assert.Throws<MailServiceException>(() => NativeMapiMailService.ValidateOutgoing(new() { DeferredDelivery = DateTime.Now }, "a@example.test", false));
        Assert.Throws<MailServiceException>(() => NativeMapiMailService.ValidateOutgoing(new() { FromAddress = "other@example.test" }, "a@example.test", false));
        Assert.Throws<MailServiceException>(() => NativeMapiMailService.ValidateOutgoing(new(), "a@example.test", true));
    }

    [Fact]
    public async Task RefusedPropertyPreventsSaveAndSubmission()
    {
        using var handler = new WriteServer { FailProperties = true };
        await using var session = await Connect(handler);
        await Assert.ThrowsAsync<MapiException>(() => Write(session, true));
        Assert.DoesNotContain((byte)0x0c, handler.Rops); Assert.DoesNotContain((byte)0x32, handler.Rops);
    }

    [Fact]
    public async Task ShortStreamWritesAreRetriedAndSavePrecedesSubmit()
    {
        using var handler = new WriteServer { ShortWrite = true };
        await using var session = await Connect(handler);
        var result = await Write(session, true);
        Assert.Equal(99ul, result.Id); Assert.Equal("longer html body", Encoding.UTF8.GetString(handler.Written.ToArray()));
        Assert.True(handler.Rops.IndexOf(0x0c) < handler.Rops.IndexOf(0x32));
        Assert.Equal(1, handler.Rops.Count(x => x == 0x32));
        Assert.Contains((byte)0x0d, handler.Rops); // Old recipients are cleared even with an empty new list.
    }

    [Fact]
    public async Task RefusedSaveIsNeverFollowedBySubmit()
    {
        using var handler = new WriteServer { FailSave = true };
        await using var session = await Connect(handler);
        await Assert.ThrowsAsync<MapiException>(() => Write(session, true));
        Assert.DoesNotContain((byte)0x32, handler.Rops);
    }

    [Fact]
    public async Task SavingADraftDoesNotSendIt()
    {
        using var handler = new WriteServer();
        await using var session = await Connect(handler);
        await Write(session, false);
        Assert.Contains((byte)0x0c, handler.Rops); Assert.DoesNotContain((byte)0x32, handler.Rops);
    }

    [Fact]
    public async Task LostSubmissionResponseRetainsDraftIdAndNeverRetries()
    {
        using var handler = new WriteServer { ThrowOnSubmit = true };
        await using var session = await Connect(handler);
        var error = await Assert.ThrowsAsync<MapiSubmissionException>(() => Write(session, true));
        Assert.True(error.OutcomeUnknown); Assert.Equal(99ul, error.SavedMessageId);
        Assert.Single(handler.Rops, x => x == 0x32); Assert.False(session.IsUsable);
    }

    [Fact]
    public async Task AttachmentIsCommittedBeforeItsMessageAndSubmission()
    {
        using var handler = new WriteServer();
        await using var session = await Connect(handler);
        var saved = await session.WriteMessageAsync(new(1, new ulong[13]), 42, null,
            new Dictionary<uint, object> { [0x0037001f] = "Draft" }, [], [],
            [new("example.txt", "text/plain", new byte[40000])], [], true);
        Assert.Single(saved.AttachmentNumbers);
        Assert.True(handler.Rops.IndexOf(0x25) < handler.Rops.IndexOf(0x0c));
        Assert.True(handler.Rops.IndexOf(0x0c) < handler.Rops.IndexOf(0x32));
    }

    [Fact]
    public void ContactOneOffUsesDocumentedProviderAndUnicodeMimeFlags()
    {
        var bytes = NativeMapiMailService.OneOff("ქართული", "a@example.test");
        var r = new WireReader(bytes); Assert.Equal(0u, r.U32());
        Assert.Equal(Convert.FromHexString("812B1FA4BEA310199D6E00DD010F5402"), r.Read(16));
        Assert.Equal(new byte[] { 0, 0, 1, 0x80 }, r.Read(4));
        Assert.Equal("ქართული", r.Text(true)); Assert.Equal("SMTP", r.Text(true)); Assert.Equal("a@example.test", r.Text(true));
        Assert.Equal(0, r.Remaining);
    }

    private static Task<(ulong Id, IReadOnlyList<uint> AttachmentNumbers)> Write(MapiHttpSession session, bool send)
        => session.WriteMessageAsync(new(1, new ulong[13]), 42, null,
            new Dictionary<uint, object> { [0x0037001f] = "Draft" }, Encoding.UTF8.GetBytes("longer html body"), [], [], [], send);

    private static async Task<MapiHttpSession> Connect(WriteServer handler)
    {
        var session = new MapiHttpSession(new("https://exchange-lab-01/mapi/emsmdb/"), new HttpClient(handler));
        await session.ConnectAsync("/o=Lab/cn=Alice"); return session;
    }

    private sealed class WriteServer : HttpMessageHandler
    {
        public bool FailProperties, FailSave, ShortWrite, ThrowOnSubmit;
        public List<byte> Rops { get; } = [];
        public List<byte> Written { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var type = request.Headers.GetValues("X-RequestType").Single();
            byte[] raw;
            if (type == "Connect") raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/02-connect.response.bin"));
            else
            {
                byte[] response;
                if (type == "Disconnect") response = new byte[12];
                else
                {
                    var b = new WireReader(await request.Content!.ReadAsByteArrayAsync(ct)); b.U32();
                    var packet = MapiWire.ReadPacket(b.Read((int)b.U32())); var r = new WireReader(packet.Operations);
                    var op = r.U8(); r.U8(); var index = r.U8(); Rops.Add(op);
                    if (op == 0x32 && ThrowOnSubmit) throw new IOException("Simulated response loss after submission.");
                    if (op is 6 or 0x2b or 0x23) index = r.U8();
                    var result = MapiWire.Write(w =>
                    {
                        if (op == 1) return;
                        w.Write(op); w.Write(index); w.Write(op == 0x0c && FailSave ? 0x80070005u : 0u);
                        if (op == 6) w.Write((byte)0);
                        if (op == 0x23) w.Write(7u);
                        if (op == 0x0a)
                        {
                            w.Write((ushort)(FailProperties ? 1 : 0));
                            if (FailProperties) { w.Write((ushort)0); w.Write(0x0037001fu); w.Write(0x80070005u); }
                        }
                        if (op == 0x2b) w.Write(0u);
                        if (op == 0x2d)
                        {
                            var data = r.Read(r.U16()); var count = ShortWrite ? Math.Min(3, data.Length) : data.Length;
                            Written.AddRange(data.Take(count)); w.Write((ushort)count);
                        }
                        if (op == 0x0c && !FailSave) { w.Write((byte)1); w.Write(99ul); }
                    });
                    var handles = packet.Handles.Select(h => h == uint.MaxValue ? 123u : h).ToArray();
                    var frame = MapiWire.Frame(result, handles);
                    response = MapiWire.Write(w => { w.Write(0u); w.Write(0u); w.Write(0u); w.Write((uint)frame.Length); w.Write(frame); w.Write(0u); });
                }
                raw = Encoding.ASCII.GetBytes("DONE\r\n\r\n").Concat(response).ToArray();
            }
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) };
            reply.Content.Headers.ContentType = new MediaTypeHeaderValue("application/mapi-http"); reply.Headers.Add("X-ResponseCode", "0"); return reply;
        }
    }
}
