namespace Posta.Mapi;

public sealed record MapiMessageData(Dictionary<uint, object?> Properties, byte[]? Html, byte[]? Text,
    IReadOnlyList<Dictionary<uint, object?>> Attachments, MapiRecipients? Recipients = null);

public sealed partial class MapiHttpSession
{
    public async Task<Dictionary<uint, object?>> ReadFolderPropertiesAsync(MapiLogon logon, ulong folderId, uint[] tags, CancellationToken ct = default)
    {
        var packet = await ExecuteAsync(MapiWire.Write(w =>
        { w.Write(new byte[] { 2, 0, 0, 1 }); w.Write(folderId); w.Write((byte)0); }), [logon.Handle, uint.MaxValue], ct);
        MapiWire.RopHeader(new WireReader(packet.Operations), 2, 1);
        var handles = packet.Handles;
        try { return await ReadPropertiesAsync(handles, 1, tags, ct); }
        finally { if (!_poisoned) await ExecuteAsync([1, 0, 1], handles, ct); }
    }
    public static readonly uint[] MessageProperties =
    [0x0037001f, 0x0c1a001f, 0x5d01001f, 0x0e060040, 0x00390040, 0x0e070003, 0x001a001f,
     0x0e04001f, 0x0e03001f, 0x00170003, 0x00360003, 0x3fde0003, 0x1035001f, 0x0e1b000b];
    public static readonly uint[] AttachmentColumns =
    [0x0e210003, 0x3707001f, 0x3704001f, 0x0e200003, 0x37050003, 0x370e001f, 0x3712001f];

    private async Task<uint[]> OpenMessageAsync(MapiLogon logon, ulong folderId, ulong messageId, CancellationToken ct, bool writable = false,
        Action<MapiRecipients>? onRecipients = null)
    {
        var ops = MapiWire.Write(w =>
        {
            w.Write(new byte[] { 3, 0, 0, 1 }); w.Write((ushort)0xfff);
            w.Write(folderId); w.Write(writable ? (byte)1 : (byte)0); w.Write(messageId);
        });
        var packet = await ExecuteAsync(ops, [logon.Handle, uint.MaxValue], ct);
        var r = new WireReader(packet.Operations);
        MapiWire.RopHeader(r, 3, 1);
        var recipients = MapiWire.ReadOpenMessageMetadata(r); onRecipients?.Invoke(recipients);
        if (r.Remaining != 0) throw new MapiException("Unexpected OpenMessage response tail.");
        if (packet.Handles.Length != 2 || packet.Handles[1] == uint.MaxValue)
            throw new MapiException("Message handle was not returned.");
        return packet.Handles;
    }

    public async Task<Dictionary<uint, object?>> ReadPropertiesAsync(uint[] handles, byte index, uint[] tags, CancellationToken ct = default)
    {
        var ops = MapiWire.Write(w =>
        {
            w.Write(new byte[] { 7, 0, index }); w.Write((ushort)0); w.Write((ushort)1);
            w.Write(checked((ushort)tags.Length)); foreach (var tag in tags) w.Write(tag);
        });
        var packet = await ExecuteAsync(ops, handles, ct);
        var r = new WireReader(packet.Operations); MapiWire.RopHeader(r, 7, index);
        var result = MapiWire.ReadRow(r, tags);
        if (r.Remaining != 0) throw new MapiException("Unexpected property response tail.");
        return result;
    }

    public async Task<byte[]?> ReadStreamAsync(uint[] handles, byte owner, uint tag, int limit = 32 * 1024 * 1024, CancellationToken ct = default)
    {
        byte output = checked((byte)handles.Length);
        var table = handles.Concat(new[] { uint.MaxValue }).ToArray();
        var ops = MapiWire.Write(w => { w.Write(new byte[] { 0x2b, 0, owner, output }); w.Write(tag); w.Write((byte)0); });
        var packet = await ExecuteAsync(ops, table, ct);
        var r = new WireReader(packet.Operations);
        if (r.U8() != 0x2b || r.U8() != output) throw new MapiException("Unexpected OpenStream response.");
        var status = r.U32();
        if (status == 0x8004010f) return null;
        MapiWire.Success(status, "OpenStream");
        var expected = r.U32();
        var streamHandles = packet.Handles;
        try
        {
            if (expected > limit) throw new MapiException($"Property stream exceeds the {limit} byte limit.");
            using var stream = new MemoryStream();
            while (true)
            {
                packet = await ExecuteAsync(MapiWire.Write(w => { w.Write(new byte[] { 0x2c, 0, output }); w.Write((ushort)16384); }), streamHandles, ct);
                streamHandles = packet.Handles;
                r = new WireReader(packet.Operations); MapiWire.RopHeader(r, 0x2c, output);
                var data = r.Read(r.U16());
                if (r.Remaining != 0) throw new MapiException("Unexpected ReadStream response tail.");
                if (data.Length == 0) break;
                if (stream.Length + data.Length > limit) throw new MapiException("Property stream exceeded its maximum size.");
                stream.Write(data);
            }
            return stream.ToArray();
        }
        finally { if (!_poisoned) await ExecuteAsync([1, 0, output], streamHandles, ct); }
    }

    public async Task<MapiMessageData> ReadMessageAsync(MapiLogon logon, ulong folderId, ulong messageId, CancellationToken ct = default)
    {
        MapiRecipients? recipients = null;
        var handles = await OpenMessageAsync(logon, folderId, messageId, ct, onRecipients: value => recipients = value);
        try
        {
            var properties = await ReadPropertiesAsync(handles, 1, MessageProperties, ct);
            var html = await ReadStreamAsync(handles, 1, 0x10130102, ct: ct);
            byte[]? text = html is not { Length: > 0 } ? await ReadStreamAsync(handles, 1, 0x1000001f, ct: ct) : null;
            if (html is not { Length: > 0 } && text == null)
            {
                var rtf = await ReadStreamAsync(handles, 1, 0x10090102, ct: ct);
                if (rtf is { Length: > 0 }) throw new MapiException("RTF-only item bodies are not supported yet; refusing to present or overwrite them as empty text.");
            }
            var attachments = properties.GetValueOrDefault(0x0e1b000bu) is true
                ? await ReadAttachmentTableAsync(handles, ct) : [];
            return new(properties, html, text, attachments, recipients);
        }
        finally { if (!_poisoned) await ExecuteAsync([1, 0, 1], handles, ct); }
    }

    private async Task<List<Dictionary<uint, object?>>> ReadAttachmentTableAsync(uint[] messageHandles, CancellationToken ct)
    {
        var table = messageHandles.Concat(new[] { uint.MaxValue }).ToArray();
        var ops = MapiWire.Write(w =>
        {
            w.Write(new byte[] { 0x21, 0, 1, 2, 0x40 });
            w.Write(new byte[] { 0x12, 0, 2, 0 }); w.Write((ushort)AttachmentColumns.Length);
            foreach (var tag in AttachmentColumns) w.Write(tag);
            Query(w, 50);
        });
        var packet = await ExecuteAsync(ops, table, ct);
        table = packet.Handles;
        try
        {
            var r = new WireReader(packet.Operations);
            MapiWire.RopHeader(r, 0x21, 2);
            MapiWire.RopHeader(r, 0x12, 2);
            if (r.U8() != 0) throw new MapiException("Attachment columns did not complete synchronously.");
            var rows = new List<Dictionary<uint, object?>>();
            while (true)
            {
                MapiWire.RopHeader(r, 0x15, 2); var bookmark = r.U8(); var count = r.U16();
                for (int i = 0; i < count; i++) rows.Add(MapiWire.ReadRow(r, AttachmentColumns));
                if (r.Remaining != 0) throw new MapiException("Unexpected attachment table response tail.");
                if (bookmark == 2 || count == 0) return rows;
                if (rows.Count > 1000) throw new MapiException("Too many attachments for the preview reader.");
                packet = await ExecuteAsync(MapiWire.Write(w => Query(w, 50)), table, ct);
                table = packet.Handles; r = new WireReader(packet.Operations);
            }
        }
        finally { if (!_poisoned) await ExecuteAsync([1, 0, 2], table, ct); }
    }

    public async Task<byte[]> ReadAttachmentAsync(MapiLogon logon, ulong folderId, ulong messageId, uint number, CancellationToken ct = default)
    {
        var message = await OpenMessageAsync(logon, folderId, messageId, ct);
        uint[]? attachment = null;
        try
        {
            var packet = await ExecuteAsync(MapiWire.Write(w =>
            { w.Write(new byte[] { 0x22, 0, 1, 2, 0 }); w.Write(number); }), [.. message, uint.MaxValue], ct);
            MapiWire.RopHeader(new WireReader(packet.Operations), 0x22, 2);
            attachment = packet.Handles;
            return await ReadStreamAsync(attachment, 2, 0x37010102, 100 * 1024 * 1024, ct)
                ?? throw new MapiException("This attachment does not contain a file stream (embedded items are not supported yet).");
        }
        finally
        {
            if (!_poisoned && attachment != null) await ExecuteAsync([1, 0, 2], attachment, ct);
            if (!_poisoned) await ExecuteAsync([1, 0, 1], message, ct);
        }
    }
}
