using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    private static readonly MapiPropertyName[] ResponseNames =
    [new(AppointmentSet, 0x8220, 0x40), new(MeetingSet, 0x04, 0x0b)];

    private static async Task ReadMeetingMailDetailsAsync(MapiHttpSession session, MapiLogon logon, ulong folder, ulong mid,
        MailMessageDetail detail, CancellationToken ct)
    {
        var tags = await session.ResolvePropertyTagsAsync(logon, [CalendarNames[0], CalendarNames[1], CalendarNames[2], CalendarNames[7], MeetingRequestNames[9], CalendarNames[5]], false, ct);
        var row = await session.ReadMessagePropertiesAsync(logon, folder, mid, [0x00600040, 0x00610040, .. tags.Where(t => t != 0)], ct);
        detail.MeetingStart = NullableDate(row, tags[0]) ?? NullableDate(row, 0x00600040);
        detail.MeetingEnd = NullableDate(row, tags[1]) ?? NullableDate(row, 0x00610040);
        detail.MeetingLocation = Text(row, tags[2]);
        detail.MeetingIsOutOfDate = Number(row, tags[4]) == 0x00080000;
        detail.CanRespondToMeeting = !detail.Id.StartsWith(SharedMailPrefix, StringComparison.Ordinal) &&
            folder != logon.FolderIds[5] && folder != logon.FolderIds[6] && folder != logon.FolderIds[7] &&
            !detail.MeetingIsOutOfDate && Number(row, tags[4]) != 0x00100000 && row.GetValueOrDefault(tags[5]) is not true;
        detail.MyResponse = Number(row, tags[3]) switch
        {
            2 => MeetingResponse.Tentative, 3 => MeetingResponse.Accept, 4 => MeetingResponse.Decline, _ => MeetingResponse.None
        };
    }

    public Task RespondToMeetingAsync(string itemId, MeetingResponse response, bool sendResponse, string? comment, CancellationToken ct = default)
        => Run(async () =>
        {
            var (status, suffix, subjectPrefix, icon) = response switch
            {
                MeetingResponse.Accept => (3u, "Pos", "Accepted: ", 0x405u),
                MeetingResponse.Tentative => (2u, "Tent", "Tentative: ", 0x407u),
                MeetingResponse.Decline => (4u, "Neg", "Declined: ", 0x406u),
                _ => throw new MailServiceException("Select Accept, Tentative or Decline.")
            };
            var (id, occurrence) = CalendarItemId(itemId);
            if (occurrence.HasValue || id.StartsWith(SharedCalendarPrefix, StringComparison.Ordinal) || id.StartsWith(SharedMailPrefix, StringComparison.Ordinal))
                throw new MailServiceException("Respond from your primary calendar. Shared and recurring meetings are not supported here.");
            var (folder, mid) = ParseMessage(id);
            var calendarFolder = ParseFolder((await DefaultFolderAsync(FolderKind.Calendar, ct)).Id);
            if (folder == Logon.FolderIds[5] || folder == Logon.FolderIds[6] || folder == Logon.FolderIds[7])
                throw new MailServiceException("Respond using the invitation in your Inbox or calendar.");
            var names = await Session.ResolvePropertyTagsAsync(Logon, CalendarNames, false, ct);
            var options = await Session.ResolvePropertyTagsAsync(Logon, MeetingRequestNames, false, ct);
            if (new[] { names[0], names[1], names[3], names[7], names[8], names[18], names[19] }.Any(tag => tag == 0))
                throw new MailServiceException("Exchange did not return this meeting's identity or response properties.");
            uint[] columns = [.. CalendarColumns(names), 0x00620003, 0x0063000b, 0x5d01001f, 0x0c1a001f, .. options.Where(t => t != 0)];
            var selected = await Session.ReadMessagePropertiesAsync(Logon, folder, mid, columns, ct);
            var messageClass = Text(selected, 0x001a001f);
            var appointment = selected;
            var target = mid;
            var canceled = messageClass == MeetingCanceledClass;
            if (messageClass == MeetingRequestClass || canceled)
            {
                if (canceled && (response != MeetingResponse.Decline || sendResponse))
                    throw new MailServiceException("The organizer canceled this meeting. Remove it from your calendar.");
                if (Number(selected, options[9]) is 0x00080000 or 0x00100000 || Number(selected, names[8]) != (canceled ? 7u : 3u) ||
                    selected.GetValueOrDefault(names[18]) is not byte[] requestId || selected.GetValueOrDefault(names[5]) is true)
                    throw new MailServiceException("This invitation is out of date, delegated, or part of a recurring series.");
                var matches = await Session.ReadTableAsync(Logon, calendarFolder, false, [0x674a0014], 2, ct,
                    MapiRestrictions.Property(names[18], requestId));
                if (matches.Total != 1 || matches.Rows.Count != 1 || matches.Rows[0].GetValueOrDefault(0x674a0014u) is not ulong matched)
                    throw new MailServiceException("A unique matching appointment was not found in your calendar.");
                target = matched;
                appointment = await Session.ReadMessagePropertiesAsync(Logon, calendarFolder, target, columns, ct);
                var selectedSequence = Number(selected, options[1]);
                var currentSequence = Number(appointment, options[1]);
                if (canceled ? selectedSequence < currentSequence : selectedSequence != currentSequence)
                    throw new MailServiceException("This invitation and the calendar version differ. Respond from the current calendar entry.");
            }
            else if (messageClass != "IPM.Appointment" || folder != calendarFolder)
                throw new MailServiceException("Select an incoming meeting invitation or a meeting in your primary calendar.");

            var state = Number(appointment, names[8]);
            if (Text(appointment, 0x001a001f) != "IPM.Appointment" || (state != 3 && !(canceled && state == 7)) ||
                appointment.GetValueOrDefault(names[5]) is true || appointment.GetValueOrDefault(options[10]) is true ||
                appointment.GetValueOrDefault(options[12]) is true)
                throw new MailServiceException("This meeting is recurring, canceled, organized by you, or has an unresolved submission.");
            if (canceled)
            {
                await Session.MoveCopyMessagesAsync(Logon, calendarFolder, Logon.FolderIds[7], [target], false, ct);
                return true;
            }
            var start = Date(appointment, names[0]); var end = Date(appointment, names[1]);
            if (start == DateTime.MinValue || start >= end) throw new MailServiceException("The meeting time is invalid.");
            var responseTags = await Session.ResolvePropertyTagsAsync(Logon, ResponseNames, true, ct);
            if (responseTags.Any(tag => tag == 0)) throw new MailServiceException("Exchange did not return the response properties.");
            var now = DateTime.UtcNow;
            var intended = appointment.GetValueOrDefault(options[3]) is uint busy ? busy : (uint)ShowAs.Busy;
            var update = new Dictionary<uint, object>
            {
                [names[3]] = response == MeetingResponse.Accept ? intended : response == MeetingResponse.Tentative && intended != 0 ? 1u : 0u,
                [names[7]] = status, [responseTags[0]] = now
            };
            if (response == MeetingResponse.Decline && names[10] != 0) update[names[10]] = false;
            if (sendResponse)
            {
                if (selected.GetValueOrDefault(0x0063000bu) is false || appointment.GetValueOrDefault(0x0063000bu) is false)
                    throw new MailServiceException("The organizer did not request a reply. Use the response option without sending a message.");
                var address = Text(appointment, 0x5d02001f);
                if (address.Length == 0) address = Text(selected, 0x5d02001f);
                if (address.Length == 0 && messageClass == MeetingRequestClass) address = Text(selected, 0x5d01001f);
                if (address.Length == 0 || address.Equals(Account.Email, StringComparison.OrdinalIgnoreCase))
                    throw new MailServiceException("The organizer's SMTP address could not be determined.");
                await SendAttendeeResponseAsync(appointment, names, options, calendarFolder, target, responseTags[1], start, end,
                    $"IPM.Schedule.Meeting.Resp.{suffix}", subjectPrefix, icon, address, comment ?? "", update, ct);
            }
            else await Session.SetMessagePropertiesAsync(Logon, calendarFolder, target, update, ct);
            if (response == MeetingResponse.Decline)
                await Session.MoveCopyMessagesAsync(Logon, calendarFolder, Logon.FolderIds[7], [target], false, ct);
            return true;
        }, ct);

    private async Task SendAttendeeResponseAsync(Dictionary<uint, object?> appointment, uint[] names, uint[] options,
        ulong folder, ulong mid, uint silentTag, DateTime start, DateTime end, string messageClass, string prefix, uint icon,
        string address, string comment, Dictionary<uint, object> update, CancellationToken ct)
    {
        var submissionTags = await Session.ResolvePropertyTagsAsync(Logon, [MeetingRequestNames[6], MeetingRequestNames[12]], true, ct);
        if (submissionTags.Any(tag => tag == 0) || appointment.GetValueOrDefault(names[18]) is not byte[] global ||
            appointment.GetValueOrDefault(names[19]) is not byte[] cleanGlobal)
            throw new MailServiceException("This meeting does not have a valid identity for an Exchange response.");
        var to = new[] { new MapiRecipient(1, Text(appointment, 0x0042001f), address) };
        _ = MapiWriteWire.Recipients(1, to);
        var plain = HtmlUtil.ToPlainText(comment);
        var properties = new Dictionary<uint, object>
        {
            [0x001a001f] = messageClass, [0x10800003] = icon, [0x003d001f] = prefix,
            [0x0037001f] = prefix + Text(appointment, 0x0037001f), [0x00620003] = Number(appointment, 0x00620003),
            [0x00600040] = start, [0x00610040] = end, [0x0063000b] = false, [0x3fde0003] = 65001u, [0x1000001f] = plain,
            [names[0]] = start, [names[1]] = end, [names[7]] = 0u,
            [names[18]] = global, [names[19]] = cleanGlobal, [silentTag] = string.IsNullOrWhiteSpace(plain),
            [submissionTags[0]] = DateTime.UtcNow
        };
        if (options[1] != 0) properties[options[1]] = Number(appointment, options[1]);
        if (options[5] != 0 && appointment.GetValueOrDefault(options[5]) is DateTime ownerChange) properties[options[5]] = ownerChange;
        _ = MapiWriteWire.Properties(1, properties);
        await Session.SetMessagePropertiesAsync(Logon, folder, mid, new Dictionary<uint, object> { [submissionTags[1]] = true }, ct);
        try
        {
            await SendMeetingMessageAsync(properties, comment, to, ct);
            update[submissionTags[1]] = false;
            await Session.SetMessagePropertiesAsync(Logon, folder, mid, update, ct);
        }
        catch (Exception ex)
        {
            throw new MailServiceException("Check Sent Items and Outbox before sending another response. The previous response may have been submitted. " + ex.Message, ex);
        }
    }
}
