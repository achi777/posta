using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Posta.App.Controls;
using Posta.App.Infrastructure;
using Posta.App.Views;
using Posta.App.Windows;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Services;

/// <summary>Mail commands shared by the main window, reading pane and item windows.</summary>
public static class MailActions
{
    private static AppSettings S => AppServices.Current.Settings;

    public static event EventHandler<DateTime>? CalendarNavigationRequested;

    public static void ShowCalendarAt(DateTime date) => CalendarNavigationRequested?.Invoke(null, date);

    // ------------------------------------------------------------------ Compose

    public static string SignatureFor(IMailService svc, bool reply)
    {
        var d = AppServices.Current.SettingsService.GetSignatureDefaults(svc.Account.Id);
        var name = reply ? d.RepliesForwards : d.NewMessages;
        var sig = S.Signatures.FirstOrDefault(s => s.Name == name);
        return sig?.Html ?? "";
    }

    public static string DefaultBodyStyle() =>
        $"font-family:{S.ComposeFontFamily.Replace("\"", "'")};font-size:{S.ComposeFontSizePt}pt;color:{S.ComposeFontColor}";

    public static void ComposeNew(IMailService? svc, IEnumerable<Recipient>? to = null, string? subject = null, string? bodyHtml = null,
        List<AttachmentInfo>? attachments = null, IEnumerable<Recipient>? cc = null)
    {
        svc ??= AppServices.Current.DefaultAccount;
        if (svc == null) return;
        var msg = new OutgoingMessage
        {
            AccountId = svc.Account.Id,
            To = to?.ToList() ?? new List<Recipient>(),
            Cc = cc?.ToList() ?? new List<Recipient>(),
            Subject = subject ?? "",
            Mode = ComposeMode.New,
            RequestReadReceipt = S.ReadReceiptAlways,
            RequestDeliveryReceipt = S.DeliveryReceiptAlways,
            Attachments = attachments ?? new List<AttachmentInfo>()
        };
        var body = bodyHtml ?? "<div><br></div>";
        msg.BodyHtml = body + "<div><br></div>" + HtmlUtil.WrapSignature(SignatureFor(svc, false));
        new ComposeWindow(svc, msg).Show();
    }

    public static void ComposeFromMailto(IMailService? svc, string uri)
    {
        try
        {
            var u = uri.Substring("mailto:".Length);
            var q = u.IndexOf('?');
            var addr = Uri.UnescapeDataString(q >= 0 ? u[..q] : u);
            string? subject = null, body = null;
            if (q >= 0)
            {
                foreach (var part in u[(q + 1)..].Split('&'))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length != 2) continue;
                    if (kv[0].Equals("subject", StringComparison.OrdinalIgnoreCase)) subject = Uri.UnescapeDataString(kv[1]);
                    if (kv[0].Equals("body", StringComparison.OrdinalIgnoreCase)) body = HtmlUtil.PlainTextToHtml(Uri.UnescapeDataString(kv[1]));
                }
            }
            ComposeNew(svc, addr.Split(';', ',').Where(a => a.Trim().Length > 0).Select(a => new Recipient("", a.Trim())), subject, body);
        }
        catch { ComposeNew(svc); }
    }

    public static string QuoteHeader(MailMessageDetail d)
    {
        var c = Loc.Instance.Culture;
        var sb = new System.Text.StringBuilder();
        sb.Append("<div style=\"border:none;border-top:solid #E1E1E1 1.0pt;padding:3.0pt 0in 0in 0in\"><p style=\"margin:0;font-family:Calibri,'Segoe UI',Sylfaen,sans-serif;font-size:11pt\">");
        sb.Append($"<b>{Loc.T("ReplyHeaderFrom")}</b> {HtmlUtil.Encode(d.From.FullDisplay)}<br>");
        sb.Append($"<b>{Loc.T("ReplyHeaderSent")}</b> {HtmlUtil.Encode((d.Sent > DateTime.MinValue ? d.Sent : d.Received).ToString("dddd, d MMMM yyyy HH:mm", c))}<br>");
        if (d.To.Count > 0) sb.Append($"<b>{Loc.T("ReplyHeaderTo")}</b> {HtmlUtil.Encode(HtmlUtil.RecipientsLineFull(d.To))}<br>");
        if (d.Cc.Count > 0) sb.Append($"<b>{Loc.T("ReplyHeaderCc")}</b> {HtmlUtil.Encode(HtmlUtil.RecipientsLineFull(d.Cc))}<br>");
        sb.Append($"<b>{Loc.T("ReplyHeaderSubject")}</b> {HtmlUtil.Encode(d.Subject)}</p></div>");
        return sb.ToString();
    }

    public static async Task ReplyAsync(IMailService svc, MailMessageDetail d, ComposeMode mode)
    {
        if (mode is ComposeMode.Reply or ComposeMode.ReplyAll && !d.ReplyRecipientsComplete)
        {
            Ui.Error("ამ წერილის სპეციალური Reply-To მიმღებები ჯერ ვერ იკითხება. პასუხის ავტომატური მისამართი არ იქნება გამოყენებული. / The special Reply-To list cannot be resolved yet. Automatic reply addressing is unavailable for this item.");
            return;
        }
        if (mode == ComposeMode.ReplyAll && !d.RecipientsComplete)
        {
            Ui.Error("მიმღებების სრული სია ვერ ჩაიტვირთა; Reply All შეჩერებულია მიმღებების დაკარგვის თავიდან ასაცილებლად. / The complete recipient list could not be loaded. Reply All is unavailable for this item.");
            return;
        }
        var me = svc.Account.Email;
        var msg = new OutgoingMessage
        {
            AccountId = svc.Account.Id,
            Mode = mode,
            SourceItemId = d.Id,
            RequestReadReceipt = S.ReadReceiptAlways,
            RequestDeliveryReceipt = S.DeliveryReceiptAlways
        };

        if (mode is ComposeMode.Reply or ComposeMode.ReplyAll)
        {
            var replyTo = d.ReplyTo.Count > 0 ? d.ReplyTo : new List<Recipient> { d.From };
            msg.To.AddRange(replyTo);
            if (mode == ComposeMode.ReplyAll)
            {
                foreach (var r in d.To.Where(r => !Same(r.Address, me) && !msg.To.Any(x => Same(x.Address, r.Address)))) msg.To.Add(r);
                foreach (var r in d.Cc.Where(r => !Same(r.Address, me) && !msg.To.Any(x => Same(x.Address, r.Address)))) msg.Cc.Add(r);
            }
            msg.Subject = HtmlUtil.SubjectPrefix(d.Subject, "RE:");
        }
        else
        {
            msg.Subject = HtmlUtil.SubjectPrefix(d.Subject, "FW:");
            msg.Attachments = d.Attachments.Where(a => !a.IsInline).Select(a => new AttachmentInfo
            {
                Id = a.Id, Name = a.Name, Size = a.Size, ContentType = a.ContentType, IsItemAttachment = a.IsItemAttachment
            }).ToList();
        }

        var original = HtmlUtil.BodyContent(HtmlUtil.Sanitize(d.BodyHtml));
        msg.BodyHtml = "<div><br></div>" + HtmlUtil.WrapSignature(SignatureFor(svc, true)) + "<div><br></div>" +
                       "<div id=\"divRplyFwdMsg\">" + QuoteHeader(d) + "</div><div><br></div>" +
                       "<div id=\"OriginalBody\">" + original + "</div>";
        var w = new ComposeWindow(svc, msg);
        w.Show();
        if (S.CloseOriginalOnReply)
        {
            foreach (var mw in Application.Current.Windows.OfType<MessageWindow>().Where(x => x.MessageId == d.Id).ToList()) mw.Close();
        }
        await Task.CompletedTask;
    }

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    public static async Task ForwardAsAttachmentAsync(IMailService svc, MailMessageDetail d)
    {
        byte[]? mime = null;
        await Ui.RunAsync(async () => mime = await svc.GetMimeContentAsync(d.Id));
        if (mime == null) return;
        ComposeNew(svc, null, HtmlUtil.SubjectPrefix(d.Subject, "FW:"), null,
            new List<AttachmentInfo> { new() { Name = HtmlUtil.FileSafe(string.IsNullOrWhiteSpace(d.Subject) ? "message" : d.Subject) + ".eml", Content = mime, Size = mime.Length, ContentType = "message/rfc822" } });
    }

    public static void ReplyWithMeeting(IMailService svc, MailMessageDetail d)
    {
        var start = DateTime.Now.Date.AddHours(DateTime.Now.Hour + 1);
        var ev = new CalendarEvent
        {
            Subject = d.Subject, Start = start, End = start.AddMinutes(S.DefaultMeetingMinutes),
            BodyHtml = "<div><br></div>" + QuoteHeader(d) + HtmlUtil.BodyContent(HtmlUtil.Sanitize(d.BodyHtml)),
            ReminderSet = S.DefaultRemindersOn, ReminderMinutes = S.DefaultReminderMinutes, IsMeeting = true
        };
        ev.Attendees.Add(new Attendee { Name = d.From.Name, Email = d.From.Address });
        foreach (var r in d.To.Concat(d.Cc).Where(r => !Same(r.Address, svc.Account.Email)))
            ev.Attendees.Add(new Attendee { Name = r.Name, Email = r.Address });
        AppointmentWindow.OpenNew(svc, null, ev, true);
    }

    public static async Task OpenDraftAsync(IMailService svc, string draftId)
    {
        OutgoingMessage? msg = null;
        if (!await Ui.RunAsync(async () => msg = await svc.LoadDraftAsync(draftId))) return;
        new ComposeWindow(svc, msg!).Show();
    }

    public static void OpenMessageWindow(IMailService svc, MailMessageDetail d)
    {
        var existing = Application.Current.Windows.OfType<MessageWindow>().FirstOrDefault(w => w.MessageId == d.Id);
        if (existing != null) { existing.Activate(); return; }
        new MessageWindow(svc, d).Show();
    }

    public static async Task OpenItemAsync(IMailService svc, MailItemSummary s)
    {
        if (s.IsDraft && !s.IsReadOnly)
        {
            await OpenDraftAsync(svc, s.Id);
            return;
        }
        MailMessageDetail? d = null;
        if (!await Ui.RunAsync(async () => d = await OpenedMessageStore.ReadAsync(svc, s.Id, allowCache: !s.IsReadOnly))) return;
        OpenMessageWindow(svc, d!);
    }

    // ------------------------------------------------------------------ Meeting responses

    public static async Task<bool> RespondToMeetingAsync(IMailService svc, string itemId, string subject, MeetingResponse response, MeetingResponseMode mode, Window? owner)
    {
        string? comment = null;
        if (mode == MeetingResponseMode.Edit)
        {
            var title = $"{Loc.T(response == MeetingResponse.Accept ? "Accept" : response == MeetingResponse.Tentative ? "Tentative" : "Decline")}: {subject}";
            var dlg = new ResponseCommentWindow(title) { Owner = owner };
            if (dlg.ShowDialog() != true) return false;
            comment = dlg.CommentHtml;
        }
        return await Ui.RunAsync(() => svc.RespondToMeetingAsync(itemId, response, mode != MeetingResponseMode.DontSend, comment), owner);
    }

    // ------------------------------------------------------------------ Attachments & saving

    public static async Task<string?> DownloadAttachmentToTempAsync(IMailService svc, string itemId, AttachmentInfo a)
    {
        byte[]? data = a.Content;
        if (data == null && !await Ui.RunAsync(async () => data = await OpenedMessageStore.ReadAttachmentAsync(svc, itemId, a.Id))) return null;
        var dir = Path.Combine(Path.GetTempPath(), "Posta", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, HtmlUtil.FileSafe(a.Name));
        await File.WriteAllBytesAsync(path, data!);
        return path;
    }

    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".scr", ".pif", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh", ".ps1", ".msi", ".msp", ".hta", ".cpl", ".jar", ".lnk", ".reg"
    };

    public static async Task OpenAttachmentAsync(IMailService svc, string itemId, AttachmentInfo a, string? verb = null)
    {
        if (DangerousExtensions.Contains(Path.GetExtension(a.Name)))
        {
            if (!Ui.Confirm($"{a.Name}\n\n⚠ " + (Loc.Instance.IsGeorgian
                    ? "ამ ტიპის ფაილი შეიძლება საშიში იყოს. გავხსნა?"
                    : "Files of this type may harm your computer. Open anyway?"), null, MessageBoxImage.Warning)) return;
        }
        var path = await DownloadAttachmentToTempAsync(svc, itemId, a);
        if (path == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = verb ?? "" });
        }
        catch (Exception ex) { Ui.Error(ex.Message); }
    }

    public static async Task SaveAttachmentAsync(IMailService svc, string itemId, AttachmentInfo a, Window? owner)
    {
        var dlg = new SaveFileDialog { FileName = HtmlUtil.FileSafe(a.Name), Filter = Loc.T("AllFilesFilter") };
        if (dlg.ShowDialog(owner) != true) return;
        byte[]? data = a.Content;
        if (data == null && !await Ui.RunAsync(async () => data = await OpenedMessageStore.ReadAttachmentAsync(svc, itemId, a.Id), owner)) return;
        await File.WriteAllBytesAsync(dlg.FileName, data!);
    }

    public static async Task SaveAllAttachmentsAsync(IMailService svc, MailMessageDetail d, Window? owner)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("SaveAttachmentsTitle") };
        if (dlg.ShowDialog(owner) != true) return;
        await Ui.RunAsync(async () =>
        {
            foreach (var a in d.Attachments.Where(x => !x.IsInline))
            {
                var data = a.Content ?? await OpenedMessageStore.ReadAttachmentAsync(svc, d.Id, a.Id);
                var path = Path.Combine(dlg.FolderName, HtmlUtil.FileSafe(a.Name));
                var n = 1;
                while (File.Exists(path)) path = Path.Combine(dlg.FolderName, $"{Path.GetFileNameWithoutExtension(a.Name)} ({n++}){Path.GetExtension(a.Name)}");
                await File.WriteAllBytesAsync(path, data);
            }
        }, owner);
    }

    public static async Task SaveMessageAsAsync(IMailService svc, MailMessageDetail d, Window? owner)
    {
        var dlg = new SaveFileDialog { FileName = HtmlUtil.FileSafe(string.IsNullOrWhiteSpace(d.Subject) ? "message" : d.Subject), Filter = Loc.T("SaveMessageFilter") };
        if (dlg.ShowDialog(owner) != true) return;
        await Ui.RunAsync(async () =>
        {
            switch (dlg.FilterIndex)
            {
                case 1:
                    await File.WriteAllBytesAsync(dlg.FileName, await svc.GetMimeContentAsync(d.Id));
                    break;
                case 2:
                    await File.WriteAllTextAsync(dlg.FileName, HtmlView.Wrap(QuoteHeader(d) + "<br>" + HtmlUtil.BodyContent(d.BodyHtml)));
                    break;
                default:
                    await File.WriteAllTextAsync(dlg.FileName, HtmlUtil.ToPlainText(QuoteHeader(d) + "<br>" + d.BodyHtml));
                    break;
            }
        }, owner);
    }

    public static ReadingPane? ActiveReadingPane;
}
