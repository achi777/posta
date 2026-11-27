using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    private const string MeetingRequestClass = "IPM.Schedule.Meeting.Request";
    private const string MeetingCanceledClass = "IPM.Schedule.Meeting.Canceled";
    private static readonly Guid PostaMeetingSet = new("5b031e7d-355f-411a-93c7-720c21623cc8");
    private static readonly MapiPropertyName[] MeetingRequestNames =
    [
        new(AppointmentSet, 0x8229, 0x0b), // FInvited
        new(AppointmentSet, 0x8201, 0x03), // AppointmentSequence
        new(AppointmentSet, 0x8202, 0x40), // AppointmentSequenceTime
        new(AppointmentSet, 0x8224, 0x03), // IntendedBusyStatus
        new(AppointmentSet, 0x825a, 0x0b), // AppointmentNotAllowPropose
        new(MeetingSet, 0x1a, 0x40), // OwnerCriticalChange
        new(MeetingSet, 0x01, 0x40), // AttendeeCriticalChange
        new(MeetingSet, 0x02, 0x1f), // Where
        new(MeetingSet, 0x24, 0x1f), // AppointmentMessageClass
        new(MeetingSet, 0x26, 0x03), // MeetingType
        new(MeetingSet, 0x05, 0x0b), // IsRecurring
        new(CommonSet, 0x8560, 0x40), // ReminderSignalTime
        new(PostaMeetingSet, 0x01, 0x0b), // Submission status could not yet be recorded.
        new(AppointmentSet, 0x8203, 0x03), // AppointmentLastSequence
        new(AppointmentSet, 0x8204, 0x03) // ChangeHighlight
    ];

    private static void ValidateMeetingSend(CalendarSource source, CalendarEvent ev, bool send)
    {
        if (ev.MeetingSubmissionNeedsReview)
            throw new MailServiceException("Check Sent Items and the calendar before retrying this meeting. The previous submission may already have been processed.");
        if (source.IsShared || !source.IsDefault)
            throw new MailServiceException("Send a meeting from your primary Exchange calendar.");
        if (!send || ev.Attendees.Count == 0)
            throw new MailServiceException("Use Send with at least one attendee to create the meeting.");
    }

    private async Task<string> SubmitMeetingAsync(CalendarEvent ev, ulong fid, ulong? mid, uint[] calendar,
        Dictionary<uint, object> appointment, Dictionary<uint, object?>? previous, uint[] removedTags, CancellationToken ct)
    {
        var defaultCalendar = await DefaultFolderAsync(FolderKind.Calendar, ct);
        if (ParseFolder(defaultCalendar.Id) != fid) throw new MailServiceException("The selected folder is not the primary calendar.");
        var participants = ev.Attendees.Where(a => a.Type != AttendeeType.Organizer)
            .Select(a => new MapiRecipient(a.Type switch { AttendeeType.Optional => 2, AttendeeType.Resource => 3, _ => 1 },
                a.Name, a.Email, Flags: 1, TrackStatus: 0)).ToArray();
        if (participants.Length == 0 || participants.Select(p => p.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count() != participants.Length)
            throw new MailServiceException("Select at least one attendee and list each address only once.");
        var organizer = new MapiRecipient(1, UserDisplayName ?? Account.DisplayName, Account.Email, Flags: 3, TrackStatus: 1);
        var meeting = await Session.ResolvePropertyTagsAsync(Logon, MeetingRequestNames, true, ct);
        if (meeting.Any(tag => tag == 0)) throw new MailServiceException("Exchange did not return the properties for a meeting request.");
        // Exchange collapses duplicate SMTP rows. If the owner invites their own address, store
        // that address as a participant so update/cancellation can still recover the destination.
        var organizerRecipients = participants.Any(p => p.Address.Equals(organizer.Address, StringComparison.OrdinalIgnoreCase))
            ? participants : [organizer, .. participants];
        var sequence = 0u;
        var changes = 0u;
        var recurring = appointment[calendar[5]] is true;
        var ownerId = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
        if (mid.HasValue && previous != null)
        {
            if (Number(previous, calendar[8]) != 1 || ev.IsOccurrence || (previous.GetValueOrDefault(calendar[5]) is true) != recurring)
                throw new MailServiceException("Update the active meeting you organized. Open the whole series for recurring meetings.");
            var stored = await Session.ReadMessagePropertiesAsync(Logon, fid, mid.Value, [0x00620003, .. meeting], ct);
            if (stored.GetValueOrDefault(meeting[12]) is not false || stored.GetValueOrDefault(meeting[0]) is not true)
                throw new MailServiceException("Only a meeting sent by Posta with a confirmed submission can be updated here.");
            var existing = await Session.ReadMessageAsync(Logon, fid, mid.Value, ct);
            if (existing.Recipients is not { Complete: true } recipients || existing.Attachments.Count != 0)
                throw new MailServiceException("The meeting has attachments or its full attendee list could not be loaded.");
            var oldAttendees = recipients.Values.Where(p => (p.Flags.GetValueOrDefault() & 2) == 0).ToArray();
            if (oldAttendees.Length != participants.Length || oldAttendees.Any(old => !participants.Any(p =>
                    p.Kind == old.Kind && p.Address.Equals(old.Address, StringComparison.OrdinalIgnoreCase))))
                throw new MailServiceException("Changing the attendee list of an existing meeting is not available yet.");
            // Keep responses and organizer identity while updating the original Calendar object.
            organizerRecipients = recipients.Values.ToArray();
            sequence = checked(Math.Max(Number(stored, meeting[1]), Number(stored, meeting[13])) + 1);
            ownerId = Number(stored, 0x00620003);
            appointment[calendar[18]] = previous.GetValueOrDefault(calendar[18]) as byte[] ?? throw new MailServiceException("The existing meeting identity is unavailable.");
            appointment[calendar[19]] = previous.GetValueOrDefault(calendar[19]) as byte[] ?? throw new MailServiceException("The existing meeting identity is unavailable.");
            if (Date(previous, calendar[0]) != ev.Start) changes |= 1;
            if (Date(previous, calendar[1]) != ev.End) changes |= 2;
            if (recurring && previous.GetValueOrDefault(calendar[6]) is byte[] oldPattern && appointment[calendar[6]] is byte[] newPattern &&
                !oldPattern.AsSpan().SequenceEqual(newPattern)) changes |= 4;
            if (Text(previous, calendar[2]) != ev.Location) changes |= 8;
            if (Text(previous, 0x0037001f) != ev.Subject) changes |= 16;
            if (PimBody(existing) != ev.BodyHtml) changes |= 128;
        }
        _ = MapiWriteWire.Recipients(1, participants);
        _ = MapiWriteWire.Recipients(1, organizerRecipients);
        var now = DateTime.UtcNow;
        appointment[0x00600040] = ev.Start;
        appointment[0x00610040] = ev.End;
        appointment[0x00620003] = ownerId;
        appointment[0x0063000b] = ev.ResponseRequested;
        appointment[0x10800003] = recurring ? 0x403u : 0x402u;
        appointment[calendar[8]] = 1u;
        appointment[meeting[0]] = mid.HasValue;
        appointment[meeting[1]] = sequence;
        appointment[meeting[13]] = sequence;
        appointment[meeting[2]] = now;
        appointment[meeting[4]] = !ev.AllowNewTimeProposal;
        appointment[meeting[5]] = now;
        appointment[meeting[10]] = recurring;
        appointment[meeting[12]] = true;
        if (ev.ReminderSet) appointment[meeting[11]] = ev.Start.AddMinutes(-ev.ReminderMinutes);

        // MS-OXOCAL 4.2.2.2: the appointment and invitation share the same object identity and time.
        var request = new Dictionary<uint, object>(appointment)
        {
            [0x001a001f] = MeetingRequestClass,
            [0x10800003] = uint.MaxValue,
            [calendar[3]] = (uint)ShowAs.Tentative,
            [calendar[7]] = 5u,
            [calendar[8]] = 3u,
            [meeting[0]] = true,
            [meeting[3]] = (uint)ev.ShowAs,
            [meeting[6]] = now,
            [meeting[7]] = ev.Location,
            [meeting[8]] = "IPM.Appointment",
            [meeting[9]] = !mid.HasValue ? 1u : (changes & 7) != 0 ? 0x10000u : 0x20000u,
            [meeting[14]] = changes
        };
        request.Remove(0x0e070003u);
        request.Remove(meeting[12]);
        request.Remove(meeting[13]);
        var when = $"{ev.Start:f} – {ev.End:g} ({TimeZoneInfo.Local.DisplayName})";
        var invitationBody = $"<div>{HtmlUtil.Encode(when)}</div><div>{HtmlUtil.Encode(ev.Location)}</div><br>" + HtmlUtil.BodyContent(ev.BodyHtml);
        request[0x1000001f] = HtmlUtil.ToPlainText(invitationBody);
        _ = MapiWriteWire.Properties(1, request);
        var saved = await Session.WriteMessageAsync(Logon, fid, mid, appointment, Encoding.UTF8.GetBytes(ev.BodyHtml), organizerRecipients, [], [], false, ct,
            mid.HasValue ? removedTags : null);
        ev.Id = $"mapi-m:{fid:X16}:{saved.Id:X16}";
        ev.MeetingSubmissionNeedsReview = true;
        try
        {
            await SendMeetingMessageAsync(request, invitationBody, participants, ct);
        }
        catch (Exception ex)
        {
            throw new MailServiceException("The calendar entry was saved. Verify the invitation status in Outbox and Sent Items before resending. " + ex.Message, ex);
        }
        try
        {
            await Session.SetMessagePropertiesAsync(Logon, fid, saved.Id,
                new Dictionary<uint, object> { [meeting[0]] = true, [meeting[12]] = false }, ct);
        }
        catch (Exception ex)
        {
            throw new MailServiceException("Exchange accepted the invitation, but the calendar confirmation could not be saved. Check Sent Items. " + ex.Message, ex);
        }
        ev.MeetingSubmissionNeedsReview = false;
        return ev.Id;
    }

    private async Task CancelMeetingAsync(ulong folder, ulong message, uint[] calendar, Dictionary<uint, object?> appointment,
        string? comment, CancellationToken ct)
    {
        if (ParseFolder((await DefaultFolderAsync(FolderKind.Calendar, ct)).Id) != folder)
            throw new MailServiceException("Cancel the meeting from the primary calendar.");
        var meeting = await Session.ResolvePropertyTagsAsync(Logon, MeetingRequestNames, false, ct);
        if (meeting.Any(tag => tag == 0)) throw new MailServiceException("The existing meeting does not have a recorded Posta submission.");
        var stored = await Session.ReadMessagePropertiesAsync(Logon, folder, message, [0x00620003, .. meeting], ct);
        if (stored.GetValueOrDefault(meeting[0]) is not true || stored.GetValueOrDefault(meeting[12]) is not false)
            throw new MailServiceException("Cancellation needs a previously confirmed Posta submission. Check Sent Items before trying again.");
        var details = await Session.ReadMessageAsync(Logon, folder, message, ct);
        if (details.Recipients is not { Complete: true } recipients) throw new MailServiceException("The meeting's full attendee list could not be loaded.");
        var attendees = recipients.Values.Where(p => (p.Flags.GetValueOrDefault() & 2) == 0).ToArray();
        if (attendees.Length == 0) throw new MailServiceException("No attendees were found on this meeting.");
        var sequence = checked(Math.Max(Number(stored, meeting[1]), Number(stored, meeting[13])) + 1);
        var now = DateTime.UtcNow;
        var start = Date(appointment, calendar[0]);
        var end = Date(appointment, calendar[1]);
        if (start == DateTime.MinValue || end <= start) throw new MailServiceException("The existing meeting time is invalid.");
        var recurring = appointment.GetValueOrDefault(calendar[5]) is true;
        if (recurring)
        {
            var (pattern, _) = RecurringData(appointment, calendar);
            if (pattern.Exceptions.Count > 0 || pattern.Deleted.Count > 0)
                throw new MailServiceException("This meeting series contains exceptions and cannot be canceled here yet.");
        }
        var body = $"<div>{HtmlUtil.Encode($"{start:f} – {end:g} ({TimeZoneInfo.Local.DisplayName})")}</div>" + (comment ?? "");
        var cancellation = new Dictionary<uint, object>
        {
            [0x001a001f] = MeetingCanceledClass, [0x10800003] = 0x408u,
            [0x0037001f] = "Canceled: " + Text(appointment, 0x0037001f), [0x003d001f] = "Canceled: ",
            [0x00600040] = start, [0x00610040] = end, [0x00620003] = Number(stored, 0x00620003),
            [0x0063000b] = false, [0x3fde0003] = 65001u, [0x1000001f] = HtmlUtil.ToPlainText(body),
            [calendar[0]] = start, [calendar[1]] = end, [calendar[2]] = Text(appointment, calendar[2]),
            [calendar[3]] = 0u, [calendar[4]] = appointment.GetValueOrDefault(calendar[4]) is true,
            [calendar[5]] = recurring, [calendar[7]] = 5u, [calendar[8]] = 7u, [calendar[10]] = false,
            [calendar[18]] = appointment.GetValueOrDefault(calendar[18]) as byte[] ?? throw new MailServiceException("The existing meeting identity is unavailable."),
            [calendar[19]] = appointment.GetValueOrDefault(calendar[19]) as byte[] ?? throw new MailServiceException("The existing meeting identity is unavailable."),
            [meeting[0]] = true, [meeting[1]] = sequence, [meeting[2]] = now, [meeting[3]] = 0u,
            [meeting[5]] = now, [meeting[6]] = now, [meeting[7]] = Text(appointment, calendar[2]),
            [meeting[8]] = "IPM.Appointment", [meeting[10]] = recurring
        };
        if (recurring)
            foreach (var index in new[] { 6, 9, 14, 15, 16, 17 })
                if (appointment.GetValueOrDefault(calendar[index]) is { } value and not MapiCellError)
                    cancellation[calendar[index]] = value;
        _ = MapiWriteWire.Recipients(1, attendees);
        _ = MapiWriteWire.Properties(1, cancellation);
        await Session.SetMessagePropertiesAsync(Logon, folder, message, new Dictionary<uint, object> { [meeting[12]] = true }, ct);
        try
        {
            await SendMeetingMessageAsync(cancellation, body, attendees, ct);
            await Session.SetMessagePropertiesAsync(Logon, folder, message, new Dictionary<uint, object>
            {
                [calendar[8]] = 5u, [calendar[10]] = false, [meeting[1]] = sequence, [meeting[2]] = now,
                [meeting[5]] = now, [meeting[12]] = false, [meeting[13]] = sequence
            }, ct);
        }
        catch (Exception ex)
        {
            throw new MailServiceException("Verify the cancellation in Outbox and Sent Items. The calendar entry was kept until its cancellation is confirmed. " + ex.Message, ex);
        }
    }

    private async Task SendMeetingMessageAsync(Dictionary<uint, object> properties, string html, IReadOnlyList<MapiRecipient> recipients, CancellationToken ct)
    {
        properties[0x674000fb] = MapiWire.Write(w => { w.Write((byte)1); w.Write(Logon.FolderIds[6]); w.Write(0ul); w.Write(0u); });
        await Session.WriteMessageAsync(Logon, Logon.FolderIds[5], null, properties, Encoding.UTF8.GetBytes(html), recipients, [], [], true, ct);
    }
}
