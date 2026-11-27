using System.Globalization;
using System.Text;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    private static readonly Guid AppointmentSet = new("00062002-0000-0000-c000-000000000046");
    private static readonly Guid MeetingSet = new("6ed8da90-450b-101b-98da-00aa003f1305");
    private static readonly MapiPropertyName[] CalendarNames =
    [new(AppointmentSet, 0x820d, 0x40), new(AppointmentSet, 0x820e, 0x40), new(AppointmentSet, 0x8208, 0x1f),
     new(AppointmentSet, 0x8205, 3), new(AppointmentSet, 0x8215, 0xb), new(AppointmentSet, 0x8223, 0xb),
     new(AppointmentSet, 0x8216, 0x102), new(AppointmentSet, 0x8218, 3), new(AppointmentSet, 0x8217, 3),
     new(AppointmentSet, 0x8233, 0x102), new(CommonSet, 0x8503, 0xb), new(CommonSet, 0x8501, 3),
     new(CommonSet, 0x8502, 0x40), new(AppointmentSet, 0x8213, 3), new(AppointmentSet, 0x8231, 3),
     new(AppointmentSet, 0x8235, 0x40), new(AppointmentSet, 0x8236, 0x40), new(AppointmentSet, 0x8234, 0x1f),
     new(MeetingSet, 3, 0x102), new(MeetingSet, 0x23, 0x102)];

    private static uint[] CalendarColumns(uint[] n) => [0x674a0014, 0x001a001f, 0x0037001f, 0x00170003, 0x00360003, 0x5d02001f, 0x0042001f, 0x65e20102, AccessTag, .. n.Where(t => t != 0)];
    private string CalendarKey(string folderId) => Account.Id + "|" + Account.Email + "|" + folderId;

    public Task<IReadOnlyList<CalendarSource>> GetOwnCalendarsAsync(CancellationToken ct = default) => Run<IReadOnlyList<CalendarSource>>(async () =>
    {
        var folders = await Folders(ct); var primary = (await DefaultFolderAsync(FolderKind.Calendar, ct)).Id;
        return folders.Where(f => f.IsCalendarFolder).Select(f => new CalendarSource
        { Key = CalendarKey(f.Id), AccountId = Account.Id, FolderId = f.Id, OwnerEmail = Account.Email,
            OwnerName = UserDisplayName ?? Account.DisplayName, DisplayName = f.DisplayName, IsDefault = f.Id == primary,
            IsChecked = f.Id == primary, Access = CalendarAccess.Owner, Group = CalendarGroupKind.MyCalendars }).ToList();
    }, ct);

    public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(CalendarSource source, DateTime start, DateTime end, CancellationToken cancellation = default) => RunViewRead<IReadOnlyList<CalendarEvent>>(async ct =>
    {
        if (source.AccountId != Account.Id) throw new MailServiceException("Calendar account mismatch.");
        var shared = source.IsShared ? await ConnectSharedCalendarAsync(source.OwnerEmail ?? "", ct) : null;
        if (shared == null) ValidateOwnCalendar(source);
        var session = shared?.Session ?? Session;
        var logon = shared?.Logon ?? Logon;
        var folder = shared?.Folder ?? ParseFolder(source.FolderId);
        if (end <= start || (end - start).TotalDays > 3700) throw new MailServiceException("Invalid calendar view range.");
        var n = await session.ResolvePropertyTagsAsync(logon, CalendarNames, false, ct);
        var table = await session.ReadTableAsync(logon, folder, false, CalendarColumns(n), 5000, ct);
        if (table.Total > table.Rows.Count) throw new MailServiceException("Calendar exceeds the current 5000-master limit.");
        var events = new List<CalendarEvent>();
        foreach (var row in table.Rows)
        {
            if (!Text(row, 0x001a001f).StartsWith("IPM.Appointment", StringComparison.OrdinalIgnoreCase)) continue;
            var item = MapCalendar(FolderId(folder), (ulong)row[0x674a0014u]!, row, n);
            if (shared != null)
            {
                ApplySharedCalendarAccess(item, shared, row);
            }
            if (item.IsRecurring)
            {
                var (pattern, zone) = RecurringData(row, n);
                item.Recurrence = pattern.ToModel(); events.AddRange(pattern.Expand(item, zone, start, end));
            }
            else if (item.Start < end && item.End > start) events.Add(item);
        }
        return events.OrderBy(e => e.Start).ToList();
    }, cancellation);

    private static (CalendarRecurrence Pattern, TimeZoneInfo Zone) RecurringData(Dictionary<uint, object?> row, uint[] n)
    {
        if (row.GetValueOrDefault(n[6]) is not byte[] blob) throw new MailServiceException("A recurring appointment has no readable recurrence data.");
        if (row.GetValueOrDefault(n[9]) is not byte[] zone) throw new MailServiceException("A recurring appointment has no readable time zone; refusing to guess its occurrence times.");
        return (CalendarRecurrence.Parse(blob), CalendarTimeZone.Parse(zone));
    }

    private CalendarEvent MapCalendar(string folder, ulong mid, Dictionary<uint, object?> row, uint[] n)
    {
        var state = Number(row, n[8]); var sender = Text(row, 0x5d02001f);
        return new CalendarEvent { Id = $"mapi-m:{ParseFolder(folder):X16}:{mid:X16}", CalendarKey = CalendarKey(folder),
            Subject = Text(row, 0x0037001f), Location = Text(row, n[2]), Start = Date(row, n[0]), End = Date(row, n[1]),
            IsAllDay = row.GetValueOrDefault(n[4]) is true, IsRecurring = row.GetValueOrDefault(n[5]) is true,
            ShowAs = (ShowAs)Math.Min(4, Number(row, n[3])), IsMeeting = (state & 1) != 0,
            IsOrganizer = (state & 2) == 0, IsCancelled = (state & 4) != 0,
            Organizer = new Recipient(Text(row, 0x0042001f), sender.Length == 0 && (state & 2) == 0 ? Account.Email : sender),
            MyResponse = Number(row, n[7]) switch { 1 => MeetingResponse.Organizer, 2 => MeetingResponse.Tentative, 3 => MeetingResponse.Accept,
                4 => MeetingResponse.Decline, 5 => MeetingResponse.NoResponseReceived, _ => MeetingResponse.None },
            ReminderSet = row.GetValueOrDefault(n[10]) is true, ReminderMinutes = checked((int)Number(row, n[11])), ReminderDueBy = NullableDate(row, n[12]),
            Sensitivity = (Sensitivity)Math.Min(3, Number(row, 0x00360003)), Importance = (Importance)Math.Min(2, Number(row, 0x00170003)),
            ChangeKey = row.GetValueOrDefault(0x65e20102u) is byte[] change ? Convert.ToBase64String(change) : null };
    }

    private void ValidateOwnCalendar(CalendarSource source)
    {
        if (source.AccountId != Account.Id || source.IsShared || !string.Equals(source.OwnerEmail ?? Account.Email, Account.Email, StringComparison.OrdinalIgnoreCase))
            throw new MailServiceException("This calendar is not in the connected primary mailbox.");
    }

    private void ApplySharedCalendarAccess(CalendarEvent item, SharedCalendar shared, Dictionary<uint, object?> row)
    {
        var access = Number(row, AccessTag);
        item.Id = shared.Prefix + item.Id;
        item.CalendarKey = $"{Account.Id}|{shared.Owner}|calendar";
        item.IsOrganizer = false;
        item.CanEditItem = !item.IsMeeting && (access & ModifyAccess) != 0;
        item.CanDeleteItem = !item.IsMeeting && (access & DeleteAccess) != 0;
        if (item.Organizer?.Address == Account.Email) item.Organizer = new(shared.Name, shared.Owner);
    }

    private static (string master, DateTime? original) CalendarItemId(string id)
    {
        var parts = id.Split(":occ:", 2, StringSplitOptions.None);
        return (parts[0], parts.Length == 2 ? CalendarRecurrence.FromMinutes(uint.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)) : null);
    }

    public Task<CalendarEvent> GetEventAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        SharedCalendar? shared = null;
        var localId = itemId;
        if (itemId.StartsWith(SharedCalendarPrefix, StringComparison.Ordinal))
        {
            var parts = SplitSharedCalendarId(itemId);
            shared = await ConnectSharedCalendarAsync(parts.Owner, ct);
            localId = parts.Id;
        }
        var (id, original) = CalendarItemId(localId);
        var (fid, mid) = ParseMessage(id);
        if (shared != null && shared.Folder != fid) throw new MailServiceException("Shared calendar folder mismatch.");
        var session = shared?.Session ?? Session;
        var logon = shared?.Logon ?? Logon;
        var n = await session.ResolvePropertyTagsAsync(logon, CalendarNames, false, ct);
        var row = await session.ReadMessagePropertiesAsync(logon, fid, mid, CalendarColumns(n), ct);
        if (!Text(row, 0x001a001f).StartsWith("IPM.Appointment", StringComparison.OrdinalIgnoreCase)) throw new MailServiceException("The selected item is not an appointment.");
        var item = MapCalendar(FolderId(fid), mid, row, n);
        if (shared != null) ApplySharedCalendarAccess(item, shared, row);
        var body = await session.ReadMessageAsync(logon, fid, mid, ct); item.BodyHtml = PimBody(body);
        if (item.IsMeeting)
        {
            var tags = await session.ResolvePropertyTagsAsync(logon, [MeetingRequestNames[4], MeetingRequestNames[12]], false, ct);
            var meetingOptions = await session.ReadMessagePropertiesAsync(logon, fid, mid, [0x0063000b, .. tags.Where(t => t != 0)], ct);
            item.ResponseRequested = meetingOptions.GetValueOrDefault(0x0063000bu) is not false;
            item.AllowNewTimeProposal = meetingOptions.GetValueOrDefault(tags[0]) is not true;
            item.MeetingSubmissionNeedsReview = meetingOptions.GetValueOrDefault(tags[1]) is true;
        }
        item.Attendees = body.Recipients?.Values.Where(r => (r.Flags.GetValueOrDefault() & 2) == 0)
            .Select(r => new Attendee { Name = r.Name, Email = r.Address,
                Type = r.Kind switch { 2 => AttendeeType.Optional, 3 => AttendeeType.Resource, _ => AttendeeType.Required },
                Response = r.TrackStatus switch { 2 => MeetingResponse.Tentative, 3 => MeetingResponse.Accept, 4 => MeetingResponse.Decline, _ => MeetingResponse.Unknown } }).ToList() ?? [];
        if (item.IsRecurring)
        {
            var (pattern, zone) = RecurringData(row, n); item.Recurrence = pattern.ToModel();
            if (original.HasValue)
            {
                var exception = pattern.Exceptions.FirstOrDefault(e => e.Original == original.Value);
                if (exception is { Flags: var flags } && (flags & 0x240) != 0)
                    throw new MailServiceException("This exception has a distinct embedded body or attachments, which must be decoded before it can be opened safely.");
                var actual = exception?.Start ?? original.Value;
                var from = TimeZoneInfo.ConvertTimeToUtc(actual, zone).ToLocalTime().Date.AddDays(-1);
                item = pattern.Expand(item, zone, from, from.AddDays(370)).Single(e => e.Id == itemId);
            }
        }
        else if (original.HasValue) throw new MailServiceException("The appointment is no longer recurring; reload the calendar.");
        return item;
    }, ct);

    public Task<string> SaveEventAsync(CalendarSource source, CalendarEvent ev, bool sendInvitations, EditScope scope, CancellationToken ct = default) => Run(async () =>
    {
        if (source.AccountId != Account.Id) throw new MailServiceException("Calendar account mismatch.");
        if (!source.IsShared) ValidateOwnCalendar(source);
        if (ev.End <= ev.Start) throw new MailServiceException("Appointment end must be after its start.");
        if (ev.Categories.Count > 0 || ev.Attachments.Count > 0) throw new MailServiceException("Calendar categories and attachments must be implemented before saving this item.");
        var meeting = ev.IsMeeting || ev.Attendees.Count > 0 || sendInvitations;
        if (meeting) ValidateMeetingSend(source, ev, sendInvitations);
        var shared = source.IsShared ? await ConnectSharedCalendarAsync(source.OwnerEmail ?? "", ct) : null;
        if (shared != null && source.FolderId != shared.FolderId) throw new MailServiceException("Shared calendar folder mismatch.");
        var session = shared?.Session ?? Session;
        var logon = shared?.Logon ?? Logon;
        var fid = shared?.Folder ?? ParseFolder(source.FolderId);
        if (shared != null && string.IsNullOrEmpty(ev.Id))
        {
            var folder = await session.ReadFolderPropertiesAsync(logon, fid, [AccessTag], ct);
            if ((Number(folder, AccessTag) & CreateContentsAccess) == 0)
                throw new MailServiceException("Exchange has not granted permission to create appointments in this calendar.");
        }
        var n = await session.ResolvePropertyTagsAsync(logon, CalendarNames, shared == null, ct);
        if (n.Any(tag => tag == 0)) throw new MailServiceException("Exchange did not provide the named properties required to save this appointment.");
        ulong? mid = null;
        Dictionary<uint, object?>? previous = null;
        if (!string.IsNullOrEmpty(ev.Id))
        {
            if (shared != null && !ev.Id.StartsWith(shared.Prefix, StringComparison.OrdinalIgnoreCase))
                throw new MailServiceException("Appointment does not belong to this shared calendar.");
            var (master, original) = CalendarItemId(shared == null ? ev.Id : ev.Id[shared.Prefix.Length..]); var parsed = ParseMessage(master);
            if (parsed.folder != fid) throw new MailServiceException("Appointment folder mismatch."); mid = parsed.message;
            previous = await session.ReadMessagePropertiesAsync(logon, fid, mid.Value, CalendarColumns(n), ct);
            if (shared != null && (Number(previous, AccessTag) & ModifyAccess) == 0)
                throw new MailServiceException("Exchange has not granted permission to edit this appointment.");
            if (Text(previous, 0x001a001f) != "IPM.Appointment") throw new MailServiceException("Only a personal appointment can be edited here.");
            if (((Number(previous, n[8]) & 1) != 0) != meeting)
                throw new MailServiceException("The existing item cannot be converted between an appointment and a meeting.");
            if (original.HasValue && scope != EditScope.Series) throw new MailServiceException("Editing one recurrence exception requires its embedded message update.");
            if (ev.ChangeKey != null && previous.GetValueOrDefault(0x65e20102u) is byte[] version && Convert.ToBase64String(version) != ev.ChangeKey)
                throw new MailServiceException("The appointment changed on the server; reload it before saving.");
            if (previous.GetValueOrDefault(n[6]) is byte[] old)
            {
                var pattern = CalendarRecurrence.Parse(old);
                if (pattern.Exceptions.Count > 0 || pattern.Deleted.Count > 0)
                    throw new MailServiceException("This series contains changed or deleted occurrences. Its exceptions must be preserved before it can be edited.");
            }
        }
        var props = new Dictionary<uint, object> { [0x001a001f] = "IPM.Appointment", [0x0e070003] = 1u, [0x0037001f] = ev.Subject,
            [0x1000001f] = HtmlUtil.ToPlainText(ev.BodyHtml), [0x3fde0003] = 65001u, [0x00170003] = (uint)ev.Importance, [0x00360003] = (uint)ev.Sensitivity,
            [n[0]] = ev.Start, [n[1]] = ev.End, [n[2]] = ev.Location, [n[3]] = (uint)ev.ShowAs, [n[4]] = ev.IsAllDay,
            [n[5]] = ev.Recurrence?.Type is not (null or RecurrenceType.None), [n[7]] = 1u, [n[8]] = 0u,
            [n[10]] = ev.ReminderSet, [n[11]] = checked((uint)ev.ReminderMinutes), [n[12]] = ev.Start,
            [n[13]] = checked((uint)(ev.End - ev.Start).TotalMinutes), [n[17]] = TimeZoneInfo.Local.Id };
        var deleted = new List<uint>();
        if (ev.Recurrence is { Type: not RecurrenceType.None } recurrence)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(ev.TimeZoneId ?? TimeZoneInfo.Local.Id);
            var localStart = TimeZoneInfo.ConvertTime(ev.Start, zone); var localEnd = TimeZoneInfo.ConvertTime(ev.End, zone);
            var pattern = CalendarRecurrence.Create(recurrence, localStart, localEnd);
            props[n[6]] = pattern.ToBytes(); props[n[9]] = CalendarTimeZone.Encode(zone, localStart.Year);
            props[n[14]] = recurrence.Type switch { RecurrenceType.Daily => 1u, RecurrenceType.Weekly => 2u,
                RecurrenceType.MonthlyAbsolute => 3u, RecurrenceType.MonthlyRelative => 4u, RecurrenceType.YearlyAbsolute => 5u, _ => 6u };
            props[n[15]] = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(pattern.StartDate, DateTimeKind.Unspecified), zone);
            props[n[16]] = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(pattern.EndDate, DateTimeKind.Unspecified), zone); props[n[17]] = zone.Id;
        }
        else deleted.AddRange(new[] { n[6], n[9], n[14], n[15], n[16] });
        if (mid == null)
        {
            var global = NewGlobalObjectId(); props[n[18]] = global; props[n[19]] = global;
        }
        if (meeting) return await SubmitMeetingAsync(ev, fid, mid, n, props, previous, deleted.ToArray(), ct);
        var saved = await session.WriteMessageAsync(logon, fid, mid, props, Encoding.UTF8.GetBytes(ev.BodyHtml), [], [], [], false, ct, deleted.ToArray());
        return ev.Id = $"{shared?.Prefix}mapi-m:{fid:X16}:{saved.Id:X16}";
    }, ct);

    internal static byte[] NewGlobalObjectId() => MapiWire.Write(w =>
    {
        w.Write(Convert.FromHexString("040000008200E00074C5B7101A82E008")); w.Write(0u);
        w.Write(DateTime.UtcNow.ToFileTimeUtc()); w.Write(0ul);
        var uid = Encoding.UTF8.GetBytes("vCal-Uid\u0001\0\0\0" + Guid.NewGuid().ToString() + "\0"); w.Write((uint)uid.Length); w.Write(uid);
    });

    public Task DeleteEventAsync(string itemId, bool notifyAttendees, string? cancellationText, EditScope scope, CancellationToken ct = default) => Run(async () =>
    {
        SharedCalendar? shared = null;
        if (itemId.StartsWith(SharedCalendarPrefix, StringComparison.Ordinal))
        {
            var parts = SplitSharedCalendarId(itemId);
            shared = await ConnectSharedCalendarAsync(parts.Owner, ct);
            itemId = parts.Id;
        }
        var (master, original) = CalendarItemId(itemId); var (fid, mid) = ParseMessage(master);
        if (shared != null && shared.Folder != fid) throw new MailServiceException("Shared calendar folder mismatch.");
        var session = shared?.Session ?? Session;
        var logon = shared?.Logon ?? Logon;
        var n = await session.ResolvePropertyTagsAsync(logon, CalendarNames, false, ct);
        var row = await session.ReadMessagePropertiesAsync(logon, fid, mid, CalendarColumns(n), ct);
        if (Text(row, 0x001a001f) != "IPM.Appointment") throw new MailServiceException("The selected item is not an appointment.");
        if ((Number(row, n[8]) & 1) != 0)
        {
            if (shared != null || (original.HasValue && scope == EditScope.Single) || Number(row, n[8]) is not (1u or 5u))
                throw new MailServiceException("Cancel a meeting you organized from your primary calendar. Select the whole series for recurring meetings.");
            if ((Number(row, n[8]) & 4) == 0)
            {
                if (!notifyAttendees) throw new MailServiceException("Cancel the meeting and send the attendees a cancellation before removing it.");
                await CancelMeetingAsync(fid, mid, n, row, cancellationText, ct);
            }
        }
        else if (notifyAttendees) throw new MailServiceException("This calendar item has no meeting to cancel.");
        if (original.HasValue && scope == EditScope.Single)
        {
            if (shared != null && (Number(row, AccessTag) & ModifyAccess) == 0)
                throw new MailServiceException("Exchange has not granted permission to change this series.");
            var (pattern, _) = RecurringData(row, n);
            await session.SetMessagePropertiesAsync(logon, fid, mid, new Dictionary<uint, object> { [n[6]] = pattern.WithDeletedInstance(original.Value) }, ct);
        }
        else if (shared != null)
        {
            if ((Number(row, AccessTag) & DeleteAccess) == 0)
                throw new MailServiceException("Exchange has not granted permission to delete this appointment.");
            await session.DeleteMessagesAsync(logon, fid, [mid], false, ct);
        }
        else await session.MoveCopyMessagesAsync(logon, fid, logon.FolderIds[7], [mid], false, ct);
        _folders = null; return true;
    }, ct);
}
