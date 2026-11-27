using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Posta.App.Infrastructure;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Services;

internal static class OpenedMessageStore
{
    private const int MaximumBytes = 1024 * 1024;

    internal static async Task<MailMessageDetail> ReadAsync(IMailService service, string itemId, CancellationToken ct = default, bool allowCache = true)
    {
        var app = AppServices.Current;
        if (!app.Settings.WorkOffline && service.State == ConnectionState.Connected)
        {
            try
            {
                var message = await service.GetMessageAsync(itemId, ct);
                if (allowCache && !message.IsDraft &&
                    Get(app.Cache, service.Account.Id, itemId)?.Attachments.Any(a => !a.IsInline && a.Content != null) != true)
                    Save(app.Cache, service.Account.Id, message);
                return message;
            }
            catch (MailServiceException) when (service.State is ConnectionState.Error or ConnectionState.Offline)
            {
                // A dropped connection can use the last cached copy. Permission and item errors still surface.
            }
        }
        ct.ThrowIfCancellationRequested();
        return (allowCache ? Get(app.Cache, service.Account.Id, itemId) : null)
            ?? throw new MailServiceException(Loc.T("MessageUnavailableOffline"));
    }

    internal static async Task<(int Saved, int Skipped)> PrepareFolderAsync(IMailService service, MailFolderInfo folder, CancellationToken ct = default)
    {
        var app = AppServices.Current;
        var result = await service.GetItemsAsync(new MailQuery { FolderId = folder.Id, PageSize = 100 }, ct);
        var latest = result.Items.Take(25).ToList();
        var saved = 0;
        foreach (var summary in latest.AsEnumerable().Reverse())
        {
            if (app.Settings.WorkOffline || service.State != ConnectionState.Connected)
                throw new MailServiceException(Loc.T("OfflinePrepareNeedsConnection"));
            if (summary.IsDraft || summary.IsReadOnly) continue;
            try
            {
                var message = await service.GetMessageAsync(summary.Id, ct);
                var remaining = MaximumBytes - Encoding.UTF8.GetByteCount(message.BodyHtml);
                foreach (var attachment in message.Attachments.Where(a => a.Content == null && !a.IsItemAttachment && string.IsNullOrEmpty(a.LocalPath)))
                {
                    if (attachment.Size <= 0 || attachment.Size > remaining / 2) continue;
                    var bytes = await service.GetAttachmentContentAsync(message.Id, attachment.Id, ct);
                    if (bytes.Length > remaining / 2) continue;
                    attachment.Content = bytes;
                    remaining -= 2 * bytes.Length;
                }
                if (Save(app.Cache, service.Account.Id, message)) saved++;
            }
            catch (MailServiceException) when (service.State == ConnectionState.Connected) { }
        }
        app.Cache.ReplaceItems(service.Account.Id, folder.Id, result.Items);
        return (saved, latest.Count - saved);
    }

    internal static Task<byte[]> ReadAttachmentAsync(IMailService service, string itemId, string attachmentId)
    {
        var app = AppServices.Current;
        if (!app.Settings.WorkOffline && service.State == ConnectionState.Connected)
            return service.GetAttachmentContentAsync(itemId, attachmentId);
        var bytes = Get(app.Cache, service.Account.Id, itemId)?.Attachments.FirstOrDefault(a => a.Id == attachmentId)?.Content;
        return bytes != null ? Task.FromResult(bytes) : Task.FromException<byte[]>(new MailServiceException(Loc.T("OfflineAttachmentUnavailable")));
    }

    private static byte[] Entropy(string accountId, string itemId) => Encoding.UTF8.GetBytes($"Posta|{accountId}|{itemId}");

    private static bool Save(MessageCache cache, string accountId, MailMessageDetail message)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(message);
            if (json.Length > MaximumBytes) return false;
            cache.SaveOpenedMessage(accountId, message.Id, ProtectedData.Protect(json, Entropy(accountId, message.Id), DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or Microsoft.Data.Sqlite.SqliteException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"Posta could not cache the opened message ({ex.GetType().Name}).");
            return false;
        }
    }

    private static MailMessageDetail? Get(MessageCache cache, string accountId, string itemId)
    {
        try
        {
            var encrypted = cache.GetOpenedMessage(accountId, itemId);
            return encrypted == null ? null : JsonSerializer.Deserialize<MailMessageDetail>(
                ProtectedData.Unprotect(encrypted, Entropy(accountId, itemId), DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or Microsoft.Data.Sqlite.SqliteException or JsonException)
        {
            return null;
        }
    }
}
