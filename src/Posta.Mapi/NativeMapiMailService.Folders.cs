using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    // Default folders and the mailbox's special PIM folders must never be renamed, moved or deleted here.
    private bool IsProtected(ulong fid) =>
        Logon.FolderIds.Take(8).Contains(fid) || _specialFolders?.ContainsKey(fid) == true;

    public Task<string> CreateFolderAsync(string parentFolderId, string name, string folderClass, CancellationToken ct = default) => Run(async () =>
    {
        var fid = await Session.CreateFolderAsync(Logon, ParseFolder(parentFolderId), name.Trim(), null, ct);
        if (!string.IsNullOrWhiteSpace(folderClass))
            await Session.SetFolderPropertiesAsync(Logon, fid, new Dictionary<uint, object> { [0x3613001f] = folderClass }, ct);
        _folders = null;
        return FolderId(fid);
    }, ct);

    public Task RenameFolderAsync(string folderId, string newName, CancellationToken ct = default) => Run(async () =>
    {
        var fid = ParseFolder(folderId);
        if (IsProtected(fid)) throw new MailServiceException("A default mailbox folder cannot be renamed.");
        await Session.SetFolderPropertiesAsync(Logon, fid, new Dictionary<uint, object> { [0x3001001f] = newName.Trim() }, ct);
        _folders = null; return true;
    }, ct);

    public Task DeleteFolderAsync(string folderId, bool permanent, CancellationToken ct = default) => Run(async () =>
    {
        var fid = ParseFolder(folderId);
        if (IsProtected(fid)) throw new MailServiceException("A default mailbox folder cannot be deleted.");
        var self = (await Folders(ct)).FirstOrDefault(f => f.Id == folderId) ?? throw new MailServiceException("Folder not found.");
        if (string.IsNullOrEmpty(self.ParentId)) throw new MailServiceException("The mailbox root cannot be deleted.");
        await Session.DeleteFolderAsync(Logon, ParseFolder(self.ParentId), fid, permanent, ct);
        _folders = null; return true;
    }, ct);

    public Task MoveFolderAsync(string folderId, string newParentId, CancellationToken ct = default) => MoveCopyFolderAsync(folderId, newParentId, false, ct);
    public Task CopyFolderAsync(string folderId, string newParentId, CancellationToken ct = default) => MoveCopyFolderAsync(folderId, newParentId, true, ct);
    private Task MoveCopyFolderAsync(string folderId, string newParentId, bool copy, CancellationToken ct) => Run(async () =>
    {
        var fid = ParseFolder(folderId);
        if (!copy && IsProtected(fid)) throw new MailServiceException("A default mailbox folder cannot be moved.");
        var self = (await Folders(ct)).FirstOrDefault(f => f.Id == folderId) ?? throw new MailServiceException("Folder not found.");
        if (string.IsNullOrEmpty(self.ParentId)) throw new MailServiceException("The mailbox root cannot be moved or copied.");
        if (folderId == newParentId) throw new MailServiceException("A folder cannot be moved into itself.");
        await Session.MoveCopyFolderAsync(Logon, ParseFolder(self.ParentId), ParseFolder(newParentId), fid, self.DisplayName, copy, ct);
        _folders = null; return true;
    }, ct);

    public Task MarkAllReadAsync(string folderId, CancellationToken ct = default) => Run(async () =>
    {
        var fid = ParseFolder(folderId);
        uint[] columns = [0x674a0014, 0x0e070003];
        var table = await Session.ReadTableAsync(Logon, fid, false, columns, 5000, ct);
        if (table.Total > table.Rows.Count) throw new MailServiceException("This folder has more than 5000 items; marking all read is not supported at that size yet.");
        var unread = table.Rows.Where(r => (Number(r, 0x0e070003) & 1) == 0).Select(r => (ulong)r[0x674a0014u]!).ToArray();
        await Session.SetReadFlagsAsync(Logon, fid, unread, true, ct);
        _folders = null; return true;
    }, ct);

    public Task EmptyFolderAsync(string folderId, bool deleteSubFolders, CancellationToken ct = default) => Run(async () =>
    {
        var fid = ParseFolder(folderId);
        if (deleteSubFolders) await Session.EmptyFolderAsync(Logon, fid, ct);
        else
        {
            var table = await Session.ReadTableAsync(Logon, fid, false, [0x674a0014], 5000, ct);
            if (table.Total > table.Rows.Count) throw new MailServiceException("This folder has more than 5000 items; emptying it is not supported at that size yet.");
            await Session.DeleteMessagesAsync(Logon, fid, table.Rows.Select(r => (ulong)r[0x674a0014u]!).ToArray(), false, ct);
        }
        _folders = null; return true;
    }, ct);

    public Task MarkAsJunkAsync(IEnumerable<string> itemIds, bool isJunk, CancellationToken ct = default) => Run(async () =>
    {
        var target = isJunk ? await Session.ReadJunkFolderAsync(Logon, ct) : Logon.FolderIds[4];
        foreach (var group in itemIds.Select(ParseMessage).GroupBy(p => p.folder))
        {
            if (group.Key == target) continue;
            await Session.MoveCopyMessagesAsync(Logon, group.Key, target, group.Select(p => p.message).ToArray(), false, ct);
        }
        _folders = null; return true;
    }, ct);
}
