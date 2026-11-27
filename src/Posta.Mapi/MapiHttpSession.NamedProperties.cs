namespace Posta.Mapi;

public sealed record MapiPropertyName(Guid Set, uint Lid, ushort Type, string? Name = null);

public sealed partial class MapiHttpSession
{
    public async Task<uint[]> ResolvePropertyTagsAsync(MapiLogon logon, IReadOnlyList<MapiPropertyName> names, bool create, CancellationToken ct = default)
    {
        var p = await ExecuteAsync(MapiWire.Write(w =>
        {
            w.Write(new byte[] { 0x56, 0, 0, create ? (byte)2 : (byte)0 }); w.Write(checked((ushort)names.Count));
            foreach (var n in names)
            {
                w.Write(n.Name == null ? (byte)0 : (byte)1); w.Write(n.Set.ToByteArray());
                if (n.Name == null) w.Write(n.Lid);
                else { w.Write(checked((byte)(System.Text.Encoding.Unicode.GetByteCount(n.Name) + 2))); MapiWriteWire.UnicodeZ(w, n.Name); }
            }
        }), [logon.Handle], ct);
        var r = new WireReader(p.Operations); MapiWire.RopHeader(r, 0x56, 0);
        if (r.U16() != names.Count) throw new MapiException("Named property response count mismatch.");
        var tags = names.Select(n => { var id = r.U16(); return id == 0 ? 0u : ((uint)id << 16) | n.Type; }).ToArray();
        MapiWriteWire.End(r);
        if (create && tags.Any(t => t == 0)) throw new MapiException("The store refused to map a required named property.");
        return tags;
    }

    public async Task<Dictionary<uint, object?>> ReadMessagePropertiesAsync(MapiLogon logon, ulong folder, ulong message, uint[] tags, CancellationToken ct = default)
    {
        var handles = await OpenMessageAsync(logon, folder, message, ct);
        try { return await ReadPropertiesAsync(handles, 1, tags, ct); }
        finally { await ReleaseSafelyAsync(handles, 1); }
    }
}
