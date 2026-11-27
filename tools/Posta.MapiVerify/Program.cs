using Posta.Core.Models;
using Posta.Core.Services;
using Posta.Mapi;
using System.Text.Json;

// Read-only by default. Roundtrip switches create isolated verification objects and clean them up.
// Only explicit self-delivery modes submit mail, and only to the configured account itself.
// Credentials are retrieved in memory from the app's existing vault entry.
// No password arguments, message subjects, recipient addresses or bodies are printed or persisted.
var settings = new SettingsService(); settings.Load();
var email = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
var account = settings.Accounts.Single(a => a.UseNativeMapi && (email == null || a.Email.Equals(email, StringComparison.OrdinalIgnoreCase)));
await using var service = new NativeMapiMailService(account);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
try
{
    await service.ConnectAsync(CredentialStore.Load(account.Id), timeout.Token);
    if (args.Contains("--meeting-response-roundtrip"))
    {
        const string selfAliasOption = "--meeting-response-to-self-alias=";
        var selfAlias = args.FirstOrDefault(a => a.StartsWith(selfAliasOption, StringComparison.Ordinal))?[selfAliasOption.Length..];
        await MeetingResponseFixture.VerifyAsync(service, account, selfAlias, timeout.Token);
        return;
    }
    if (args.Contains("--meeting-self-delivery"))
    {
        var calendar = (await service.GetOwnCalendarsAsync(timeout.Token)).Single(c => c.IsDefault);
        var sentFolder = await service.GetWellKnownFolderAsync(FolderKind.SentItems, ct: timeout.Token)
            ?? throw new InvalidOperationException("Sent Items unavailable.");
        var inboxFolder = await service.GetWellKnownFolderAsync(FolderKind.Inbox, ct: timeout.Token)
            ?? throw new InvalidOperationException("Inbox unavailable.");
        var marker = "Posta meeting test " + Guid.NewGuid().ToString("N");
        var start = DateTime.Today.AddDays(5).AddHours(12);
        var meeting = new CalendarEvent { IsMeeting = true, Subject = marker, Location = "ქართული ოთახი",
            Start = start, End = start.AddMinutes(25), BodyHtml = "<p>საცდელი შეხვედრა</p>", ResponseRequested = false,
            ReminderSet = false, Attendees = [new Attendee { Email = account.Email, Name = "ქართული სახელი", Type = AttendeeType.Required }] };
        var recurring = args.Contains("--meeting-recurring");
        if (recurring)
            meeting.Recurrence = new RecurrenceInfo { Type = RecurrenceType.Daily, StartDate = start.Date,
                EndType = RecurrenceEnd.AfterOccurrences, Occurrences = 3 };
        var testMail = new List<string>();
        try
        {
            await service.SaveEventAsync(calendar, meeting, true, EditScope.Single, timeout.Token);
            var organizerCopy = await service.GetEventAsync(meeting.Id, timeout.Token);
            if (!organizerCopy.IsMeeting || !organizerCopy.IsOrganizer || organizerCopy.MeetingSubmissionNeedsReview ||
                organizerCopy.Start != start || organizerCopy.Location != meeting.Location)
                throw new InvalidOperationException("The organizer calendar item did not roundtrip.");
            if (recurring && (await service.GetEventsAsync(calendar, start.Date, start.Date.AddDays(3), timeout.Token))
                    .Count(e => e.SeriesMasterId == meeting.Id) != 3)
                throw new InvalidOperationException("The meeting series occurrences did not roundtrip.");

            async Task<MailItemSummary?> FindRequest(MailFolderInfo folder, string subject, string itemClass)
            {
                return (await service.GetItemsAsync(new MailQuery { FolderId = folder.Id, Search = marker, PageSize = 10 }, timeout.Token))
                    .Items.FirstOrDefault(m => m.Subject == subject && m.ItemClass == itemClass);
            }

            async Task<MailItemSummary> WaitForSent(string subject, string itemClass)
            {
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    var result = await FindRequest(sentFolder, subject, itemClass);
                    if (result != null) { testMail.Add(result.Id); return result; }
                    await Task.Delay(1000, timeout.Token);
                }
                throw new InvalidOperationException("An accepted meeting submission did not appear in Sent Items.");
            }
            const string requestClass = "IPM.Schedule.Meeting.Request";
            var sentRequest = await WaitForSent(marker, requestClass);
            var detail = await service.GetMessageAsync(sentRequest.Id, timeout.Token);
            if (!detail.BodyHtml.Contains("საცდელი შეხვედრა", StringComparison.Ordinal) ||
                !detail.To.Any(r => r.Address.Equals(account.Email, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The submitted invitation did not preserve the Georgian body or its destination.");
            var received = await FindRequest(inboxFolder, marker, requestClass);
            if (received != null) testMail.Add(received.Id);
            Console.WriteLine(received != null ? "MEETING_CALENDAR_SENT_AND_SELF_DELIVERY_OK" : "MEETING_CALENDAR_AND_SENT_COPY_OK_SELF_DELIVERY_NOT_OBSERVED");
            if (!args.Contains("--meeting-lifecycle")) return;

            organizerCopy.Subject = marker + " updated";
            organizerCopy.Start = start.AddMinutes(10);
            organizerCopy.End = start.AddMinutes(35);
            await service.SaveEventAsync(calendar, organizerCopy, true, recurring ? EditScope.Series : EditScope.Single, timeout.Token);
            var checkedUpdate = await service.GetEventAsync(meeting.Id, timeout.Token);
            if (checkedUpdate.Start != organizerCopy.Start || checkedUpdate.MeetingSubmissionNeedsReview)
                throw new InvalidOperationException("The meeting update did not persist on the same organizer item.");
            await WaitForSent(organizerCopy.Subject, requestClass);
            Console.WriteLine("MEETING_UPDATE_CALENDAR_AND_SENT_COPY_OK");

            await service.DeleteEventAsync(meeting.Id, true, "<p>Test cancellation</p>", recurring ? EditScope.Series : EditScope.Single, timeout.Token);
            meeting.Id = "";
            await WaitForSent("Canceled: " + organizerCopy.Subject, "IPM.Schedule.Meeting.Canceled");
            if ((await service.GetEventsAsync(calendar, start.Date, start.Date.AddDays(3), timeout.Token)).Any(e => e.Subject == organizerCopy.Subject))
                throw new InvalidOperationException("The canceled meeting remained in the calendar.");
            Console.WriteLine("MEETING_CANCELLATION_CALENDAR_AND_SENT_COPY_OK");
        }
        finally
        {
            if (service.State == ConnectionState.Connected)
            {
                if (!string.IsNullOrEmpty(meeting.Id)) testMail.Add(meeting.Id);
                if (testMail.Count > 0) await service.DeleteItemsAsync(testMail, false, timeout.Token);
                Console.WriteLine("VERIFICATION_ITEMS_MOVED_TO_DELETED_ITEMS");
            }
        }
        return;
    }
    if (args.Contains("--automatic-replies"))
    {
        var replies = await service.GetOofAsync(timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "automatic_replies_read", state = replies.State.ToString() }));
        return;
    }
    if (args.Contains("--group-expansion"))
    {
        var group = (await service.ResolveNamesAsync("", true, timeout.Token))
            .FirstOrDefault(p => p.Kind == RecipientKind.DistributionList && p.Email.Contains('@'))
            ?? throw new InvalidOperationException("No distribution group was visible in the address book.");
        var members = await service.ExpandGroupAsync(group.Email, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "group_expansion", members = members.Count }));
        return;
    }
    if (args.Contains("--shared-mail-review"))
    {
        var owner = account.AdditionalMailboxes.FirstOrDefault() ?? account.SharedCalendars.FirstOrDefault()?.OwnerEmail
            ?? throw new InvalidOperationException("No known delegated mailbox is configured.");
        try
        {
            var delegatedFolders = await service.GetFolderTreeAsync(owner, timeout.Token);
            var folder = delegatedFolders.FirstOrDefault(f => f.Kind == FolderKind.Inbox) ?? delegatedFolders.First();
            var page = await service.GetItemsAsync(new MailQuery { FolderId = folder.Id, PageSize = 1 }, timeout.Token);
            var item = page.Items.FirstOrDefault();
            if (item != null)
            {
                var read = await service.GetMessageAsync(item.Id, timeout.Token);
                if (!item.IsReadOnly || read.FolderId != folder.Id || !item.Id.StartsWith("mapi-mail:", StringComparison.Ordinal))
                    throw new InvalidOperationException("The shared item was not isolated from the primary mailbox.");
            }
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "shared_mail", folders = delegatedFolders.Count, itemChecked = item != null }));
        }
        catch (MailServiceException ex) when (ex.Message.Contains("did not grant access", StringComparison.Ordinal) ||
            ex.Message.Contains("0x80070005", StringComparison.Ordinal) || ex.Message.Contains("0x8004010F", StringComparison.Ordinal))
        {
            Console.WriteLine("SHARED_MAIL_ACCESS_DENIED_BY_EXCHANGE");
        }
        var primary = await service.GetWellKnownFolderAsync(FolderKind.Inbox, ct: timeout.Token)
            ?? throw new InvalidOperationException("Primary Inbox was unavailable after opening a shared mailbox.");
        await service.GetItemsAsync(new MailQuery { FolderId = primary.Id, PageSize = 1 }, timeout.Token);
        Console.WriteLine("PRIMARY_MAILBOX_STILL_CONNECTED");
        return;
    }
    if (args.Contains("--self-delivery"))
    {
        var marker = "Posta self-test " + Guid.NewGuid().ToString("N");
        await service.SendAsync(new OutgoingMessage
        {
            AccountId = account.Id,
            To = [new Recipient("Posta verification", account.Email)],
            Subject = marker,
            BodyHtml = "<p>ავტომატური სატესტო წერილი Posta-დან</p>",
            SaveSentCopy = true
        }, timeout.Token);
        var deliveryInbox = await service.GetWellKnownFolderAsync(FolderKind.Inbox, ct: timeout.Token)
            ?? throw new InvalidOperationException("Inbox unavailable.");
        var sent = await service.GetWellKnownFolderAsync(FolderKind.SentItems, ct: timeout.Token)
            ?? throw new InvalidOperationException("Sent Items unavailable.");
        async Task<MailItemSummary?> Find(MailFolderInfo folder)
        {
            var results = await service.GetItemsAsync(new MailQuery { FolderId = folder.Id, Search = marker }, timeout.Token);
            return results.Items.SingleOrDefault(item => item.Subject == marker);
        }
        for (int attempt = 0; attempt < 15; attempt++)
        {
            var received = await Find(deliveryInbox);
            var copy = await Find(sent);
            if (received != null && copy != null)
            {
                var detail = await service.GetMessageAsync(received.Id, timeout.Token);
                if (!detail.BodyHtml.Contains("ავტომატური სატესტო წერილი", StringComparison.Ordinal))
                    throw new InvalidOperationException("The delivered Georgian body did not roundtrip.");
                Console.WriteLine("SELF_DELIVERY_AND_SENT_COPY_OK");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        }
        throw new InvalidOperationException("Exchange accepted the submission, but Inbox delivery and Sent Items were not both observed.");
    }
    if (args.Contains("--mail-paging"))
    {
        var pagingFolder = await service.GetWellKnownFolderAsync(FolderKind.Inbox, ct: timeout.Token)
            ?? throw new InvalidOperationException("Exchange did not return the inbox.");
        foreach (var sort in Enum.GetValues<SortField>())
        {
            var whole = await service.GetItemsAsync(new MailQuery { FolderId = pagingFolder.Id, Sort = sort, PageSize = 8 }, timeout.Token);
            var firstPage = await service.GetItemsAsync(new MailQuery { FolderId = pagingFolder.Id, Sort = sort, PageSize = 4 }, timeout.Token);
            var second = await service.GetItemsAsync(new MailQuery { FolderId = pagingFolder.Id, Sort = sort, Offset = 4, PageSize = 4 }, timeout.Token);
            if (whole.Items.Count < 8 || whole.TotalCount != firstPage.TotalCount || firstPage.TotalCount != second.TotalCount ||
                !whole.Items.Select(m => m.Id).SequenceEqual(firstPage.Items.Concat(second.Items).Select(m => m.Id)))
                throw new InvalidOperationException($"Exchange paging differs from a single page when sorting by {sort}.");
            Console.WriteLine($"PAGING_{sort}_OK");
        }
        foreach (var filter in Enum.GetValues<MailFilter>().Where(f => f is not (MailFilter.All or MailFilter.Mentioned)))
        {
            var result = await service.GetItemsAsync(new MailQuery { FolderId = pagingFolder.Id, Filter = filter, PageSize = 10 }, timeout.Token);
            if (result.Items.Any(m => filter switch
                {
                    MailFilter.Unread => m.IsRead,
                    MailFilter.Flagged => m.Flag != FlagState.Flagged,
                    MailFilter.HasAttachments => !m.HasAttachments,
                    MailFilter.Important => m.Importance != Importance.High,
                    MailFilter.ThisWeek => m.Received.Date < DateTime.Today.AddDays(-((int)DateTime.Today.DayOfWeek + 6) % 7),
                    _ => false
                })) throw new InvalidOperationException($"Exchange returned a mail item that does not match {filter}.");
            Console.WriteLine($"FILTER_{filter}_OK");
        }
        return;
    }
    if (args.Contains("--rule-roundtrip"))
    {
        var marker = "Posta verification " + Guid.NewGuid().ToString("N");
        var baseline = await service.GetRulesAsync(timeout.Token);
        var target = await service.GetWellKnownFolderAsync(FolderKind.DeletedItems, ct: timeout.Token)
            ?? throw new InvalidOperationException("Exchange did not return Deleted Items for the disabled copy/move rule fixture.");
        try
        {
            var rule = new RuleInfo
            {
                Name = marker, IsEnabled = false, SubjectContains = [marker], BodyContains = [marker],
                HasAttachments = true, WithImportance = Importance.Low, MarkAsRead = true,
                MarkImportance = Importance.High, CopyToFolderId = target.Id, MoveToFolderId = target.Id
            };
            await service.SaveRulesAsync([.. baseline, rule], [], timeout.Token);
            var after = await service.GetRulesAsync(timeout.Token);
            if (after.Count != baseline.Count + 1 || after.Single(r => r.Name == marker).IsEnabled)
                throw new InvalidOperationException("The isolated disabled rule did not roundtrip.");
            var createdRule = after.Single(r => r.Name == marker);
            createdRule.IsEnabled = true;
            await service.SaveRulesAsync(after, [], timeout.Token);
            var updated = (await service.GetRulesAsync(timeout.Token)).Single(r => r.Id == createdRule.Id);
            if (!updated.IsEnabled || updated.Name != createdRule.Name)
                throw new InvalidOperationException("The isolated rule could not be enabled.");
            Console.WriteLine("TEST_RULE_CREATED_AND_ENABLED");
        }
        finally
        {
            var all = await service.GetRulesAsync(timeout.Token);
            var created = all.SingleOrDefault(r => r.Name == marker || r.Name == marker + " renamed");
            if (created?.Id is { } id)
            {
                await service.SaveRulesAsync(all.Where(r => r.Id != id).ToArray(), [id], timeout.Token);
                if ((await service.GetRulesAsync(timeout.Token)).Any(r => r.Id == id))
                    throw new InvalidOperationException("The verification rule was not removed.");
                Console.WriteLine("TEST_RULE_REMOVED");
            }
        }
        return;
    }
    if (args.FirstOrDefault(a => a.StartsWith("--shared-calendar=", StringComparison.Ordinal)) is { } shared)
    {
        var calendar = await service.OpenSharedCalendarAsync(shared["--shared-calendar=".Length..], timeout.Token);
        var events = await service.GetEventsAsync(calendar, DateTime.Today.AddDays(-30), DateTime.Today.AddDays(30), timeout.Token);
        if (events.Count > 0) await service.GetEventAsync(events[0].Id, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "shared_calendar", events = events.Count, detailsChecked = events.Count > 0 }));
        return;
    }
    if (args.Contains("--saved-shared-calendars"))
    {
        if (account.SharedCalendars.Count == 0) throw new InvalidOperationException("No shared calendar is saved for this account.");
        foreach (var reference in account.SharedCalendars)
        {
            var calendar = await service.OpenSharedCalendarAsync(reference.OwnerEmail, timeout.Token);
            var events = await service.GetEventsAsync(calendar, DateTime.Today.AddDays(-30), DateTime.Today.AddDays(30), timeout.Token);
            var details = events.Count > 0 ? await service.GetEventAsync(events[0].Id, timeout.Token) : null;
            if (details != null && (details.CanEditItem == null || details.CanDeleteItem == null))
                throw new InvalidOperationException("The shared appointment did not expose its item permissions.");
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "saved_shared_calendar", events = events.Count,
                detailsChecked = details != null, canCreate = calendar.CanCreate, canEditSample = details?.CanEditItem, canDeleteSample = details?.CanDeleteItem }));
        }
        return;
    }
    if (args.FirstOrDefault(a => a.StartsWith("--gal-query=", StringComparison.Ordinal)) is { } lookup)
    {
        var query = lookup["--gal-query=".Length..];
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var matches = await service.ResolveNamesAsync(query, true, timeout.Token);
        var nameMatches = matches.Count(row => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "gal_display_names", matches = matches.Count, nameMatches }));
        if (nameMatches == 0) Environment.ExitCode = 1;
        return;
    }
    var folders = await service.GetFolderTreeAsync(ct: timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { stage = "folders", count = folders.Count,
        kinds = folders.GroupBy(f => f.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count()) }));
    var inbox = folders.Single(f => f.Kind == FolderKind.Inbox);
    if (args.Contains("--calendar"))
    {
        var calendars = await service.GetOwnCalendarsAsync(timeout.Token);
        foreach (var calendar in calendars)
        {
            var permissions = await service.GetFolderPermissionsAsync(calendar.FolderId, timeout.Token);
            var events = await service.GetEventsAsync(calendar, DateTime.Today.AddDays(-30), DateTime.Today.AddDays(30), timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "calendar", calendar.IsDefault, events = events.Count,
                recurring = events.Count(e => e.IsOccurrence), permissions = permissions.Count }));
        }
        if (args.Contains("--appointment-roundtrip"))
        {
            var calendar = calendars.Single(c => c.IsDefault);
            var start = DateTime.Today.AddDays(3).AddHours(13);
            var appointment = new CalendarEvent
            {
                Subject = "Posta verification " + Guid.NewGuid().ToString("N"),
                Start = start, End = start.AddHours(1), Location = "საცდელი", BodyHtml = "<p>ქართული</p>",
                Recurrence = new RecurrenceInfo { Type = RecurrenceType.Daily, StartDate = start.Date,
                    EndType = RecurrenceEnd.AfterOccurrences, Occurrences = 3 }
            };
            string? id = null;
            try
            {
                id = await service.SaveEventAsync(calendar, appointment, false, EditScope.Series, timeout.Token);
                var master = await service.GetEventAsync(id, timeout.Token);
                if (master.Location != appointment.Location || !master.BodyHtml.Contains("ქართული")) throw new Exception("Appointment metadata did not roundtrip.");
                var instances = (await service.GetEventsAsync(calendar, start.Date, start.Date.AddDays(4), timeout.Token))
                    .Where(e => e.SeriesMasterId == id).OrderBy(e => e.Start).ToArray();
                if (instances.Length != 3 || instances[0].Start != start) throw new Exception("Recurring instances did not roundtrip.");
                var busy = (await service.GetAvailabilityAsync([account.Email], start.AddMinutes(5), start.AddMinutes(25), timeout.Token)).Single();
                if (busy.HasError || !busy.Slots.Any(s => s.Status == ShowAs.Busy &&
                    s.Start == start.AddMinutes(5) && s.End == start.AddMinutes(25)))
                    throw new Exception("The appointment was not reflected in the requested busy interval.");
                await service.DeleteEventAsync(instances[1].Id, false, null, EditScope.Single, timeout.Token);
                var remaining = (await service.GetEventsAsync(calendar, start.Date, start.Date.AddDays(4), timeout.Token))
                    .Where(e => e.SeriesMasterId == id).Select(e => e.Id).ToArray();
                if (remaining.Length != 2 || remaining.Contains(instances[1].Id)) throw new Exception("Deleted instance remained visible.");
                Console.WriteLine("APPOINTMENT_RECURRENCE_AND_SINGLE_DELETION_OK");
            }
            finally
            {
                if (id != null) await service.DeleteEventAsync(id, false, null, EditScope.Series, timeout.Token);
            }
        }
        var reminders = await service.GetUpcomingRemindersAsync(DateTime.Now, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "reminders", count = reminders.Count }));
        var now = DateTime.Now;
        var availability = (await service.GetAvailabilityAsync([account.Email], now.AddDays(-30), now.AddDays(30), timeout.Token)).Single();
        if (availability.HasError || availability.Slots.Any(slot => slot.Start < now.AddDays(-30) || slot.End > now.AddDays(30)))
            throw new InvalidOperationException("Personal availability did not roundtrip.");
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "calendar_availability", busySlots = availability.Slots.Count }));
        if (args.Contains("--permissions-roundtrip"))
        {
            var root = folders.Single(f => f.Kind == FolderKind.Root);
            var testFolder = await service.CreateFolderAsync(root.Id, "Posta verification " + Guid.NewGuid().ToString("N"), "IPF.Note", timeout.Token);
            try
            {
                var permissions = (await service.GetFolderPermissionsAsync(testFolder, timeout.Token)).ToList();
                var original = permissions.Single(p => p.StandardUser == "Default");
                var visible = original.IsFolderVisible;
                original.IsFolderVisible = !visible;
                await service.SetFolderPermissionsAsync(testFolder, permissions, timeout.Token);
                var refreshed = (await service.GetFolderPermissionsAsync(testFolder, timeout.Token)).ToList();
                var updated = refreshed.Single(p => p.StandardUser == "Default");
                if (updated.IsFolderVisible == visible) throw new Exception("The permission change did not persist.");
                updated.IsFolderVisible = visible;
                await service.SetFolderPermissionsAsync(testFolder, refreshed, timeout.Token);
                Console.WriteLine("PERMISSION_UPDATE_AND_RESTORE_OK");
            }
            finally
            {
                await service.DeleteFolderAsync(testFolder, false, timeout.Token);
            }
        }
        var addressBook = await service.ResolveNamesAsync(account.Email, true, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "gal", exact = addressBook.Any(p =>
            p.Email.Equals(account.Email, StringComparison.OrdinalIgnoreCase)) }));
        var rules = await service.GetRulesAsync(timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "standard_inbox_rules", count = rules.Count }));
        return;
    }
    var messages = await service.GetItemsAsync(new() { FolderId = inbox.Id, PageSize = 5 }, timeout.Token);
    foreach (var summary in messages.Items)
    {
        var item = await service.GetMessageAsync(summary.Id, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "body", characters = item.BodyHtml.Length,
            recipientsComplete = item.RecipientsComplete, recipients = item.To.Count + item.Cc.Count + item.Bcc.Count,
            attachments = item.Attachments.Count, inlineImages = item.Attachments.Count(a => a.IsInline && a.Content?.Length > 0) }));
        var attachment = item.Attachments.FirstOrDefault(a => !a.IsInline && !a.IsItemAttachment);
        if (attachment != null)
        {
            var bytes = await service.GetAttachmentContentAsync(item.Id, attachment.Id, timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "attachment", bytes = bytes.Length, expectedBytes = attachment.Size }));
        }
    }
    Console.WriteLine("READ_ONLY_VERIFICATION_OK");
    if (messages.Items.FirstOrDefault() is { Subject.Length: > 0 } first)
    {
        var search = await service.GetItemsAsync(new() { FolderId = inbox.Id, Search = first.Subject, PageSize = 100 }, timeout.Token);
        if (!search.Items.Any(m => m.Id == first.Id)) throw new Exception("Server-side search did not find its known subject.");
        var absent = await service.GetItemsAsync(new() { FolderId = inbox.Id, Search = Guid.NewGuid().ToString("N"), PageSize = 100 }, timeout.Token);
        if (absent.TotalCount != 0) throw new Exception("Absent search term returned unexpected matches.");
        Console.WriteLine("SERVER_SIDE_SEARCH_POSITIVE_NEGATIVE_OK");
    }
    foreach (var folder in folders.Where(f => f.Kind == FolderKind.Contacts))
    {
        var contacts = await service.GetContactsAsync(folder.Id, null, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "contacts", count = contacts.Count }));
    }
    foreach (var folder in folders.Where(f => f.Kind == FolderKind.Tasks))
    {
        var tasks = await service.GetTasksAsync(folder.Id, true, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "tasks", count = tasks.Count }));
    }
    var allTasks = await service.GetTasksAsync("", true, timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { stage = "aggregate_tasks", count = allTasks.Count, flagged = allTasks.Count(t => t.IsFlaggedMail) }));
    if (args.Contains("--pim-roundtrip"))
    {
        var marker = "Posta verification " + Guid.NewGuid().ToString("N");
        string? contactId = null, taskId = null;
        try
        {
            var contactFolder = (await service.GetWellKnownFolderAsync(FolderKind.Contacts, ct: timeout.Token))!;
            var contact = new ContactInfo { DisplayName = marker, GivenName = "Posta", Surname = "Verification", FileAs = marker,
                Email1 = "posta-verification@example.test", Email1Name = marker, Notes = "ქართული საცდელი კონტაქტი" };
            contactId = await service.SaveContactAsync(contactFolder.Id, contact, timeout.Token);
            var loadedContact = await service.GetContactAsync(contactId, timeout.Token);
            if (loadedContact.Email1 != contact.Email1 || loadedContact.DisplayName != marker || loadedContact.Notes != contact.Notes)
                throw new Exception($"Contact fields did not roundtrip (email={loadedContact.Email1 == contact.Email1}, name={loadedContact.DisplayName == marker}, notes={loadedContact.Notes == contact.Notes}, notesLength={loadedContact.Notes.Length}).");
            Console.WriteLine("CONTACT_ROUNDTRIP_OK");
            var taskFolder = (await service.GetWellKnownFolderAsync(FolderKind.Tasks, ct: timeout.Token))!;
            var task = new TaskInfo { Subject = marker, BodyHtml = "<p>ქართული საცდელი ამოცანა</p>", StartDate = DateTime.Today,
                DueDate = DateTime.Today.AddDays(3), PercentComplete = 55, Status = TaskState.InProgress, Importance = Importance.High };
            taskId = await service.SaveTaskAsync(taskFolder.Id, task, timeout.Token);
            var loadedTask = await service.GetTaskAsync(taskId, timeout.Token);
            if (loadedTask.Subject != marker || loadedTask.PercentComplete != 55 || loadedTask.DueDate?.Date != task.DueDate?.Date)
                throw new Exception("Task fields did not roundtrip.");
            loadedTask.StartDate = null; loadedTask.DueDate = null;
            await service.SaveTaskAsync(taskFolder.Id, loadedTask, timeout.Token);
            var updated = await service.GetTaskAsync(taskId, timeout.Token);
            if (updated.StartDate != null || updated.DueDate != null) throw new Exception("Task dates were not cleared.");
            Console.WriteLine("TASK_CREATE_UPDATE_CLEAR_DATES_ROUNDTRIP_OK");
        }
        finally
        {
            if (contactId != null) { await service.DeleteContactAsync(contactId, timeout.Token); Console.WriteLine("VERIFICATION_CONTACT_MOVED_TO_DELETED_ITEMS"); }
            if (taskId != null) { await service.DeleteTaskAsync(taskId, timeout.Token); Console.WriteLine("VERIFICATION_TASK_MOVED_TO_DELETED_ITEMS"); }
        }
    }
    if (args.Contains("--draft-roundtrip"))
    {
        string? created = null;
        try
        {
            var attachmentBytes = System.Text.Encoding.UTF8.GetBytes("Posta attachment verification — ქართული");
            var message = new OutgoingMessage { AccountId = account.Id, Subject = "Posta verification draft " + Guid.NewGuid().ToString("N"),
                BodyHtml = "<p>Posta verification — ქართული long body to check shortening</p>",
                To = [new Recipient("ქართული სახელი", account.Email)],
                Attachments = [new() { Name = "Posta-verification.txt", ContentType = "text/plain", Content = attachmentBytes }] };
            created = await service.SaveDraftAsync(message, timeout.Token);
            var loaded = await service.LoadDraftAsync(created, timeout.Token);
            if (loaded.Subject != message.Subject || loaded.Attachments.Count != 1) throw new Exception("Draft metadata did not roundtrip.");
            await service.SetCategoriesAsync(created, ["Posta verification"], timeout.Token);
            await service.SetFlagAsync([created], FlagState.Flagged, DateTime.Today, DateTime.Today.AddDays(1), timeout.Token);
            await service.SetImportanceAsync(created, Importance.High, timeout.Token);
            await service.SetReadAsync([created], false, timeout.Token);
            var tagged = await service.GetMessageAsync(created, timeout.Token);
            if (!tagged.Categories.Contains("Posta verification") || tagged.Flag != FlagState.Flagged || tagged.Importance != Importance.High || tagged.IsRead)
                throw new Exception("Categories, flag, importance or unread state did not roundtrip.");
            await service.SetCategoriesAsync(created, [], timeout.Token);
            await service.SetFlagAsync([created], FlagState.NotFlagged, null, null, timeout.Token);
            await service.SetReadAsync([created], true, timeout.Token);
            var cleared = await service.GetMessageAsync(created, timeout.Token);
            if (cleared.Categories.Count != 0 || cleared.Flag != FlagState.NotFlagged || !cleared.IsRead) throw new Exception("Tags or read state were not cleared.");
            Console.WriteLine("MAIL_TAGS_IMPORTANCE_READ_FLAGS_ROUNDTRIP_OK");
            var content = await service.GetAttachmentContentAsync(created, loaded.Attachments[0].Id, timeout.Token);
            if (!content.SequenceEqual(attachmentBytes)) throw new Exception("Draft attachment bytes did not roundtrip.");
            loaded.BodyHtml = "<p>მოკლე</p>";
            created = await service.SaveDraftAsync(loaded, timeout.Token);
            var edited = await service.LoadDraftAsync(created, timeout.Token);
            if (!edited.BodyHtml.Contains("მოკლე") || edited.BodyHtml.Contains("long body") || edited.Attachments.Count != 1)
                throw new Exception("Draft update lost data, duplicated attachments or retained trailing body bytes.");
            Console.WriteLine("DRAFT_ATTACHMENT_UPDATE_ROUNDTRIP_OK_NO_MAIL_SENT");
            using var emlBytes = new MemoryStream(await service.GetMimeContentAsync(created, timeout.Token));
            var eml = await MimeKit.MimeMessage.LoadAsync(emlBytes, timeout.Token);
            using var emlAttachment = new MemoryStream();
            if (eml.Attachments.Single() is not MimeKit.MimePart { Content: { } partContent }) throw new Exception("Expected an EML file attachment.");
            await partContent.DecodeToAsync(emlAttachment, timeout.Token);
            if (eml.Subject != message.Subject || eml.HtmlBody?.Contains("მოკლე") != true ||
                eml.To.Mailboxes.Single().Name != "ქართული სახელი" || !emlAttachment.ToArray().SequenceEqual(attachmentBytes))
                throw new Exception("The EML export lost text, recipient or attachment data.");
            Console.WriteLine("EML_GEORGIAN_AND_ATTACHMENT_ROUNDTRIP_OK");
        }
        finally
        {
            if (created != null)
            {
                await service.DeleteItemsAsync([created], false, timeout.Token);
                Console.WriteLine("VERIFICATION_DRAFT_MOVED_TO_DELETED_ITEMS");
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new { stage = "error", type = ex.GetType().Name, message = ex.Message }));
    Environment.ExitCode = 1;
}
