using Posta.Core.Models;
using Posta.Core.Services;
using Ews = Microsoft.Exchange.WebServices.Data;

namespace Posta.Exchange;

public sealed partial class EwsMailService
{
    private static readonly Ews.PropertySet EventListProps = new(Ews.BasePropertySet.IdOnly,
        Ews.AppointmentSchema.Subject, Ews.AppointmentSchema.Location, Ews.AppointmentSchema.Start, Ews.AppointmentSchema.End,
        Ews.AppointmentSchema.IsAllDayEvent, Ews.AppointmentSchema.LegacyFreeBusyStatus, Ews.AppointmentSchema.Organizer,
        Ews.AppointmentSchema.IsMeeting, Ews.AppointmentSchema.IsCancelled, Ews.AppointmentSchema.IsRecurring,
        Ews.AppointmentSchema.AppointmentType, Ews.AppointmentSchema.MyResponseType, Ews.ItemSchema.Sensitivity,
        Ews.ItemSchema.Categories, Ews.ItemSchema.IsReminderSet, Ews.ItemSchema.ReminderMinutesBeforeStart,
        Ews.ItemSchema.ReminderDueBy, Ews.ItemSchema.Importance, Ews.AppointmentSchema.DisplayTo, Ews.ItemSchema.HasAttachments);

    private static string CalKey(string accountId, string? owner, string folderId) => $"{accountId}|{owner ?? "self"}|{folderId}";

    public Task<IReadOnlyList<CalendarSource>> GetOwnCalendarsAsync(CancellationToken ct = default) => Run(async () =>
    {
        var list = new List<CalendarSource>();
        var root = await Ews.Folder.Bind(Svc, Ews.WellKnownFolderName.Calendar, new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.FolderSchema.DisplayName));
        list.Add(new CalendarSource
        {
            Key = CalKey(Account.Id, null, root.Id.UniqueId), AccountId = Account.Id, FolderId = root.Id.UniqueId,
            DisplayName = root.DisplayName, OwnerName = Account.DisplayName, OwnerEmail = Account.Email, IsDefault = true,
            Group = CalendarGroupKind.MyCalendars, Access = CalendarAccess.Owner
        });

        // Additional calendars anywhere in the mailbox (sub-folders of Calendar and others)
        try
        {
            var view = new Ews.FolderView(200)
            {
                Traversal = Ews.FolderTraversal.Deep,
                PropertySet = new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.FolderSchema.DisplayName, Ews.FolderSchema.FolderClass, PidTagAttributeHidden)
            };
            var filter = new Ews.SearchFilter.IsEqualTo(Ews.FolderSchema.FolderClass, "IPF.Appointment");
            var found = await Svc.FindFolders(Ews.WellKnownFolderName.MsgFolderRoot, filter, view);
            foreach (var f in found.Folders)
            {
                if (f.Id.UniqueId == root.Id.UniqueId) continue;
                if (f.TryGetProperty(PidTagAttributeHidden, out bool hidden) && hidden) continue;
                list.Add(new CalendarSource
                {
                    Key = CalKey(Account.Id, null, f.Id.UniqueId), AccountId = Account.Id, FolderId = f.Id.UniqueId,
                    DisplayName = f.DisplayName, OwnerName = Account.DisplayName, OwnerEmail = Account.Email,
                    Group = CalendarGroupKind.MyCalendars, Access = CalendarAccess.Owner
                });
            }
        }
        catch { /* not fatal */ }
        return (IReadOnlyList<CalendarSource>)list;
    });

    public Task<CalendarSource> OpenSharedCalendarAsync(string ownerEmail, CancellationToken ct = default) => Run(async () =>
    {
        string ownerName = ownerEmail;
        try
        {
            var res = await Svc.ResolveName(ownerEmail, Ews.ResolveNameSearchLocation.ContactsThenDirectory, false);
            var first = res.FirstOrDefault();
            if (first?.Mailbox != null)
            {
                ownerName = first.Mailbox.Name ?? ownerEmail;
                if (!string.IsNullOrEmpty(first.Mailbox.Address)) ownerEmail = first.Mailbox.Address;
            }
        }
        catch { }

        var src = new CalendarSource
        {
            AccountId = Account.Id, OwnerEmail = ownerEmail, OwnerName = ownerName, IsShared = true,
            DisplayName = ownerName, Group = CalendarGroupKind.SharedCalendars
        };

        try
        {
            var folder = await Ews.Folder.Bind(Svc, new Ews.FolderId(Ews.WellKnownFolderName.Calendar, new Ews.Mailbox(ownerEmail)),
                new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.FolderSchema.DisplayName, Ews.FolderSchema.EffectiveRights));
            src.FolderId = folder.Id.UniqueId;
            var rights = folder.EffectiveRights;
            src.Access =
                rights.HasFlag(Ews.EffectiveRights.Modify) && rights.HasFlag(Ews.EffectiveRights.Delete) ? CalendarAccess.Editor :
                rights.HasFlag(Ews.EffectiveRights.CreateContents) ? CalendarAccess.Author :
                rights.HasFlag(Ews.EffectiveRights.Read) ? CalendarAccess.ReadAll : CalendarAccess.FreeBusyDetails;
        }
        catch (Ews.ServiceResponseException ex) when (ex.ErrorCode is Ews.ServiceError.ErrorAccessDenied or Ews.ServiceError.ErrorFolderNotFound
                                                      or Ews.ServiceError.ErrorItemNotFound or Ews.ServiceError.ErrorNonExistentMailbox)
        {
            if (ex.ErrorCode == Ews.ServiceError.ErrorNonExistentMailbox) throw new MailServiceException("The mailbox does not exist: " + ownerEmail, ex);
            // No folder access: free/busy information only (via the Availability service).
            src.FolderId = "";
            src.Access = CalendarAccess.FreeBusyDetails;
        }
        src.Key = CalKey(Account.Id, ownerEmail.ToLowerInvariant(), "calendar");
        return src;
    });

    private Ews.FolderId CalendarFolderId(CalendarSource source)
    {
        if (!string.IsNullOrEmpty(source.FolderId)) return Fid(source.FolderId);
        if (!string.IsNullOrEmpty(source.OwnerEmail) && source.IsShared)
            return new Ews.FolderId(Ews.WellKnownFolderName.Calendar, new Ews.Mailbox(source.OwnerEmail));
        return new Ews.FolderId(Ews.WellKnownFolderName.Calendar);
    }

    public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(CalendarSource source, DateTime start, DateTime end, CancellationToken ct = default) => Run(async () =>
    {
        if (source.IsShared && (source.Access < CalendarAccess.ReadAll || string.IsNullOrEmpty(source.FolderId)))
            return await GetFreeBusyEventsAsync(source, start, end);

        var result = new List<CalendarEvent>();
        var view = new Ews.CalendarView(start, end, 1000) { PropertySet = EventListProps };
        var found = await Svc.FindAppointments(CalendarFolderId(source), view);
        foreach (var a in found.Items) result.Add(ToEvent(a, source.Key));
        return (IReadOnlyList<CalendarEvent>)result;
    });

    private async Task<IReadOnlyList<CalendarEvent>> GetFreeBusyEventsAsync(CalendarSource source, DateTime start, DateTime end)
    {
        var result = new List<CalendarEvent>();
        var avail = await GetAvailabilityAsync(new[] { source.OwnerEmail! }, start, end);
        foreach (var slot in avail.SelectMany(a => a.Slots))
        {
            if (slot.Status == ShowAs.Free) continue;
            result.Add(new CalendarEvent
            {
                Id = "fb:" + slot.Start.Ticks,
                CalendarKey = source.Key,
                Subject = slot.Subject ?? StatusText(slot.Status),
                Location = slot.Location ?? "",
                Start = slot.Start,
                End = slot.End,
                ShowAs = slot.Status,
                IsFreeBusyOnly = true,
                IsOrganizer = false
            });
        }
        return result;
    }

    private static string StatusText(ShowAs s) => s switch
    {
        ShowAs.Tentative => "Tentative",
        ShowAs.OutOfOffice => "Away",
        ShowAs.WorkingElsewhere => "Working elsewhere",
        _ => "Busy"
    };

    private static ShowAs MapShowAs(Ews.LegacyFreeBusyStatus s) => s switch
    {
        Ews.LegacyFreeBusyStatus.Free => ShowAs.Free,
        Ews.LegacyFreeBusyStatus.Tentative => ShowAs.Tentative,
        Ews.LegacyFreeBusyStatus.OOF => ShowAs.OutOfOffice,
        Ews.LegacyFreeBusyStatus.WorkingElsewhere => ShowAs.WorkingElsewhere,
        Ews.LegacyFreeBusyStatus.NoData => ShowAs.NoData,
        _ => ShowAs.Busy
    };

    private static Ews.LegacyFreeBusyStatus MapShowAs(ShowAs s) => s switch
    {
        ShowAs.Free => Ews.LegacyFreeBusyStatus.Free,
        ShowAs.Tentative => Ews.LegacyFreeBusyStatus.Tentative,
        ShowAs.OutOfOffice => Ews.LegacyFreeBusyStatus.OOF,
        ShowAs.WorkingElsewhere => Ews.LegacyFreeBusyStatus.WorkingElsewhere,
        _ => Ews.LegacyFreeBusyStatus.Busy
    };

    private CalendarEvent ToEvent(Ews.Appointment a, string calendarKey)
    {
        var e = new CalendarEvent
        {
            Id = a.Id.UniqueId,
            ChangeKey = a.Id.ChangeKey,
            CalendarKey = calendarKey,
            Subject = TryGet(a, Ews.ItemSchema.Subject, ""),
            Location = TryGet(a, Ews.AppointmentSchema.Location, "") ?? "",
            Start = TryGet(a, Ews.AppointmentSchema.Start, DateTime.MinValue),
            End = TryGet(a, Ews.AppointmentSchema.End, DateTime.MinValue),
            IsAllDay = TryGet(a, Ews.AppointmentSchema.IsAllDayEvent, false),
            ShowAs = MapShowAs(TryGet(a, Ews.AppointmentSchema.LegacyFreeBusyStatus, Ews.LegacyFreeBusyStatus.Busy)),
            IsMeeting = TryGet(a, Ews.AppointmentSchema.IsMeeting, false),
            IsCancelled = TryGet(a, Ews.AppointmentSchema.IsCancelled, false),
            IsRecurring = TryGet(a, Ews.AppointmentSchema.IsRecurring, false),
            Sensitivity = MapSensitivity(TryGet(a, Ews.ItemSchema.Sensitivity, Ews.Sensitivity.Normal)),
            Importance = MapImportance(TryGet(a, Ews.ItemSchema.Importance, Ews.Importance.Normal)),
            ReminderSet = TryGet(a, Ews.ItemSchema.IsReminderSet, false),
            ReminderMinutes = TryGet(a, Ews.ItemSchema.ReminderMinutesBeforeStart, 15),
            MyResponse = MapResponse(TryGet(a, Ews.AppointmentSchema.MyResponseType, Ews.MeetingResponseType.Unknown)),
        };
        var type = TryGet(a, Ews.AppointmentSchema.AppointmentType, Ews.AppointmentType.Single);
        e.IsOccurrence = type is Ews.AppointmentType.Occurrence or Ews.AppointmentType.Exception;
        e.IsException = type == Ews.AppointmentType.Exception;
        e.IsRecurring = e.IsRecurring || type != Ews.AppointmentType.Single;
        if (a.TryGetProperty(Ews.ItemSchema.ReminderDueBy, out DateTime due)) e.ReminderDueBy = due;
        if (a.TryGetProperty(Ews.ItemSchema.Categories, out Ews.StringList cats) && cats != null) e.Categories = cats.ToList();
        var org = TryGet<Ews.EmailAddress?>(a, Ews.AppointmentSchema.Organizer, null);
        if (org != null) e.Organizer = new Recipient(org.Name ?? "", org.Address ?? "");
        e.IsOrganizer = !e.IsMeeting || e.MyResponse == MeetingResponse.Organizer ||
                        (org != null && string.Equals(org.Address, Account.Email, StringComparison.OrdinalIgnoreCase));
        return e;
    }

    public Task<CalendarEvent> GetEventAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        var props = new Ews.PropertySet(Ews.BasePropertySet.FirstClassProperties,
            Ews.AppointmentSchema.RequiredAttendees, Ews.AppointmentSchema.OptionalAttendees, Ews.AppointmentSchema.Resources,
            Ews.AppointmentSchema.Recurrence, Ews.AppointmentSchema.AppointmentType, Ews.AppointmentSchema.AllowNewTimeProposal,
            Ews.AppointmentSchema.IsResponseRequested, Ews.ItemSchema.Attachments)
        { RequestedBodyType = Ews.BodyType.HTML };
        var a = await Ews.Appointment.Bind(Svc, Iid(itemId), props);
        var e = ToEvent(a, "");
        var body = TryGet<Ews.MessageBody?>(a, Ews.ItemSchema.Body, null);
        e.BodyHtml = body == null ? "" : body.BodyType == Ews.BodyType.Text ? HtmlUtil.PlainTextToHtml(body.Text) : body.Text ?? "";
        e.AllowNewTimeProposal = TryGet(a, Ews.AppointmentSchema.AllowNewTimeProposal, true);
        e.ResponseRequested = TryGet(a, Ews.AppointmentSchema.IsResponseRequested, true);

        void AddAtt(IEnumerable<Ews.Attendee>? list, AttendeeType t)
        {
            if (list == null) return;
            foreach (var x in list)
                e.Attendees.Add(new Attendee
                {
                    Name = x.Name ?? "", Email = x.Address ?? "", Type = t,
                    Response = x.ResponseType.HasValue ? MapResponse(x.ResponseType.Value) : MeetingResponse.NoResponseReceived
                });
        }
        AddAtt(TryGet<Ews.AttendeeCollection?>(a, Ews.AppointmentSchema.RequiredAttendees, null), AttendeeType.Required);
        AddAtt(TryGet<Ews.AttendeeCollection?>(a, Ews.AppointmentSchema.OptionalAttendees, null), AttendeeType.Optional);
        AddAtt(TryGet<Ews.AttendeeCollection?>(a, Ews.AppointmentSchema.Resources, null), AttendeeType.Resource);

        var rec = TryGet<Ews.Recurrence?>(a, Ews.AppointmentSchema.Recurrence, null);
        if (rec != null) e.Recurrence = FromEwsRecurrence(rec);

        foreach (var att in a.Attachments.Where(x => !x.IsInline))
            e.Attachments.Add(new AttachmentInfo { Id = att.Id, Name = att.Name ?? "attachment", Size = att.Size, ContentType = att.ContentType ?? "" });
        return e;
    });

    private static DayOfWeek ToDow(Ews.DayOfTheWeek d) => d switch
    {
        Ews.DayOfTheWeek.Sunday => DayOfWeek.Sunday, Ews.DayOfTheWeek.Monday => DayOfWeek.Monday,
        Ews.DayOfTheWeek.Tuesday => DayOfWeek.Tuesday, Ews.DayOfTheWeek.Wednesday => DayOfWeek.Wednesday,
        Ews.DayOfTheWeek.Thursday => DayOfWeek.Thursday, Ews.DayOfTheWeek.Friday => DayOfWeek.Friday,
        _ => DayOfWeek.Saturday
    };

    private static Ews.DayOfTheWeek ToEwsDow(DayOfWeek d) => (Ews.DayOfTheWeek)Enum.Parse(typeof(Ews.DayOfTheWeek), d.ToString());

    private static RecurrenceInfo FromEwsRecurrence(Ews.Recurrence r)
    {
        var info = new RecurrenceInfo { StartDate = r.StartDate };
        switch (r)
        {
            case Ews.Recurrence.DailyPattern d:
                info.Type = RecurrenceType.Daily; info.Interval = d.Interval; break;
            case Ews.Recurrence.WeeklyPattern w:
                info.Type = RecurrenceType.Weekly; info.Interval = w.Interval;
                info.DaysOfWeek = w.DaysOfTheWeek.Select(ToDow).ToList();
                if (w.Interval == 1 && info.DaysOfWeek.Count == 5 && !info.DaysOfWeek.Contains(DayOfWeek.Saturday) && !info.DaysOfWeek.Contains(DayOfWeek.Sunday))
                    info.EveryWeekday = true;
                break;
            case Ews.Recurrence.MonthlyPattern m:
                info.Type = RecurrenceType.MonthlyAbsolute; info.Interval = m.Interval; info.DayOfMonth = m.DayOfMonth; break;
            case Ews.Recurrence.RelativeMonthlyPattern rm:
                info.Type = RecurrenceType.MonthlyRelative; info.Interval = rm.Interval;
                info.WeekIndex = (WeekIndex)(int)rm.DayOfTheWeekIndex; info.RelativeDay = ToDowSafe(rm.DayOfTheWeek); break;
            case Ews.Recurrence.YearlyPattern y:
                info.Type = RecurrenceType.YearlyAbsolute; info.Month = (int)y.Month; info.DayOfMonth = y.DayOfMonth; break;
            case Ews.Recurrence.RelativeYearlyPattern ry:
                info.Type = RecurrenceType.YearlyRelative; info.Month = (int)ry.Month;
                info.WeekIndex = (WeekIndex)(int)ry.DayOfTheWeekIndex; info.RelativeDay = ToDowSafe(ry.DayOfTheWeek); break;
        }
        if (r.NumberOfOccurrences.HasValue) { info.EndType = RecurrenceEnd.AfterOccurrences; info.Occurrences = r.NumberOfOccurrences.Value; }
        else if (r.EndDate.HasValue) { info.EndType = RecurrenceEnd.ByDate; info.EndDate = r.EndDate.Value; }
        else info.EndType = RecurrenceEnd.NoEnd;
        return info;
    }

    private static DayOfWeek ToDowSafe(Ews.DayOfTheWeek d) => d is Ews.DayOfTheWeek.Day or Ews.DayOfTheWeek.Weekday or Ews.DayOfTheWeek.WeekendDay ? DayOfWeek.Monday : ToDow(d);

    private static Ews.Recurrence ToEwsRecurrence(RecurrenceInfo r)
    {
        Ews.Recurrence rec = r.Type switch
        {
            RecurrenceType.Daily when r.EveryWeekday => new Ews.Recurrence.WeeklyPattern(r.StartDate, 1,
                Ews.DayOfTheWeek.Monday, Ews.DayOfTheWeek.Tuesday, Ews.DayOfTheWeek.Wednesday, Ews.DayOfTheWeek.Thursday, Ews.DayOfTheWeek.Friday),
            RecurrenceType.Daily => new Ews.Recurrence.DailyPattern(r.StartDate, Math.Max(1, r.Interval)),
            RecurrenceType.Weekly => new Ews.Recurrence.WeeklyPattern(r.StartDate, Math.Max(1, r.Interval),
                (r.DaysOfWeek.Count == 0 ? new List<DayOfWeek> { r.StartDate.DayOfWeek } : r.DaysOfWeek).Select(ToEwsDow).ToArray()),
            RecurrenceType.MonthlyAbsolute => new Ews.Recurrence.MonthlyPattern(r.StartDate, Math.Max(1, r.Interval), r.DayOfMonth),
            RecurrenceType.MonthlyRelative => new Ews.Recurrence.RelativeMonthlyPattern(r.StartDate, Math.Max(1, r.Interval), ToEwsDow(r.RelativeDay), (Ews.DayOfTheWeekIndex)(int)r.WeekIndex),
            RecurrenceType.YearlyAbsolute => new Ews.Recurrence.YearlyPattern(r.StartDate, (Ews.Month)r.Month, r.DayOfMonth),
            RecurrenceType.YearlyRelative => new Ews.Recurrence.RelativeYearlyPattern(r.StartDate, (Ews.Month)r.Month, ToEwsDow(r.RelativeDay), (Ews.DayOfTheWeekIndex)(int)r.WeekIndex),
            _ => throw new ArgumentException("No recurrence")
        };
        switch (r.EndType)
        {
            case RecurrenceEnd.AfterOccurrences: rec.NumberOfOccurrences = Math.Max(1, r.Occurrences); break;
            case RecurrenceEnd.ByDate: rec.EndDate = r.EndDate.Date; break;
            default: rec.NeverEnds(); break;
        }
        return rec;
    }

    public Task<string> SaveEventAsync(CalendarSource source, CalendarEvent ev, bool sendInvitations, EditScope scope, CancellationToken ct = default) => Run(async () =>
    {
        Ews.Appointment a;
        bool isNew = string.IsNullOrEmpty(ev.Id);
        if (isNew)
        {
            a = new Ews.Appointment(Svc);
        }
        else
        {
            var props = new Ews.PropertySet(Ews.BasePropertySet.FirstClassProperties, Ews.AppointmentSchema.RequiredAttendees,
                Ews.AppointmentSchema.OptionalAttendees, Ews.AppointmentSchema.Resources, Ews.ItemSchema.Attachments);
            a = scope == EditScope.Series && ev.IsOccurrence
                ? await Ews.Appointment.BindToRecurringMaster(Svc, Iid(ev.Id), props)
                : await Ews.Appointment.Bind(Svc, Iid(ev.Id), props);
        }

        a.Subject = ev.Subject;
        a.Location = ev.Location;
        a.IsAllDayEvent = ev.IsAllDay;
        if (!(scope == EditScope.Series && ev.IsOccurrence))
        {
            a.Start = ev.Start;
            a.End = ev.End;
        }
        else
        {
            // Keep the series' dates, update the times of day.
            a.Start = a.Start.Date + ev.Start.TimeOfDay;
            a.End = a.Start + (ev.End - ev.Start);
        }
        a.LegacyFreeBusyStatus = MapShowAs(ev.ShowAs);
        a.Body = new Ews.MessageBody(Ews.BodyType.HTML, ev.BodyHtml ?? "");
        a.IsReminderSet = ev.ReminderSet;
        if (ev.ReminderSet) a.ReminderMinutesBeforeStart = ev.ReminderMinutes;
        a.Sensitivity = MapSensitivity(ev.Sensitivity);
        a.Importance = MapImportance(ev.Importance);
        a.Categories = new Ews.StringList(ev.Categories);
        a.IsResponseRequested = ev.ResponseRequested;
        try { a.AllowNewTimeProposal = ev.AllowNewTimeProposal; } catch { }

        a.RequiredAttendees.Clear();
        a.OptionalAttendees.Clear();
        a.Resources.Clear();
        foreach (var at in ev.Attendees)
        {
            if (string.IsNullOrWhiteSpace(at.Email)) continue;
            var x = new Ews.Attendee(at.Name, at.Email);
            switch (at.Type)
            {
                case AttendeeType.Optional: a.OptionalAttendees.Add(x); break;
                case AttendeeType.Resource: a.Resources.Add(x); break;
                case AttendeeType.Organizer: break;
                default: a.RequiredAttendees.Add(x); break;
            }
        }

        if (ev.Recurrence != null && ev.Recurrence.Type != RecurrenceType.None && (isNew || scope == EditScope.Series || !ev.IsOccurrence))
        {
            var r = ev.Recurrence.Clone();
            if (isNew || !ev.IsOccurrence) r.StartDate = ev.Start.Date;
            a.Recurrence = ToEwsRecurrence(r);
        }
        else if (ev.Recurrence != null && ev.Recurrence.Type == RecurrenceType.None && !isNew && !ev.IsOccurrence && ev.IsRecurring)
        {
            a.Recurrence = null;
        }

        foreach (var att in ev.Attachments.Where(x => x.LocalPath != null || x.Content != null))
        {
            if (att.Content != null) a.Attachments.AddFileAttachment(att.Name, att.Content);
            else a.Attachments.AddFileAttachment(att.Name, att.LocalPath!);
        }

        var hasAttendees = ev.Attendees.Any(x => x.Type != AttendeeType.Organizer && !string.IsNullOrWhiteSpace(x.Email));
        if (isNew)
        {
            await a.Save(CalendarFolderId(source), hasAttendees && sendInvitations
                ? Ews.SendInvitationsMode.SendToAllAndSaveCopy : Ews.SendInvitationsMode.SendToNone);
        }
        else
        {
            await a.Update(Ews.ConflictResolutionMode.AutoResolve, hasAttendees && sendInvitations
                ? Ews.SendInvitationsOrCancellationsMode.SendToAllAndSaveCopy : Ews.SendInvitationsOrCancellationsMode.SendToNone);
        }
        return a.Id.UniqueId;
    });

    public Task DeleteEventAsync(string itemId, bool notifyAttendees, string? cancellationText, EditScope scope, CancellationToken ct = default) => Run(async () =>
    {
        var props = new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.AppointmentSchema.IsMeeting, Ews.AppointmentSchema.MyResponseType, Ews.AppointmentSchema.AppointmentType);
        var a = await Ews.Appointment.Bind(Svc, Iid(itemId), props);
        if (scope == EditScope.Series && a.AppointmentType is Ews.AppointmentType.Occurrence or Ews.AppointmentType.Exception)
            a = await Ews.Appointment.BindToRecurringMaster(Svc, Iid(itemId), props);

        var isOrganizerMeeting = a.IsMeeting && a.MyResponseType == Ews.MeetingResponseType.Organizer;
        if (isOrganizerMeeting && notifyAttendees)
        {
            if (string.IsNullOrWhiteSpace(cancellationText)) await a.CancelMeeting();
            else await a.CancelMeeting(cancellationText);
        }
        else if (a.IsMeeting && !isOrganizerMeeting && notifyAttendees)
        {
            var decline = a.CreateDeclineMessage();
            if (!string.IsNullOrWhiteSpace(cancellationText)) decline.Body = new Ews.MessageBody(Ews.BodyType.HTML, cancellationText);
            await decline.SendAndSaveCopy();
        }
        else
        {
            await a.Delete(Ews.DeleteMode.MoveToDeletedItems, Ews.SendCancellationsMode.SendToNone);
        }
    });

    public Task RespondToMeetingAsync(string itemId, MeetingResponse response, bool sendResponse, string? comment, CancellationToken ct = default) => Run(async () =>
    {
        var item = await Ews.Item.Bind(Svc, Iid(itemId), Ews.PropertySet.IdOnly);
        var body = string.IsNullOrWhiteSpace(comment) ? null : new Ews.MessageBody(Ews.BodyType.HTML, comment);

        if (item is Ews.MeetingRequest mr)
        {
            await RespondCore(response, sendResponse, body,
                t => mr.CreateAcceptMessage(t), () => mr.CreateDeclineMessage(),
                s => mr.Accept(s), s => mr.AcceptTentatively(s), s => mr.Decline(s));
        }
        else if (item is Ews.Appointment ap)
        {
            await RespondCore(response, sendResponse, body,
                t => ap.CreateAcceptMessage(t), () => ap.CreateDeclineMessage(),
                s => ap.Accept(s), s => ap.AcceptTentatively(s), s => ap.Decline(s));
        }
        else if (item is Ews.MeetingCancellation mc)
        {
            // "Remove from calendar"
            await mc.RemoveMeetingFromCalendar();
        }
        else
        {
            throw new MailServiceException("This item is not a meeting.");
        }
    });

    private static async Task RespondCore(MeetingResponse response, bool send, Ews.MessageBody? body,
        Func<bool, Ews.AcceptMeetingInvitationMessage> createAccept, Func<Ews.DeclineMeetingInvitationMessage> createDecline,
        Func<bool, Task> accept, Func<bool, Task> tentative, Func<bool, Task> decline)
    {
        if (send && body != null)
        {
            switch (response)
            {
                case MeetingResponse.Accept:
                case MeetingResponse.Tentative:
                    var am = createAccept(response == MeetingResponse.Tentative);
                    am.Body = body;
                    await am.SendAndSaveCopy();
                    return;
                case MeetingResponse.Decline:
                    var dm = createDecline();
                    dm.Body = body;
                    await dm.SendAndSaveCopy();
                    return;
            }
        }
        switch (response)
        {
            case MeetingResponse.Accept: await accept(send); break;
            case MeetingResponse.Tentative: await tentative(send); break;
            case MeetingResponse.Decline: await decline(send); break;
        }
    }

    public Task<IReadOnlyList<AttendeeAvailability>> GetAvailabilityAsync(IEnumerable<string> emails, DateTime start, DateTime end, CancellationToken ct = default) => Run(async () =>
    {
        var list = emails.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<AttendeeAvailability>();
        if (list.Count == 0) return (IReadOnlyList<AttendeeAvailability>)result;

        var winStart = start.Date;
        var winEnd = end.Date <= winStart ? winStart.AddDays(1) : end.Date.AddDays(end.TimeOfDay > TimeSpan.Zero ? 1 : 0);
        if ((winEnd - winStart).TotalHours < 24) winEnd = winStart.AddDays(1);

        var res = await Svc.GetUserAvailability(list.Select(e => new Ews.AttendeeInfo(e)), new Ews.TimeWindow(winStart, winEnd),
            Ews.AvailabilityData.FreeBusy, new Ews.AvailabilityOptions { RequestedFreeBusyView = Ews.FreeBusyViewType.Detailed, MaximumNonWorkHoursSuggestionsPerDay = 0 });
        int i = 0;
        foreach (var a in res.AttendeesAvailability)
        {
            var aa = new AttendeeAvailability { Email = list[i++] };
            if (a.Result != Ews.ServiceResult.Success) aa.HasError = true;
            else
            {
                foreach (var ce in a.CalendarEvents)
                {
                    aa.Slots.Add(new BusySlot
                    {
                        Start = ce.StartTime, End = ce.EndTime, Status = MapShowAs(ce.FreeBusyStatus),
                        Subject = ce.Details?.Subject, Location = ce.Details?.Location
                    });
                }
            }
            result.Add(aa);
        }
        return (IReadOnlyList<AttendeeAvailability>)result;
    });

    public Task DismissReminderAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        var item = await Ews.Item.Bind(Svc, Iid(itemId), new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.ItemSchema.IsReminderSet, Ews.AppointmentSchema.AppointmentType));
        if (item is Ews.Appointment ap && ap.AppointmentType != Ews.AppointmentType.Single) return; // occurrences are dismissed locally
        item.IsReminderSet = false;
        await UpdateItem(item);
    });

    public Task<IReadOnlyList<ReminderInfo>> GetUpcomingRemindersAsync(DateTime until, CancellationToken ct = default) => Run(async () =>
    {
        var result = new List<ReminderInfo>();
        var view = new Ews.CalendarView(DateTime.Now.AddHours(-12), until.AddDays(1), 500) { PropertySet = EventListProps };
        var found = await Svc.FindAppointments(Ews.WellKnownFolderName.Calendar, view);
        foreach (var a in found.Items)
        {
            if (!TryGet(a, Ews.ItemSchema.IsReminderSet, false)) continue;
            var start = a.Start;
            var minutes = TryGet(a, Ews.ItemSchema.ReminderMinutesBeforeStart, 15);
            var dueBy = start.AddMinutes(-minutes);
            if (dueBy > until) continue;
            if (a.End < DateTime.Now) continue;
            result.Add(new ReminderInfo
            {
                Key = $"{a.Id.UniqueId}|{start:O}", AccountId = Account.Id, ItemId = a.Id.UniqueId,
                Subject = TryGet(a, Ews.ItemSchema.Subject, ""), Location = TryGet(a, Ews.AppointmentSchema.Location, "") ?? "",
                Start = start, DueBy = dueBy
            });
        }
        return (IReadOnlyList<ReminderInfo>)result;
    });
}
