using MimeKit;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    public async Task<byte[]> GetMimeContentAsync(string itemId, CancellationToken ct = default)
    {
        var detail = await GetMessageAsync(itemId, ct);
        if (!detail.RecipientsComplete || detail.Attachments.Any(attachment => attachment.IsItemAttachment))
            throw new MailServiceException("An EML file cannot be reconstructed with incomplete recipients or embedded Exchange items.");

        var message = new MimeMessage { Subject = detail.Subject };
        var timestamp = detail.Sent != DateTime.MinValue ? detail.Sent : detail.Received;
        if (timestamp != DateTime.MinValue) message.Date = new DateTimeOffset(timestamp);
        if (!string.IsNullOrWhiteSpace(detail.InternetMessageId))
            message.MessageId = detail.InternetMessageId.Trim().Trim('<', '>');
        if (!string.IsNullOrWhiteSpace(detail.From?.Address)) message.From.Add(MimeAddress(detail.From));
        message.To.AddRange(detail.To.Select(MimeAddress));
        message.Cc.AddRange(detail.Cc.Select(MimeAddress));
        message.Bcc.AddRange(detail.Bcc.Select(MimeAddress));

        var builder = new BodyBuilder { TextBody = HtmlUtil.ToPlainText(detail.BodyHtml) };
        var html = detail.BodyHtml;
        foreach (var attachment in detail.Attachments)
        {
            var bytes = attachment.Content ?? await GetAttachmentContentAsync(itemId, attachment.Id, ct);
            var type = ContentType.TryParse(attachment.ContentType, out var parsed) ? parsed : new ContentType("application", "octet-stream");
            var name = string.IsNullOrWhiteSpace(attachment.Name) ? "attachment" : attachment.Name;
            if (attachment.IsInline && !string.IsNullOrWhiteSpace(attachment.ContentId))
            {
                var resource = builder.LinkedResources.Add(name, bytes, type);
                resource.ContentId = attachment.ContentId.Trim('<', '>');
                if (attachment.Content != null)
                    html = html.Replace($"data:{attachment.ContentType};base64,{Convert.ToBase64String(bytes)}",
                        "cid:" + resource.ContentId, StringComparison.Ordinal);
            }
            else builder.Attachments.Add(name, bytes, type);
        }
        builder.HtmlBody = html;
        message.Body = builder.ToMessageBody();
        using var stream = new MemoryStream();
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        await message.WriteToAsync(options, stream, ct);
        return stream.ToArray();
    }

    private static MailboxAddress MimeAddress(Recipient recipient)
    {
        if (!MailboxAddress.TryParse(recipient.Address, out var address))
            throw new MailServiceException("An Exchange recipient does not have a valid internet email address for EML export.");
        return new MailboxAddress(recipient.Name, address.Address);
    }
}
