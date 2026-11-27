using System.Globalization;
using System.IO;
using System.Text;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Outlook;

/// <summary>
/// <see cref="IMailService"/> backed by a locally installed Microsoft Outlook (classic) through its
/// COM object model. Used when EWS is blocked by the server but Outlook itself connects (MAPI/HTTP).
/// All COM access happens on a dedicated STA thread (<see cref="StaComContext"/>).
/// </summary>
public sealed partial class OutlookMailService : IMailService, IDisposable
{
    private const char Sep = '\u0001';

    private readonly StaComContext _sta;
    private dynamic? _app;        // Outlook.Application
    private dynamic? _ns;         // NameSpace (MAPI)
    private dynamic? _store;      // primary account's delivery store
    private string _primarySmtp = "";
    private readonly Dictionary<string, FolderKind> _wellKnown = new(StringComparer.OrdinalIgnoreCase);

    public AccountConfig Account { get; }
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string? LastError { get; private set; }
    public string? ServerVersion { get; private set; }
    public string? UserDisplayName { get; private set; }
    public event EventHandler? StateChanged;

    public OutlookMailService(AccountConfig account)
    {
        Account = account;
        _sta = new StaComContext("Outlook-COM-" + account.Id);
    }

    private void SetState(ConnectionState s, string? error = null)
    {
        State = s;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private Task<T> Com<T>(Func<T> f) => _sta.Run(f);
    private Task Com(Action a) => _sta.Run(a);

    // =====================================================================================
    // Connection
    // =====================================================================================

    public Task ConnectAsync(string? password, CancellationToken ct = default)
    {
        return Com(() =>
        {
            try
            {
                SetState(ConnectionState.Connecting);
                var progId = Type.GetTypeFromProgID("Outlook.Application")
                    ?? throw new MailServiceException("Microsoft Outlook არ არის დაყენებული ამ კომპიუტერზე.");
                // Attaches to the running Outlook, or starts it (Outlook is a COM singleton).
                _app = Activator.CreateInstance(progId)!;
                _ns = _app!.GetNamespace("MAPI");

                try { ServerVersion = (string)_app!.Version; } catch { }

                // Locate the store that matches the requested e-mail address (or the default store).
                dynamic? match = null;
                dynamic stores = _ns!.Stores;
                for (int i = 1; i <= (int)stores.Count; i++)
                {
                    dynamic st = stores[i];
                    string smtp = GetStoreSmtp(st);
                    string disp = TryStr(() => (string)st.DisplayName);
                    if (!string.IsNullOrEmpty(Account.Email) &&
                        (string.Equals(disp, Account.Email, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(smtp, Account.Email, StringComparison.OrdinalIgnoreCase)))
                    {
                        match = st; break;
                    }
                }
                _store = match ?? _ns!.DefaultStore;
                _primarySmtp = TryStr(() => (string)GetStoreSmtp(_store!));
                if (string.IsNullOrEmpty(_primarySmtp)) _primarySmtp = Account.Email;

                try { UserDisplayName = (string)_ns!.CurrentUser.Name; } catch { }
                if (string.IsNullOrWhiteSpace(Account.DisplayName))
                    Account.DisplayName = UserDisplayName ?? Account.Email;

                CacheWellKnown();
                SetState(ConnectionState.Connected);
            }
            catch (MailServiceException) { SetState(ConnectionState.Error, LastError); throw; }
            catch (Exception ex)
            {
                SetState(ConnectionState.Error, ex.Message);
                throw new MailServiceException("Outlook-თან დაკავშირება ვერ მოხერხდა: " + ex.Message, ex);
            }
        });
    }

    private string GetStoreSmtp(dynamic store)
    {
        // The store's root folder belongs to a user whose SMTP we can read from the account.
        try
        {
            dynamic accounts = _ns!.Accounts;
            for (int i = 1; i <= (int)accounts.Count; i++)
            {
                dynamic acc = accounts[i];
                try { if (acc.DeliveryStore != null && (string)acc.DeliveryStore.StoreID == (string)store.StoreID) return (string)acc.SmtpAddress; }
                catch { }
            }
        }
        catch { }
        return "";
    }

    private void CacheWellKnown()
    {
        _wellKnown.Clear();
        void Add(int def, FolderKind kind)
        {
            try { dynamic f = _ns!.GetDefaultFolder(def); _wellKnown[(string)f.EntryID] = kind; } catch { }
        }
        Add(OL.Inbox, FolderKind.Inbox);
        Add(OL.Drafts, FolderKind.Drafts);
        Add(OL.SentMail, FolderKind.SentItems);
        Add(OL.DeletedItems, FolderKind.DeletedItems);
        Add(OL.Junk, FolderKind.JunkEmail);
        Add(OL.Outbox, FolderKind.Outbox);
        Add(OL.Calendar, FolderKind.Calendar);
        Add(OL.Contacts, FolderKind.Contacts);
        Add(OL.Tasks, FolderKind.Tasks);
        Add(OL.Notes, FolderKind.Notes);
        Add(OL.Journal, FolderKind.Journal);
    }

    // =====================================================================================
    // Id helpers
    // =====================================================================================

    private static string Id(string entryId, string? storeId) =>
        string.IsNullOrEmpty(storeId) ? entryId : entryId + Sep + storeId;

    private static (string entry, string? store) Split(string id)
    {
        int i = id.IndexOf(Sep);
        return i < 0 ? (id, null) : (id[..i], id[(i + 1)..]);
    }

    private dynamic GetFolder(string id)
    {
        var (entry, store) = Split(id);
        return store == null ? _ns!.GetFolderFromID(entry) : _ns!.GetFolderFromID(entry, store);
    }

    private dynamic GetItem(string id)
    {
        var (entry, store) = Split(id);
        return store == null ? _ns!.GetItemFromID(entry) : _ns!.GetItemFromID(entry, store);
    }

    private static string TryStr(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    private static T? Try<T>(Func<T> f) { try { return f(); } catch { return default; } }

    private void Ensure()
    {
        if (_ns == null || State != ConnectionState.Connected)
            throw new MailServiceException("Outlook არ არის დაკავშირებული.");
    }

    // =====================================================================================
    // Mapping helpers
    // =====================================================================================

    private static Importance MapImportance(int v) => v switch { OL.ImportanceLow => Importance.Low, OL.ImportanceHigh => Importance.High, _ => Importance.Normal };
    private static int MapImportance(Importance v) => v switch { Importance.Low => OL.ImportanceLow, Importance.High => OL.ImportanceHigh, _ => OL.ImportanceNormal };
    private static Sensitivity MapSensitivity(int v) => v switch { OL.Personal => Sensitivity.Personal, OL.Private => Sensitivity.Private, OL.Confidential => Sensitivity.Confidential, _ => Sensitivity.Normal };
    private static int MapSensitivity(Sensitivity v) => v switch { Sensitivity.Personal => OL.Personal, Sensitivity.Private => OL.Private, Sensitivity.Confidential => OL.Confidential, _ => OL.Normal };

    private static ShowAs MapBusy(int v) => v switch { OL.Free => ShowAs.Free, OL.Tentative => ShowAs.Tentative, OL.OutOfOffice => ShowAs.OutOfOffice, OL.WorkingElsewhere => ShowAs.WorkingElsewhere, _ => ShowAs.Busy };
    private static int MapBusy(ShowAs v) => v switch { ShowAs.Free => OL.Free, ShowAs.Tentative => OL.Tentative, ShowAs.OutOfOffice => OL.OutOfOffice, ShowAs.WorkingElsewhere => OL.WorkingElsewhere, _ => OL.Busy };

    private static MeetingResponse MapResponse(int v) => v switch
    {
        OL.ResponseOrganized => MeetingResponse.Organizer,
        OL.ResponseTentative => MeetingResponse.Tentative,
        OL.ResponseAccepted => MeetingResponse.Accept,
        OL.ResponseDeclined => MeetingResponse.Decline,
        OL.ResponseNotResponded => MeetingResponse.NoResponseReceived,
        _ => MeetingResponse.None
    };

    private string SmtpOfAddressEntry(dynamic ae)
    {
        try
        {
            string type = TryStr(() => (string)ae.Type);
            if (string.Equals(type, "EX", StringComparison.OrdinalIgnoreCase))
            {
                var ex = Try(() => (object?)ae.GetExchangeUser());
                if (ex != null) { string s = TryStr(() => (string)((dynamic)ex).PrimarySmtpAddress); if (!string.IsNullOrEmpty(s)) return s; }
                var dl = Try(() => (object?)ae.GetExchangeDistributionList());
                if (dl != null) { string s = TryStr(() => (string)((dynamic)dl).PrimarySmtpAddress); if (!string.IsNullOrEmpty(s)) return s; }
            }
            string addr = TryStr(() => (string)ae.Address);
            return addr;
        }
        catch { return ""; }
    }

    private Recipient SenderRecipient(dynamic mail)
    {
        string name = TryStr(() => (string)mail.SenderName);
        string addr = "";
        var sender = Try(() => (object?)mail.Sender);
        if (sender != null) addr = SmtpOfAddressEntry((dynamic)sender);
        if (string.IsNullOrEmpty(addr)) addr = TryStr(() => (string)mail.SenderEmailAddress);
        if (addr.StartsWith("/", StringComparison.Ordinal)) addr = name; // legacyExchangeDN fallback
        return new Recipient(name, addr);
    }

    private List<Recipient> MapRecipients(dynamic item, int type)
    {
        var list = new List<Recipient>();
        try
        {
            dynamic recips = item.Recipients;
            int n = (int)recips.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic r = recips[i];
                if (type >= 0 && (int)r.Type != type) continue;
                string name = TryStr(() => (string)r.Name);
                string addr = "";
                var ae = Try(() => (object?)r.AddressEntry);
                if (ae != null) addr = SmtpOfAddressEntry((dynamic)ae);
                if (string.IsNullOrEmpty(addr)) addr = TryStr(() => (string)r.Address);
                list.Add(new Recipient(name, addr));
            }
        }
        catch { }
        return list;
    }

    private static FlagState MapFlag(int status) => status switch { OL.FlagComplete => FlagState.Complete, OL.FlagMarked => FlagState.Flagged, _ => FlagState.NotFlagged };

    private static List<string> SplitCategories(string? s) =>
        string.IsNullOrWhiteSpace(s) ? new() : s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // =====================================================================================
    // Folders
    // =====================================================================================

    public Task<IReadOnlyList<MailFolderInfo>> GetFolderTreeAsync(string? mailboxEmail = null, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        var result = new List<MailFolderInfo>();
        dynamic root = _store!.GetRootFolder();
        string storeId = (string)_store!.StoreID;
        WalkFolders(root, null, storeId, result);
        return (IReadOnlyList<MailFolderInfo>)result;
    });

    private void WalkFolders(dynamic folder, string? parentId, string storeId, List<MailFolderInfo> acc)
    {
        string entry = (string)folder.EntryID;
        string id = Id(entry, storeId);
        var info = new MailFolderInfo
        {
            Id = id,
            ParentId = parentId,
            AccountId = Account.Id,
            DisplayName = (string)folder.Name,
            Kind = _wellKnown.TryGetValue(entry, out var k) ? k : FolderKind.Generic,
            FolderClass = ClassFor(folder),
        };
        try { info.UnreadCount = (int)folder.UnReadItemCount; } catch { }
        try { info.TotalCount = (int)folder.Items.Count; } catch { }
        dynamic subs = folder.Folders;
        int count = 0;
        try { count = (int)subs.Count; } catch { }
        info.ChildFolderCount = count;
        acc.Add(info);
        for (int i = 1; i <= count; i++)
            WalkFolders(subs[i], id, storeId, acc);
    }

    private static string ClassFor(dynamic folder)
    {
        int t = OL.MailItem;
        try { t = (int)folder.DefaultItemType; } catch { }
        return t switch
        {
            OL.AppointmentItem => "IPF.Appointment",
            OL.ContactItem => "IPF.Contact",
            OL.TaskItem => "IPF.Task",
            OL.NoteItem => "IPF.StickyNote",
            OL.JournalItem => "IPF.Journal",
            _ => "IPF.Note"
        };
    }

    public Task<MailFolderInfo?> GetWellKnownFolderAsync(FolderKind kind, string? mailboxEmail = null, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        int def = kind switch
        {
            FolderKind.Inbox => OL.Inbox,
            FolderKind.Drafts => OL.Drafts,
            FolderKind.SentItems => OL.SentMail,
            FolderKind.DeletedItems => OL.DeletedItems,
            FolderKind.JunkEmail => OL.Junk,
            FolderKind.Outbox => OL.Outbox,
            FolderKind.Calendar => OL.Calendar,
            FolderKind.Contacts => OL.Contacts,
            FolderKind.Tasks => OL.Tasks,
            FolderKind.Notes => OL.Notes,
            FolderKind.Journal => OL.Journal,
            _ => OL.Inbox
        };
        dynamic f = _ns!.GetDefaultFolder(def);
        string storeId = TryStr(() => (string)f.Store.StoreID);
        var info = new MailFolderInfo
        {
            Id = Id((string)f.EntryID, storeId),
            AccountId = Account.Id,
            DisplayName = (string)f.Name,
            Kind = kind,
            FolderClass = ClassFor(f),
        };
        try { info.UnreadCount = (int)f.UnReadItemCount; } catch { }
        try { info.TotalCount = (int)f.Items.Count; } catch { }
        return (MailFolderInfo?)info;
    });

    public Task<(int unread, int total)> GetFolderCountsAsync(string folderId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic f = GetFolder(folderId);
        int unread = 0, total = 0;
        try { unread = (int)f.UnReadItemCount; } catch { }
        try { total = (int)f.Items.Count; } catch { }
        return (unread, total);
    });

    public Task<string> CreateFolderAsync(string parentFolderId, string name, string folderClass, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic parent = GetFolder(parentFolderId);
        int type = folderClass switch
        {
            var s when s.StartsWith("IPF.Appointment") => OL.AppointmentItem,
            var s when s.StartsWith("IPF.Contact") => OL.ContactItem,
            var s when s.StartsWith("IPF.Task") => OL.TaskItem,
            _ => OL.MailItem
        };
        dynamic f = parent.Folders.Add(name, type);
        return Id((string)f.EntryID, TryStr(() => (string)f.Store.StoreID));
    });

    public Task RenameFolderAsync(string folderId, string newName, CancellationToken ct = default) => Com(() =>
    {
        Ensure(); dynamic f = GetFolder(folderId); f.Name = newName;
    });

    public Task DeleteFolderAsync(string folderId, bool permanent, CancellationToken ct = default) => Com(() =>
    {
        Ensure(); dynamic f = GetFolder(folderId); f.Delete();
    });

    public Task MoveFolderAsync(string folderId, string newParentId, CancellationToken ct = default) => Com(() =>
    {
        Ensure(); dynamic f = GetFolder(folderId); dynamic p = GetFolder(newParentId); f.MoveTo(p);
    });

    public Task CopyFolderAsync(string folderId, string newParentId, CancellationToken ct = default) => Com(() =>
    {
        Ensure(); dynamic f = GetFolder(folderId); dynamic p = GetFolder(newParentId); f.CopyTo(p);
    });

    public Task MarkAllReadAsync(string folderId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic f = GetFolder(folderId);
        dynamic items = f.Items.Restrict("[UnRead] = true");
        // Restrict returns a filtered collection; iterate a snapshot of ids by walking backwards.
        for (int i = (int)items.Count; i >= 1; i--)
        {
            try { items[i].UnRead = false; } catch { }
        }
    });

    public Task EmptyFolderAsync(string folderId, bool deleteSubFolders, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic f = GetFolder(folderId);
        dynamic items = f.Items;
        for (int i = (int)items.Count; i >= 1; i--) { try { items[i].Delete(); } catch { } }
        if (deleteSubFolders)
        {
            dynamic subs = f.Folders;
            for (int i = (int)subs.Count; i >= 1; i--) { try { subs[i].Delete(); } catch { } }
        }
    });

    // =====================================================================================
    // Mail list & read
    // =====================================================================================

    public Task<Paged<MailItemSummary>> GetItemsAsync(MailQuery query, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic folder = GetFolder(query.FolderId);
        string folderId = query.FolderId;
        dynamic items = folder.Items;

        var filter = BuildFilter(query);
        if (filter != null) items = items.Restrict(filter);

        string sortProp = query.Sort switch
        {
            SortField.From => "[SenderName]",
            SortField.Subject => "[Subject]",
            SortField.Size => "[Size]",
            SortField.Importance => "[Importance]",
            _ => "[ReceivedTime]"
        };
        try { items.Sort(sortProp, query.Descending); } catch { }

        int total = 0;
        try { total = (int)items.Count; } catch { }

        var list = new List<MailItemSummary>();
        int start = query.Offset + 1;
        int end = Math.Min(total, query.Offset + query.PageSize);
        for (int i = start; i <= end; i++)
        {
            dynamic it;
            try { it = items[i]; } catch { continue; }
            var s = MapSummary(it, folderId);
            if (s != null) list.Add(s);
        }
        return new Paged<MailItemSummary> { Items = list, TotalCount = total, MoreAvailable = end < total };
    });

    private static string? BuildFilter(MailQuery q)
    {
        var clauses = new List<string>();
        switch (q.Filter)
        {
            case MailFilter.Unread: clauses.Add("[UnRead] = true"); break;
            case MailFilter.Flagged: clauses.Add("[FlagStatus] = 2"); break;
            case MailFilter.HasAttachments: clauses.Add(@"""urn:schemas:httpmail:hasattachment"" = 1"); break;
            case MailFilter.Important: clauses.Add("[Importance] = 2"); break;
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Replace("'", "''");
            clauses.Add($@"(""urn:schemas:httpmail:subject"" LIKE '%{term}%' OR ""urn:schemas:httpmail:fromname"" LIKE '%{term}%')");
        }
        if (clauses.Count == 0) return null;
        // DASL filter prefix is required when using schema URLs.
        bool dasl = clauses.Any(c => c.Contains("urn:schemas"));
        return dasl ? "@SQL=" + string.Join(" AND ", clauses.Select(DaslWrap)) : string.Join(" AND ", clauses);
    }

    private static string DaslWrap(string clause)
    {
        // Convert the simple [Prop] clauses to DASL when mixed with schema clauses.
        return clause
            .Replace("[UnRead] = true", @"""urn:schemas:httpmail:read"" = 0")
            .Replace("[FlagStatus] = 2", @"""http://schemas.microsoft.com/mapi/proptag/0x10900003"" = 2")
            .Replace("[Importance] = 2", @"""urn:schemas:httpmail:importance"" = 2");
    }

    private MailItemSummary? MapSummary(dynamic it, string folderId)
    {
        try
        {
            string cls = TryStr(() => (string)it.MessageClass);
            var s = new MailItemSummary
            {
                Id = Id((string)it.EntryID, TryStr(() => (string)it.Parent.Store.StoreID)),
                AccountId = Account.Id,
                FolderId = folderId,
                ItemClass = string.IsNullOrEmpty(cls) ? "IPM.Note" : cls,
            };
            bool isMail = cls.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase) || cls.StartsWith("IPM.Schedule", StringComparison.OrdinalIgnoreCase);
            s.Subject = TryStr(() => (string)it.Subject);
            try { s.Received = ((DateTime)it.ReceivedTime); } catch { try { s.Received = (DateTime)it.CreationTime; } catch { } }
            try { s.Sent = (DateTime)it.SentOn; } catch { s.Sent = s.Received; }
            try { s.Size = (int)it.Size; } catch { }
            try { s.IsRead = !(bool)it.UnRead; } catch { s.IsRead = true; }
            try { s.HasAttachments = (int)it.Attachments.Count > 0; } catch { }
            try { s.Importance = MapImportance((int)it.Importance); } catch { }
            try { s.Flag = MapFlag((int)it.FlagStatus); } catch { }
            try { s.Categories = SplitCategories((string)it.Categories); } catch { }
            s.From = isMail ? SenderRecipient(it) : new Recipient(TryStr(() => (string)it.SenderName), "");
            s.DisplayTo = TryStr(() => (string)it.To);
            s.Preview = TryStr(() => { string b = (string)it.Body; return b.Length > 200 ? b[..200] : b; }).Replace("\r", " ").Replace("\n", " ").Trim();
            bool draft = false;
            try { draft = _wellKnown.TryGetValue(Split(folderId).entry, out var fk) && fk == FolderKind.Drafts; } catch { }
            s.IsDraft = draft;
            s.ListName = draft || IsSentItems(folderId) ? s.DisplayTo : s.From.Display;
            return s;
        }
        catch { return null; }
    }

    private bool IsSentItems(string folderId) =>
        _wellKnown.TryGetValue(Split(folderId).entry, out var fk) && fk == FolderKind.SentItems;

    public Task<MailMessageDetail> GetMessageAsync(string itemId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic it = GetItem(itemId);
        string cls = TryStr(() => (string)it.MessageClass);
        var d = new MailMessageDetail
        {
            Id = itemId,
            AccountId = Account.Id,
            FolderId = Id(TryStr(() => (string)it.Parent.EntryID), TryStr(() => (string)it.Parent.Store.StoreID)),
            ItemClass = string.IsNullOrEmpty(cls) ? "IPM.Note" : cls,
            Subject = TryStr(() => (string)it.Subject),
        };
        d.From = SenderRecipient(it);
        d.To = MapRecipients(it, OL.To);
        d.Cc = MapRecipients(it, OL.CC);
        try { d.Sent = (DateTime)it.SentOn; } catch { }
        try { d.Received = (DateTime)it.ReceivedTime; } catch { d.Received = d.Sent; }
        int fmt = OL.FormatHTML;
        try { fmt = (int)it.BodyFormat; } catch { }
        if (fmt == OL.FormatHTML) { d.BodyHtml = TryStr(() => (string)it.HTMLBody); d.BodyIsHtml = true; }
        if (string.IsNullOrEmpty(d.BodyHtml)) { d.BodyHtml = TryStr(() => (string)it.HTMLBody); }
        if (string.IsNullOrEmpty(d.BodyHtml)) { d.BodyHtml = PlainToHtml(TryStr(() => (string)it.Body)); d.BodyIsHtml = false; }
        try { d.Importance = MapImportance((int)it.Importance); } catch { }
        try { d.Sensitivity = MapSensitivity((int)it.Sensitivity); } catch { }
        try { d.IsRead = !(bool)it.UnRead; } catch { }
        try { d.Flag = MapFlag((int)it.FlagStatus); } catch { }
        try { d.Categories = SplitCategories((string)it.Categories); } catch { }
        d.IsDraft = IsDraftFolder(d.FolderId);
        d.Attachments = MapAttachments(it);

        if (d.IsMeetingItem)
        {
            var appt = Try(() => (object?)it.GetAssociatedAppointment(false));
            if (appt != null)
            {
                dynamic a = appt;
                try { d.MeetingStart = (DateTime)a.Start; } catch { }
                try { d.MeetingEnd = (DateTime)a.End; } catch { }
                try { d.MeetingLocation = (string)a.Location; } catch { }
                try { d.MyResponse = MapResponse((int)a.ResponseStatus); } catch { }
                try { d.AssociatedAppointmentId = Id((string)a.EntryID, TryStr(() => (string)a.Parent.Store.StoreID)); } catch { }
            }
        }
        return d;
    });

    private bool IsDraftFolder(string folderId) =>
        _wellKnown.TryGetValue(Split(folderId).entry, out var fk) && fk == FolderKind.Drafts;

    private List<AttachmentInfo> MapAttachments(dynamic it)
    {
        var list = new List<AttachmentInfo>();
        try
        {
            dynamic atts = it.Attachments;
            int n = (int)atts.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic a = atts[i];
                var info = new AttachmentInfo { Id = i.ToString(CultureInfo.InvariantCulture) };
                info.Name = TryStr(() => (string)a.FileName);
                if (string.IsNullOrEmpty(info.Name)) info.Name = TryStr(() => (string)a.DisplayName);
                try { info.Size = (long)(int)a.Size; } catch { }
                try { info.IsItemAttachment = (int)a.Type == OL.EmbeddedItem; } catch { }
                list.Add(info);
            }
        }
        catch { }
        return list;
    }

    private static string PlainToHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder("<div style=\"font-family:Segoe UI,Calibri,sans-serif\">");
        sb.Append(System.Net.WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>"));
        sb.Append("</div>");
        return sb.ToString();
    }

    public Task<byte[]> GetAttachmentContentAsync(string itemId, string attachmentId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic it = GetItem(itemId);
        if (!int.TryParse(attachmentId, out int idx)) throw new MailServiceException("დანართი ვერ მოიძებნა.");
        dynamic a = it.Attachments[idx];
        string tmp = Path.Combine(Path.GetTempPath(), "Posta", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        string name = TryStr(() => (string)a.FileName);
        if (string.IsNullOrEmpty(name)) name = "attachment.dat";
        string path = Path.Combine(tmp, name);
        a.SaveAsFile(path);
        var bytes = File.ReadAllBytes(path);
        try { File.Delete(path); } catch { }
        return bytes;
    });

    public Task<byte[]> GetMimeContentAsync(string itemId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic it = GetItem(itemId);
        string tmp = Path.Combine(Path.GetTempPath(), "Posta", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        string path = Path.Combine(tmp, "message.msg");
        // True RFC822 MIME is not exposed by the Outlook object model; .msg is the closest fidelity.
        it.SaveAs(path, OL.SaveMSGUnicode);
        var bytes = File.ReadAllBytes(path);
        try { File.Delete(path); } catch { }
        return bytes;
    });

    public Task SetReadAsync(IEnumerable<string> itemIds, bool isRead, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        foreach (var id in itemIds) { try { dynamic it = GetItem(id); it.UnRead = !isRead; it.Save(); } catch { } }
    });

    public Task SetFlagAsync(IEnumerable<string> itemIds, FlagState state, DateTime? start, DateTime? due, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        foreach (var id in itemIds)
        {
            try
            {
                dynamic it = GetItem(id);
                switch (state)
                {
                    case FlagState.NotFlagged: it.FlagStatus = OL.NoFlag; try { it.ClearTaskFlag(); } catch { } break;
                    case FlagState.Complete: it.FlagStatus = OL.FlagComplete; break;
                    case FlagState.Flagged:
                        it.FlagStatus = OL.FlagMarked;
                        try { if (due.HasValue) it.TaskDueDate = due.Value; } catch { }
                        try { if (start.HasValue) it.TaskStartDate = start.Value; } catch { }
                        break;
                }
                it.Save();
            }
            catch { }
        }
    });

    public Task SetCategoriesAsync(string itemId, IEnumerable<string> categories, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic it = GetItem(itemId);
        it.Categories = string.Join("; ", categories);
        it.Save();
    });

    public Task SetImportanceAsync(string itemId, Importance importance, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic it = GetItem(itemId);
        it.Importance = MapImportance(importance);
        it.Save();
    });

    public Task DeleteItemsAsync(IEnumerable<string> itemIds, bool permanent, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic? deleted = permanent ? null : Try(() => (object?)_ns!.GetDefaultFolder(OL.DeletedItems));
        foreach (var id in itemIds)
        {
            try
            {
                dynamic it = GetItem(id);
                if (!permanent && deleted != null) it.Move(deleted);
                else it.Delete();
            }
            catch { }
        }
    });

    public Task MoveItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic dest = GetFolder(destinationFolderId);
        foreach (var id in itemIds) { try { GetItem(id).Move(dest); } catch { } }
    });

    public Task CopyItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic dest = GetFolder(destinationFolderId);
        foreach (var id in itemIds) { try { dynamic copy = GetItem(id).Copy(); copy.Move(dest); } catch { } }
    });

    public Task MarkAsJunkAsync(IEnumerable<string> itemIds, bool isJunk, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        int def = isJunk ? OL.Junk : OL.Inbox;
        dynamic dest = _ns!.GetDefaultFolder(def);
        foreach (var id in itemIds) { try { GetItem(id).Move(dest); } catch { } }
    });

    public Task IgnoreConversationAsync(string itemId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        try { dynamic it = GetItem(itemId); dynamic conv = it.GetConversation(); } catch { }
        // Outlook's Ignore is not exposed; move the single item to Deleted Items as a fallback.
        try { dynamic del = _ns!.GetDefaultFolder(OL.DeletedItems); GetItem(itemId).Move(del); } catch { }
    });

    public Task<IReadOnlyList<NewMailNotification>> GetNewMailSinceAsync(DateTime sinceUtc, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        var list = new List<NewMailNotification>();
        dynamic inbox = _ns!.GetDefaultFolder(OL.Inbox);
        dynamic items = inbox.Items;
        try { items.Sort("[ReceivedTime]", true); } catch { }
        DateTime sinceLocal = sinceUtc.ToLocalTime();
        int n = 0;
        try { n = (int)items.Count; } catch { }
        for (int i = 1; i <= n && i <= 50; i++)
        {
            dynamic it;
            try { it = items[i]; } catch { break; }
            DateTime rt;
            try { rt = (DateTime)it.ReceivedTime; } catch { continue; }
            if (rt <= sinceLocal) break;
            bool unread = true; try { unread = (bool)it.UnRead; } catch { }
            if (!unread) continue;
            list.Add(new NewMailNotification
            {
                AccountId = Account.Id,
                ItemId = Id((string)it.EntryID, TryStr(() => (string)it.Parent.Store.StoreID)),
                From = SenderRecipient(it).Display,
                Subject = TryStr(() => (string)it.Subject),
                Preview = TryStr(() => { string b = (string)it.Body; return b.Length > 120 ? b[..120] : b; }).Replace("\r", " ").Replace("\n", " ").Trim(),
                Received = rt
            });
        }
        return (IReadOnlyList<NewMailNotification>)list;
    });

    // =====================================================================================
    // Send / draft
    // =====================================================================================

    public Task SendAsync(OutgoingMessage message, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic mail = BuildOutgoing(message);
        mail.Send();
    });

    public Task<string> SaveDraftAsync(OutgoingMessage message, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic mail = BuildOutgoing(message);
        mail.Save();
        return Id((string)mail.EntryID, TryStr(() => (string)mail.Parent.Store.StoreID));
    });

    private dynamic BuildOutgoing(OutgoingMessage m)
    {
        dynamic mail;
        if (!string.IsNullOrEmpty(m.DraftId)) mail = GetItem(m.DraftId);
        else if ((m.Mode == ComposeMode.Reply || m.Mode == ComposeMode.ReplyAll || m.Mode == ComposeMode.Forward) && !string.IsNullOrEmpty(m.SourceItemId))
        {
            dynamic src = GetItem(m.SourceItemId!);
            mail = m.Mode switch
            {
                ComposeMode.Reply => src.Reply(),
                ComposeMode.ReplyAll => src.ReplyAll(),
                _ => src.Forward()
            };
        }
        else mail = _app!.CreateItem(OL.MailItem);

        // Recipients: clear and re-add from the message so edits apply.
        try { while ((int)mail.Recipients.Count > 0) mail.Recipients.Remove(1); } catch { }
        AddRecips(mail, m.To, OL.To);
        AddRecips(mail, m.Cc, OL.CC);
        AddRecips(mail, m.Bcc, OL.BCC);
        try { mail.Recipients.ResolveAll(); } catch { }

        mail.Subject = m.Subject ?? "";
        mail.HTMLBody = m.BodyHtml ?? "";
        mail.Importance = MapImportance(m.Importance);
        mail.Sensitivity = MapSensitivity(m.Sensitivity);
        if (m.Categories.Count > 0) mail.Categories = string.Join("; ", m.Categories);
        if (m.RequestReadReceipt) try { mail.ReadReceiptRequested = true; } catch { }
        if (m.RequestDeliveryReceipt) try { mail.OriginatorDeliveryReportRequested = true; } catch { }
        if (!string.IsNullOrEmpty(m.FromAddress)) try { mail.SentOnBehalfOfName = m.FromAddress; } catch { }
        if (m.DeferredDelivery.HasValue) try { mail.DeferredDeliveryTime = m.DeferredDelivery.Value; } catch { }

        foreach (var id in m.RemovedAttachmentIds)
        {
            if (int.TryParse(id, out int idx)) { try { mail.Attachments.Remove(idx); } catch { } }
        }
        foreach (var a in m.Attachments)
        {
            try
            {
                if (!string.IsNullOrEmpty(a.LocalPath) && File.Exists(a.LocalPath)) mail.Attachments.Add(a.LocalPath);
                else if (a.Content != null)
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "Posta", Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(tmp);
                    string p = Path.Combine(tmp, a.Name);
                    File.WriteAllBytes(p, a.Content);
                    mail.Attachments.Add(p);
                }
            }
            catch { }
        }
        return mail;
    }

    private void AddRecips(dynamic mail, List<Recipient> recips, int type)
    {
        foreach (var r in recips)
        {
            if (string.IsNullOrWhiteSpace(r.Address) && string.IsNullOrWhiteSpace(r.Name)) continue;
            try
            {
                dynamic added = mail.Recipients.Add(string.IsNullOrWhiteSpace(r.Address) ? r.Name : r.Address);
                added.Type = type;
            }
            catch { }
        }
    }

    public Task<OutgoingMessage> LoadDraftAsync(string draftId, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        dynamic it = GetItem(draftId);
        var m = new OutgoingMessage
        {
            AccountId = Account.Id,
            DraftId = draftId,
            Mode = ComposeMode.EditDraft,
            Subject = TryStr(() => (string)it.Subject),
            BodyHtml = TryStr(() => (string)it.HTMLBody),
            To = MapRecipients(it, OL.To),
            Cc = MapRecipients(it, OL.CC),
            Bcc = MapRecipients(it, OL.BCC),
        };
        try { m.Importance = MapImportance((int)it.Importance); } catch { }
        m.Attachments = MapAttachments(it);
        return m;
    });

    // =====================================================================================
    // Directory
    // =====================================================================================

    public Task<IReadOnlyList<ResolvedName>> ResolveNamesAsync(string query, bool directoryOnly = false, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        var list = new List<ResolvedName>();
        if (string.IsNullOrWhiteSpace(query))
        {
            // Browse the Global Address List (capped).
            try
            {
                dynamic gal = _ns!.GetGlobalAddressList();
                dynamic entries = gal.AddressEntries;
                int n = (int)entries.Count;
                for (int i = 1; i <= n && list.Count < 200; i++)
                {
                    var rn = MapAddressEntry(entries[i]);
                    if (rn != null) list.Add(rn);
                }
            }
            catch { }
            return (IReadOnlyList<ResolvedName>)list;
        }

        try
        {
            dynamic recip = _ns!.CreateRecipient(query);
            recip.Resolve();
            if ((bool)recip.Resolved)
            {
                var rn = MapAddressEntry(recip.AddressEntry);
                if (rn != null) list.Add(rn);
            }
        }
        catch { }

        if (list.Count == 0)
        {
            // Partial matches from the GAL.
            try
            {
                dynamic gal = _ns!.GetGlobalAddressList();
                dynamic entries = gal.AddressEntries;
                int n = (int)entries.Count;
                for (int i = 1; i <= n && list.Count < 50; i++)
                {
                    dynamic e = entries[i];
                    string nm = TryStr(() => (string)e.Name);
                    if (nm.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        var rn = MapAddressEntry(e);
                        if (rn != null) list.Add(rn);
                    }
                }
            }
            catch { }
        }
        return (IReadOnlyList<ResolvedName>)list;
    });

    private ResolvedName? MapAddressEntry(dynamic e)
    {
        try
        {
            string name = TryStr(() => (string)e.Name);
            string smtp = SmtpOfAddressEntry(e);
            var rn = new ResolvedName { Name = name, Email = smtp, Kind = RecipientKind.Mailbox };
            var ex = Try(() => (object?)e.GetExchangeUser());
            if (ex != null)
            {
                dynamic u = ex;
                rn.Title = TryStr(() => (string)u.JobTitle);
                rn.Department = TryStr(() => (string)u.Department);
                rn.Company = TryStr(() => (string)u.CompanyName);
                rn.Office = TryStr(() => (string)u.OfficeLocation);
                rn.Phone = TryStr(() => (string)u.BusinessTelephoneNumber);
                rn.Mobile = TryStr(() => (string)u.MobileTelephoneNumber);
                rn.Alias = TryStr(() => (string)u.Alias);
            }
            else
            {
                try { if ((int)e.AddressEntryUserType is 1 or 2) rn.Kind = RecipientKind.DistributionList; } catch { }
            }
            if (string.IsNullOrEmpty(rn.Email)) return null;
            return rn;
        }
        catch { return null; }
    }

    public Task<IReadOnlyList<Recipient>> ExpandGroupAsync(string email, CancellationToken ct = default) => Com(() =>
    {
        Ensure();
        var list = new List<Recipient>();
        try
        {
            dynamic recip = _ns!.CreateRecipient(email);
            recip.Resolve();
            if ((bool)recip.Resolved)
            {
                var dl = Try(() => (object?)recip.AddressEntry.GetExchangeDistributionList());
                if (dl != null)
                {
                    dynamic members = ((dynamic)dl).GetExchangeDistributionListMembers();
                    if (members != null)
                    {
                        int n = (int)members.Count;
                        for (int i = 1; i <= n; i++)
                        {
                            dynamic m = members[i];
                            list.Add(new Recipient(TryStr(() => (string)m.Name), SmtpOfAddressEntry(m)));
                        }
                    }
                }
                else
                {
                    dynamic ae = recip.AddressEntry;
                    var mem = Try(() => (object?)ae.Members);
                    if (mem != null)
                    {
                        dynamic members = mem;
                        int n = (int)members.Count;
                        for (int i = 1; i <= n; i++)
                        {
                            dynamic m = members[i];
                            list.Add(new Recipient(TryStr(() => (string)m.Name), SmtpOfAddressEntry(m)));
                        }
                    }
                }
            }
        }
        catch { }
        return (IReadOnlyList<Recipient>)list;
    });

    public Task<byte[]?> GetUserPhotoAsync(string email, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    public void Dispose()
    {
        try { _sta.Dispose(); } catch { }
    }
}
