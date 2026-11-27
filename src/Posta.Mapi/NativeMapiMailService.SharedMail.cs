using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    private const string SharedMailPrefix = "mapi-mail:";
    private readonly Dictionary<string, DelegateMailbox> _sharedMailboxes = new(StringComparer.OrdinalIgnoreCase);

    private async Task<DelegateMailbox> ConnectSharedMailAsync(string email, CancellationToken ct)
    {
        var owner = DelegateOwner(email);
        if (_sharedMailboxes.TryGetValue(owner, out var existing))
        {
            if (existing.Session.IsUsable) return existing;
            _sharedMailboxes.Remove(owner);
            await existing.Session.DisposeAsync();
        }
        var mailbox = await OpenDelegateMailboxAsync(owner, ct);
        _sharedMailboxes.Add(owner, mailbox);
        return mailbox;
    }

    private async Task<IReadOnlyList<MailFolderInfo>> SharedMailFoldersAsync(string email, CancellationToken ct)
    {
        var mailbox = await ConnectSharedMailAsync(email, ct);
        var (owner, session, logon) = mailbox;
        var root = logon.FolderIds[3];
        var folders = new List<MailFolderInfo>();
        try
        {
            var page = await session.ReadTableAsync(logon, root, true, MapiHttpSession.HierarchyColumns, 5000, ct);
            if (page.Total > page.Rows.Count) throw new MailServiceException("This mailbox contains more than 5000 visible folders.");
            foreach (var row in page.Rows)
            {
                if (row.GetValueOrDefault(0x67480014u) is not ulong fid || fid == root) continue;
                var parent = row.GetValueOrDefault(0x67490014u) is ulong pid && pid != root ? pid : (ulong?)null;
                var folder = SharedMailFolder(mailbox, fid, row, parent);
                if (folder.IsMailFolder) folders.Add(folder);
            }
        }
        catch (MapiException ex) when (ex.OperationCode is 0x80070005 or 0x8004010f)
        {
            // Folder-only delegation may expose Inbox without the mailbox hierarchy.
        }

        if (folders.Count == 0)
        {
            var inbox = logon.FolderIds[4];
            try
            {
                await session.ReadTableAsync(logon, inbox, false, [0x674a0014], 1, ct);
                var row = await session.ReadFolderPropertiesAsync(logon, inbox, [0x3001001f, 0x3613001f, 0x36020003, 0x36030003], ct);
                folders.Add(SharedMailFolder(mailbox, inbox, row, null));
            }
            catch (MapiException ex) when (ex.OperationCode is 0x80070005 or 0x8004010f)
            {
                throw new MailServiceException("Exchange did not grant access to the shared mailbox's mail folders.", ex);
            }
        }
        var keys = folders.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (folder.ParentId != null && !keys.Contains(folder.ParentId)) folder.ParentId = null;
            folder.ChildFolderCount = folders.Count(child => child.ParentId == folder.Id);
        }
        return folders;
    }

    private MailFolderInfo SharedMailFolder(DelegateMailbox mailbox, ulong fid, Dictionary<uint, object?> row, ulong? parent)
    {
        var (owner, _, logon) = mailbox;
        var prefix = SharedMailPrefix + owner + "|";
        var kind = fid == logon.FolderIds[4] ? FolderKind.Inbox : fid == logon.FolderIds[6] ? FolderKind.SentItems
            : fid == logon.FolderIds[7] ? FolderKind.DeletedItems : FolderKind.Generic;
        return new MailFolderInfo
        {
            Id = prefix + FolderId(fid), ParentId = parent.HasValue ? prefix + FolderId(parent.Value) : null,
            AccountId = Account.Id, MailboxEmail = owner, DisplayName = Text(row, 0x3001001f), FolderClass = Text(row, 0x3613001f),
            Kind = kind, UnreadCount = (int)Number(row, 0x36030003), TotalCount = (int)Number(row, 0x36020003)
        };
    }

    private async Task<(MapiHttpSession Session, MapiLogon Logon, string Id, string Prefix)> MailLocationAsync(string id, CancellationToken ct)
    {
        if (!id.StartsWith(SharedMailPrefix, StringComparison.Ordinal)) return (Session, Logon, id, "");
        var separator = id.IndexOf('|', SharedMailPrefix.Length);
        if (separator <= SharedMailPrefix.Length) throw new MailServiceException("Invalid shared mail identifier.");
        var mailbox = await ConnectSharedMailAsync(id[SharedMailPrefix.Length..separator], ct);
        return (mailbox.Session, mailbox.Logon, id[(separator + 1)..], SharedMailPrefix + mailbox.Owner + "|");
    }

    private async Task CloseSharedMailAsync()
    {
        foreach (var mailbox in _sharedMailboxes.Values) await mailbox.Session.DisposeAsync();
        _sharedMailboxes.Clear();
    }
}
