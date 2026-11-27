using Posta.Core.Models;

namespace Posta.Core.Services;

/// <summary>
/// In-memory mailbox used for the offline demo / UI preview (start the app with --demo).
/// </summary>
public sealed class DemoMailService : IMailService
{
    private readonly List<MailFolderInfo> _folders = new();
    private readonly List<MailMessageDetail> _messages = new();
    private readonly List<CalendarEvent> _events = new();
    private readonly List<ContactInfo> _contacts = new();
    private readonly List<TaskInfo> _tasks = new();
    private readonly Dictionary<string, List<PermissionEntry>> _permissions = new();
    private List<RuleInfo> _rules = new();
    private OofSettingsInfo _oof = new();
    private int _seq;

    public DemoMailService(AccountConfig account)
    {
        Account = account;
        Seed();
    }

    public AccountConfig Account { get; }
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string? LastError => null;
    public string? ServerVersion => "15.2.1544.4";
    public string? UserDisplayName => Account.DisplayName;
    public event EventHandler? StateChanged;

    private string NewId() => $"demo-{Interlocked.Increment(ref _seq)}";

    private MailFolderInfo F(string id, string name, FolderKind kind, string? parent = null, string cls = "IPF.Note")
    {
        var f = new MailFolderInfo { Id = id, DisplayName = name, Kind = kind, ParentId = parent, AccountId = Account.Id, FolderClass = cls };
        _folders.Add(f);
        return f;
    }

    private static readonly (string name, string email)[] People =
    {
        ("ნინო ბერიძე", "nino.beridze@contoso.example"),
        ("გიორგი კაპანაძე", "giorgi.kapanadze@contoso.example"),
        ("Mariam Lomidze", "mariam.lomidze@contoso.example"),
        ("David Smith", "david.smith@fabrikam.example"),
        ("ლევან ჯაფარიძე", "levan.japaridze@contoso.example"),
        ("Ana Tsiklauri", "ana.tsiklauri@contoso.example"),
        ("IT Helpdesk", "helpdesk@contoso.example"),
        ("Sarah Johnson", "sarah.johnson@northwind.example"),
    };

    private void Seed()
    {
        F("inbox", "Inbox", FolderKind.Inbox);
        F("drafts", "Drafts", FolderKind.Drafts);
        F("sent", "Sent Items", FolderKind.SentItems);
        F("deleted", "Deleted Items", FolderKind.DeletedItems);
        F("archive", "Archive", FolderKind.Archive);
        F("conv", "Conversation History", FolderKind.ConversationHistory);
        F("junk", "Junk Email", FolderKind.JunkEmail);
        F("outbox", "Outbox", FolderKind.Outbox);
        F("rss", "RSS Feeds", FolderKind.RssFeeds);
        F("sync", "Sync Issues", FolderKind.SyncIssues);
        F("projects", "პროექტები", FolderKind.Generic, "inbox");
        F("reports", "ანგარიშები", FolderKind.Generic, "inbox");
        F("calendar", "Calendar", FolderKind.Calendar, null, "IPF.Appointment");
        F("contacts", "Contacts", FolderKind.Contacts, null, "IPF.Contact");
        F("tasks", "Tasks", FolderKind.Tasks, null, "IPF.Task");

        var subjects = new[]
        {
            ("კვარტალური ანგარიში - Q3", "გთხოვთ გადახედოთ თანდართულ ანგარიშს და ხვალამდე მომაწოდოთ შენიშვნები. მადლობა!"),
            ("Project kickoff meeting notes", "Hi team, attached are the notes from today's kickoff. Please review the action items assigned to you."),
            ("შვებულების განაცხადი", "გამარჯობა, მინდა დავადასტურო ჩემი შვებულება 12-დან 19 დეკემბრამდე."),
            ("Server maintenance this weekend", "The Exchange servers will be patched on Saturday between 22:00 and 02:00. Expect short interruptions."),
            ("RE: ბიუჯეტის დამტკიცება", "ბიუჯეტი დამტკიცებულია. შეგიძლიათ დაიწყოთ შესყიდვების პროცესი."),
            ("Welcome to the new intranet", "We are happy to announce the launch of our new intranet portal."),
            ("ხელშეკრულების პროექტი", "თანდართულია ხელშეკრულების განახლებული ვერსია. ცვლილებები მონიშნულია ყვითლად."),
            ("Lunch on Friday?", "Are you free for lunch on Friday? There's a new place near the office."),
            ("FW: Invoice #2024-1187", "Please process the attached invoice before the end of the month."),
            ("ტრენინგის განრიგი", "ახალი თანამშრომლების ტრენინგი გაიმართება ორშაბათს, 10:00 საათზე."),
            ("Password expiry notice", "Your password will expire in 7 days. Please change it using Ctrl+Alt+Del."),
            ("Design review feedback", "Overall the design looks great. A few comments on the navigation and color contrast."),
        };
        var now = DateTime.Now;
        for (int i = 0; i < 40; i++)
        {
            var (subj, text) = subjects[i % subjects.Length];
            var p = People[i % People.Length];
            var received = now.AddHours(-i * 7.3 - 0.4);
            var html = $"<p>{HtmlUtil.Encode(text)}</p><p>პატივისცემით,<br>{HtmlUtil.Encode(p.name)}</p>";
            var m = new MailMessageDetail
            {
                Id = NewId(), AccountId = Account.Id, FolderId = i % 9 == 8 ? "projects" : "inbox",
                Subject = subj, From = new Recipient(p.name, p.email), To = { new Recipient(Account.DisplayName, Account.Email) },
                Sent = received.AddMinutes(-1), Received = received, BodyHtml = html, IsRead = i > 4 && i % 5 != 0,
                Importance = i % 7 == 0 ? Importance.High : Importance.Normal,
                Flag = i % 6 == 1 ? FlagState.Flagged : FlagState.NotFlagged,
                Categories = i % 4 == 0 ? new List<string> { "Blue Category" } : new List<string>()
            };
            if (i % 3 == 0) m.Attachments.Add(new AttachmentInfo { Id = NewId(), Name = i % 2 == 0 ? "ანგარიში.xlsx" : "Notes.docx", Size = 48_211 + i * 1000, ContentType = "application/octet-stream" });
            _messages.Add(m);
        }
        // Meeting request
        _messages.Add(new MailMessageDetail
        {
            Id = NewId(), AccountId = Account.Id, FolderId = "inbox", ItemClass = "IPM.Schedule.Meeting.Request",
            Subject = "სტრატეგიული დაგეგმვა 2027", From = new Recipient(People[1].name, People[1].email),
            To = { new Recipient(Account.DisplayName, Account.Email) }, Received = now.AddMinutes(-25), Sent = now.AddMinutes(-26),
            BodyHtml = "<p>გთხოვთ დაესწროთ შეხვედრას.</p>", MeetingStart = DateTime.Today.AddDays(2).AddHours(11),
            MeetingEnd = DateTime.Today.AddDays(2).AddHours(12), MeetingLocation = "საკონფერენციო დარბაზი 3", MyResponse = MeetingResponse.NoResponseReceived
        });
        for (int i = 0; i < 6; i++)
        {
            var p = People[(i + 2) % People.Length];
            _messages.Add(new MailMessageDetail
            {
                Id = NewId(), AccountId = Account.Id, FolderId = "sent", Subject = "RE: " + subjects[i].Item1,
                From = new Recipient(Account.DisplayName, Account.Email), To = { new Recipient(p.name, p.email) },
                Received = now.AddDays(-i - 0.2), Sent = now.AddDays(-i - 0.2), BodyHtml = "<p>მადლობა, მივიღე.</p>", IsRead = true
            });
        }

        // Calendar
        var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        void Ev(int day, double h, double dur, string subj, string loc, ShowAs sa = ShowAs.Busy, bool meeting = false, string cal = "self", bool allDay = false, string? cat = null)
        {
            var e = new CalendarEvent
            {
                Id = NewId(), CalendarKey = cal, Subject = subj, Location = loc, Start = monday.AddDays(day).AddHours(h),
                End = monday.AddDays(day).AddHours(h + dur), ShowAs = sa, IsMeeting = meeting, IsOrganizer = !meeting,
                Organizer = meeting ? new Recipient(People[0].name, People[0].email) : new Recipient(Account.DisplayName, Account.Email),
                IsAllDay = allDay, MyResponse = meeting ? MeetingResponse.Accept : MeetingResponse.Organizer, BodyHtml = "<p></p>"
            };
            if (allDay) { e.Start = monday.AddDays(day); e.End = monday.AddDays(day + 1); }
            if (meeting) e.Attendees.Add(new Attendee { Name = People[2].name, Email = People[2].email, Response = MeetingResponse.Accept });
            if (cat != null) e.Categories.Add(cat);
            _events.Add(e);
        }
        for (int w = -2; w <= 3; w++)
        {
            int o = w * 7;
            Ev(o + 0, 9.5, 0.5, "დღის სტენდაპი", "Teams", meeting: true);
            Ev(o + 1, 10, 1.5, "Project review", "საკონფერენციო 2", meeting: true, cat: "Green Category");
            Ev(o + 1, 13, 1, "ლანჩი კლიენტთან", "რესტორანი „ფუნიკულიორი“", ShowAs.OutOfOffice);
            Ev(o + 2, 9.5, 0.5, "დღის სტენდაპი", "Teams", meeting: true);
            Ev(o + 2, 14, 2, "Budget planning", "Board room", meeting: true, cat: "Orange Category");
            Ev(o + 3, 11, 1, "1:1 – მენეჯერთან", "ოფისი 401", ShowAs.Tentative);
            Ev(o + 3, 11.5, 1, "Vendor call", "Phone", meeting: true);
            Ev(o + 4, 9.5, 0.5, "დღის სტენდაპი", "Teams", meeting: true);
            Ev(o + 4, 16, 1, "კვირის შეჯამება", "", cat: "Blue Category");
        }
        Ev(4, 0, 0, "ეროვნული დღესასწაული", "", ShowAs.Free, allDay: true);
        Ev(2, 8, 1, "Doctor appointment", "Clinic", ShowAs.OutOfOffice, cal: "shared:nino");
        Ev(1, 10, 2, "Client workshop", "Fabrikam HQ", cal: "shared:nino");
        Ev(3, 15, 1.5, "Interview – Frontend dev", "Room 5", cal: "shared:nino");

        // Contacts
        foreach (var (name, email) in People)
        {
            var parts = name.Split(' ');
            _contacts.Add(new ContactInfo
            {
                Id = NewId(), AccountId = Account.Id, FolderId = "contacts", DisplayName = name, GivenName = parts[0], Surname = parts.Length > 1 ? parts[1] : "",
                FileAs = parts.Length > 1 ? $"{parts[1]}, {parts[0]}" : name, Email1 = email, Company = email.Contains("contoso") ? "Contoso Georgia" : "Fabrikam",
                JobTitle = "Specialist", Department = "Operations", BusinessPhone = "+995 32 2 00 00 00", MobilePhone = "+995 555 12 34 56",
                BusinessAddress = new PostalAddress { Street = "რუსთაველის გამზ. 12", City = "თბილისი", Country = "საქართველო", PostalCode = "0108" }
            });
        }
        _contacts.Add(new ContactInfo { Id = NewId(), AccountId = Account.Id, FolderId = "contacts", IsGroup = true, DisplayName = "გუნდი – ოპერაციები", FileAs = "გუნდი – ოპერაციები",
            Members = People.Take(4).Select(p => new Recipient(p.name, p.email)).ToList() });

        // Tasks
        _tasks.Add(new TaskInfo { Id = NewId(), AccountId = Account.Id, FolderId = "tasks", Subject = "ანგარიშის მომზადება", DueDate = DateTime.Today.AddDays(1), Status = TaskState.InProgress, PercentComplete = 50 });
        _tasks.Add(new TaskInfo { Id = NewId(), AccountId = Account.Id, FolderId = "tasks", Subject = "Review pull requests", DueDate = DateTime.Today, Importance = Importance.High });
        _tasks.Add(new TaskInfo { Id = NewId(), AccountId = Account.Id, FolderId = "tasks", Subject = "ხელშეკრულების ხელმოწერა", DueDate = DateTime.Today.AddDays(-1) });
        _tasks.Add(new TaskInfo { Id = NewId(), AccountId = Account.Id, FolderId = "tasks", Subject = "Order new laptops", DueDate = DateTime.Today.AddDays(7), Status = TaskState.Completed, PercentComplete = 100 });

        _permissions["calendar"] = new List<PermissionEntry>
        {
            new() { StandardUser = "Default", Level = PermissionLevel.FreeBusyTimeOnly, ReadItems = ReadAccess.TimeOnly },
            new() { StandardUser = "Anonymous", Level = PermissionLevel.None },
            new() { Name = People[0].name, Email = People[0].email, Level = PermissionLevel.Reviewer, ReadItems = ReadAccess.FullDetails, IsFolderVisible = true },
        };
        _rules.Add(new RuleInfo { Id = NewId(), Name = "Helpdesk → IT", FromAddresses = { "helpdesk@contoso.example" }, MoveToFolderId = "reports", MoveToFolderName = "ანგარიშები" });
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        foreach (var f in _folders)
        {
            var items = _messages.Where(m => m.FolderId == f.Id).ToList();
            f.TotalCount = items.Count;
            f.UnreadCount = items.Count(m => !m.IsRead);
            f.ChildFolderCount = _folders.Count(c => c.ParentId == f.Id);
        }
    }

    private static Task Done => Task.CompletedTask;
    private static Task Delay() => Task.Delay(60);

    public async Task ConnectAsync(string? password, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting; StateChanged?.Invoke(this, EventArgs.Empty);
        await Task.Delay(200, ct);
        State = ConnectionState.Connected; StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<MailFolderInfo>> GetFolderTreeAsync(string? mailboxEmail = null, CancellationToken ct = default)
    {
        await Delay();
        UpdateCounts();
        return _folders.Select(f => new MailFolderInfo
        {
            Id = f.Id, DisplayName = f.DisplayName, Kind = f.Kind, ParentId = f.ParentId, AccountId = f.AccountId, FolderClass = f.FolderClass,
            UnreadCount = f.UnreadCount, TotalCount = f.TotalCount, ChildFolderCount = f.ChildFolderCount, MailboxEmail = mailboxEmail
        }).ToList();
    }

    public Task<MailFolderInfo?> GetWellKnownFolderAsync(FolderKind kind, string? mailboxEmail = null, CancellationToken ct = default) =>
        Task.FromResult(_folders.FirstOrDefault(f => f.Kind == kind));

    public Task<(int unread, int total)> GetFolderCountsAsync(string folderId, CancellationToken ct = default)
    {
        UpdateCounts();
        var f = _folders.FirstOrDefault(x => x.Id == folderId);
        return Task.FromResult(f == null ? (0, 0) : (f.UnreadCount, f.TotalCount));
    }

    public Task<string> CreateFolderAsync(string parentFolderId, string name, string folderClass, CancellationToken ct = default)
    {
        var f = F(NewId(), name, FolderKind.Generic, parentFolderId == "root" ? null : parentFolderId, folderClass);
        return Task.FromResult(f.Id);
    }

    public Task RenameFolderAsync(string folderId, string newName, CancellationToken ct = default)
    {
        var f = _folders.First(x => x.Id == folderId); f.DisplayName = newName; return Done;
    }

    public Task DeleteFolderAsync(string folderId, bool permanent, CancellationToken ct = default)
    {
        var f = _folders.First(x => x.Id == folderId);
        if (permanent) _folders.Remove(f); else f.ParentId = "deleted";
        return Done;
    }

    public Task MoveFolderAsync(string folderId, string newParentId, CancellationToken ct = default)
    {
        _folders.First(x => x.Id == folderId).ParentId = newParentId == "root" ? null : newParentId; return Done;
    }

    public Task CopyFolderAsync(string folderId, string newParentId, CancellationToken ct = default)
    {
        var f = _folders.First(x => x.Id == folderId);
        F(NewId(), f.DisplayName, FolderKind.Generic, newParentId, f.FolderClass); return Done;
    }

    public Task MarkAllReadAsync(string folderId, CancellationToken ct = default)
    {
        foreach (var m in _messages.Where(m => m.FolderId == folderId)) m.IsRead = true; return Done;
    }

    public Task EmptyFolderAsync(string folderId, bool deleteSubFolders, CancellationToken ct = default)
    {
        _messages.RemoveAll(m => m.FolderId == folderId);
        if (deleteSubFolders) _folders.RemoveAll(f => f.ParentId == folderId);
        return Done;
    }

    private MailItemSummary ToSummary(MailMessageDetail m) => new()
    {
        Id = m.Id, AccountId = m.AccountId, FolderId = m.FolderId, ItemClass = m.ItemClass, From = m.From,
        DisplayTo = HtmlUtil.RecipientsLine(m.To), Received = m.Received, Sent = m.Sent, Size = m.BodyHtml.Length * 2 + m.Attachments.Sum(a => (int)a.Size),
        HasAttachments = m.Attachments.Any(a => !a.IsInline), Subject = m.Subject, Preview = HtmlUtil.Preview(m.BodyHtml), IsRead = m.IsRead,
        Importance = m.Importance, Flag = m.Flag, Categories = new List<string>(m.Categories), IsDraft = m.IsDraft, ConversationId = m.Subject.Replace("RE: ", "")
    };

    public async Task<Paged<MailItemSummary>> GetItemsAsync(MailQuery q, CancellationToken ct = default)
    {
        await Delay();
        IEnumerable<MailMessageDetail> items = _messages.Where(m => m.FolderId == q.FolderId);
        items = q.Filter switch
        {
            MailFilter.Unread => items.Where(m => !m.IsRead),
            MailFilter.Flagged => items.Where(m => m.Flag == FlagState.Flagged),
            MailFilter.HasAttachments => items.Where(m => m.Attachments.Any()),
            MailFilter.Important => items.Where(m => m.Importance == Importance.High),
            _ => items
        };
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            items = items.Where(m => m.Subject.Contains(s, StringComparison.CurrentCultureIgnoreCase) || m.From.Display.Contains(s, StringComparison.CurrentCultureIgnoreCase) ||
                                     m.BodyHtml.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        }
        items = q.Sort switch
        {
            // Secondary sort by newest first inside each group, like EWS/Outlook.
            SortField.From => (q.Descending ? items.OrderByDescending(m => m.From.Display) : items.OrderBy(m => m.From.Display)).ThenByDescending(m => m.Received),
            SortField.Subject => (q.Descending ? items.OrderByDescending(m => m.Subject) : items.OrderBy(m => m.Subject)).ThenByDescending(m => m.Received),
            SortField.Size => (q.Descending ? items.OrderByDescending(m => m.BodyHtml.Length) : items.OrderBy(m => m.BodyHtml.Length)).ThenByDescending(m => m.Received),
            SortField.To => Sec(m => m.To.FirstOrDefault()?.Display ?? ""),
            SortField.Importance => Sec(m => (int)m.Importance),
            SortField.Attachments => Sec(m => m.Attachments.Count > 0),
            SortField.Categories => Sec(m => m.Categories.FirstOrDefault() ?? ""),
            SortField.Flag => Sec(m => (int)m.Flag),
            _ => q.Descending ? items.OrderByDescending(m => m.Received) : items.OrderBy(m => m.Received)
        };
        IEnumerable<MailMessageDetail> Sec<TKey>(Func<MailMessageDetail, TKey> key) =>
            (q.Descending ? items.OrderByDescending(key) : items.OrderBy(key)).ThenByDescending(m => m.Received);
        var list = items.ToList();
        var page = list.Skip(q.Offset).Take(q.PageSize).Select(ToSummary).ToList();
        return new Paged<MailItemSummary> { Items = page, TotalCount = list.Count, MoreAvailable = q.Offset + page.Count < list.Count };
    }

    public async Task<MailMessageDetail> GetMessageAsync(string itemId, CancellationToken ct = default)
    {
        await Delay();
        return _messages.First(m => m.Id == itemId);
    }

    public Task<byte[]> GetAttachmentContentAsync(string itemId, string attachmentId, CancellationToken ct = default) =>
        Task.FromResult(System.Text.Encoding.UTF8.GetBytes("Demo attachment content"));

    public Task<byte[]> GetMimeContentAsync(string itemId, CancellationToken ct = default)
    {
        var m = _messages.First(x => x.Id == itemId);
        var mime = $"From: {m.From.FullDisplay}\r\nTo: {HtmlUtil.RecipientsLineFull(m.To)}\r\nSubject: {m.Subject}\r\nDate: {m.Sent:R}\r\nMIME-Version: 1.0\r\nContent-Type: text/html; charset=utf-8\r\n\r\n{m.BodyHtml}";
        return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(mime));
    }

    public Task SetReadAsync(IEnumerable<string> itemIds, bool isRead, CancellationToken ct = default)
    {
        foreach (var m in _messages.Where(m => itemIds.Contains(m.Id))) m.IsRead = isRead; return Done;
    }

    public Task SetFlagAsync(IEnumerable<string> itemIds, FlagState state, DateTime? start, DateTime? due, CancellationToken ct = default)
    {
        foreach (var m in _messages.Where(m => itemIds.Contains(m.Id))) m.Flag = state; return Done;
    }

    public Task SetCategoriesAsync(string itemId, IEnumerable<string> categories, CancellationToken ct = default)
    {
        var m = _messages.FirstOrDefault(x => x.Id == itemId);
        if (m != null) m.Categories = categories.ToList();
        var e = _events.FirstOrDefault(x => x.Id == itemId);
        if (e != null) e.Categories = categories.ToList();
        return Done;
    }

    public Task SetImportanceAsync(string itemId, Importance importance, CancellationToken ct = default)
    {
        var m = _messages.FirstOrDefault(x => x.Id == itemId); if (m != null) m.Importance = importance; return Done;
    }

    public Task DeleteItemsAsync(IEnumerable<string> itemIds, bool permanent, CancellationToken ct = default)
    {
        foreach (var m in _messages.Where(m => itemIds.Contains(m.Id)).ToList())
        {
            if (permanent || m.FolderId == "deleted") _messages.Remove(m); else m.FolderId = "deleted";
        }
        return Done;
    }

    public Task MoveItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default)
    {
        foreach (var m in _messages.Where(m => itemIds.Contains(m.Id))) m.FolderId = destinationFolderId; return Done;
    }

    public Task CopyItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default)
    {
        foreach (var m in _messages.Where(m => itemIds.Contains(m.Id)).ToList())
        {
            var c = SettingsService.Deserialize<MailMessageDetail>(SettingsService.Serialize(m))!;
            c.Id = NewId(); c.FolderId = destinationFolderId; _messages.Add(c);
        }
        return Done;
    }

    public Task MarkAsJunkAsync(IEnumerable<string> itemIds, bool isJunk, CancellationToken ct = default) =>
        MoveItemsAsync(itemIds, isJunk ? "junk" : "inbox", ct);

    public Task IgnoreConversationAsync(string itemId, CancellationToken ct = default) => DeleteItemsAsync(new[] { itemId }, false, ct);

    public async Task SendAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        await Task.Delay(300, ct);
        if (message.DraftId != null) _messages.RemoveAll(m => m.Id == message.DraftId);
        _messages.Add(new MailMessageDetail
        {
            Id = NewId(), AccountId = Account.Id, FolderId = "sent", Subject = message.Subject, From = new Recipient(Account.DisplayName, Account.Email),
            To = message.To, Cc = message.Cc, Bcc = message.Bcc, BodyHtml = message.BodyHtml, Sent = DateTime.Now, Received = DateTime.Now, IsRead = true,
            Attachments = message.Attachments.Select(a => new AttachmentInfo { Id = NewId(), Name = a.Name, Size = a.Size }).ToList()
        });
    }

    public Task<string> SaveDraftAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        var d = message.DraftId != null ? _messages.FirstOrDefault(m => m.Id == message.DraftId) : null;
        if (d == null)
        {
            d = new MailMessageDetail { Id = NewId(), AccountId = Account.Id, FolderId = "drafts", IsDraft = true, IsRead = true };
            _messages.Add(d);
        }
        d.Subject = message.Subject; d.To = message.To; d.Cc = message.Cc; d.Bcc = message.Bcc; d.BodyHtml = message.BodyHtml;
        d.From = new Recipient(Account.DisplayName, Account.Email); d.Received = d.Sent = DateTime.Now;
        message.DraftId = d.Id;
        return Task.FromResult(d.Id);
    }

    public Task<OutgoingMessage> LoadDraftAsync(string draftId, CancellationToken ct = default)
    {
        var d = _messages.First(m => m.Id == draftId);
        return Task.FromResult(new OutgoingMessage
        {
            AccountId = Account.Id, DraftId = d.Id, Mode = ComposeMode.EditDraft, To = d.To, Cc = d.Cc, Bcc = d.Bcc, Subject = d.Subject, BodyHtml = d.BodyHtml
        });
    }

    public Task<IReadOnlyList<NewMailNotification>> GetNewMailSinceAsync(DateTime sinceUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NewMailNotification>>(Array.Empty<NewMailNotification>());

    public Task<IReadOnlyList<ResolvedName>> ResolveNamesAsync(string query, bool directoryOnly = false, CancellationToken ct = default)
    {
        var q = query.Trim();
        var res = People.Where(p => p.name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.email.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(p => new ResolvedName { Name = p.name, Email = p.email, Kind = RecipientKind.Mailbox, Title = "Specialist", Department = "Operations", Company = "Contoso", Phone = "+995 32 2 00 00 00", Office = "თბილისი" })
            .ToList();
        return Task.FromResult<IReadOnlyList<ResolvedName>>(res);
    }

    public Task<IReadOnlyList<Recipient>> ExpandGroupAsync(string email, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Recipient>>(People.Take(3).Select(p => new Recipient(p.name, p.email)).ToList());

    public Task<byte[]?> GetUserPhotoAsync(string email, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    public Task<IReadOnlyList<CalendarSource>> GetOwnCalendarsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CalendarSource>>(new List<CalendarSource>
        {
            new() { Key = "self", AccountId = Account.Id, FolderId = "calendar", DisplayName = "Calendar", OwnerEmail = Account.Email, OwnerName = Account.DisplayName, IsDefault = true, Group = CalendarGroupKind.MyCalendars, Access = CalendarAccess.Owner }
        });

    public Task<CalendarSource> OpenSharedCalendarAsync(string ownerEmail, CancellationToken ct = default)
    {
        var p = People.FirstOrDefault(x => x.email.Equals(ownerEmail, StringComparison.OrdinalIgnoreCase) || x.name == ownerEmail);
        var name = p.name ?? ownerEmail;
        return Task.FromResult(new CalendarSource
        {
            Key = p.email == People[0].email ? "shared:nino" : "shared:" + ownerEmail.ToLowerInvariant(), AccountId = Account.Id, FolderId = "",
            OwnerEmail = p.email ?? ownerEmail, OwnerName = name, DisplayName = name, IsShared = true, Group = CalendarGroupKind.SharedCalendars,
            Access = CalendarAccess.Editor
        });
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(CalendarSource source, DateTime start, DateTime end, CancellationToken ct = default)
    {
        await Delay();
        return _events.Where(e => e.CalendarKey == source.Key && e.End > start && e.Start < end).Select(e => e.CloneShallow()).ToList();
    }

    public Task<CalendarEvent> GetEventAsync(string itemId, CancellationToken ct = default) =>
        Task.FromResult(_events.First(e => e.Id == itemId).CloneShallow());

    public Task<string> SaveEventAsync(CalendarSource source, CalendarEvent ev, bool sendInvitations, EditScope scope, CancellationToken ct = default)
    {
        var copy = ev.CloneShallow();
        copy.CalendarKey = source.Key;
        if (string.IsNullOrEmpty(copy.Id)) copy.Id = NewId();
        _events.RemoveAll(e => e.Id == copy.Id);
        copy.IsMeeting = copy.Attendees.Any();
        copy.Recurrence = ev.Recurrence;
        copy.IsRecurring = ev.Recurrence != null && ev.Recurrence.Type != RecurrenceType.None;
        copy.IsOccurrence = copy.IsRecurring; // like EWS CalendarView: every instance is an occurrence
        _events.Add(copy);
        if (copy.IsRecurring && string.IsNullOrEmpty(ev.Id))
        {
            // expand a few occurrences for the demo
            var r = ev.Recurrence!;
            for (int i = 1; i < 12; i++)
            {
                var o = copy.CloneShallow();
                o.Id = NewId();
                var shift = r.Type switch { RecurrenceType.Daily => TimeSpan.FromDays(i * r.Interval), RecurrenceType.Weekly => TimeSpan.FromDays(7 * i * r.Interval), _ => TimeSpan.FromDays(30 * i) };
                o.Start += shift; o.End += shift; o.IsOccurrence = true;
                _events.Add(o);
            }
        }
        return Task.FromResult(copy.Id);
    }

    public Task DeleteEventAsync(string itemId, bool notifyAttendees, string? cancellationText, EditScope scope, CancellationToken ct = default)
    {
        _events.RemoveAll(e => e.Id == itemId); return Done;
    }

    public Task RespondToMeetingAsync(string itemId, MeetingResponse response, bool sendResponse, string? comment, CancellationToken ct = default)
    {
        var m = _messages.FirstOrDefault(x => x.Id == itemId);
        if (m != null)
        {
            m.MyResponse = response;
            if (response != MeetingResponse.Decline && m.MeetingStart.HasValue)
            {
                _events.Add(new CalendarEvent
                {
                    Id = NewId(), CalendarKey = "self", Subject = m.Subject, Location = m.MeetingLocation ?? "", Start = m.MeetingStart.Value, End = m.MeetingEnd!.Value,
                    IsMeeting = true, IsOrganizer = false, Organizer = m.From, MyResponse = response, ShowAs = response == MeetingResponse.Tentative ? ShowAs.Tentative : ShowAs.Busy
                });
            }
            m.FolderId = "deleted";
        }
        var e = _events.FirstOrDefault(x => x.Id == itemId);
        if (e != null) e.MyResponse = response;
        return Done;
    }

    public Task<IReadOnlyList<AttendeeAvailability>> GetAvailabilityAsync(IEnumerable<string> emails, DateTime start, DateTime end, CancellationToken ct = default)
    {
        var rnd = new Random(42);
        var list = emails.Select(e => new AttendeeAvailability
        {
            Email = e,
            Slots = Enumerable.Range(0, (int)Math.Max(1, (end - start).TotalDays))
                .SelectMany(d => new[]
                {
                    new BusySlot { Start = start.Date.AddDays(d).AddHours(9 + rnd.Next(0, 3)), End = start.Date.AddDays(d).AddHours(10.5 + rnd.Next(0, 2)), Status = ShowAs.Busy },
                    new BusySlot { Start = start.Date.AddDays(d).AddHours(14 + rnd.Next(0, 2)), End = start.Date.AddDays(d).AddHours(15.5 + rnd.Next(0, 2)), Status = rnd.Next(3) == 0 ? ShowAs.Tentative : ShowAs.Busy },
                }).ToList()
        }).ToList();
        return Task.FromResult<IReadOnlyList<AttendeeAvailability>>(list);
    }

    public Task DismissReminderAsync(string itemId, CancellationToken ct = default) => Done;

    public Task<IReadOnlyList<ReminderInfo>> GetUpcomingRemindersAsync(DateTime until, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ReminderInfo>>(_events.Where(e => e.CalendarKey == "self" && e.ReminderSet && e.Start.AddMinutes(-e.ReminderMinutes) <= until && e.End > DateTime.Now && !e.IsAllDay)
            .Select(e => new ReminderInfo { Key = e.Id + "|" + e.Start.ToString("O"), AccountId = Account.Id, ItemId = e.Id, Subject = e.Subject, Location = e.Location, Start = e.Start, DueBy = e.Start.AddMinutes(-e.ReminderMinutes) })
            .ToList());

    public Task<IReadOnlyList<PermissionEntry>> GetFolderPermissionsAsync(string folderId, CancellationToken ct = default)
    {
        if (!_permissions.TryGetValue(folderId, out var list))
            _permissions[folderId] = list = new List<PermissionEntry> { new() { StandardUser = "Default", Level = PermissionLevel.None }, new() { StandardUser = "Anonymous", Level = PermissionLevel.None } };
        return Task.FromResult<IReadOnlyList<PermissionEntry>>(list.ToList());
    }

    public Task SetFolderPermissionsAsync(string folderId, IEnumerable<PermissionEntry> permissions, CancellationToken ct = default)
    {
        _permissions[folderId] = permissions.ToList(); return Done;
    }

    public Task<IReadOnlyList<ContactInfo>> GetContactsAsync(string folderId, string? search, CancellationToken ct = default)
    {
        IEnumerable<ContactInfo> list = _contacts;
        if (!string.IsNullOrWhiteSpace(search)) list = list.Where(c => c.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase) || c.Email1.Contains(search, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<ContactInfo>>(list.OrderBy(c => c.SortKey).ToList());
    }

    public Task<ContactInfo> GetContactAsync(string itemId, CancellationToken ct = default) => Task.FromResult(_contacts.First(c => c.Id == itemId));

    public Task<string> SaveContactAsync(string folderId, ContactInfo contact, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(contact.Id)) contact.Id = NewId();
        _contacts.RemoveAll(c => c.Id == contact.Id);
        contact.AccountId = Account.Id;
        if (string.IsNullOrWhiteSpace(contact.DisplayName)) contact.DisplayName = $"{contact.GivenName} {contact.Surname}".Trim();
        if (string.IsNullOrWhiteSpace(contact.FileAs)) contact.FileAs = string.IsNullOrWhiteSpace(contact.Surname) ? contact.DisplayName : $"{contact.Surname}, {contact.GivenName}";
        _contacts.Add(contact);
        return Task.FromResult(contact.Id);
    }

    public Task DeleteContactAsync(string itemId, CancellationToken ct = default) { _contacts.RemoveAll(c => c.Id == itemId); return Done; }

    public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(string folderId, bool includeCompleted, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TaskInfo>>(_tasks.Where(t => includeCompleted || !t.IsComplete)
            // Like EWS: flagged inbox mail also shows up in the To-Do list.
            .Concat(string.IsNullOrEmpty(folderId)
                ? _messages.Where(m => m.FolderId == "inbox" && m.Flag == FlagState.Flagged).Select(m => new TaskInfo
                  {
                      Id = m.Id, AccountId = Account.Id, FolderId = m.FolderId, Subject = m.Subject, DueDate = null,
                      Importance = m.Importance, IsFlaggedMail = true, Status = TaskState.NotStarted, Categories = m.Categories
                  })
                : Enumerable.Empty<TaskInfo>())
            .ToList());

    public Task<TaskInfo> GetTaskAsync(string itemId, CancellationToken ct = default) => Task.FromResult(_tasks.First(t => t.Id == itemId));

    public Task<string> SaveTaskAsync(string folderId, TaskInfo task, CancellationToken ct = default)
    {
        if (task.IsFlaggedMail && _messages.FirstOrDefault(m => m.Id == task.Id) is { } msg)
        {
            msg.Flag = task.IsComplete ? FlagState.Complete : FlagState.Flagged;
            return Task.FromResult(task.Id);
        }
        if (string.IsNullOrEmpty(task.Id)) task.Id = NewId();
        _tasks.RemoveAll(t => t.Id == task.Id);
        _tasks.Add(task);
        return Task.FromResult(task.Id);
    }

    public Task DeleteTaskAsync(string itemId, CancellationToken ct = default) { _tasks.RemoveAll(t => t.Id == itemId); return Done; }

    public Task<OofSettingsInfo> GetOofAsync(CancellationToken ct = default) => Task.FromResult(_oof);
    public Task SetOofAsync(OofSettingsInfo settings, CancellationToken ct = default) { _oof = settings; return Done; }
    public Task<IReadOnlyList<RuleInfo>> GetRulesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RuleInfo>>(_rules.ToList());

    public Task SaveRulesAsync(IReadOnlyList<RuleInfo> rules, IReadOnlyList<string> deletedRuleIds, CancellationToken ct = default)
    {
        _rules = rules.ToList();
        foreach (var r in _rules.Where(r => string.IsNullOrEmpty(r.Id))) r.Id = NewId();
        return Done;
    }
}
