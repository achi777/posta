namespace Posta.Mapi;

public sealed partial class MapiHttpSession
{
    public async Task SetMessagePropertiesAsync(MapiLogon logon, ulong folder, ulong message,
        IReadOnlyDictionary<uint, object> values, CancellationToken ct = default, uint[]? deleteProperties = null)
    {
        var encoded = MapiWriteWire.Properties(1, values); // Validate before acquiring a writable object.
        var handles = await OpenMessageAsync(logon, folder, message, ct, writable: true);
        try
        {
            var p = await ExecuteAsync(encoded, handles, ct);
            var r = new WireReader(p.Operations); MapiWriteWire.PropertyResult(r, 1); MapiWriteWire.End(r);
            await DeletePropertiesAsync(handles, deleteProperties, ct);
            await SaveMessageAsync(handles, false, ct);
        }
        finally { await ReleaseSafelyAsync(handles, 1); }
    }

    private async Task ReleaseSafelyAsync(uint[] handles, byte index)
    {
        if (_poisoned) return;
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await ExecuteAsync([1, 0, index], handles, timeout.Token); }
        catch { /* Preserve the operation's error. A failed transport already poisons the session. */ }
    }

    private async Task<ulong> SaveMessageAsync(uint[] handles, bool submit, CancellationToken ct)
    {
        // Save and submit are separate requests: do not submit when a save was refused.
        var packet = await ExecuteAsync([0x0c, 0, 1, 1, 2], handles, ct);
        var r = new WireReader(packet.Operations); MapiWire.RopHeader(r, 0x0c, 1);
        if (r.U8() != 1) throw new MapiException("Invalid saved-message handle.");
        var id = r.U64(); MapiWriteWire.End(r);
        if (submit)
        {
            bool answered = false;
            try
            {
                packet = await ExecuteAsync([0x32, 0, 1, 0], handles, ct); answered = true;
                r = new(packet.Operations); MapiWire.RopHeader(r, 0x32, 1); MapiWriteWire.End(r);
            }
            catch (Exception ex)
            {
                throw new MapiSubmissionException(id, !answered, !answered
                    ? "The connection failed during submission. Delivery is unknown. Check Sent Items and Drafts before sending again. " + ex.Message
                    : "Exchange refused submission; the message remains saved in Drafts. " + ex.Message);
            }
        }
        return id;
    }

    public async Task<(ulong Id, IReadOnlyList<uint> AttachmentNumbers)> WriteMessageAsync(MapiLogon logon, ulong folder, ulong? existing,
        IReadOnlyDictionary<uint, object> properties, byte[] html, IReadOnlyList<MapiRecipient> recipients,
        IReadOnlyList<MapiAttachment> attachments, IReadOnlyList<uint> removedAttachments, bool submit, CancellationToken ct = default, uint[]? deleteProperties = null)
    {
        var encoded = MapiWriteWire.Properties(1, properties);
        var recipientOps = MapiWriteWire.Recipients(1, recipients);
        foreach (var a in attachments)
        {
            if (a.Content.Length > 100 * 1024 * 1024) throw new ArgumentException("Attachment exceeds 100 MB.");
            _ = AttachmentProperties(a); // String/size validation before any mutation.
        }
        uint[] handles;
        if (existing.HasValue) handles = await OpenMessageAsync(logon, folder, existing.Value, ct, writable: true);
        else
        {
            var p = await ExecuteAsync(MapiWire.Write(w =>
            { w.Write(new byte[] { 6, 0, 0, 1 }); w.Write((ushort)0xfff); w.Write(folder); w.Write((byte)0); }), [logon.Handle, uint.MaxValue], ct);
            var r = new WireReader(p.Operations); MapiWire.RopHeader(r, 6, 1);
            if (r.U8() != 0) r.U64(); MapiWriteWire.End(r);
            handles = p.Handles;
        }
        try
        {
            var p = await ExecuteAsync(encoded, handles, ct);
            var r = new WireReader(p.Operations); MapiWriteWire.PropertyResult(r, 1); MapiWriteWire.End(r);
            await DeletePropertiesAsync(handles, deleteProperties, ct);
            // Replacement, not append: removing a Cc/Bcc recipient must really remove that recipient.
            p = await ExecuteAsync([0x0d, 0, 1, 0, 0, 0, 0], handles, ct);
            r = new(p.Operations); MapiWire.RopHeader(r, 0x0d, 1); MapiWriteWire.End(r);
            if (recipients.Count > 0)
            {
                p = await ExecuteAsync(recipientOps, handles, ct);
                r = new(p.Operations); MapiWire.RopHeader(r, 0x0e, 1); MapiWriteWire.End(r);
            }
            await WriteStreamAsync(handles, 1, 0x10130102, html, ct);
            foreach (var number in removedAttachments.Distinct())
            {
                p = await ExecuteAsync(MapiWire.Write(w => { w.Write(new byte[] { 0x24, 0, 1 }); w.Write(number); }), handles, ct);
                r = new(p.Operations); MapiWire.RopHeader(r, 0x24, 1); MapiWriteWire.End(r);
            }
            var numbers = new List<uint>();
            foreach (var a in attachments) numbers.Add(await WriteAttachmentAsync(handles, a, ct));
            var id = await SaveMessageAsync(handles, submit, ct);
            return (id, numbers);
        }
        finally { await ReleaseSafelyAsync(handles, 1); }
    }

    private static byte[] AttachmentProperties(MapiAttachment a)
    {
        var props = new Dictionary<uint, object> { [0x37050003] = 1u, [0x3707001f] = a.Name,
            [0x3704001f] = a.Name, [0x370e001f] = a.ContentType, [0x7ffe000b] = !string.IsNullOrEmpty(a.ContentId) };
        if (!string.IsNullOrEmpty(a.ContentId)) props[0x3712001f] = a.ContentId;
        return MapiWriteWire.Properties(2, props);
    }

    private async Task DeletePropertiesAsync(uint[] handles, uint[]? tags, CancellationToken ct)
    {
        if (tags is not { Length: > 0 }) return;
        var p = await ExecuteAsync(MapiWire.Write(w =>
        { w.Write(new byte[] { 0x0b, 0, 1 }); w.Write(checked((ushort)tags.Length)); foreach (var tag in tags) w.Write(tag); }), handles, ct);
        var r = new WireReader(p.Operations); MapiWriteWire.PropertyResult(r, 1, 0x0b); MapiWriteWire.End(r);
    }

    private async Task<uint> WriteAttachmentAsync(uint[] message, MapiAttachment a, CancellationToken ct)
    {
        var p = await ExecuteAsync([0x23, 0, 1, 2], [.. message, uint.MaxValue], ct);
        var r = new WireReader(p.Operations); MapiWire.RopHeader(r, 0x23, 2); var number = r.U32(); MapiWriteWire.End(r);
        var handles = p.Handles;
        try
        {
            p = await ExecuteAsync(AttachmentProperties(a), handles, ct);
            r = new(p.Operations); MapiWriteWire.PropertyResult(r, 2); MapiWriteWire.End(r);
            await WriteStreamAsync(handles, 2, 0x37010102, a.Content, ct);
            p = await ExecuteAsync([0x25, 0, 2, 2, 2], handles, ct);
            r = new(p.Operations); MapiWire.RopHeader(r, 0x25, 2); MapiWriteWire.End(r);
            return number;
        }
        finally { await ReleaseSafelyAsync(handles, 2); }
    }

    public async Task WriteStreamAsync(uint[] ownerHandles, byte owner, uint tag, byte[] data, CancellationToken ct = default)
    {
        var output = checked((byte)ownerHandles.Length);
        var p = await ExecuteAsync(MapiWire.Write(w =>
        { w.Write(new byte[] { 0x2b, 0, owner, output }); w.Write(tag); w.Write((byte)2); }), [.. ownerHandles, uint.MaxValue], ct);
        var r = new WireReader(p.Operations); MapiWire.RopHeader(r, 0x2b, output); r.U32(); MapiWriteWire.End(r);
        var handles = p.Handles;
        try
        {
            // SetStreamSize is necessary when updating a draft with a shorter body.
            p = await ExecuteAsync(MapiWire.Write(w => { w.Write(new byte[] { 0x2f, 0, output }); w.Write((ulong)data.Length); }), handles, ct);
            r = new(p.Operations); MapiWire.RopHeader(r, 0x2f, output); MapiWriteWire.End(r);
            for (int offset = 0; offset < data.Length;)
            {
                var count = Math.Min(16384, data.Length - offset);
                p = await ExecuteAsync(MapiWire.Write(w =>
                { w.Write(new byte[] { 0x2d, 0, output }); w.Write((ushort)count); w.Write(data, offset, count); }), handles, ct);
                r = new(p.Operations); MapiWire.RopHeader(r, 0x2d, output); var written = r.U16(); MapiWriteWire.End(r);
                if (written == 0 || written > count) throw new MapiException("Invalid/zero stream write; message was not saved.");
                offset += written;
            }
            p = await ExecuteAsync([0x5d, 0, output], handles, ct);
            r = new(p.Operations); MapiWire.RopHeader(r, 0x5d, output); MapiWriteWire.End(r);
        }
        finally { await ReleaseSafelyAsync(handles, output); }
    }

    public async Task SetReadFlagsAsync(MapiLogon logon, ulong folder, ulong[] messages, bool read, CancellationToken ct = default)
    {
        if (messages.Length == 0) return;
        var p = await ExecuteAsync(MapiWire.Write(w =>
        { w.Write(new byte[] { 2, 0, 0, 1 }); w.Write(folder); w.Write((byte)0); }), [logon.Handle, uint.MaxValue], ct);
        var r = new WireReader(p.Operations); MapiWire.RopHeader(r, 2, 1); r.U8();
        if (r.U8() != 0) throw new MapiException("Ghosted folder unsupported."); MapiWriteWire.End(r);
        var handles = p.Handles;
        try
        {
            foreach (var batch in messages.Chunk(100))
            {
                p = await ExecuteAsync(MapiWire.Write(w =>
                {
                    w.Write(new byte[] { 0x66, 0, 1, 0, read ? (byte)1 : (byte)4 });
                    w.Write((ushort)batch.Length); foreach (var mid in batch) w.Write(mid);
                }), handles, ct);
                r = new(p.Operations); MapiWire.RopHeader(r, 0x66, 1);
                if (r.U8() != 0) throw new MapiException("Server only partially changed the read flags.");
                MapiWriteWire.End(r);
            }
        }
        finally { await ReleaseSafelyAsync(handles, 1); }
    }
}
