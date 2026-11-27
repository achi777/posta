using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<PermissionEntry, PermissionIdentity> _permissionIdentities = new();
    private sealed record PermissionIdentity(Guid Mailbox, ulong Folder, ulong Member, Dictionary<ulong, uint> Snapshot);
    private static readonly byte[] AddressBookProvider = Convert.FromHexString("DCA740C8C042101AB4B908002B2FE182");

    public Task<IReadOnlyList<PermissionEntry>> GetFolderPermissionsAsync(string folderId, CancellationToken ct = default)
        => Run<IReadOnlyList<PermissionEntry>>(async () =>
        {
            await Folders(ct);
            var folder = ParseFolder(folderId);
            var freeBusy = _specialFolders?.GetValueOrDefault(folder) == FolderKind.Calendar;
            var rows = await Session.ReadPermissionsAsync(Logon, folder, freeBusy, ct);
            var result = new List<PermissionEntry>();
            var snapshot = new Dictionary<ulong, uint>();
            foreach (var row in rows)
            {
                if (row.GetValueOrDefault(0x66710014u) is not ulong memberId || row.GetValueOrDefault(0x66730003u) is not uint rights)
                    throw new MailServiceException("Exchange returned an incomplete permission row.");
                var entry = DecodePermission(memberId, Text(row, 0x6672001f), rights, freeBusy);
                if (row.GetValueOrDefault(0x0fff0102u) is byte[] { Length: > 28 } identity &&
                    identity.AsSpan(4, 16).SequenceEqual(AddressBookProvider) && _addressBookUri != null)
                {
                    var dn = new WireReader(identity[28..]).Text(false);
                    var directory = await DirectoryAsync(ct);
                    entry.Email = Text(directory.FirstOrDefault(r => string.Equals(Text(r, 0x3003001f), dn, StringComparison.OrdinalIgnoreCase)) ?? [], 0x39fe001f);
                }
                _permissionIdentities.Add(entry, new(Logon.MailboxGuid, folder, memberId, snapshot));
                snapshot.Add(memberId, rights);
                result.Add(entry);
            }
            return result;
        }, ct);

    public Task SetFolderPermissionsAsync(string folderId, IEnumerable<PermissionEntry> permissions, CancellationToken ct = default)
        => Run(async () =>
        {
            var folder = ParseFolder(folderId);
            var entries = permissions.ToArray();
            var anchor = entries.Select(p => _permissionIdentities.TryGetValue(p, out var id) ? id : null).FirstOrDefault(id => id != null);
            if (anchor == null || anchor.Mailbox != Logon.MailboxGuid || anchor.Folder != folder)
                throw new MailServiceException("Reload the permissions before editing them.");
            var snapshot = anchor.Snapshot;
            var freeBusy = _specialFolders?.GetValueOrDefault(folder) == FolderKind.Calendar;
            var rows = await Session.ReadPermissionsAsync(Logon, folder, freeBusy, ct);
            var current = rows.ToDictionary(row => (ulong)row[0x66710014u]!, row => (uint)row[0x66730003u]!);
            if (snapshot.Count != current.Count || snapshot.Any(pair => !current.TryGetValue(pair.Key, out var rights) || rights != pair.Value))
                throw new MailServiceException("Server permissions changed. Reload them before saving.");
            var changes = new List<(byte Action, Dictionary<uint, object> Values)>();
            var retained = new HashSet<ulong>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var rights = EncodePermission(entry, freeBusy);
                if (_permissionIdentities.TryGetValue(entry, out var identity))
                {
                    if (!ReferenceEquals(identity.Snapshot, snapshot) ||
                        !current.TryGetValue(identity.Member, out var previous) || !retained.Add(identity.Member))
                        throw new MailServiceException("A permission entry is stale or duplicated. Reload permissions before saving.");
                    var baseline = EncodePermission(DecodePermission(identity.Member, "", previous, freeBusy), freeBusy);
                    var changed = rights ^ baseline;
                    if ((changed & 0x1801) != 0) changed |= freeBusy ? 0x1801u : 1u;
                    if ((changed & 0x28) != 0) changed |= 0x28;
                    if ((changed & 0x50) != 0) changed |= 0x50;
                    if (changed != 0)
                        changes.Add((2, new() { [0x66710014] = identity.Member, [0x66730003] = (previous & ~changed) | (rights & changed) }));
                    continue;
                }
                if (entry.StandardUser != null || string.IsNullOrWhiteSpace(entry.Email) || !added.Add(entry.Email))
                    throw new MailServiceException("A new permission must identify a unique directory recipient.");
                var directory = await DirectoryAsync(ct);
                var matches = directory.Where(row => string.Equals(Text(row, 0x39fe001f), entry.Email, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (matches.Length != 1 || !Text(matches[0], 0x3003001f).StartsWith("/o=", StringComparison.OrdinalIgnoreCase))
                    throw new MailServiceException("The recipient could not be uniquely resolved to an Exchange address book object.");
                var address = MapiWire.Write(w =>
                {
                    w.Write(0u); w.Write(AddressBookProvider); w.Write(1u);
                    w.Write(Number(matches[0], 0x0ffe0003) == 8 ? 1u : 0u);
                    MapiWire.AsciiZ(w, Text(matches[0], 0x3003001f));
                });
                changes.Add((1, new() { [0x0fff0102] = address, [0x66730003] = rights }));
            }
            foreach (var member in current.Keys.Where(id => id != 0 && id != ulong.MaxValue && !retained.Contains(id)))
                changes.Add((4, new() { [0x66710014] = member }));
            await Session.ModifyPermissionsAsync(Logon, folder, freeBusy, changes, ct);
            return true;
        }, ct);

    private static uint EncodePermission(PermissionEntry entry, bool freeBusy)
    {
        if (!freeBusy && entry.ReadItems is ReadAccess.TimeOnly or ReadAccess.TimeAndSubjectAndLocation)
            throw new MailServiceException("Free/busy permissions are supported on the default calendar only.");
        var rights = entry.ReadItems switch
        {
            ReadAccess.FullDetails => 1u, ReadAccess.TimeOnly => 0x800u,
            ReadAccess.TimeAndSubjectAndLocation => 0x1800u, _ => 0u
        };
        if (entry.CanCreateItems) rights |= 2;
        if (entry.CanCreateSubFolders) rights |= 0x80;
        if (entry.IsFolderOwner) rights |= 0x100;
        if (entry.IsFolderContact) rights |= 0x200;
        if (entry.IsFolderVisible) rights |= 0x400;
        rights |= entry.EditItems switch { ItemAccessScope.Owned => 8u, ItemAccessScope.All => 0x28u, _ => 0u };
        rights |= entry.DeleteItems switch { ItemAccessScope.Owned => 0x10u, ItemAccessScope.All => 0x50u, _ => 0u };
        return rights;
    }

    internal static PermissionEntry DecodePermission(ulong memberId, string name, uint rights, bool freeBusy)
    {
        if (!freeBusy) rights &= ~0x1800u;
        var permission = new PermissionEntry
        {
            Name = name,
            StandardUser = memberId switch { 0 => "Default", ulong.MaxValue => "Anonymous", _ => null },
            CanCreateItems = (rights & 2) != 0,
            CanCreateSubFolders = (rights & 0x80) != 0,
            IsFolderOwner = (rights & 0x100) != 0,
            IsFolderContact = (rights & 0x200) != 0,
            IsFolderVisible = (rights & 0x400) != 0,
            EditItems = (rights & 0x20) != 0 ? ItemAccessScope.All : (rights & 8) != 0 ? ItemAccessScope.Owned : ItemAccessScope.None,
            DeleteItems = (rights & 0x40) != 0 ? ItemAccessScope.All : (rights & 0x10) != 0 ? ItemAccessScope.Owned : ItemAccessScope.None,
            ReadItems = (rights & 1) != 0 ? ReadAccess.FullDetails : (rights & 0x1000) != 0 ? ReadAccess.TimeAndSubjectAndLocation
                : (rights & 0x800) != 0 ? ReadAccess.TimeOnly : ReadAccess.None
        };
        permission.Level = (rights & ((rights & 1) != 0 ? ~0x1800u : uint.MaxValue)) switch
        {
            0 => PermissionLevel.None, 0x5fb or 0x7fb => PermissionLevel.Owner,
            0x4fb => PermissionLevel.PublishingEditor, 0x47b => PermissionLevel.Editor,
            0x49b => PermissionLevel.PublishingAuthor, 0x41b => PermissionLevel.Author,
            0x413 => PermissionLevel.NoneditingAuthor, 0x401 => PermissionLevel.Reviewer,
            0x402 => PermissionLevel.Contributor, 0x800 => PermissionLevel.FreeBusyTimeOnly,
            0x1800 => PermissionLevel.FreeBusyTimeAndSubjectAndLocation, _ => PermissionLevel.Custom
        };
        return permission;
    }
}
