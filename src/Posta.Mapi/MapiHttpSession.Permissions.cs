namespace Posta.Mapi;

public sealed partial class MapiHttpSession
{
    internal Task ModifyPermissionsAsync(MapiLogon logon, ulong folder, bool freeBusy,
        IReadOnlyList<(byte Action, Dictionary<uint, object> Values)> changes, CancellationToken ct)
        => ModifyFolderMetadataAsync(logon, folder, 0x40, freeBusy ? (byte)2 : (byte)0, changes, ct);

    internal Task ModifyRulesAsync(MapiLogon logon, ulong folder,
        IReadOnlyList<(byte Action, Dictionary<uint, object> Values)> changes, CancellationToken ct)
        => ModifyFolderMetadataAsync(logon, folder, 0x41, 0, changes, ct);

    private async Task ModifyFolderMetadataAsync(MapiLogon logon, ulong folder, byte operation, byte flags,
        IReadOnlyList<(byte Action, Dictionary<uint, object> Values)> changes, CancellationToken ct)
    {
        if (changes.Count == 0) return;
        var request = MapiWire.Write(w =>
        {
            w.Write(new byte[] { 2, 0, 0, 1 }); w.Write(folder); w.Write((byte)0);
            w.Write(new byte[] { operation, 0, 1, flags });
            w.Write(checked((ushort)changes.Count));
            foreach (var (action, values) in changes)
            {
                w.Write(action);
                w.Write((ushort)values.Count);
                foreach (var (tag, value) in values) MapiWriteWire.Value(w, tag, value);
            }
        });
        var packet = await ExecuteAsync(request, [logon.Handle, uint.MaxValue], ct);
        try
        {
            var reader = new WireReader(packet.Operations);
            MapiWire.RopHeader(reader, 2, 1);
            reader.U8();
            if (reader.U8() != 0) throw new MapiException("Ghosted folder cannot be opened here.");
            MapiWire.RopHeader(reader, operation, 1);
            MapiWriteWire.End(reader);
        }
        finally
        {
            if (packet.Handles.Length > 1 && packet.Handles[1] != uint.MaxValue)
                await ReleaseSafelyAsync(packet.Handles, 1);
        }
    }

    public Task<IReadOnlyList<Dictionary<uint, object?>>> ReadPermissionsAsync(MapiLogon logon, ulong folder, bool freeBusy, CancellationToken ct)
        => ReadFolderMetadataTableAsync(logon, folder, 0x3e, freeBusy ? (byte)2 : (byte)0,
            [0x66710014, 0x6672001f, 0x66730003, 0x0fff0102], ct);

    internal Task<IReadOnlyList<Dictionary<uint, object?>>> ReadRulesAsync(MapiLogon logon, ulong folder, CancellationToken ct)
        => ReadFolderMetadataTableAsync(logon, folder, 0x3f, 0x40, [0x66740014, 0x66760003, 0x66770003, 0x6682001f], ct);

    private async Task<IReadOnlyList<Dictionary<uint, object?>>> ReadFolderMetadataTableAsync(MapiLogon logon, ulong folder,
        byte operation, byte flags, uint[] columns, CancellationToken ct)
    {
        var packet = await ExecuteAsync(MapiWire.Write(w =>
        {
            w.Write(new byte[] { 2, 0, 0, 1 }); w.Write(folder); w.Write((byte)0);
            w.Write(new byte[] { operation, 0, 1, 2, flags });
            w.Write(new byte[] { 0x12, 0, 2, 0 }); w.Write((ushort)columns.Length);
            foreach (var column in columns) w.Write(column);
            Query(w, 50);
        }), [logon.Handle, uint.MaxValue, uint.MaxValue], ct);
        var handles = packet.Handles;
        try
        {
            var reader = new WireReader(packet.Operations);
            MapiWire.RopHeader(reader, 2, 1);
            reader.U8();
            if (reader.U8() != 0) throw new MapiException("Ghosted folder cannot be opened here.");
            MapiWire.RopHeader(reader, operation, 2);
            MapiWire.RopHeader(reader, 0x12, 2);
            if (reader.U8() != 0) throw new MapiException("Metadata table columns did not complete synchronously.");
            var rows = new List<Dictionary<uint, object?>>();
            while (true)
            {
                MapiWire.RopHeader(reader, 0x15, 2);
                var bookmark = reader.U8();
                var count = reader.U16();
                for (int i = 0; i < count; i++) rows.Add(MapiWire.ReadRow(reader, columns));
                MapiWriteWire.End(reader);
                if (bookmark == 2 || count == 0) return rows;
                if (rows.Count >= 5000) throw new MapiException("Metadata table exceeds 5000 rows.");
                packet = await ExecuteAsync(MapiWire.Write(w => Query(w, 50)), handles, ct);
                handles = packet.Handles;
                reader = new WireReader(packet.Operations);
            }
        }
        finally
        {
            if (handles.Length > 2 && handles[2] != uint.MaxValue) await ReleaseSafelyAsync(handles, 2);
            if (handles.Length > 1 && handles[1] != uint.MaxValue) await ReleaseSafelyAsync(handles, 1);
        }
    }
}
