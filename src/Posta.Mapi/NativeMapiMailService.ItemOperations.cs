using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    public Task MoveItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default)
        => MoveCopyAsync(itemIds, destinationFolderId, false, ct);
    public Task CopyItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default)
        => MoveCopyAsync(itemIds, destinationFolderId, true, ct);
    private Task MoveCopyAsync(IEnumerable<string> ids, string destination, bool copy, CancellationToken ct) => Run(async () =>
    {
        var target = ParseFolder(destination);
        var groups = ids.Select(ParseMessage).GroupBy(p => p.folder).ToArray();
        foreach (var group in groups)
        {
            if (!copy && group.Key == target) continue;
            await Session.MoveCopyMessagesAsync(Logon, group.Key, target, group.Select(p => p.message).ToArray(), copy, ct);
        }
        _folders = null; return true;
    }, ct);

    public Task DeleteItemsAsync(IEnumerable<string> itemIds, bool permanent, CancellationToken ct = default) => Run(async () =>
    {
        var groups = itemIds.Select(ParseMessage).GroupBy(p => p.folder).ToArray();
        foreach (var group in groups)
        {
            var messages = group.Select(p => p.message).ToArray();
            // Items already in Deleted Items, or an explicit permanent delete, are hard-deleted; everything else moves to Deleted Items.
            if (permanent || group.Key == Logon.FolderIds[7])
                await Session.DeleteMessagesAsync(Logon, group.Key, messages, true, ct);
            else
                await Session.MoveCopyMessagesAsync(Logon, group.Key, Logon.FolderIds[7], messages, false, ct);
        }
        _folders = null; return true;
    }, ct);
}
