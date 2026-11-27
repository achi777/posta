using System.Globalization;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Outlook;

public sealed partial class OutlookMailService
{
    private static string D(DateTime d) => d.ToString("MM/dd/yyyy hh:mm tt", CultureInfo.InvariantCulture);

    // =====================================================================================
    // Calendar
    // =====================================================================================

    public Task<IReadOnlyList<CalendarSource>> GetOwnCalendarsAsync(CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        var list = new List<CalendarSource>();
        dynamic cal = _ns!.GetDefaultFolder(OL.Calendar);
        string storeId = TryStr(() => (string)cal.Store.StoreID);
        string id = Id((string)cal.EntryID, storeId);
        list.Add(new CalendarSource
        {
            Key = $"{Account.Id}||{id}",
            AccountId = Account.Id,
            FolderId = id,
            OwnerName = UserDisplayName ?? Account.Email,
            IsDefault = true,
            Group = CalendarGroupKind.MyCalendars,
            Access = CalendarAccess.Owner,
            DisplayName = (string)cal.Name,
            IsChecked = true,
            Color = "#A4C2F4",
        });
        // Other calendar folders in the mailbox.
        try
        {
            dynamic subs = cal.Folders;
            for (int i = 1; i <= (int)subs.Count; i++)
            {
                dynamic f = subs[i];
                string fid = Id((string)f.EntryID, storeId);
                list.Add(new CalendarSource
                {
                    Key = $"{Account.Id}||{fid}",
                    AccountId = Account.Id,
                    FolderId = fid,
                    OwnerName = UserDisplayName ?? Account.Email,
                    Group = CalendarGroupKind.MyCalendars,
                    Access = CalendarAccess.Owner,
                    DisplayName = (string)f.Name,
                    Color = "#B6D7A8",
                });
            }
        }
        catch { }
        return (IReadOnlyList<CalendarSource>)list;
    });

    public Task<CalendarSource> OpenSharedCalendarAsync(string ownerEmail, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic recip = _ns!.CreateRecipient(ownerEmail);
        recip.Resolve();
        if (!(bool)recip.Resolved) throw new MailServiceException($"მომხმარებელი ვერ მოიძებნა: {ownerEmail}");
        dynamic f = _ns!.GetSharedDefaultFolder(recip, OL.Calendar);
        string storeId = TryStr(() => (string)f.Store.StoreID);
        string fid = Id((string)f.EntryID, storeId);
        return new CalendarSource
        {
            Key = $"{Account.Id}|{ownerEmail}|{fid}",
            AccountId = Account.Id,
            FolderId = fid,
            OwnerEmail = ownerEmail,
            OwnerName = TryStr(() => (string)recip.Name),
            IsShared = true,
            Group = CalendarGroupKind.SharedCalendars,
            Access = CalendarAccess.ReadAll,
            DisplayName = TryStr(() => (string)recip.Name),
            IsChecked = true,
            Color = "#C0E4A8",
        };
    });

    public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(CalendarSource source, DateTime start, DateTime end, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        var list = new List<CalendarEvent>();
        dynamic folder = CalendarFolderOf(source);
        dynamic items = folder.Items;
        try { items.Sort("[Start]"); } catch { }
        try { items.IncludeRecurrences = true; } catch { }
        string filter = $"[Start] < '{D(end)}' AND [End] > '{D(start)}'";
        dynamic restricted;
        try { restricted = items.Restrict(filter); } catch { restricted = items; }
        int n = 0;
        try { n = (int)restricted.Count; } catch { }
        int guard = 0;
        for (int i = 1; i <= n && guard < 1000; i++, guard++)
        {
            dynamic a;
            try { a = restricted[i]; } catch { break; }
            var ev = MapAppointment(a, source.Key, false);
            if (ev != null) list.Add(ev);
        }
        return (IReadOnlyList<CalendarEvent>)list;
    });

    private dynamic CalendarFolderOf(CalendarSource source)
    {
        if (!string.IsNullOrEmpty(source.FolderId)) return GetFolder(source.FolderId);
        if (!string.IsNullOrEmpty(source.OwnerEmail))
        {
            dynamic recip = _ns!.CreateRecipient(source.OwnerEmail);
            recip.Resolve();
            return _ns!.GetSharedDefaultFolder(recip, OL.Calendar);
        }
        return _ns!.GetDefaultFolder(OL.Calendar);
    }

    private CalendarEvent? MapAppointment(dynamic a, string calendarKey, bool withRecurrence)
    {
        try
        {
            string storeId = TryStr(() => (string)a.Parent.Store.StoreID);
            var ev = new CalendarEvent
            {
                Id = Id((string)a.EntryID, storeId),
                CalendarKey = calendarKey,
                Subject = TryStr(() => (string)a.Subject),
                Location = TryStr(() => (string)a.Location),
            };
            try { ev.Start = (DateTime)a.Start; } catch { }
            try { ev.End = (DateTime)a.End; } catch { }
            try { ev.IsAllDay = (bool)a.AllDayEvent; } catch { }
            try { ev.ShowAs = MapBusy((int)a.BusyStatus); } catch { }
            ev.BodyHtml = PlainToHtml(TryStr(() => (string)a.Body));
            try { ev.ReminderSet = (bool)a.ReminderSet; } catch { }
            try { ev.ReminderMinutes = (int)a.ReminderMinutesBeforeStart; } catch { }
            try { ev.Importance = MapImportance((int)a.Importance); } catch { }
            try { ev.Sensitivity = MapSensitivity((int)a.Sensitivity); } catch { }
            try { ev.Categories = SplitCategories((string)a.Categories); } catch { }
            int recState = 0;
            try { recState = (int)a.RecurrenceState; } catch { }
            ev.IsRecurring = recState != 0;
            ev.IsOccurrence = recState == 2;
            ev.IsException = recState == 3;
            int meetStatus = OL.NonMeeting;
            try { meetStatus = (int)a.MeetingStatus; } catch { }
            ev.IsMeeting = meetStatus != OL.NonMeeting;
            ev.IsCancelled = meetStatus is OL.MeetingCanceled or OL.MeetingReceivedAndCanceled;
            try { ev.MyResponse = MapResponse((int)a.ResponseStatus); } catch { }
            try { ev.IsOrganizer = (int)a.ResponseStatus == OL.ResponseOrganized || (int)a.ResponseStatus == OL.ResponseNone; } catch { }
            ev.Organizer = new Recipient(TryStr(() => (string)a.Organizer), "");
            ev.Attendees = MapAttendees(a);
            if (ev.IsMeeting && ev.Attendees.Count == 0) ev.IsMeeting = ev.Attendees.Count > 0;
            if (withRecurrence && ev.IsRecurring && recState == 1)
                ev.Recurrence = ReadRecurrence(a);
            return ev;
        }
        catch { return null; }
    }

    private List<Attendee> MapAttendees(dynamic a)
    {
        var list = new List<Attendee>();
        try
        {
            dynamic recips = a.Recipients;
            int n = (int)recips.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic r = recips[i];
                var at = new Attendee { Name = TryStr(() => (string)r.Name) };
                var ae = Try(() => (object?)r.AddressEntry);
                at.Email = ae != null ? SmtpOfAddressEntry((dynamic)ae) : TryStr(() => (string)r.Address);
                int t = 1; try { t = (int)r.Type; } catch { }
                at.Type = t switch { 0 => AttendeeType.Organizer, 2 => AttendeeType.Optional, 3 => AttendeeType.Resource, _ => AttendeeType.Required };
                try { at.Response = MapResponse((int)r.MeetingResponseStatus); } catch { }
                list.Add(at);
            }
        }
        catch { }
        return list;
    }

    private RecurrenceInfo? ReadRecurrence(dynamic a)
    {
        try
        {
            dynamic p = a.GetRecurrencePattern();
            var r = new RecurrenceInfo { Interval = (int)p.Interval };
            int rt = (int)p.RecurrenceType;
            r.Type = rt switch
            {
                OL.RecursDaily => RecurrenceType.Daily,
                OL.RecursWeekly => RecurrenceType.Weekly,
                OL.RecursMonthly => RecurrenceType.MonthlyAbsolute,
                OL.RecursMonthNth => RecurrenceType.MonthlyRelative,
                OL.RecursYearly => RecurrenceType.YearlyAbsolute,
                OL.RecursYearNth => RecurrenceType.YearlyRelative,
                _ => RecurrenceType.None
            };
            try { r.DaysOfWeek = DecodeDows((int)p.DayOfWeekMask); } catch { }
            try { r.DayOfMonth = (int)p.DayOfMonth; } catch { }
            try { r.Month = (int)p.MonthOfYear; } catch { }
            try { r.StartDate = (DateTime)p.PatternStartDate; } catch { }
            try
            {
                if (!(bool)p.NoEndDate)
                {
                    r.EndType = RecurrenceEnd.ByDate;
                    r.EndDate = (DateTime)p.PatternEndDate;
                    r.Occurrences = (int)p.Occurrences;
                }
                else r.EndType = RecurrenceEnd.NoEnd;
            }
            catch { }
            return r;
        }
        catch { return null; }
    }

    private static List<DayOfWeek> DecodeDows(int mask)
    {
        var days = new List<DayOfWeek>();
        if ((mask & OL.Sunday) != 0) days.Add(DayOfWeek.Sunday);
        if ((mask & OL.Monday) != 0) days.Add(DayOfWeek.Monday);
        if ((mask & OL.Tuesday) != 0) days.Add(DayOfWeek.Tuesday);
        if ((mask & OL.Wednesday) != 0) days.Add(DayOfWeek.Wednesday);
        if ((mask & OL.Thursday) != 0) days.Add(DayOfWeek.Thursday);
        if ((mask & OL.Friday) != 0) days.Add(DayOfWeek.Friday);
        if ((mask & OL.Saturday) != 0) days.Add(DayOfWeek.Saturday);
        return days;
    }

    private static int EncodeDows(IEnumerable<DayOfWeek> days)
    {
        int mask = 0;
        foreach (var d in days)
            mask |= d switch
            {
                DayOfWeek.Sunday => OL.Sunday,
                DayOfWeek.Monday => OL.Monday,
                DayOfWeek.Tuesday => OL.Tuesday,
                DayOfWeek.Wednesday => OL.Wednesday,
                DayOfWeek.Thursday => OL.Thursday,
                DayOfWeek.Friday => OL.Friday,
                _ => OL.Saturday
            };
        return mask;
    }

    public Task<CalendarEvent> GetEventAsync(string itemId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic a = GetItem(itemId);
        CalendarEvent? ev = MapAppointment(a, "", true);
        return ev ?? throw new MailServiceException("შეხვედრა ვერ მოიძებნა.");
    });

    public Task<string> SaveEventAsync(CalendarSource source, CalendarEvent ev, bool sendInvitations, EditScope scope, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic a;
        if (string.IsNullOrEmpty(ev.Id))
        {
            dynamic folder = CalendarFolderOf(source);
            a = folder.Items.Add(OL.AppointmentItem);
        }
        else a = GetItem(ev.Id);

        a.Subject = ev.Subject ?? "";
        a.Location = ev.Location ?? "";
        a.Start = ev.Start;
        a.End = ev.End;
        a.AllDayEvent = ev.IsAllDay;
        a.BusyStatus = MapBusy(ev.ShowAs);
        a.Body = HtmlToPlain(ev.BodyHtml);
        a.ReminderSet = ev.ReminderSet;
        if (ev.ReminderSet) a.ReminderMinutesBeforeStart = ev.ReminderMinutes;
        a.Importance = MapImportance(ev.Importance);
        a.Sensitivity = MapSensitivity(ev.Sensitivity);
        if (ev.Categories.Count > 0) a.Categories = string.Join("; ", ev.Categories);

        if (ev.IsMeeting && ev.Attendees.Count > 0)
        {
            try { a.MeetingStatus = OL.Meeting; } catch { }
            try { while ((int)a.Recipients.Count > 0) a.Recipients.Remove(1); } catch { }
            foreach (var at in ev.Attendees)
            {
                if (at.Type == AttendeeType.Organizer) continue;
                try
                {
                    dynamic r = a.Recipients.Add(string.IsNullOrWhiteSpace(at.Email) ? at.Name : at.Email);
                    r.Type = at.Type switch { AttendeeType.Optional => 2, AttendeeType.Resource => 3, _ => 1 };
                }
                catch { }
            }
            try { a.Recipients.ResolveAll(); } catch { }
        }

        if (ev.Recurrence != null && ev.Recurrence.Type != RecurrenceType.None && string.IsNullOrEmpty(ev.Id))
            WriteRecurrence(a, ev.Recurrence);

        if (ev.IsMeeting && ev.Attendees.Count > 0 && sendInvitations) a.Send();
        else a.Save();

        return Id((string)a.EntryID, TryStr(() => (string)a.Parent.Store.StoreID));
    });

    private void WriteRecurrence(dynamic a, RecurrenceInfo r)
    {
        try
        {
            dynamic p = a.GetRecurrencePattern();
            p.RecurrenceType = r.Type switch
            {
                RecurrenceType.Daily => OL.RecursDaily,
                RecurrenceType.Weekly => OL.RecursWeekly,
                RecurrenceType.MonthlyAbsolute => OL.RecursMonthly,
                RecurrenceType.MonthlyRelative => OL.RecursMonthNth,
                RecurrenceType.YearlyAbsolute => OL.RecursYearly,
                RecurrenceType.YearlyRelative => OL.RecursYearNth,
                _ => OL.RecursDaily
            };
            if (r.Interval > 0) try { p.Interval = r.Interval; } catch { }
            if (r.DaysOfWeek.Count > 0) try { p.DayOfWeekMask = EncodeDows(r.DaysOfWeek); } catch { }
            if (r.Type is RecurrenceType.MonthlyAbsolute or RecurrenceType.YearlyAbsolute) try { p.DayOfMonth = r.DayOfMonth; } catch { }
            if (r.Type is RecurrenceType.YearlyAbsolute or RecurrenceType.YearlyRelative) try { p.MonthOfYear = r.Month; } catch { }
            try { p.PatternStartDate = r.StartDate; } catch { }
            switch (r.EndType)
            {
                case RecurrenceEnd.AfterOccurrences: try { p.Occurrences = r.Occurrences; } catch { } break;
                case RecurrenceEnd.ByDate: try { p.PatternEndDate = r.EndDate; } catch { } break;
            }
        }
        catch { }
    }

    public Task DeleteEventAsync(string itemId, bool notifyAttendees, string? cancellationText, EditScope scope, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic a = GetItem(itemId);
        int meetStatus = OL.NonMeeting;
        try { meetStatus = (int)a.MeetingStatus; } catch { }
        bool organizer = true;
        try { organizer = (int)a.ResponseStatus is OL.ResponseOrganized or OL.ResponseNone; } catch { }
        if (notifyAttendees && meetStatus == OL.Meeting && organizer)
        {
            try
            {
                a.MeetingStatus = OL.MeetingCanceled;
                if (!string.IsNullOrEmpty(cancellationText)) a.Body = cancellationText + "\r\n\r\n" + TryStr(() => (string)a.Body);
                a.Save();
                a.Send();
            }
            catch { }
        }
        a.Delete();
    });

    public Task RespondToMeetingAsync(string itemId, MeetingResponse response, bool sendResponse, string? comment, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic it = GetItem(itemId);
        dynamic appt = it;
        string cls = TryStr(() => (string)it.MessageClass);
        if (cls.StartsWith("IPM.Schedule.Meeting", StringComparison.OrdinalIgnoreCase))
        {
            try { dynamic assoc = it.GetAssociatedAppointment(true); if (assoc != null) appt = assoc; } catch { }
        }
        int resp = response switch
        {
            MeetingResponse.Accept => OL.RespAccepted,
            MeetingResponse.Tentative => OL.RespTentative,
            MeetingResponse.Decline => OL.RespDeclined,
            _ => OL.RespTentative
        };
        dynamic responseItem = appt.Respond(resp, true, false);
        if (!string.IsNullOrEmpty(comment)) try { responseItem.Body = comment + "\r\n\r\n" + TryStr(() => (string)responseItem.Body); } catch { }
        if (sendResponse) responseItem.Send(); else try { responseItem.Save(); } catch { }
    });

    public Task<IReadOnlyList<AttendeeAvailability>> GetAvailabilityAsync(IEnumerable<string> emails, DateTime start, DateTime end, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        var result = new List<AttendeeAvailability>();
        foreach (var email in emails)
        {
            var av = new AttendeeAvailability { Email = email, Name = email };
            try
            {
                dynamic recip = _ns!.CreateRecipient(email);
                recip.Resolve();
                if (!(bool)recip.Resolved) { av.HasError = true; result.Add(av); continue; }
                av.Name = TryStr(() => (string)recip.Name);
                // One char per 30 minutes: '0' free, '1' tentative, '2' busy, '3' OOF.
                string fb = (string)recip.FreeBusy(start, 30, true);
                var dayStart = start.Date;
                for (int i = 0; i < fb.Length; i++)
                {
                    char c = fb[i];
                    if (c == '0') continue;
                    var slotStart = dayStart.AddMinutes(30 * i);
                    if (slotStart < start || slotStart >= end) continue;
                    av.Slots.Add(new BusySlot
                    {
                        Start = slotStart,
                        End = slotStart.AddMinutes(30),
                        Status = c switch { '1' => ShowAs.Tentative, '3' => ShowAs.OutOfOffice, _ => ShowAs.Busy }
                    });
                }
                MergeSlots(av.Slots);
            }
            catch { av.HasError = true; }
            result.Add(av);
        }
        return (IReadOnlyList<AttendeeAvailability>)result;
    });

    private static void MergeSlots(List<BusySlot> slots)
    {
        slots.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = slots.Count - 1; i > 0; i--)
        {
            if (slots[i - 1].End >= slots[i].Start && slots[i - 1].Status == slots[i].Status)
            {
                slots[i - 1].End = slots[i].End > slots[i - 1].End ? slots[i].End : slots[i - 1].End;
                slots.RemoveAt(i);
            }
        }
    }

    public Task DismissReminderAsync(string itemId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        var (entry, _) = Split(itemId);
        try
        {
            dynamic reminders = _app!.Reminders;
            int n = (int)reminders.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic rem = reminders[i];
                try { if ((string)rem.Item.EntryID == entry) { rem.Dismiss(); return; } } catch { }
            }
        }
        catch { }
    });

    public Task<IReadOnlyList<ReminderInfo>> GetUpcomingRemindersAsync(DateTime until, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        var list = new List<ReminderInfo>();
        try
        {
            dynamic reminders = _app!.Reminders;
            int n = (int)reminders.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic rem = reminders[i];
                try
                {
                    if (!(bool)rem.IsVisible) continue;
                    dynamic it = rem.Item;
                    string cls = TryStr(() => (string)it.MessageClass);
                    bool isTask = cls.StartsWith("IPM.Task", StringComparison.OrdinalIgnoreCase);
                    DateTime start = DateTime.Now;
                    try { start = isTask ? (DateTime)it.DueDate : (DateTime)it.Start; } catch { }
                    DateTime due = DateTime.Now;
                    try { due = (DateTime)rem.OriginalReminderDate; } catch { due = start; }
                    list.Add(new ReminderInfo
                    {
                        Key = TryStr(() => (string)it.EntryID),
                        AccountId = Account.Id,
                        ItemId = Id(TryStr(() => (string)it.EntryID), TryStr(() => (string)it.Parent.Store.StoreID)),
                        Subject = TryStr(() => (string)it.Subject),
                        Location = isTask ? "" : TryStr(() => (string)it.Location),
                        Start = start,
                        DueBy = due,
                        IsTask = isTask
                    });
                }
                catch { }
            }
        }
        catch { }
        return (IReadOnlyList<ReminderInfo>)list;
    });

    // =====================================================================================
    // Contacts
    // =====================================================================================

    public Task<IReadOnlyList<ContactInfo>> GetContactsAsync(string folderId, string? search, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic folder = string.IsNullOrEmpty(folderId) ? _ns!.GetDefaultFolder(OL.Contacts) : GetFolder(folderId);
        string fid = Id((string)folder.EntryID, TryStr(() => (string)folder.Store.StoreID));
        dynamic items = folder.Items;
        var list = new List<ContactInfo>();
        int n = 0;
        try { n = (int)items.Count; } catch { }
        for (int i = 1; i <= n; i++)
        {
            dynamic it;
            try { it = items[i]; } catch { continue; }
            string cls = TryStr(() => (string)it.MessageClass);
            ContactInfo? c = cls.StartsWith("IPM.DistList", StringComparison.OrdinalIgnoreCase) ? MapDistList(it, fid) : MapContact(it, fid, false);
            if (c == null) continue;
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                if (!(c.DisplayName.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                      c.FileAs.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                      c.Company.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                      c.PrimaryEmail.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }
            list.Add(c);
        }
        return (IReadOnlyList<ContactInfo>)list;
    });

    private ContactInfo? MapContact(dynamic it, string folderId, bool full)
    {
        try
        {
            var c = new ContactInfo
            {
                Id = Id((string)it.EntryID, TryStr(() => (string)it.Parent.Store.StoreID)),
                AccountId = Account.Id,
                FolderId = folderId,
                DisplayName = TryStr(() => (string)it.FullName),
                FileAs = TryStr(() => (string)it.FileAs),
                GivenName = TryStr(() => (string)it.FirstName),
                MiddleName = TryStr(() => (string)it.MiddleName),
                Surname = TryStr(() => (string)it.LastName),
                Title = TryStr(() => (string)it.Title),
                Suffix = TryStr(() => (string)it.Suffix),
                Nickname = TryStr(() => (string)it.NickName),
                Company = TryStr(() => (string)it.CompanyName),
                Department = TryStr(() => (string)it.Department),
                JobTitle = TryStr(() => (string)it.JobTitle),
                Office = TryStr(() => (string)it.OfficeLocation),
                Manager = TryStr(() => (string)it.ManagerName),
                Assistant = TryStr(() => (string)it.AssistantName),
                Email1 = TryStr(() => (string)it.Email1Address),
                Email1Name = TryStr(() => (string)it.Email1DisplayName),
                Email2 = TryStr(() => (string)it.Email2Address),
                Email3 = TryStr(() => (string)it.Email3Address),
                BusinessPhone = TryStr(() => (string)it.BusinessTelephoneNumber),
                BusinessPhone2 = TryStr(() => (string)it.Business2TelephoneNumber),
                HomePhone = TryStr(() => (string)it.HomeTelephoneNumber),
                MobilePhone = TryStr(() => (string)it.MobileTelephoneNumber),
                BusinessFax = TryStr(() => (string)it.BusinessFaxNumber),
                Pager = TryStr(() => (string)it.PagerNumber),
                WebPage = TryStr(() => (string)it.WebPage),
                ImAddress = TryStr(() => (string)it.IMAddress),
                SpouseName = TryStr(() => (string)it.Spouse),
                Notes = TryStr(() => (string)it.Body),
            };
            if (string.IsNullOrEmpty(c.DisplayName)) c.DisplayName = string.Join(" ", new[] { c.GivenName, c.Surname }.Where(x => !string.IsNullOrEmpty(x)));
            c.BusinessAddress = new PostalAddress
            {
                Street = TryStr(() => (string)it.BusinessAddressStreet),
                City = TryStr(() => (string)it.BusinessAddressCity),
                State = TryStr(() => (string)it.BusinessAddressState),
                PostalCode = TryStr(() => (string)it.BusinessAddressPostalCode),
                Country = TryStr(() => (string)it.BusinessAddressCountry),
            };
            c.HomeAddress = new PostalAddress
            {
                Street = TryStr(() => (string)it.HomeAddressStreet),
                City = TryStr(() => (string)it.HomeAddressCity),
                State = TryStr(() => (string)it.HomeAddressState),
                PostalCode = TryStr(() => (string)it.HomeAddressPostalCode),
                Country = TryStr(() => (string)it.HomeAddressCountry),
            };
            try { var b = (DateTime)it.Birthday; if (b.Year > 1900 && b.Year < 4000) c.Birthday = b; } catch { }
            try { var an = (DateTime)it.Anniversary; if (an.Year > 1900 && an.Year < 4000) c.Anniversary = an; } catch { }
            try { c.Categories = SplitCategories((string)it.Categories); } catch { }
            return c;
        }
        catch { return null; }
    }

    private ContactInfo? MapDistList(dynamic it, string folderId)
    {
        try
        {
            var c = new ContactInfo
            {
                Id = Id((string)it.EntryID, TryStr(() => (string)it.Parent.Store.StoreID)),
                AccountId = Account.Id,
                FolderId = folderId,
                IsGroup = true,
                DisplayName = TryStr(() => (string)it.DLName),
                Notes = TryStr(() => (string)it.Body),
            };
            if (string.IsNullOrEmpty(c.DisplayName)) c.DisplayName = TryStr(() => (string)it.Subject);
            try
            {
                int n = (int)it.MemberCount;
                for (int i = 1; i <= n; i++)
                {
                    dynamic m = it.GetMember(i);
                    c.Members.Add(new Recipient(TryStr(() => (string)m.Name), TryStr(() => (string)m.Address)));
                }
            }
            catch { }
            try { c.Categories = SplitCategories((string)it.Categories); } catch { }
            return c;
        }
        catch { return null; }
    }

    public Task<ContactInfo> GetContactAsync(string itemId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic it = GetItem(itemId);
        string cls = TryStr(() => (string)it.MessageClass);
        ContactInfo? c = cls.StartsWith("IPM.DistList", StringComparison.OrdinalIgnoreCase) ? MapDistList(it, "") : MapContact(it, "", true);
        return c ?? throw new MailServiceException("კონტაქტი ვერ მოიძებნა.");
    });

    public Task<string> SaveContactAsync(string folderId, ContactInfo contact, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic it;
        if (string.IsNullOrEmpty(contact.Id))
        {
            dynamic folder = string.IsNullOrEmpty(folderId) ? _ns!.GetDefaultFolder(OL.Contacts) : GetFolder(folderId);
            it = folder.Items.Add(contact.IsGroup ? OL.DistributionListItem : OL.ContactItem);
        }
        else it = GetItem(contact.Id);

        if (contact.IsGroup)
        {
            try { it.DLName = contact.DisplayName; } catch { }
            it.Body = contact.Notes ?? "";
            it.Save();
            return Id((string)it.EntryID, TryStr(() => (string)it.Parent.Store.StoreID));
        }

        it.FullName = contact.DisplayName ?? "";
        Set(() => it.FirstName = contact.GivenName);
        Set(() => it.MiddleName = contact.MiddleName);
        Set(() => it.LastName = contact.Surname);
        Set(() => it.Title = contact.Title);
        Set(() => it.Suffix = contact.Suffix);
        Set(() => it.NickName = contact.Nickname);
        Set(() => it.CompanyName = contact.Company);
        Set(() => it.Department = contact.Department);
        Set(() => it.JobTitle = contact.JobTitle);
        Set(() => it.OfficeLocation = contact.Office);
        Set(() => it.ManagerName = contact.Manager);
        Set(() => it.AssistantName = contact.Assistant);
        if (!string.IsNullOrEmpty(contact.FileAs)) Set(() => it.FileAs = contact.FileAs);
        Set(() => it.Email1Address = contact.Email1);
        Set(() => it.Email2Address = contact.Email2);
        Set(() => it.Email3Address = contact.Email3);
        Set(() => it.BusinessTelephoneNumber = contact.BusinessPhone);
        Set(() => it.Business2TelephoneNumber = contact.BusinessPhone2);
        Set(() => it.HomeTelephoneNumber = contact.HomePhone);
        Set(() => it.MobileTelephoneNumber = contact.MobilePhone);
        Set(() => it.BusinessFaxNumber = contact.BusinessFax);
        Set(() => it.PagerNumber = contact.Pager);
        Set(() => it.WebPage = contact.WebPage);
        Set(() => it.IMAddress = contact.ImAddress);
        Set(() => it.Spouse = contact.SpouseName);
        Set(() => it.Body = contact.Notes);
        Set(() => it.BusinessAddressStreet = contact.BusinessAddress.Street);
        Set(() => it.BusinessAddressCity = contact.BusinessAddress.City);
        Set(() => it.BusinessAddressState = contact.BusinessAddress.State);
        Set(() => it.BusinessAddressPostalCode = contact.BusinessAddress.PostalCode);
        Set(() => it.BusinessAddressCountry = contact.BusinessAddress.Country);
        Set(() => it.HomeAddressStreet = contact.HomeAddress.Street);
        Set(() => it.HomeAddressCity = contact.HomeAddress.City);
        Set(() => it.HomeAddressState = contact.HomeAddress.State);
        Set(() => it.HomeAddressPostalCode = contact.HomeAddress.PostalCode);
        Set(() => it.HomeAddressCountry = contact.HomeAddress.Country);
        if (contact.Birthday.HasValue) Set(() => it.Birthday = contact.Birthday!.Value);
        if (contact.Anniversary.HasValue) Set(() => it.Anniversary = contact.Anniversary!.Value);
        if (contact.Categories.Count > 0) Set(() => it.Categories = string.Join("; ", contact.Categories));
        it.Save();
        return Id((string)it.EntryID, TryStr(() => (string)it.Parent.Store.StoreID));
    });

    private static void Set(Action a) { try { a(); } catch { } }

    public Task DeleteContactAsync(string itemId, CancellationToken ct = default) => Com(() =>
    {
        Ensure(); GetItem(itemId).Delete();
    });

    // =====================================================================================
    // Tasks
    // =====================================================================================

    public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(string folderId, bool includeCompleted, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic folder = string.IsNullOrEmpty(folderId) ? _ns!.GetDefaultFolder(OL.Tasks) : GetFolder(folderId);
        string fid = Id((string)folder.EntryID, TryStr(() => (string)folder.Store.StoreID));
        dynamic items = folder.Items;
        var list = new List<TaskInfo>();
        int n = 0; try { n = (int)items.Count; } catch { }
        for (int i = 1; i <= n; i++)
        {
            dynamic it; try { it = items[i]; } catch { continue; }
            string cls = TryStr(() => (string)it.MessageClass);
            if (!cls.StartsWith("IPM.Task", StringComparison.OrdinalIgnoreCase)) continue;
            var t = MapTask(it, fid);
            if (t == null) continue;
            if (!includeCompleted && t.IsComplete) continue;
            list.Add(t);
        }
        return (IReadOnlyList<TaskInfo>)list;
    });

    private TaskInfo? MapTask(dynamic it, string folderId)
    {
        try
        {
            var t = new TaskInfo
            {
                Id = Id((string)it.EntryID, TryStr(() => (string)it.Parent.Store.StoreID)),
                AccountId = Account.Id,
                FolderId = folderId,
                Subject = TryStr(() => (string)it.Subject),
                BodyHtml = PlainToHtml(TryStr(() => (string)it.Body)),
                Owner = TryStr(() => (string)it.Owner),
            };
            try { int s = (int)it.Status; t.Status = s switch { OL.TaskInProgress => TaskState.InProgress, OL.TaskComplete => TaskState.Completed, OL.TaskWaiting => TaskState.WaitingOnOthers, OL.TaskDeferred => TaskState.Deferred, _ => TaskState.NotStarted }; } catch { }
            try { t.PercentComplete = (int)it.PercentComplete; } catch { }
            try { t.Importance = MapImportance((int)it.Importance); } catch { }
            try { var d = (DateTime)it.StartDate; if (d.Year is > 1900 and < 4000) t.StartDate = d; } catch { }
            try { var d = (DateTime)it.DueDate; if (d.Year is > 1900 and < 4000) t.DueDate = d; } catch { }
            try { var d = (DateTime)it.DateCompleted; if (d.Year is > 1900 and < 4000) t.CompleteDate = d; } catch { }
            try { t.ReminderSet = (bool)it.ReminderSet; } catch { }
            try { if (t.ReminderSet) { var d = (DateTime)it.ReminderTime; if (d.Year is > 1900 and < 4000) t.ReminderTime = d; } } catch { }
            try { t.Categories = SplitCategories((string)it.Categories); } catch { }
            return t;
        }
        catch { return null; }
    }

    public Task<TaskInfo> GetTaskAsync(string itemId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic it = GetItem(itemId);
        TaskInfo? t = MapTask(it, "");
        return t ?? throw new MailServiceException("ამოცანა ვერ მოიძებნა.");
    });

    public Task<string> SaveTaskAsync(string folderId, TaskInfo task, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic it;
        if (string.IsNullOrEmpty(task.Id))
        {
            dynamic folder = string.IsNullOrEmpty(folderId) ? _ns!.GetDefaultFolder(OL.Tasks) : GetFolder(folderId);
            it = folder.Items.Add(OL.TaskItem);
        }
        else it = GetItem(task.Id);

        it.Subject = task.Subject ?? "";
        Set(() => it.Body = HtmlToPlain(task.BodyHtml));
        Set(() => it.Status = task.Status switch
        {
            TaskState.InProgress => OL.TaskInProgress,
            TaskState.Completed => OL.TaskComplete,
            TaskState.WaitingOnOthers => OL.TaskWaiting,
            TaskState.Deferred => OL.TaskDeferred,
            _ => OL.TaskNotStarted
        });
        Set(() => it.PercentComplete = task.PercentComplete);
        Set(() => it.Importance = MapImportance(task.Importance));
        Set(() => it.StartDate = task.StartDate ?? new DateTime(4501, 1, 1));
        Set(() => it.DueDate = task.DueDate ?? new DateTime(4501, 1, 1));
        Set(() => it.ReminderSet = task.ReminderSet);
        if (task.ReminderSet && task.ReminderTime.HasValue) Set(() => it.ReminderTime = task.ReminderTime.Value);
        if (task.Categories.Count > 0) Set(() => it.Categories = string.Join("; ", task.Categories));
        it.Save();
        return Id((string)it.EntryID, TryStr(() => (string)it.Parent.Store.StoreID));
    });

    public Task DeleteTaskAsync(string itemId, CancellationToken ct = default) => Com(() =>
    {
        Ensure(); GetItem(itemId).Delete();
    });

    // =====================================================================================
    // Mailbox settings (limited through the Outlook object model)
    // =====================================================================================

    public Task<OofSettingsInfo> GetOofAsync(CancellationToken ct = default) =>
        Task.FromResult(new OofSettingsInfo { State = OofState.Disabled });

    public Task SetOofAsync(OofSettingsInfo settings, CancellationToken ct = default) =>
        throw new MailServiceException("ავტომატური პასუხების (Out of Office) მართვა Outlook-ის რეჟიმში მიუწვდომელია. გამოიყენეთ ვებფოსტა (OWA).");

    public Task<IReadOnlyList<RuleInfo>> GetRulesAsync(CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        var list = new List<RuleInfo>();
        try
        {
            dynamic rules = _store!.GetRules();
            int n = (int)rules.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic r = rules[i];
                list.Add(new RuleInfo
                {
                    Id = TryStr(() => (string)r.Name),
                    Name = TryStr(() => (string)r.Name),
                    IsEnabled = TryBool(() => (bool)r.Enabled, true),
                    Priority = i,
                    HasUnsupportedParts = true,
                });
            }
        }
        catch { }
        return (IReadOnlyList<RuleInfo>)list;
    });

    private static bool TryBool(Func<bool> f, bool fallback) { try { return f(); } catch { return fallback; } }

    public Task SaveRulesAsync(IReadOnlyList<RuleInfo> rules, IReadOnlyList<string> deletedRuleIds, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        try
        {
            dynamic ruleSet = _store!.GetRules();
            bool changed = false;
            // Enable/disable existing rules by name; brand-new rules are not supported here.
            foreach (var r in rules)
            {
                if (string.IsNullOrEmpty(r.Id)) continue;
                for (int i = 1; i <= (int)ruleSet.Count; i++)
                {
                    dynamic rule = ruleSet[i];
                    if ((string)rule.Name == r.Id) { try { rule.Enabled = r.IsEnabled; changed = true; } catch { } break; }
                }
            }
            foreach (var id in deletedRuleIds)
            {
                for (int i = 1; i <= (int)ruleSet.Count; i++)
                {
                    dynamic rule = ruleSet[i];
                    if ((string)rule.Name == id) { try { ruleSet.Remove(i); changed = true; } catch { } break; }
                }
            }
            if (changed) ruleSet.Save(true);
            if (rules.Any(r => string.IsNullOrEmpty(r.Id)))
                throw new MailServiceException("ახალი წესის შექმნა Outlook-ის რეჟიმში მიუწვდომელია. ჩართვა/გამორთვა და წაშლა მუშაობს.");
        }
        catch (MailServiceException) { throw; }
        catch (Exception ex) { throw new MailServiceException("წესების შენახვა ვერ მოხერხდა: " + ex.Message, ex); }
    });

    public Task<IReadOnlyList<PermissionEntry>> GetFolderPermissionsAsync(string folderId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PermissionEntry>>(Array.Empty<PermissionEntry>());

    public Task SetFolderPermissionsAsync(string folderId, IEnumerable<PermissionEntry> permissions, CancellationToken ct = default) =>
        throw new MailServiceException("საქაღალდის ნებართვების მართვა Outlook-ის რეჟიმში მიუწვდომელია. გამოიყენეთ ვებფოსტა (OWA).");

    private static string HtmlToPlain(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<br\\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "</p>", "\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
}
