using Posta.Core.Models;

namespace Posta.Mapi;

public sealed partial class MapiHttpSession
{
    public async Task<Dictionary<ulong, FolderKind>> ReadSpecialFoldersAsync(MapiLogon logon, CancellationToken ct = default)
    {
        var tags = new Dictionary<uint, FolderKind> { [0x36d70102] = FolderKind.Drafts, [0x36d00102] = FolderKind.Calendar,
            [0x36d10102] = FolderKind.Contacts, [0x36d40102] = FolderKind.Tasks, [0x36d30102] = FolderKind.Notes, [0x36d20102] = FolderKind.Journal };
        var row = await ReadFolderPropertiesAsync(logon, logon.FolderIds[4], tags.Keys.ToArray(), ct);
        var result = new Dictionary<ulong, FolderKind>();
        foreach (var (tag, kind) in tags)
        {
            if (row.GetValueOrDefault(tag) is not byte[] bytes) continue;
            var fid = await ResolveFolderEntryIdAsync(logon, bytes, ct);
            result[fid] = kind;
        }
        return result;
    }

    internal async Task<ulong> ReadDelegatorCalendarIdAsync(MapiLogon logon, CancellationToken ct)
    {
        const uint calendarEntryId = 0x36d00102;
        var properties = await ReadFolderPropertiesAsync(logon, logon.FolderIds[0], [calendarEntryId], ct);
        if (properties.GetValueOrDefault(calendarEntryId) is not byte[] bytes)
            throw new MapiException("Exchange did not expose the owner's default calendar to this account.");
        return await ResolveFolderEntryIdAsync(logon, bytes, ct);
    }

    private async Task<ulong> ResolveFolderEntryIdAsync(MapiLogon logon, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length != 46 || bytes[20] != 1 || bytes[21] != 0 || new Guid(bytes[4..20]) != logon.MailboxGuid)
            throw new MapiException("Special folder EntryID does not belong to the connected private mailbox.");
        var packet = await ExecuteAsync(MapiWire.Write(w =>
        {
            w.Write(new byte[] { 0x44, 0, 0 });
            w.Write(bytes[22..]);
        }), [logon.Handle], ct);
        var reader = new WireReader(packet.Operations);
        MapiWire.RopHeader(reader, 0x44, 0);
        var id = reader.U64();
        MapiWriteWire.End(reader);
        return id;
    }
}
