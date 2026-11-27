using System.Net;
using System.Text.RegularExpressions;
using Posta.Core.Models;
using Posta.Core.Services;
using Ews = Microsoft.Exchange.WebServices.Data;

namespace Posta.Exchange;

/// <summary>
/// Exchange Web Services implementation (Exchange 2013 / 2016 / 2019, on-premises).
/// </summary>
public sealed partial class EwsMailService : IMailService
{
    private Ews.ExchangeService? _service;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly Dictionary<string, FolderKind> _wellKnownIds = new();
    private string? _calendarFolderId;
    private string? _contactsFolderId;
    private string? _tasksFolderId;
    private string? _inboxFolderId;

    private static readonly Ews.ExtendedPropertyDefinition PidTagAttributeHidden = new(0x10F4, Ews.MapiPropertyType.Boolean);
    private static readonly Ews.ExtendedPropertyDefinition PidTagFlagStatus = new(0x1090, Ews.MapiPropertyType.Integer);
    private static readonly Ews.ExtendedPropertyDefinition PidTagDeferredSendTime = new(0x3FEF, Ews.MapiPropertyType.SystemTime);

    public EwsMailService(AccountConfig account) => Account = account;

    public AccountConfig Account { get; }
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string? LastError { get; private set; }
    public string? ServerVersion { get; private set; }
    public string? UserDisplayName { get; private set; }
    public event EventHandler? StateChanged;

    private Ews.ExchangeService Svc => _service ?? throw new MailServiceException("Account is not connected.");

    private void SetState(ConnectionState s, string? error = null)
    {
        State = s;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // =====================================================================================
    // Connection
    // =====================================================================================

    public async Task ConnectAsync(string? password, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            SetState(ConnectionState.Connecting);
            var svc = new Ews.ExchangeService(Ews.ExchangeVersion.Exchange2013_SP1)
            {
                UserAgent = "Posta/1.0 (EWS)",
                Timeout = 100_000,
                AcceptGzipEncoding = true,
                PreferredCulture = System.Globalization.CultureInfo.CurrentUICulture
            };

            if (Account.UseWindowsCredentials)
            {
                svc.UseDefaultCredentials = true;
            }
            else
            {
                if (string.IsNullOrEmpty(password))
                {
                    SetState(ConnectionState.NeedsPassword, "Password required");
                    throw new MailServiceException("Password required");
                }
                var user = Account.EffectiveUser;
                var domain = Account.Domain;
                if (string.IsNullOrEmpty(domain) && user.Contains('\\'))
                {
                    var parts = user.Split('\\', 2);
                    domain = parts[0];
                    user = parts[1];
                }
                svc.Credentials = string.IsNullOrEmpty(domain)
                    ? new Ews.WebCredentials(user, password)
                    : new Ews.WebCredentials(user, password, domain);
            }

            if (!string.IsNullOrWhiteSpace(Account.ServerUrl))
            {
                svc.Url = NormalizeEwsUrl(Account.ServerUrl);
            }
            else
            {
                svc.Url = await AutodiscoverAsync(svc, Account.Email, ct);
                Account.ServerUrl = svc.Url.ToString();
            }

            // Verify connection & cache well-known folder ids.
            await LoadWellKnownFoldersAsync(svc);
            _service = svc;

            var info = svc.ServerInfo;
            if (info != null) ServerVersion = $"{info.MajorVersion}.{info.MinorVersion}.{info.MajorBuildNumber}.{info.MinorBuildNumber}";

            try
            {
                var res = await svc.ResolveName(Account.Email, Ews.ResolveNameSearchLocation.DirectoryOnly, false);
                var first = res.FirstOrDefault();
                if (first?.Mailbox != null) UserDisplayName = first.Mailbox.Name;
            }
            catch { /* optional */ }

            if (string.IsNullOrWhiteSpace(Account.DisplayName)) Account.DisplayName = UserDisplayName ?? Account.Email;
            SetState(ConnectionState.Connected);
        }
        catch (MailServiceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var msg = Describe(ex);
            var unauthorized = ex is Ews.ServiceRequestException sre && sre.Message.Contains("401");
            SetState(unauthorized ? ConnectionState.NeedsPassword : ConnectionState.Error, msg);
            throw new MailServiceException(msg, ex);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private static Uri NormalizeEwsUrl(string url)
    {
        url = url.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        var uri = new Uri(url);
        if (uri.AbsolutePath is "/" or "")
            uri = new Uri(uri, "/EWS/Exchange.asmx");
        return uri;
    }

    private static async Task<Uri> AutodiscoverAsync(Ews.ExchangeService svc, string email, CancellationToken ct)
    {
        Exception? last = null;
        try
        {
            await Task.Run(() => svc.AutodiscoverUrl(email, u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)), ct);
            if (svc.Url != null) return svc.Url;
        }
        catch (Exception ex) { last = ex; }

        // Fallback: probe the most common host names for the e-mail domain.
        var domain = email.Contains('@') ? email[(email.IndexOf('@') + 1)..] : email;
        foreach (var host in new[] { "mail." + domain, "owa." + domain, "exchange." + domain, "webmail." + domain, domain })
        {
            var uri = new Uri($"https://{host}/EWS/Exchange.asmx");
            try
            {
                svc.Url = uri;
                await Ews.Folder.Bind(svc, Ews.WellKnownFolderName.Inbox, Ews.PropertySet.IdOnly);
                return uri;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new MailServiceException("Autodiscover failed. Enter the server address manually. " + (last != null ? Describe(last) : ""), last);
    }

    private async Task LoadWellKnownFoldersAsync(Ews.ExchangeService svc)
    {
        _wellKnownIds.Clear();
        var map = new (Ews.WellKnownFolderName name, FolderKind kind)[]
        {
            (Ews.WellKnownFolderName.Inbox, FolderKind.Inbox),
            (Ews.WellKnownFolderName.Drafts, FolderKind.Drafts),
            (Ews.WellKnownFolderName.SentItems, FolderKind.SentItems),
            (Ews.WellKnownFolderName.DeletedItems, FolderKind.DeletedItems),
            (Ews.WellKnownFolderName.JunkEmail, FolderKind.JunkEmail),
            (Ews.WellKnownFolderName.Outbox, FolderKind.Outbox),
            (Ews.WellKnownFolderName.Calendar, FolderKind.Calendar),
            (Ews.WellKnownFolderName.Contacts, FolderKind.Contacts),
            (Ews.WellKnownFolderName.Tasks, FolderKind.Tasks),
            (Ews.WellKnownFolderName.Notes, FolderKind.Notes),
            (Ews.WellKnownFolderName.Journal, FolderKind.Journal),
            (Ews.WellKnownFolderName.ConversationHistory, FolderKind.ConversationHistory),
            (Ews.WellKnownFolderName.SyncIssues, FolderKind.SyncIssues),
            (Ews.WellKnownFolderName.MsgFolderRoot, FolderKind.Root),
        };
        var ids = map.Select(m => new Ews.FolderId(m.name)).ToList();
        var responses = await svc.BindToFolders(ids, Ews.PropertySet.IdOnly);
        int i = 0;
        foreach (var r in responses)
        {
            var kind = map[i++].kind;
            if (r.Result != Ews.ServiceResult.Success || r.Folder == null) continue;
            _wellKnownIds[r.Folder.Id.UniqueId] = kind;
            switch (kind)
            {
                case FolderKind.Calendar: _calendarFolderId = r.Folder.Id.UniqueId; break;
                case FolderKind.Contacts: _contactsFolderId = r.Folder.Id.UniqueId; break;
                case FolderKind.Tasks: _tasksFolderId = r.Folder.Id.UniqueId; break;
                case FolderKind.Inbox: _inboxFolderId = r.Folder.Id.UniqueId; break;
            }
        }
        if (_inboxFolderId == null) throw new MailServiceException("Unable to open the mailbox (Inbox).");
    }

    internal static string Describe(Exception ex)
    {
        return ex switch
        {
            Ews.ServiceResponseException sre => $"{sre.Message} ({sre.ErrorCode})",
            Ews.ServiceRequestException req when req.InnerException != null => req.Message + " → " + req.InnerException.Message,
            Ews.AutodiscoverLocalException ad => ad.Message,
            HttpRequestException http => http.Message,
            WebException web => web.Message,
            _ => ex.InnerException != null ? ex.Message + " → " + ex.InnerException.Message : ex.Message
        };
    }

    private async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            var r = await action();
            if (State != ConnectionState.Connected) SetState(ConnectionState.Connected);
            return r;
        }
        catch (Ews.ServiceRequestException ex)
        {
            SetState(ConnectionState.Offline, Describe(ex));
            throw new MailServiceException(Describe(ex), ex);
        }
        catch (HttpRequestException ex)
        {
            SetState(ConnectionState.Offline, Describe(ex));
            throw new MailServiceException(Describe(ex), ex);
        }
        catch (Ews.ServiceResponseException ex)
        {
            throw new MailServiceException(Describe(ex), ex);
        }
    }

    private Task Run(Func<Task> action) => Run(async () => { await action(); return true; });

    private static Ews.FolderId Fid(string id) => new(id);
    private static Ews.ItemId Iid(string id) => new(id);

    private static Ews.FolderId MailboxFolder(Ews.WellKnownFolderName name, string? mailbox) =>
        string.IsNullOrEmpty(mailbox) ? new Ews.FolderId(name) : new Ews.FolderId(name, new Ews.Mailbox(mailbox));

    // =====================================================================================
    // Folders
    // =====================================================================================

    public Task<IReadOnlyList<MailFolderInfo>> GetFolderTreeAsync(string? mailboxEmail = null, CancellationToken ct = default) => Run(async () =>
    {
        var props = new Ews.PropertySet(Ews.BasePropertySet.IdOnly,
            Ews.FolderSchema.DisplayName, Ews.FolderSchema.ParentFolderId, Ews.FolderSchema.FolderClass,
            Ews.FolderSchema.ChildFolderCount, Ews.FolderSchema.UnreadCount, Ews.FolderSchema.TotalCount, PidTagAttributeHidden);

        var rootId = MailboxFolder(Ews.WellKnownFolderName.MsgFolderRoot, mailboxEmail);
        var root = await Ews.Folder.Bind(Svc, rootId, new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.FolderSchema.DisplayName));

        // Well-known folder ids in the target mailbox
        var wk = _wellKnownIds;
        if (!string.IsNullOrEmpty(mailboxEmail))
        {
            wk = new Dictionary<string, FolderKind>();
            var names = new (Ews.WellKnownFolderName, FolderKind)[]
            {
                (Ews.WellKnownFolderName.Inbox, FolderKind.Inbox), (Ews.WellKnownFolderName.Drafts, FolderKind.Drafts),
                (Ews.WellKnownFolderName.SentItems, FolderKind.SentItems), (Ews.WellKnownFolderName.DeletedItems, FolderKind.DeletedItems),
                (Ews.WellKnownFolderName.JunkEmail, FolderKind.JunkEmail), (Ews.WellKnownFolderName.Outbox, FolderKind.Outbox),
                (Ews.WellKnownFolderName.Calendar, FolderKind.Calendar), (Ews.WellKnownFolderName.Contacts, FolderKind.Contacts),
                (Ews.WellKnownFolderName.Tasks, FolderKind.Tasks)
            };
            var resp = await Svc.BindToFolders(names.Select(n => MailboxFolder(n.Item1, mailboxEmail)), Ews.PropertySet.IdOnly);
            int i = 0;
            foreach (var r in resp)
            {
                var kind = names[i++].Item2;
                if (r.Result == Ews.ServiceResult.Success && r.Folder != null) wk[r.Folder.Id.UniqueId] = kind;
            }
        }

        var result = new List<MailFolderInfo>();
        int offset = 0;
        while (true)
        {
            var view = new Ews.FolderView(500, offset) { Traversal = Ews.FolderTraversal.Deep, PropertySet = props };
            var found = await Svc.FindFolders(new Ews.FolderId(root.Id.UniqueId), view);
            foreach (var f in found.Folders)
            {
                if (f.TryGetProperty(PidTagAttributeHidden, out bool hidden) && hidden) continue;
                var folderClass = f.FolderClass ?? "IPF.Note";
                // Skip internal system folders not shown by the desktop client
                if (folderClass.StartsWith("IPF.Configuration", StringComparison.OrdinalIgnoreCase) ||
                    folderClass.StartsWith("IPF.Contact.MOC.QuickContacts", StringComparison.OrdinalIgnoreCase) ||
                    folderClass.StartsWith("IPF.Contact.MOC.ImContactList", StringComparison.OrdinalIgnoreCase) ||
                    folderClass.StartsWith("IPF.Contact.GalContacts", StringComparison.OrdinalIgnoreCase) ||
                    folderClass.StartsWith("IPF.Contact.RecipientCache", StringComparison.OrdinalIgnoreCase) ||
                    folderClass.StartsWith("IPF.Files", StringComparison.OrdinalIgnoreCase))
                    continue;

                var id = f.Id.UniqueId;
                var kind = wk.TryGetValue(id, out var k) ? k : FolderKind.Generic;
                if (kind == FolderKind.Generic && (f.DisplayName.Equals("Archive", StringComparison.OrdinalIgnoreCase) || f.DisplayName == "არქივი"))
                    kind = FolderKind.Archive;
                if (kind == FolderKind.Generic && folderClass.Equals("IPF.Note.OutlookHomepage", StringComparison.OrdinalIgnoreCase))
                    kind = FolderKind.RssFeeds;

                var parent = f.ParentFolderId?.UniqueId;
                result.Add(new MailFolderInfo
                {
                    Id = id,
                    ParentId = parent == root.Id.UniqueId ? null : parent,
                    AccountId = Account.Id,
                    MailboxEmail = mailboxEmail,
                    Kind = kind,
                    FolderClass = folderClass,
                    DisplayName = f.DisplayName,
                    ChildFolderCount = f.ChildFolderCount,
                    UnreadCount = SafeInt(() => f.UnreadCount),
                    TotalCount = f.TotalCount
                });
            }
            if (!found.MoreAvailable) break;
            offset = found.NextPageOffset ?? (offset + 500);
        }

        // Remove children of skipped folders
        var ids = new HashSet<string>(result.Select(r => r.Id));
        result.RemoveAll(r => r.ParentId != null && !ids.Contains(r.ParentId));
        return (IReadOnlyList<MailFolderInfo>)result;
    });

    private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }

    public Task<MailFolderInfo?> GetWellKnownFolderAsync(FolderKind kind, string? mailboxEmail = null, CancellationToken ct = default) => Run(async () =>
    {
        var name = kind switch
        {
            FolderKind.Inbox => Ews.WellKnownFolderName.Inbox,
            FolderKind.Drafts => Ews.WellKnownFolderName.Drafts,
            FolderKind.SentItems => Ews.WellKnownFolderName.SentItems,
            FolderKind.DeletedItems => Ews.WellKnownFolderName.DeletedItems,
            FolderKind.JunkEmail => Ews.WellKnownFolderName.JunkEmail,
            FolderKind.Outbox => Ews.WellKnownFolderName.Outbox,
            FolderKind.Calendar => Ews.WellKnownFolderName.Calendar,
            FolderKind.Contacts => Ews.WellKnownFolderName.Contacts,
            FolderKind.Tasks => Ews.WellKnownFolderName.Tasks,
            FolderKind.Notes => Ews.WellKnownFolderName.Notes,
            _ => Ews.WellKnownFolderName.MsgFolderRoot
        };
        try
        {
            var f = await Ews.Folder.Bind(Svc, MailboxFolder(name, mailboxEmail),
                new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.FolderSchema.DisplayName, Ews.FolderSchema.FolderClass, Ews.FolderSchema.TotalCount));
            return (MailFolderInfo?)new MailFolderInfo
            {
                Id = f.Id.UniqueId, AccountId = Account.Id, MailboxEmail = mailboxEmail, Kind = kind,
                DisplayName = f.DisplayName, FolderClass = f.FolderClass ?? "IPF.Note", TotalCount = f.TotalCount
            };
        }
        catch (Ews.ServiceResponseException)
        {
            return null;
        }
    });

    public Task<(int unread, int total)> GetFolderCountsAsync(string folderId, CancellationToken ct = default) => Run(async () =>
    {
        var f = await Ews.Folder.Bind(Svc, Fid(folderId), new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.FolderSchema.TotalCount, Ews.FolderSchema.UnreadCount));
        return (SafeInt(() => f.UnreadCount), f.TotalCount);
    });

    public Task<string> CreateFolderAsync(string parentFolderId, string name, string folderClass, CancellationToken ct = default) => Run(async () =>
    {
        Ews.Folder folder = folderClass switch
        {
            var c when c.StartsWith("IPF.Appointment") => new Ews.CalendarFolder(Svc),
            var c when c.StartsWith("IPF.Contact") => new Ews.ContactsFolder(Svc),
            var c when c.StartsWith("IPF.Task") => new Ews.TasksFolder(Svc),
            _ => new Ews.Folder(Svc) { FolderClass = "IPF.Note" }
        };
        folder.DisplayName = name;
        await folder.Save(Fid(parentFolderId));
        return folder.Id.UniqueId;
    });

    public Task RenameFolderAsync(string folderId, string newName, CancellationToken ct = default) => Run(async () =>
    {
        var f = await Ews.Folder.Bind(Svc, Fid(folderId), new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.FolderSchema.DisplayName));
        f.DisplayName = newName;
        await f.Update();
    });

    public Task DeleteFolderAsync(string folderId, bool permanent, CancellationToken ct = default) => Run(async () =>
    {
        var f = await Ews.Folder.Bind(Svc, Fid(folderId), Ews.PropertySet.IdOnly);
        await f.Delete(permanent ? Ews.DeleteMode.HardDelete : Ews.DeleteMode.MoveToDeletedItems);
    });

    public Task MoveFolderAsync(string folderId, string newParentId, CancellationToken ct = default) => Run(async () =>
    {
        var f = await Ews.Folder.Bind(Svc, Fid(folderId), Ews.PropertySet.IdOnly);
        await f.Move(Fid(newParentId));
    });

    public Task CopyFolderAsync(string folderId, string newParentId, CancellationToken ct = default) => Run(async () =>
    {
        var f = await Ews.Folder.Bind(Svc, Fid(folderId), Ews.PropertySet.IdOnly);
        await f.Copy(Fid(newParentId));
    });

    public Task MarkAllReadAsync(string folderId, CancellationToken ct = default) => Run(async () =>
    {
        var f = await Ews.Folder.Bind(Svc, Fid(folderId), Ews.PropertySet.IdOnly);
        await f.MarkAllItemsAsRead(true);
    });

    public Task EmptyFolderAsync(string folderId, bool deleteSubFolders, CancellationToken ct = default) => Run(async () =>
    {
        var f = await Ews.Folder.Bind(Svc, Fid(folderId), Ews.PropertySet.IdOnly);
        try
        {
            await f.Empty(Ews.DeleteMode.HardDelete, deleteSubFolders);
        }
        catch (Ews.ServiceResponseException)
        {
            // Empty is not supported on some folders – fall back to deleting items page by page.
            while (true)
            {
                var items = await f.FindItems(new Ews.ItemView(200) { PropertySet = Ews.PropertySet.IdOnly });
                if (!items.Items.Any()) break;
                await Svc.DeleteItems(items.Items.Select(i => i.Id), Ews.DeleteMode.HardDelete, Ews.SendCancellationsMode.SendToNone, Ews.AffectedTaskOccurrence.AllOccurrences);
                if (!items.MoreAvailable) break;
            }
        }
    });

    // =====================================================================================
    // Mail
    // =====================================================================================

    private static readonly Ews.PropertySet SummaryProps = new(Ews.BasePropertySet.IdOnly,
        Ews.ItemSchema.Subject, Ews.ItemSchema.DateTimeReceived, Ews.ItemSchema.DateTimeSent, Ews.ItemSchema.DisplayTo,
        Ews.ItemSchema.HasAttachments, Ews.ItemSchema.Importance, Ews.ItemSchema.Size, Ews.ItemSchema.ItemClass,
        Ews.ItemSchema.Categories, Ews.ItemSchema.Flag, Ews.ItemSchema.ConversationId, Ews.ItemSchema.Preview,
        Ews.ItemSchema.IsDraft, Ews.ItemSchema.ParentFolderId, Ews.EmailMessageSchema.From, Ews.EmailMessageSchema.IsRead);

    private MailItemSummary ToSummary(Ews.Item item, string folderId)
    {
        var s = new MailItemSummary
        {
            Id = item.Id.UniqueId,
            AccountId = Account.Id,
            FolderId = item.TryGetProperty(Ews.ItemSchema.ParentFolderId, out Ews.FolderId pf) && pf != null ? pf.UniqueId : folderId,
            ItemClass = TryGet(item, Ews.ItemSchema.ItemClass, "IPM.Note"),
            Subject = TryGet(item, Ews.ItemSchema.Subject, ""),
            Received = TryGet(item, Ews.ItemSchema.DateTimeReceived, DateTime.MinValue),
            Sent = TryGet(item, Ews.ItemSchema.DateTimeSent, DateTime.MinValue),
            DisplayTo = TryGet(item, Ews.ItemSchema.DisplayTo, ""),
            HasAttachments = TryGet(item, Ews.ItemSchema.HasAttachments, false),
            Size = TryGet(item, Ews.ItemSchema.Size, 0),
            Importance = MapImportance(TryGet(item, Ews.ItemSchema.Importance, Ews.Importance.Normal)),
            Preview = TryGet(item, Ews.ItemSchema.Preview, "") ?? "",
            IsDraft = TryGet(item, Ews.ItemSchema.IsDraft, false),
            IsRead = true
        };
        if (item.TryGetProperty(Ews.ItemSchema.ConversationId, out Ews.ConversationId conv) && conv != null) s.ConversationId = conv.UniqueId;
        if (item.TryGetProperty(Ews.ItemSchema.Categories, out Ews.StringList cats) && cats != null) s.Categories = cats.ToList();
        if (item.TryGetProperty(Ews.ItemSchema.Flag, out Ews.Flag flag) && flag != null)
        {
            s.Flag = flag.FlagStatus switch { Ews.ItemFlagStatus.Flagged => FlagState.Flagged, Ews.ItemFlagStatus.Complete => FlagState.Complete, _ => FlagState.NotFlagged };
            if (s.Flag == FlagState.Flagged) { try { s.FlagDue = flag.DueDate; } catch { } }
        }
        if (item.TryGetProperty(Ews.EmailMessageSchema.From, out Ews.EmailAddress from) && from != null)
            s.From = new Recipient(from.Name ?? "", from.Address ?? "");
        if (item.TryGetProperty(Ews.EmailMessageSchema.IsRead, out bool isRead)) s.IsRead = isRead;
        if (s.Received == DateTime.MinValue) s.Received = s.Sent;
        return s;
    }

    private static T TryGet<T>(Ews.ServiceObject o, Ews.PropertyDefinitionBase def, T fallback)
    {
        try
        {
            if (o.TryGetProperty(def, out T value) && value != null) return value;
        }
        catch { }
        return fallback;
    }

    private static Importance MapImportance(Ews.Importance i) => i switch { Ews.Importance.High => Importance.High, Ews.Importance.Low => Importance.Low, _ => Importance.Normal };
    private static Ews.Importance MapImportance(Importance i) => i switch { Importance.High => Ews.Importance.High, Importance.Low => Ews.Importance.Low, _ => Ews.Importance.Normal };
    private static Sensitivity MapSensitivity(Ews.Sensitivity s) => s switch { Ews.Sensitivity.Personal => Sensitivity.Personal, Ews.Sensitivity.Private => Sensitivity.Private, Ews.Sensitivity.Confidential => Sensitivity.Confidential, _ => Sensitivity.Normal };
    private static Ews.Sensitivity MapSensitivity(Sensitivity s) => s switch { Sensitivity.Personal => Ews.Sensitivity.Personal, Sensitivity.Private => Ews.Sensitivity.Private, Sensitivity.Confidential => Ews.Sensitivity.Confidential, _ => Ews.Sensitivity.Normal };

    public Task<Paged<MailItemSummary>> GetItemsAsync(MailQuery q, CancellationToken ct = default) => Run(async () =>
    {
        var view = new Ews.ItemView(q.PageSize, q.Offset, Ews.OffsetBasePoint.Beginning) { PropertySet = SummaryProps };
        var dir = q.Descending ? Ews.SortDirection.Descending : Ews.SortDirection.Ascending;
        switch (q.Sort)
        {
            case SortField.From: view.OrderBy.Add(Ews.EmailMessageSchema.From, dir); break;
            case SortField.To: view.OrderBy.Add(Ews.ItemSchema.DisplayTo, dir); break;
            case SortField.Subject: view.OrderBy.Add(Ews.ItemSchema.Subject, dir); break;
            case SortField.Size: view.OrderBy.Add(Ews.ItemSchema.Size, dir); break;
            case SortField.Importance: view.OrderBy.Add(Ews.ItemSchema.Importance, dir); break;
            case SortField.Attachments: view.OrderBy.Add(Ews.ItemSchema.HasAttachments, dir); break;
            case SortField.Flag: view.OrderBy.Add(PidTagFlagStatus, dir); break;
            default: view.OrderBy.Add(Ews.ItemSchema.DateTimeReceived, dir); break;
        }
        if (q.Sort != SortField.Date) view.OrderBy.Add(Ews.ItemSchema.DateTimeReceived, Ews.SortDirection.Descending);

        Ews.FindItemsResults<Ews.Item> found;
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var aqs = q.Search!.Trim();
            aqs += q.Filter switch
            {
                MailFilter.Unread => " isread:false",
                MailFilter.HasAttachments => " hasattachment:true",
                MailFilter.Important => " importance:high",
                MailFilter.Flagged => " isflagged:true",
                _ => ""
            };
            found = await Svc.FindItems(Fid(q.FolderId), aqs, view);
        }
        else
        {
            var filter = BuildFilter(q.Filter);
            found = filter == null ? await Svc.FindItems(Fid(q.FolderId), view) : await Svc.FindItems(Fid(q.FolderId), filter, view);
        }

        return new Paged<MailItemSummary>
        {
            Items = found.Items.Select(i => ToSummary(i, q.FolderId)).ToList(),
            TotalCount = found.TotalCount,
            MoreAvailable = found.MoreAvailable
        };
    });

    private static Ews.SearchFilter? BuildFilter(MailFilter f)
    {
        switch (f)
        {
            case MailFilter.Unread: return new Ews.SearchFilter.IsEqualTo(Ews.EmailMessageSchema.IsRead, false);
            case MailFilter.HasAttachments: return new Ews.SearchFilter.IsEqualTo(Ews.ItemSchema.HasAttachments, true);
            case MailFilter.Important: return new Ews.SearchFilter.IsEqualTo(Ews.ItemSchema.Importance, Ews.Importance.High);
            case MailFilter.Flagged: return new Ews.SearchFilter.IsEqualTo(PidTagFlagStatus, 2);
            case MailFilter.ThisWeek:
                var today = DateTime.Today;
                var start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                return new Ews.SearchFilter.IsGreaterThanOrEqualTo(Ews.ItemSchema.DateTimeReceived, start);
            default: return null;
        }
    }

    public Task<MailMessageDetail> GetMessageAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        var props = new Ews.PropertySet(Ews.BasePropertySet.FirstClassProperties, Ews.ItemSchema.ParentFolderId) { RequestedBodyType = Ews.BodyType.HTML };
        var item = await Ews.Item.Bind(Svc, Iid(itemId), props);

        var d = new MailMessageDetail
        {
            Id = item.Id.UniqueId,
            AccountId = Account.Id,
            FolderId = TryGet<Ews.FolderId?>(item, Ews.ItemSchema.ParentFolderId, null)?.UniqueId ?? "",
            ItemClass = TryGet(item, Ews.ItemSchema.ItemClass, "IPM.Note"),
            Subject = TryGet(item, Ews.ItemSchema.Subject, ""),
            Received = TryGet(item, Ews.ItemSchema.DateTimeReceived, DateTime.MinValue),
            Sent = TryGet(item, Ews.ItemSchema.DateTimeSent, DateTime.MinValue),
            Importance = MapImportance(TryGet(item, Ews.ItemSchema.Importance, Ews.Importance.Normal)),
            Sensitivity = MapSensitivity(TryGet(item, Ews.ItemSchema.Sensitivity, Ews.Sensitivity.Normal)),
            IsDraft = TryGet(item, Ews.ItemSchema.IsDraft, false),
            IsRead = true
        };
        if (item.TryGetProperty(Ews.ItemSchema.Categories, out Ews.StringList cats) && cats != null) d.Categories = cats.ToList();
        if (item.TryGetProperty(Ews.ItemSchema.Flag, out Ews.Flag flag) && flag != null)
            d.Flag = flag.FlagStatus switch { Ews.ItemFlagStatus.Flagged => FlagState.Flagged, Ews.ItemFlagStatus.Complete => FlagState.Complete, _ => FlagState.NotFlagged };

        if (item is Ews.EmailMessage em)
        {
            d.From = ToRecipient(TryGet<Ews.EmailAddress?>(em, Ews.EmailMessageSchema.From, null));
            var sender = TryGet<Ews.EmailAddress?>(em, Ews.EmailMessageSchema.Sender, null);
            if (sender != null && !string.Equals(sender.Address, d.From.Address, StringComparison.OrdinalIgnoreCase)) d.Sender = ToRecipient(sender);
            d.To = ToRecipients(TryGet<Ews.EmailAddressCollection?>(em, Ews.EmailMessageSchema.ToRecipients, null));
            d.Cc = ToRecipients(TryGet<Ews.EmailAddressCollection?>(em, Ews.EmailMessageSchema.CcRecipients, null));
            d.Bcc = ToRecipients(TryGet<Ews.EmailAddressCollection?>(em, Ews.EmailMessageSchema.BccRecipients, null));
            d.ReplyTo = ToRecipients(TryGet<Ews.EmailAddressCollection?>(em, Ews.EmailMessageSchema.ReplyTo, null));
            d.IsRead = TryGet(em, Ews.EmailMessageSchema.IsRead, true);
            d.InternetMessageId = TryGet<string?>(em, Ews.EmailMessageSchema.InternetMessageId, null);
            d.ReadReceiptRequested = TryGet(em, Ews.EmailMessageSchema.IsReadReceiptRequested, false);
        }

        if (item is Ews.MeetingRequest mr)
        {
            d.MeetingStart = TryGet<DateTime?>(mr, Ews.MeetingRequestSchema.Start, null);
            d.MeetingEnd = TryGet<DateTime?>(mr, Ews.MeetingRequestSchema.End, null);
            d.MeetingLocation = TryGet<string?>(mr, Ews.MeetingRequestSchema.Location, null);
            d.MyResponse = MapResponse(TryGet(mr, Ews.MeetingRequestSchema.MyResponseType, Ews.MeetingResponseType.Unknown));
            d.MeetingIsOutOfDate = TryGet(mr, Ews.MeetingMessageSchema.IsOutOfDate, false);
            d.AssociatedAppointmentId = TryGet<Ews.ItemId?>(mr, Ews.MeetingMessageSchema.AssociatedAppointmentId, null)?.UniqueId;
        }
        else if (item is Ews.MeetingMessage mm)
        {
            d.MeetingStart = TryGet<DateTime?>(mm, Ews.AppointmentSchema.Start, null);
            d.MeetingEnd = TryGet<DateTime?>(mm, Ews.AppointmentSchema.End, null);
            d.MeetingLocation = TryGet<string?>(mm, Ews.AppointmentSchema.Location, null);
        }

        var body = TryGet<Ews.MessageBody?>(item, Ews.ItemSchema.Body, null);
        string html = body?.Text ?? "";
        if (body != null && body.BodyType == Ews.BodyType.Text) html = HtmlUtil.PlainTextToHtml(html);

        foreach (var a in item.Attachments)
        {
            var info = new AttachmentInfo
            {
                Id = a.Id, Name = a.Name ?? "attachment", Size = a.Size, ContentType = a.ContentType ?? "application/octet-stream",
                IsInline = a.IsInline, ContentId = a.ContentId, IsItemAttachment = a is Ews.ItemAttachment
            };
            if (a is Ews.ItemAttachment && !info.Name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)) info.Name += ".eml";

            if (a is Ews.FileAttachment fa && !string.IsNullOrEmpty(a.ContentId) &&
                html.Contains("cid:" + a.ContentId, StringComparison.OrdinalIgnoreCase) && a.Size < 8 * 1024 * 1024)
            {
                try
                {
                    await fa.Load();
                    var dataUri = $"data:{(string.IsNullOrEmpty(fa.ContentType) ? "image/png" : fa.ContentType)};base64,{Convert.ToBase64String(fa.Content)}";
                    html = Regex.Replace(html, "cid:" + Regex.Escape(a.ContentId), dataUri, RegexOptions.IgnoreCase);
                    info.IsInline = true;
                }
                catch { }
            }
            else if (a.IsInline && string.IsNullOrEmpty(a.ContentId))
            {
                info.IsInline = false;
            }
            d.Attachments.Add(info);
        }
        d.BodyHtml = html;
        return d;
    });

    private static Recipient ToRecipient(Ews.EmailAddress? a) => a == null ? new Recipient() : new Recipient(a.Name ?? "", string.IsNullOrEmpty(a.Address) ? a.Name ?? "" : a.Address);
    private static List<Recipient> ToRecipients(IEnumerable<Ews.EmailAddress>? list) => list?.Select(ToRecipient).ToList() ?? new List<Recipient>();

    private static MeetingResponse MapResponse(Ews.MeetingResponseType t) => t switch
    {
        Ews.MeetingResponseType.Accept => MeetingResponse.Accept,
        Ews.MeetingResponseType.Decline => MeetingResponse.Decline,
        Ews.MeetingResponseType.Tentative => MeetingResponse.Tentative,
        Ews.MeetingResponseType.Organizer => MeetingResponse.Organizer,
        Ews.MeetingResponseType.NoResponseReceived => MeetingResponse.NoResponseReceived,
        _ => MeetingResponse.Unknown
    };

    public Task<byte[]> GetAttachmentContentAsync(string itemId, string attachmentId, CancellationToken ct = default) => Run(async () =>
    {
        var item = await Ews.Item.Bind(Svc, Iid(itemId), new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.ItemSchema.Attachments));
        var a = item.Attachments.FirstOrDefault(x => x.Id == attachmentId) ?? throw new MailServiceException("Attachment not found");
        if (a is Ews.FileAttachment fa)
        {
            await fa.Load();
            return fa.Content;
        }
        if (a is Ews.ItemAttachment ia)
        {
            await ia.Load(Ews.ItemSchema.MimeContent);
            return ia.Item.MimeContent.Content;
        }
        throw new MailServiceException("Unsupported attachment type");
    });

    public Task<byte[]> GetMimeContentAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        var item = await Ews.Item.Bind(Svc, Iid(itemId), new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.ItemSchema.MimeContent));
        return item.MimeContent.Content;
    });

    private async Task<List<Ews.Item>> BindItems(IEnumerable<string> ids, Ews.PropertySet props)
    {
        var list = new List<Ews.Item>();
        foreach (var chunk in ids.Chunk(100))
        {
            var resp = await Svc.BindToItems(chunk.Select(Iid), props);
            list.AddRange(resp.Where(r => r.Result == Ews.ServiceResult.Success && r.Item != null).Select(r => r.Item));
        }
        return list;
    }

    public Task SetReadAsync(IEnumerable<string> itemIds, bool isRead, CancellationToken ct = default) => Run(async () =>
    {
        var items = await BindItems(itemIds, new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.EmailMessageSchema.IsRead));
        var msgs = items.OfType<Ews.EmailMessage>().ToList();
        foreach (var m in msgs) m.IsRead = isRead;
        if (msgs.Count > 0)
            await Svc.UpdateItems(msgs, null, Ews.ConflictResolutionMode.AutoResolve, null, null, true);
    });

    public Task SetFlagAsync(IEnumerable<string> itemIds, FlagState state, DateTime? start, DateTime? due, CancellationToken ct = default) => Run(async () =>
    {
        var items = await BindItems(itemIds, new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.ItemSchema.Flag));
        foreach (var item in items)
        {
            var flag = new Ews.Flag();
            switch (state)
            {
                case FlagState.Flagged:
                    flag.FlagStatus = Ews.ItemFlagStatus.Flagged;
                    if (start.HasValue && due.HasValue) { flag.StartDate = start.Value; flag.DueDate = due.Value; }
                    break;
                case FlagState.Complete:
                    flag.FlagStatus = Ews.ItemFlagStatus.Complete;
                    flag.CompleteDate = DateTime.Now;
                    break;
                default:
                    flag.FlagStatus = Ews.ItemFlagStatus.NotFlagged;
                    break;
            }
            item.Flag = flag;
        }
        if (items.Count > 0) await Svc.UpdateItems(items, null, Ews.ConflictResolutionMode.AutoResolve, null, null, true);
    });

    public Task SetCategoriesAsync(string itemId, IEnumerable<string> categories, CancellationToken ct = default) => Run(async () =>
    {
        var item = await Ews.Item.Bind(Svc, Iid(itemId), new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.ItemSchema.Categories));
        item.Categories = new Ews.StringList(categories);
        await UpdateItem(item);
    });

    public Task SetImportanceAsync(string itemId, Importance importance, CancellationToken ct = default) => Run(async () =>
    {
        var item = await Ews.Item.Bind(Svc, Iid(itemId), new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.ItemSchema.Importance));
        item.Importance = MapImportance(importance);
        await UpdateItem(item);
    });

    private static async Task UpdateItem(Ews.Item item)
    {
        if (item is Ews.Appointment ap)
            await ap.Update(Ews.ConflictResolutionMode.AutoResolve, Ews.SendInvitationsOrCancellationsMode.SendToNone);
        else
            await item.Update(Ews.ConflictResolutionMode.AutoResolve, true);
    }

    public Task DeleteItemsAsync(IEnumerable<string> itemIds, bool permanent, CancellationToken ct = default) => Run(async () =>
    {
        foreach (var chunk in itemIds.Chunk(100))
            await Svc.DeleteItems(chunk.Select(Iid), permanent ? Ews.DeleteMode.SoftDelete : Ews.DeleteMode.MoveToDeletedItems,
                Ews.SendCancellationsMode.SendToNone, Ews.AffectedTaskOccurrence.AllOccurrences);
    });

    public Task MoveItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default) => Run(async () =>
    {
        foreach (var chunk in itemIds.Chunk(100))
            await Svc.MoveItems(chunk.Select(Iid), Fid(destinationFolderId));
    });

    public Task CopyItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default) => Run(async () =>
    {
        foreach (var chunk in itemIds.Chunk(100))
            await Svc.CopyItems(chunk.Select(Iid), Fid(destinationFolderId));
    });

    public Task MarkAsJunkAsync(IEnumerable<string> itemIds, bool isJunk, CancellationToken ct = default) => Run(async () =>
    {
        try
        {
            await Svc.MarkAsJunk(itemIds.Select(Iid), isJunk, true);
        }
        catch (Ews.ServiceVersionException)
        {
            var dest = isJunk ? Ews.WellKnownFolderName.JunkEmail : Ews.WellKnownFolderName.Inbox;
            await Svc.MoveItems(itemIds.Select(Iid), new Ews.FolderId(dest));
        }
    });

    public Task IgnoreConversationAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        var item = await Ews.Item.Bind(Svc, Iid(itemId), new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.ItemSchema.ConversationId));
        await Svc.EnableAlwaysDeleteItemsInConversations(new[] { item.ConversationId }, true);
    });

    // ---------------------------------------- Sending ----------------------------------------

    [GeneratedRegex(@"src\s*=\s*([""'])data:(image/[a-zA-Z0-9.+-]+);base64,([^""']+)\1", RegexOptions.IgnoreCase)]
    private static partial Regex DataUriImageRx();

    /// <summary>Converts embedded data: URI images to inline (cid:) attachments, the way MIME mail expects them.</summary>
    private static string ExtractInlineImages(string html, List<(string cid, string contentType, byte[] data)> inline)
    {
        int n = 0;
        return DataUriImageRx().Replace(html, m =>
        {
            try
            {
                var bytes = Convert.FromBase64String(m.Groups[3].Value);
                var cid = $"image{++n:000}.{Guid.NewGuid():N}@posta";
                inline.Add((cid, m.Groups[2].Value, bytes));
                return $"src={m.Groups[1].Value}cid:{cid}{m.Groups[1].Value}";
            }
            catch
            {
                return m.Value;
            }
        });
    }

    private async Task<Ews.EmailMessage> PrepareMessageAsync(OutgoingMessage m)
    {
        Ews.EmailMessage msg;
        if (!string.IsNullOrEmpty(m.DraftId))
        {
            msg = await Ews.EmailMessage.Bind(Svc, Iid(m.DraftId), new Ews.PropertySet(Ews.BasePropertySet.FirstClassProperties));
        }
        else if (m.Mode is ComposeMode.Reply or ComposeMode.ReplyAll or ComposeMode.Forward && !string.IsNullOrEmpty(m.SourceItemId))
        {
            // Create a server-side response draft so that conversation threading, "replied/forwarded" state
            // and (for forwards) the original attachments are preserved. The body is replaced with the edited one.
            var original = await Ews.Item.Bind(Svc, Iid(m.SourceItemId!), Ews.PropertySet.IdOnly);
            Ews.ResponseMessage resp = original switch
            {
                Ews.EmailMessage om when m.Mode == ComposeMode.Forward => om.CreateForward(),
                Ews.EmailMessage om => om.CreateReply(m.Mode == ComposeMode.ReplyAll),
                Ews.Appointment ap when m.Mode == ComposeMode.Forward => ap.CreateForward(),
                Ews.Appointment ap => ap.CreateReply(m.Mode == ComposeMode.ReplyAll),
                _ => throw new MailServiceException("This item cannot be replied to.")
            };
            resp.BodyPrefix = new Ews.MessageBody(Ews.BodyType.HTML, "");
            var draft = await resp.Save(Ews.WellKnownFolderName.Drafts);
            msg = await Ews.EmailMessage.Bind(Svc, draft.Id, new Ews.PropertySet(Ews.BasePropertySet.FirstClassProperties));
            m.DraftId = msg.Id.UniqueId;
        }
        else
        {
            msg = new Ews.EmailMessage(Svc);
        }

        msg.Subject = m.Subject;
        msg.ToRecipients.Clear(); foreach (var r in m.To) msg.ToRecipients.Add(new Ews.EmailAddress(r.Name, r.Address));
        msg.CcRecipients.Clear(); foreach (var r in m.Cc) msg.CcRecipients.Add(new Ews.EmailAddress(r.Name, r.Address));
        msg.BccRecipients.Clear(); foreach (var r in m.Bcc) msg.BccRecipients.Add(new Ews.EmailAddress(r.Name, r.Address));
        if (!string.IsNullOrWhiteSpace(m.FromAddress) && !string.Equals(m.FromAddress, Account.Email, StringComparison.OrdinalIgnoreCase))
            msg.From = new Ews.EmailAddress(m.FromAddress);
        msg.Importance = MapImportance(m.Importance);
        msg.Sensitivity = MapSensitivity(m.Sensitivity);
        msg.IsReadReceiptRequested = m.RequestReadReceipt;
        msg.IsDeliveryReceiptRequested = m.RequestDeliveryReceipt;
        if (m.Categories.Count > 0 || msg.Id != null) msg.Categories = new Ews.StringList(m.Categories);
        if (m.DeferredDelivery.HasValue) msg.SetExtendedProperty(PidTagDeferredSendTime, m.DeferredDelivery.Value.ToUniversalTime());

        var inline = new List<(string cid, string contentType, byte[] data)>();
        var html = ExtractInlineImages(m.BodyHtml, inline);
        msg.Body = new Ews.MessageBody(Ews.BodyType.HTML, html);

        // Attachments: remove stale inline images & removed attachments, then add new ones.
        if (msg.Id != null)
        {
            foreach (var a in msg.Attachments.ToList())
            {
                if (a.IsInline || m.RemovedAttachmentIds.Contains(a.Id)) msg.Attachments.Remove(a);
            }
        }
        foreach (var (cid, contentType, data) in inline)
        {
            var fa = msg.Attachments.AddFileAttachment(cid.Split('@')[0] + ExtensionFor(contentType), data);
            fa.ContentId = cid;
            fa.IsInline = true;
            fa.ContentType = contentType;
        }
        foreach (var a in m.Attachments.Where(a => string.IsNullOrEmpty(a.Id) || a.LocalPath != null || a.Content != null))
        {
            if (a.Content != null) msg.Attachments.AddFileAttachment(a.Name, a.Content);
            else if (a.LocalPath != null && File.Exists(a.LocalPath)) msg.Attachments.AddFileAttachment(a.Name, a.LocalPath);
        }
        return msg;
    }

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/svg+xml" => ".svg",
        "image/webp" => ".webp",
        _ => ".png"
    };

    public Task SendAsync(OutgoingMessage m, CancellationToken ct = default) => Run(async () =>
    {
        var msg = await PrepareMessageAsync(m);
        if (msg.Id != null)
        {
            await msg.Update(Ews.ConflictResolutionMode.AlwaysOverwrite);
        }
        if (m.SaveSentCopy)
            await msg.SendAndSaveCopy();
        else
            await msg.Send();
    });

    public Task<string> SaveDraftAsync(OutgoingMessage m, CancellationToken ct = default) => Run(async () =>
    {
        var msg = await PrepareMessageAsync(m);
        if (msg.Id == null)
            await msg.Save(Ews.WellKnownFolderName.Drafts);
        else
            await msg.Update(Ews.ConflictResolutionMode.AlwaysOverwrite);
        m.DraftId = msg.Id!.UniqueId;
        // After saving, the ids of attachments changed; drop the local copies so they are not re-added.
        m.Attachments.RemoveAll(a => a.LocalPath != null || a.Content != null);
        return msg.Id.UniqueId;
    });

    public Task<OutgoingMessage> LoadDraftAsync(string draftId, CancellationToken ct = default) => Run(async () =>
    {
        var d = await GetMessageAsync(draftId, ct);
        return new OutgoingMessage
        {
            AccountId = Account.Id,
            DraftId = d.Id,
            Mode = ComposeMode.EditDraft,
            To = d.To, Cc = d.Cc, Bcc = d.Bcc,
            Subject = d.Subject,
            BodyHtml = d.BodyHtml,
            Importance = d.Importance,
            Sensitivity = d.Sensitivity,
            Categories = d.Categories,
            Attachments = d.Attachments.Where(a => !a.IsInline).ToList()
        };
    });

    public Task<IReadOnlyList<NewMailNotification>> GetNewMailSinceAsync(DateTime sinceUtc, CancellationToken ct = default) => Run(async () =>
    {
        var filter = new Ews.SearchFilter.SearchFilterCollection(Ews.LogicalOperator.And,
            new Ews.SearchFilter.IsGreaterThan(Ews.ItemSchema.DateTimeReceived, sinceUtc.ToLocalTime()),
            new Ews.SearchFilter.IsEqualTo(Ews.EmailMessageSchema.IsRead, false));
        var view = new Ews.ItemView(20) { PropertySet = SummaryProps };
        view.OrderBy.Add(Ews.ItemSchema.DateTimeReceived, Ews.SortDirection.Descending);
        var found = await Svc.FindItems(Ews.WellKnownFolderName.Inbox, filter, view);
        return (IReadOnlyList<NewMailNotification>)found.Items.Select(i =>
        {
            var s = ToSummary(i, _inboxFolderId ?? "");
            return new NewMailNotification { AccountId = Account.Id, ItemId = s.Id, From = s.From.Display, Subject = s.Subject, Preview = s.Preview, Received = s.Received };
        }).ToList();
    });
}
