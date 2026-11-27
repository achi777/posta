namespace Posta.Mapi;

// MS-OXCFOLD folder operations. Each request opens the folders it needs by id and releases
// every handle it acquires, success or failure, so a refused operation never leaks server state.
public sealed partial class MapiHttpSession
{
    // MS-OXCFOLD 2.2.1.2 RopCreateFolder (Generic, Unicode, no OpenExisting).
    public async Task<ulong> CreateFolderAsync(MapiLogon logon, ulong parent, string name, string? comment, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var ops = MapiWire.Write(w =>
        {
            w.Write(new byte[] { 2, 0, 0, 1 }); w.Write(parent); w.Write((byte)0);        // OpenFolder parent -> handle 1
            w.Write(new byte[] { 0x1c, 0, 1, 2, 1, 1, 0, 0 });                            // CreateFolder: Generic, Unicode, no-open-existing, reserved
            MapiWriteWire.UnicodeZ(w, name); MapiWriteWire.UnicodeZ(w, comment ?? "");
        });
        var packet = await ExecuteAsync(ops, [logon.Handle, uint.MaxValue, uint.MaxValue], ct);
        var handles = packet.Handles;
        try
        {
            var r = new WireReader(packet.Operations);
            MapiWire.RopHeader(r, 2, 1); r.U8(); if (r.U8() != 0) throw new MapiException("Ghosted parent folder unsupported.");
            MapiWire.RopHeader(r, 0x1c, 2);
            var fid = r.U64();
            if (r.U8() != 0) throw new MapiException("A folder with that name already exists.");
            MapiWriteWire.End(r);
            return fid;
        }
        finally { await ReleaseSafelyAsync(handles, 2); await ReleaseSafelyAsync(handles, 1); }
    }

    // MS-OXCFOLD 2.2.1.3 RopDeleteFolder. Soft delete (DEL_MESSAGES | DEL_FOLDERS); items remain recoverable.
    public async Task DeleteFolderAsync(MapiLogon logon, ulong parent, ulong folder, bool hard, CancellationToken ct = default)
    {
        var ops = MapiWire.Write(w =>
        {
            w.Write(new byte[] { 2, 0, 0, 1 }); w.Write(parent); w.Write((byte)0);
            w.Write(new byte[] { 0x1d, 0, 1, (byte)(hard ? 0x15 : 0x05) }); w.Write(folder);
        });
        var packet = await ExecuteAsync(ops, [logon.Handle, uint.MaxValue], ct);
        var handles = packet.Handles;
        try
        {
            var r = new WireReader(packet.Operations);
            MapiWire.RopHeader(r, 2, 1); r.U8(); if (r.U8() != 0) throw new MapiException("Ghosted parent folder unsupported.");
            MapiWire.RopHeader(r, 0x1d, 1);
            if (r.U8() != 0) throw new MapiException("The server only partially deleted the folder.");
            MapiWriteWire.End(r);
        }
        finally { await ReleaseSafelyAsync(handles, 1); }
    }

    // MS-OXCFOLD 2.2.1.5 RopEmptyFolder (soft). Removes messages and subfolders of the opened folder.
    public async Task EmptyFolderAsync(MapiLogon logon, ulong folder, CancellationToken ct = default)
    {
        var ops = MapiWire.Write(w =>
        {
            w.Write(new byte[] { 2, 0, 0, 1 }); w.Write(folder); w.Write((byte)0);
            w.Write(new byte[] { 0x58, 0, 1, 0, 0 }); // RopEmptyFolder: WantAsynchronous=0, WantDeleteAssociated=0
        });
        var packet = await ExecuteAsync(ops, [logon.Handle, uint.MaxValue], ct);
        var handles = packet.Handles;
        try
        {
            var r = new WireReader(packet.Operations);
            MapiWire.RopHeader(r, 2, 1); r.U8(); if (r.U8() != 0) throw new MapiException("Ghosted folder unsupported.");
            MapiWire.RopHeader(r, 0x58, 1);
            if (r.U8() != 0) throw new MapiException("The server only partially emptied the folder.");
            MapiWriteWire.End(r);
        }
        finally { await ReleaseSafelyAsync(handles, 1); }
    }

    // MS-OXCFOLD 2.2.1.9 RopDeleteMessages (soft). Deletes only the named messages, leaving subfolders intact.
    public async Task DeleteMessagesAsync(MapiLogon logon, ulong folder, ulong[] messages, bool hard, CancellationToken ct = default)
    {
        if (messages.Length == 0) return;
        var ops = MapiWire.Write(w =>
        { w.Write(new byte[] { 2, 0, 0, 1 }); w.Write(folder); w.Write((byte)0); });
        var packet = await ExecuteAsync(ops, [logon.Handle, uint.MaxValue], ct);
        var handles = packet.Handles;
        try
        {
            var r = new WireReader(packet.Operations);
            MapiWire.RopHeader(r, 2, 1); r.U8(); if (r.U8() != 0) throw new MapiException("Ghosted folder unsupported.");
            MapiWriteWire.End(r);
            foreach (var batch in messages.Chunk(100))
            {
                packet = await ExecuteAsync(MapiWire.Write(w =>
                {
                    w.Write(new byte[] { (byte)(hard ? 0x91 : 0x1e), 0, 1, 0, 0 }); // WantAsynchronous=0, NotifyNonRead=0
                    w.Write((ushort)batch.Length); foreach (var mid in batch) w.Write(mid);
                }), handles, ct);
                r = new(packet.Operations); MapiWire.RopHeader(r, (byte)(hard ? 0x91 : 0x1e), 1);
                if (r.U8() != 0) throw new MapiException("The server only partially deleted the selected messages.");
                MapiWriteWire.End(r);
            }
        }
        finally { await ReleaseSafelyAsync(handles, 1); }
    }

    // MS-OXCFOLD 2.2.1.6 RopMoveFolder / 2.2.1.7 RopCopyFolder.
    public async Task MoveCopyFolderAsync(MapiLogon logon, ulong sourceParent, ulong destParent, ulong folder, string newName, bool copy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var ops = MapiWire.Write(w =>
        {
            w.Write(new byte[] { 2, 0, 0, 1 }); w.Write(sourceParent); w.Write((byte)0);  // OpenFolder source parent -> 1
            w.Write(new byte[] { 2, 0, 0, 2 }); w.Write(destParent); w.Write((byte)0);     // OpenFolder dest parent   -> 2
            if (copy) w.Write(new byte[] { 0x36, 0, 1, 2, 0, 1, 1 });                      // Copy: async=0, recursive=1, unicode=1
            else w.Write(new byte[] { 0x35, 0, 1, 2, 0, 1 });                              // Move: async=0, unicode=1
            w.Write(folder); MapiWriteWire.UnicodeZ(w, newName);
        });
        var packet = await ExecuteAsync(ops, [logon.Handle, uint.MaxValue, uint.MaxValue], ct);
        var handles = packet.Handles;
        try
        {
            var r = new WireReader(packet.Operations);
            for (byte i = 1; i <= 2; i++) { MapiWire.RopHeader(r, 2, i); r.U8(); if (r.U8() != 0) throw new MapiException("Ghosted folders unsupported."); }
            MapiWire.RopHeader(r, (byte)(copy ? 0x36 : 0x35), 1);
            if (r.U8() != 0) throw new MapiException($"The server only partially {(copy ? "copied" : "moved")} the folder.");
            MapiWriteWire.End(r);
        }
        finally { await ReleaseSafelyAsync(handles, 2); await ReleaseSafelyAsync(handles, 1); }
    }

    // SetProperties on a folder object (rename, container class).
    public async Task SetFolderPropertiesAsync(MapiLogon logon, ulong folder, IReadOnlyDictionary<uint, object> values, CancellationToken ct = default)
    {
        var encoded = MapiWriteWire.Properties(1, values); // Validate before opening the folder.
        var ops = MapiWire.Write(w =>
        { w.Write(new byte[] { 2, 0, 0, 1 }); w.Write(folder); w.Write((byte)0); });
        var packet = await ExecuteAsync(ops, [logon.Handle, uint.MaxValue], ct);
        var handles = packet.Handles;
        try
        {
            var r = new WireReader(packet.Operations);
            MapiWire.RopHeader(r, 2, 1); r.U8(); if (r.U8() != 0) throw new MapiException("Ghosted folder unsupported.");
            MapiWriteWire.End(r);
            packet = await ExecuteAsync(encoded, handles, ct);
            r = new(packet.Operations); MapiWriteWire.PropertyResult(r, 1); MapiWriteWire.End(r);
        }
        finally { await ReleaseSafelyAsync(handles, 1); }
    }

    // The Junk Email folder is advertised as entry 4 of PidTagAdditionalRenEntryIds on the Inbox.
    public async Task<ulong> ReadJunkFolderAsync(MapiLogon logon, CancellationToken ct = default)
    {
        var row = await ReadFolderPropertiesAsync(logon, logon.FolderIds[4], [0x36d81102], ct);
        if (row.GetValueOrDefault(0x36d81102u) is not byte[][] { Length: > 4 } ids || ids[4] is not { Length: 46 } entry)
            throw new MapiException("The mailbox does not advertise a Junk Email folder.");
        if (entry[20] != 1 || entry[21] != 0 || new Guid(entry[4..20]) != logon.MailboxGuid)
            throw new MapiException("Junk Email EntryID does not belong to the connected mailbox.");
        var packet = await ExecuteAsync(MapiWire.Write(w => { w.Write(new byte[] { 0x44, 0, 0 }); w.Write(entry[22..]); }), [logon.Handle], ct);
        var r = new WireReader(packet.Operations); MapiWire.RopHeader(r, 0x44, 0); var fid = r.U64(); MapiWriteWire.End(r);
        return fid;
    }
}
