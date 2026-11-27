using System.Text;
using System.Text.RegularExpressions;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    public Task SetReadAsync(IEnumerable<string> itemIds, bool isRead, CancellationToken ct = default) => Run(async () =>
    {
        var groups = itemIds.Select(ParseMessage).GroupBy(p => p.folder).ToArray();
        foreach (var group in groups) await Session.SetReadFlagsAsync(Logon, group.Key, group.Select(p => p.message).ToArray(), isRead, ct);
        _folders = null; return true;
    }, ct);

    public Task SetImportanceAsync(string itemId, Importance importance, CancellationToken ct = default) => Run(async () =>
    {
        if (!Enum.IsDefined(importance)) throw new ArgumentException("Invalid importance.");
        var (fid, mid) = ParseMessage(itemId);
        await Session.SetMessagePropertiesAsync(Logon, fid, mid, new Dictionary<uint, object> { [0x00170003] = (uint)importance }, ct);
        return true;
    }, ct);

    public async Task SendAsync(OutgoingMessage message, CancellationToken ct = default)
    { await WriteOutgoingAsync(message, true, ct); }

    public Task<string> SaveDraftAsync(OutgoingMessage message, CancellationToken ct = default) => WriteOutgoingAsync(message, false, ct);

    private async Task<string> WriteOutgoingAsync(OutgoingMessage m, bool send, CancellationToken ct)
    {
        ValidateOutgoing(m, Account.Email, send);
        var recipients = m.To.Select(r => new MapiRecipient(1, r.Name, r.Address))
            .Concat(m.Cc.Select(r => new MapiRecipient(2, r.Name, r.Address)))
            .Concat(m.Bcc.Select(r => new MapiRecipient(3, r.Name, r.Address))).ToList();
        _ = MapiWriteWire.Recipients(1, recipients); // Validate all addresses before fetching/uploading anything.
        var outgoing = new List<MapiAttachment>();
        var uploaded = new List<AttachmentInfo>();
        foreach (var a in m.Attachments)
        {
            if (!string.IsNullOrEmpty(m.DraftId) && !string.IsNullOrEmpty(a.Id) && a.LocalPath == null && a.Content == null) continue;
            if (a.IsItemAttachment) throw new MailServiceException("Embedded-item attachments cannot be sent in native MAPI yet.");
            var data = a.Content;
            if (data == null && a.LocalPath != null)
            {
                var file = new FileInfo(a.LocalPath);
                if (file.Length > 100 * 1024 * 1024) throw new MailServiceException("Attachment exceeds 100 MB.");
                data = await File.ReadAllBytesAsync(a.LocalPath, ct);
            }
            if (data == null && m.SourceItemId != null && m.Mode == ComposeMode.Forward)
                data = await GetAttachmentContentAsync(m.SourceItemId, a.Id, ct);
            if (data == null) throw new MailServiceException("Attachment bytes are unavailable: " + a.Name);
            outgoing.Add(new(a.Name, a.ContentType, data, a.IsInline ? a.ContentId : null)); uploaded.Add(a);
        }
        var html = Regex.Replace(m.BodyHtml, "src\\s*=\\s*([\"'])data:(image/[a-zA-Z0-9.+-]+);base64,([^\"']+)\\1", match =>
        {
            var type = match.Groups[2].Value; var cid = Guid.NewGuid().ToString("N") + "@posta";
            outgoing.Add(new("image-" + cid.Split('@')[0], type, Convert.FromBase64String(match.Groups[3].Value), cid));
            return "src=\"cid:" + cid + "\"";
        }, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        var removed = m.RemovedAttachmentIds.Select(s => uint.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return await Run(async () =>
        {
            var folders = await Folders(ct);
            var drafts = folders.FirstOrDefault(f => f.Kind == FolderKind.Drafts)
                ?? throw new MailServiceException("The mailbox did not provide its Drafts folder.");
            var fid = ParseFolder(drafts.Id); ulong? mid = null;
            if (!string.IsNullOrEmpty(m.DraftId))
            {
                var parsed = ParseMessage(m.DraftId);
                if (parsed.folder != fid) throw new MailServiceException("Only an item in the mailbox Drafts folder can be edited/submitted.");
                mid = parsed.message;
                var current = await Session.ReadMessageAsync(Logon, fid, mid.Value, ct);
                if ((Number(current.Properties, 0x0e070003) & 8) == 0) throw new MailServiceException("This item is not an unsent draft.");
            }
            var props = new Dictionary<uint, object> { [0x001a001f] = "IPM.Note", [0x0037001f] = m.Subject,
                [0x00170003] = (uint)m.Importance, [0x00360003] = (uint)m.Sensitivity, [0x3fde0003] = 65001u,
                [0x1000001f] = HtmlUtil.ToPlainText(m.BodyHtml), [0x0029000b] = m.RequestReadReceipt,
                [0x0023000b] = m.RequestDeliveryReceipt, [0x0e01000b] = !m.SaveSentCopy };
            if (m.SaveSentCopy) props[0x674000fb] = MapiWire.Write(w => { w.Write((byte)1); w.Write(Logon.FolderIds[6]); w.Write(0ul); w.Write(0u); });
            if (m.SourceItemId != null && m.Mode is ComposeMode.Reply or ComposeMode.ReplyAll)
            {
                var (sourceSession, sourceLogon, sourceId, _) = await MailLocationAsync(m.SourceItemId, ct);
                var (sf, sm) = ParseMessage(sourceId);
                var source = await sourceSession.ReadMessageAsync(sourceLogon, sf, sm, ct);
                var internetId = Text(source.Properties, 0x1035001f);
                if (internetId.Length > 0) props[0x1042001f] = internetId;
            }
            (ulong Id, IReadOnlyList<uint> AttachmentNumbers) result;
            try { result = await Session.WriteMessageAsync(Logon, fid, mid, props, Encoding.UTF8.GetBytes(html), recipients, outgoing, removed, send, ct); }
            catch (MapiSubmissionException ex)
            {
                m.DraftId = $"mapi-m:{fid:X16}:{ex.SavedMessageId:X16}";
                // The saved object already owns attachments. Refuse an in-place retry until reloaded.
                m.SubmissionOutcomeUnknown = true;
                throw;
            }
            var id = $"mapi-m:{fid:X16}:{result.Id:X16}";
            if (!send)
            {
                m.DraftId = id;
                for (int i = 0; i < uploaded.Count; i++)
                { uploaded[i].Id = result.AttachmentNumbers[i].ToString(); uploaded[i].Content = null; uploaded[i].LocalPath = null; }
                // The persisted draft now owns inline images; subsequent saves replace their generated counterparts.
                m.RemovedAttachmentIds.Clear();
                foreach (var number in result.AttachmentNumbers.Skip(uploaded.Count)) m.RemovedAttachmentIds.Add(number.ToString());
            }
            _folders = null; return id;
        }, ct);
    }

    internal static void ValidateOutgoing(OutgoingMessage m, string accountEmail, bool send)
    {
        if (m.SubmissionOutcomeUnknown) throw new MailServiceException("This compose session cannot retry after an uncertain/failed submission. Check Sent Items, then reopen the saved Draft if it was not sent.");
        if (send && m.To.Count + m.Cc.Count + m.Bcc.Count == 0) throw new MailServiceException("At least one SMTP recipient is required.");
        if (!string.IsNullOrWhiteSpace(m.FromAddress) && !m.FromAddress.Equals(accountEmail, StringComparison.OrdinalIgnoreCase))
            throw new MailServiceException("Send-as/delegated mailboxes are not implemented in native MAPI yet.");
        if (m.DeferredDelivery != null || m.Categories.Count > 0 || m.Flag != FlagState.NotFlagged || m.Mode == ComposeMode.ForwardAsAttachment)
            throw new MailServiceException("Deferred delivery, outgoing categories/flags and embedded-message forwarding are not implemented in native MAPI yet.");
        if (!Enum.IsDefined(m.Importance) || !Enum.IsDefined(m.Sensitivity)) throw new MailServiceException("Invalid message importance/sensitivity.");
    }

    public async Task<OutgoingMessage> LoadDraftAsync(string draftId, CancellationToken ct = default)
    {
        var d = await GetMessageAsync(draftId, ct);
        if (!d.IsDraft) throw new MailServiceException("The selected item is not a draft.");
        if (!d.RecipientsComplete) throw new MailServiceException("The complete SMTP recipient list could not be read. Editing would risk losing recipients.");
        return new() { AccountId = Account.Id, DraftId = d.Id, Mode = ComposeMode.EditDraft, Subject = d.Subject,
            BodyHtml = d.BodyHtml, To = d.To, Cc = d.Cc, Bcc = d.Bcc, Importance = d.Importance,
            Sensitivity = d.Sensitivity, Attachments = d.Attachments.Where(a => !a.IsInline).ToList(),
            RemovedAttachmentIds = d.Attachments.Where(a => a.IsInline).Select(a => a.Id).ToList() };
    }
}
