namespace Posta.Mapi;

public sealed partial class MapiHttpSession
{
    public async Task MoveCopyMessagesAsync(MapiLogon logon, ulong source, ulong destination, ulong[] messages, bool copy, CancellationToken ct = default)
    {
        if (messages.Length == 0) return;
        var p = await ExecuteAsync(MapiWire.Write(w =>
        {
            w.Write(new byte[] { 2, 0, 0, 1 }); w.Write(source); w.Write((byte)0);
            w.Write(new byte[] { 2, 0, 0, 2 }); w.Write(destination); w.Write((byte)0);
        }), [logon.Handle, uint.MaxValue, uint.MaxValue], ct);
        var handles = p.Handles;
        try
        {
            var r = new WireReader(p.Operations);
            for (byte i = 1; i <= 2; i++)
            { MapiWire.RopHeader(r, 2, i); r.U8(); if (r.U8() != 0) throw new MapiException("Ghosted folders unsupported."); }
            MapiWriteWire.End(r);
            foreach (var batch in messages.Chunk(100))
            {
                p = await ExecuteAsync(MapiWire.Write(w =>
                {
                    w.Write(new byte[] { 0x33, 0, 1, 2 }); w.Write((ushort)batch.Length);
                    foreach (var mid in batch) w.Write(mid); w.Write((byte)0); w.Write(copy);
                }), handles, ct);
                r = new(p.Operations); MapiWire.RopHeader(r, 0x33, 1);
                if (r.U8() != 0) throw new MapiException("Server only partially moved/copied the selected messages.");
                MapiWriteWire.End(r);
            }
        }
        finally { await ReleaseSafelyAsync(handles, 2); await ReleaseSafelyAsync(handles, 1); }
    }
}
