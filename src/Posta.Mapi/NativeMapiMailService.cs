using System.Globalization;
using System.Net;
using System.Text;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

/// <summary>Experimental direct Exchange adapter. Unsupported operations fail explicitly.</summary>
public sealed partial class NativeMapiMailService(AccountConfig account) : IMailService, IAsyncDisposable
{
    public AccountConfig Account { get; } = account;
    public ConnectionState State { get; private set; }
    public string? LastError { get; private set; }
    public string? ServerVersion { get; private set; }
    public string? UserDisplayName { get; private set; }
    public event EventHandler? StateChanged;
    private MapiHttpSession? _session;
    private MapiLogon? _logon;
    private MapiAddressBook? _addressBook;
    private Uri? _addressBookUri;
    private ICredentials? _credentials;
    private Uri? _discoveryUri;
    private string? _primaryLegacyDn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<MailFolderInfo>? _folders;
    private Dictionary<ulong, FolderKind>? _specialFolders;
    private MapiHttpSession Session => _session ?? throw new MailServiceException("MAPI session is not connected.");
    private MapiLogon Logon => _logon ?? throw new MailServiceException("MAPI mailbox is not logged on.");

    public async Task ConnectAsync(string? password, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            State = ConnectionState.Connecting; StateChanged?.Invoke(this, EventArgs.Empty);
            await CloseSharedCalendarsAsync();
            await CloseSharedMailAsync();
            if (string.IsNullOrWhiteSpace(Account.ServerUrl))
                throw new MailServiceException("MAPI/HTTP: specify the Autodiscover host in Advanced options (for example mail.example.com).");
            var url = Account.ServerUrl.Contains("://", StringComparison.Ordinal) ? Account.ServerUrl : "https://" + Account.ServerUrl;
            var server = new Uri(url.Trim()); MapiHttpSession.ValidateEndpoint(server);
            var discovery = new Uri(server.GetLeftPart(UriPartial.Authority) + "/autodiscover/autodiscover.xml");
            var user = Account.EffectiveUser; var domain = Account.Domain;
            if (user.Contains('\\')) { var parts = user.Split('\\', 2); domain = parts[0]; user = parts[1]; }
            if (!Account.UseWindowsCredentials && string.IsNullOrEmpty(password)) throw new MailServiceException("Password required.");
            ICredentials credentials = Account.UseWindowsCredentials ? CredentialCache.DefaultNetworkCredentials : new NetworkCredential(user, password, domain);
            var endpoint = await MapiHttpSession.DiscoverAsync(discovery, Account.Email, credentials, ct);
            if (!string.Equals(endpoint.MailStore.Host, discovery.Host, StringComparison.OrdinalIgnoreCase))
                throw new MailServiceException($"Autodiscover returned another host ({endpoint.MailStore.Host}). Verify it and enter that server explicitly.");
            if (_session != null) await _session.DisposeAsync();
            _session = new(endpoint.MailStore, credentials);
            var connected = await _session.ConnectAsync(endpoint.LegacyDn, ct);
            _logon = await _session.LogonAsync(endpoint.LegacyDn, ct);
            ServerVersion = connected.ServerVersion; UserDisplayName = connected.DisplayName; _folders = null; _specialFolders = null;
            // Keep the directory endpoint only when it is the same host we authenticated to.
            _credentials = credentials;
            _discoveryUri = discovery;
            _primaryLegacyDn = endpoint.LegacyDn;
            _addressBookUri = endpoint.AddressBook != null && string.Equals(endpoint.AddressBook.Host, endpoint.MailStore.Host, StringComparison.OrdinalIgnoreCase)
                ? endpoint.AddressBook : null;
            if (_addressBook != null) { await _addressBook.DisposeAsync(); _addressBook = null; }
            _directory = null;
            if (string.IsNullOrWhiteSpace(Account.DisplayName)) Account.DisplayName = UserDisplayName;
            LastError = null; State = ConnectionState.Connected;
        }
        catch (Exception ex)
        {
            if (_session != null) { await _session.DisposeAsync(); _session = null; }
            _logon = null; LastError = ex.Message; State = ConnectionState.Error;
            throw new MailServiceException("Native MAPI/HTTP: " + ex.Message, ex);
        }
        finally { StateChanged?.Invoke(this, EventArgs.Empty); _gate.Release(); }
    }

    private async Task<T> Run<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await action(); }
        catch (Exception ex) when (ex is not MailServiceException && ex is not OperationCanceledException)
        {
            if (_session is { IsUsable: false } && State != ConnectionState.Error)
            {
                State = ConnectionState.Error; LastError = ex.Message; StateChanged?.Invoke(this, EventArgs.Empty);
            }
            throw new MailServiceException("Native MAPI/HTTP: " + ex.Message, ex);
        }
        finally { _gate.Release(); }
    }

    // UI navigation can discard a read, but interrupting a request in flight invalidates
    // the server session and its handles. Let a started read finish within the HTTP timeout.
    private Task<T> RunViewRead<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellation) => Run(async () =>
    {
        cancellation.ThrowIfCancellationRequested();
        var result = await read(CancellationToken.None);
        cancellation.ThrowIfCancellationRequested();
        return result;
    }, cancellation);

    private static string FolderId(ulong id) => "mapi-f:" + id.ToString("X16");
    private static ulong ParseFolder(string id) => id.StartsWith("mapi-f:", StringComparison.Ordinal)
        ? ulong.Parse(id[7..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : throw new MailServiceException("Invalid MAPI folder id.");
    private static (ulong folder, ulong message) ParseMessage(string id)
    {
        var parts = id.Split(':');
        if (parts.Length != 3 || parts[0] != "mapi-m") throw new MailServiceException("Invalid MAPI message id.");
        return (ulong.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture), ulong.Parse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static string Text(Dictionary<uint, object?> row, uint tag) => row.GetValueOrDefault(tag) as string ?? "";
    private static uint Number(Dictionary<uint, object?> row, uint tag) => row.GetValueOrDefault(tag) is uint n ? n : 0;
    private static DateTime Date(Dictionary<uint, object?> row, uint tag) => row.GetValueOrDefault(tag) is DateTime d ? d.ToLocalTime() : DateTime.MinValue;
    private FolderKind Kind(ulong id, string folderClass)
    {
        var known = new Dictionary<int, FolderKind> { [3] = FolderKind.Root, [4] = FolderKind.Inbox, [5] = FolderKind.Outbox, [6] = FolderKind.SentItems, [7] = FolderKind.DeletedItems };
        foreach (var (index, kind) in known) if (Logon.FolderIds[index] == id) return kind;
        if (_specialFolders?.TryGetValue(id, out var special) == true) return special;
        return folderClass switch { "IPF.Appointment" => FolderKind.Calendar, "IPF.Contact" => FolderKind.Contacts,
            "IPF.Task" => FolderKind.Tasks, "IPF.StickyNote" => FolderKind.Notes, _ => FolderKind.Generic };
    }

    private async Task<IReadOnlyList<MailFolderInfo>> Folders(CancellationToken ct)
    {
        if (_folders != null) return _folders;
        _specialFolders ??= await Session.ReadSpecialFoldersAsync(Logon, ct);
        var page = await Session.ReadTableAsync(Logon, Logon.FolderIds[3], true, MapiHttpSession.HierarchyColumns, 5000, ct);
        if (page.Total > page.Rows.Count) throw new MailServiceException("This preview supports up to 5000 folders. The hierarchy would be incomplete.");
        var root = FolderId(Logon.FolderIds[3]);
        var folders = new List<MailFolderInfo> { new() { Id = root, AccountId = Account.Id, Kind = FolderKind.Root, DisplayName = Account.Email } };
        foreach (var row in page.Rows)
        {
            if (row.GetValueOrDefault(0x67480014u) is not ulong fid) continue;
            var parent = row.GetValueOrDefault(0x67490014u) is ulong pid ? FolderId(pid) : root;
            var cls = Text(row, 0x3613001f);
            folders.Add(new() { Id = FolderId(fid), ParentId = parent, AccountId = Account.Id,
                DisplayName = Text(row, 0x3001001f), FolderClass = cls, Kind = Kind(fid, cls),
                TotalCount = checked((int)Number(row, 0x36020003)), UnreadCount = checked((int)Number(row, 0x36030003)) });
        }
        foreach (var f in folders) f.ChildFolderCount = folders.Count(c => c.ParentId == f.Id);
        return _folders = folders;
    }

    public Task<IReadOnlyList<MailFolderInfo>> GetFolderTreeAsync(string? mailboxEmail = null, CancellationToken ct = default) => Run(async () =>
    {
        if (!string.IsNullOrEmpty(mailboxEmail) && !string.Equals(mailboxEmail, Account.Email, StringComparison.OrdinalIgnoreCase))
            return await SharedMailFoldersAsync(mailboxEmail, ct);
        return await Folders(ct);
    }, ct);

    private async Task<MailFolderInfo> DefaultFolderAsync(FolderKind kind, CancellationToken ct)
    {
        var folders = await Folders(ct);
        var special = _specialFolders?.FirstOrDefault(p => p.Value == kind).Key;
        return (special is > 0 ? folders.FirstOrDefault(f => f.Id == FolderId(special.Value)) : folders.FirstOrDefault(f => f.Kind == kind))
            ?? throw new MailServiceException("The mailbox does not expose the requested default folder: " + kind);
    }

    public Task<MailFolderInfo?> GetWellKnownFolderAsync(FolderKind kind, string? mailboxEmail = null, CancellationToken ct = default) => Run(async () =>
    {
        if (!string.IsNullOrEmpty(mailboxEmail) && !string.Equals(mailboxEmail, Account.Email, StringComparison.OrdinalIgnoreCase))
            return (await SharedMailFoldersAsync(mailboxEmail, ct)).FirstOrDefault(folder => folder.Kind == kind);
        var folders = await Folders(ct);
        var special = _specialFolders?.FirstOrDefault(p => p.Value == kind).Key;
        return special is > 0 ? folders.FirstOrDefault(f => f.Id == FolderId(special.Value)) : folders.FirstOrDefault(f => f.Kind == kind);
    }, ct);

    public Task<(int unread, int total)> GetFolderCountsAsync(string folderId, CancellationToken ct = default) => Run(async () =>
    {
        uint[] tags = [0x36020003, 0x36030003];
        var (session, logon, id, _) = await MailLocationAsync(folderId, ct);
        var row = await session.ReadFolderPropertiesAsync(logon, ParseFolder(id), tags, ct);
        return ((int)Number(row, 0x36030003), (int)Number(row, 0x36020003));
    }, ct);

    public Task<Paged<MailItemSummary>> GetItemsAsync(MailQuery query, CancellationToken cancellation = default) => RunViewRead(async ct =>
    {
        var (session, logon, id, prefix) = await MailLocationAsync(query.FolderId, ct);
        var fid = ParseFolder(id);
        var namedTags = await session.ResolvePropertyTagsAsync(logon, [CategoriesName, new(TaskSet, 0x8105, 0x40)], false, ct);
        var categoryTag = namedTags[0]; var flagDueTag = namedTags[1];
        uint[] columns = [.. MapiHttpSession.ContentsColumns, 0x001a001f, 0x0e080003, 0x00170003, 0x10900003, 0x0e04001f, 0x00390040, 0x5d01001f,
            .. namedTags.Where(t => t != 0)];
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : MapiRestrictions.MailText(query.Search);
        var filter = query.Filter switch
        {
            MailFilter.All => null,
            MailFilter.Unread => MapiRestrictions.BitsClear(0x0e070003, 1),
            MailFilter.Flagged => MapiRestrictions.Property(0x10900003, 2u),
            MailFilter.HasAttachments => MapiRestrictions.Property(0x0e1b000b, true),
            MailFilter.Important => MapiRestrictions.Property(0x00170003, (uint)Importance.High),
            MailFilter.ToMe => MapiRestrictions.Property(0x0057000b, true),
            MailFilter.ThisWeek => MapiRestrictions.AtLeast(0x0e060040, DateTime.Today.AddDays(-((int)DateTime.Today.DayOfWeek + 6) % 7)),
            _ => throw Unsupported()
        };
        var restriction = search == null ? filter : filter == null ? search : MapiRestrictions.And(search, filter);
        const uint deliveryTime = 0x0e060040;
        var sortTag = query.Sort switch
        {
            SortField.Date => deliveryTime, SortField.From => 0x0c1a001fu, SortField.To => 0x0e04001fu,
            SortField.Subject => 0u, SortField.Size => 0x0e080003u, SortField.Importance => 0x00170003u,
            SortField.Attachments => 0x0e1b000bu, SortField.Flag => 0x10900003u, SortField.Categories => 0u,
            _ => throw Unsupported()
        };
        var serverSort = new List<(uint, bool)>();
        if (sortTag != 0) serverSort.Add((sortTag, query.Descending));
        if (sortTag != deliveryTime) serverSort.Add((deliveryTime, true));
        serverSort.Add((0x674a0014, false));
        // Exchange rejects the full, computed subject as a sort key, and multivalued categories expand into duplicate rows.
        var localSort = sortTag == 0;
        var page = await session.ReadTableAsync(logon, fid, false, columns, localSort ? 5000 : query.PageSize, ct, restriction,
            serverSort, localSort ? 0 : query.Offset);
        IEnumerable<MailItemSummary> items = page.Rows.Select(row => new MailItemSummary
        {
            Id = $"{prefix}mapi-m:{fid:X16}:{(ulong)row[0x674a0014u]!:X16}", FolderId = query.FolderId, AccountId = Account.Id,
            IsReadOnly = prefix.Length != 0,
            Subject = Text(row, 0x0037001f), From = new(Text(row, 0x0c1a001f), Text(row, 0x5d01001f)),
            ItemClass = Text(row, 0x001a001f), Received = Date(row, 0x0e060040), Sent = Date(row, 0x00390040),
            IsRead = (Number(row, 0x0e070003) & 1) != 0, IsDraft = (Number(row, 0x0e070003) & 8) != 0,
            HasAttachments = row.GetValueOrDefault(0x0e1b000bu) is true, DisplayTo = Text(row, 0x0e04001f),
            Size = checked((int)Number(row, 0x0e080003)), Importance = (Importance)Math.Min(2, Number(row, 0x00170003)),
            Categories = row.GetValueOrDefault(categoryTag) is string[] categories ? categories.ToList() : [],
            FlagDue = NullableDate(row, flagDueTag),
            Flag = Number(row, 0x10900003) switch { 1 => FlagState.Complete, 2 => FlagState.Flagged, _ => FlagState.NotFlagged }
        });
        if (localSort)
        {
            if (page.Total > page.Rows.Count) throw new MailServiceException("This sort supports up to 5000 matching messages.");
            string Key(MailItemSummary item) => query.Sort == SortField.Subject ? item.Subject : string.Join(", ", item.Categories);
            items = (query.Descending ? items.OrderByDescending(Key) : items.OrderBy(Key)).Skip(query.Offset).Take(query.PageSize);
        }
        var result = items.ToList();
        return new Paged<MailItemSummary> { Items = result, TotalCount = checked((int)page.Total),
            MoreAvailable = result.Count > 0 && (long)query.Offset + result.Count < page.Total };
    }, cancellation);

    public Task<MailMessageDetail> GetMessageAsync(string itemId, CancellationToken cancellation = default) => RunViewRead(async ct =>
    {
        var (session, logon, id, prefix) = await MailLocationAsync(itemId, ct);
        var (fid, mid) = ParseMessage(id); var data = await session.ReadMessageAsync(logon, fid, mid, ct); var row = data.Properties;
        var categoriesTag = (await session.ResolvePropertyTagsAsync(logon, [CategoriesName], false, ct))[0];
        var tags = new List<uint> { 0x10900003, 0x004f0102 }; if (categoriesTag != 0) tags.Add(categoriesTag);
        var extra = await session.ReadMessagePropertiesAsync(logon, fid, mid, tags.ToArray(), ct);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string html;
        if (data.Html is { Length: > 0 })
        {
            var codePage = Number(row, 0x3fde0003);
            if (codePage == 0) throw new MailServiceException("The message HTML code page is unavailable; refusing to guess its encoding.");
            html = Encoding.GetEncoding((int)codePage).GetString(data.Html);
        }
        else if (data.Text != null) html = "<html><body><pre>" + WebUtility.HtmlEncode(Encoding.Unicode.GetString(data.Text)) + "</pre></body></html>";
        else throw new MailServiceException("This message has no supported HTML/plain body. RTF decoding is not implemented yet.");
        // Rewrite inline cid references to data URLs using the attachment's byte stream.
        var attachments = data.Attachments.Select(a => new AttachmentInfo
        {
            Id = Number(a, 0x0e210003).ToString(CultureInfo.InvariantCulture), Name = Text(a, 0x3707001f) is { Length: > 0 } n ? n : Text(a, 0x3704001f),
            Size = Number(a, 0x0e200003), ContentType = Text(a, 0x370e001f), ContentId = Text(a, 0x3712001f),
            IsItemAttachment = Number(a, 0x37050003) == 5, IsInline = !string.IsNullOrEmpty(Text(a, 0x3712001f))
        }).ToList();
        foreach (var a in attachments.Where(a => a.IsInline && !a.IsItemAttachment && a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
        {
            if (a.Size > 5 * 1024 * 1024 || !html.Contains("cid:" + a.ContentId, StringComparison.OrdinalIgnoreCase)) continue;
            a.Content = await session.ReadAttachmentAsync(logon, fid, mid, uint.Parse(a.Id), ct);
            html = html.Replace("cid:" + a.ContentId, "data:" + a.ContentType + ";base64," + Convert.ToBase64String(a.Content), StringComparison.OrdinalIgnoreCase);
        }
        var detail = new MailMessageDetail { Id = itemId, FolderId = prefix + FolderId(fid), AccountId = Account.Id, Subject = Text(row, 0x0037001f),
            ItemClass = Text(row, 0x001a001f), From = new(Text(row, 0x0c1a001f), Text(row, 0x5d01001f)), Received = Date(row, 0x0e060040), Sent = Date(row, 0x00390040),
            IsRead = (Number(row, 0x0e070003) & 1) != 0, IsDraft = (Number(row, 0x0e070003) & 8) != 0, BodyHtml = html,
            Importance = (Importance)Math.Min(2, Number(row, 0x00170003)), Attachments = attachments,
            Sensitivity = (Sensitivity)Math.Min(3, Number(row, 0x00360003)),
            Flag = Number(extra, 0x10900003) switch { 1 => FlagState.Complete, 2 => FlagState.Flagged, _ => FlagState.NotFlagged },
            Categories = extra.GetValueOrDefault(categoriesTag) is string[] categories ? categories.ToList() : [],
            RecipientsComplete = data.Recipients?.Complete == true,
            ReplyRecipientsComplete = extra.GetValueOrDefault(0x004f0102u) is not byte[] { Length: > 0 },
            To = data.Recipients?.Values.Where(r => r.Kind == 1).Select(r => new Recipient(r.Name, r.Address)).ToList() ?? [],
            Cc = data.Recipients?.Values.Where(r => r.Kind == 2).Select(r => new Recipient(r.Name, r.Address)).ToList() ?? [],
            Bcc = data.Recipients?.Values.Where(r => r.Kind == 3).Select(r => new Recipient(r.Name, r.Address)).ToList() ?? [],
            InternetMessageId = Text(row, 0x1035001f) };
        if (detail.IsMeetingItem) await ReadMeetingMailDetailsAsync(session, logon, fid, mid, detail, ct);
        return detail;
    }, cancellation);

    public Task<byte[]> GetAttachmentContentAsync(string itemId, string attachmentId, CancellationToken ct = default) => Run(async () =>
    {
        var (session, logon, id, _) = await MailLocationAsync(itemId, ct);
        var (folder, message) = ParseMessage(id);
        return await session.ReadAttachmentAsync(logon, folder, message, uint.Parse(attachmentId, CultureInfo.InvariantCulture), ct);
    }, ct);

    public Task<byte[]?> GetUserPhotoAsync(string email, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    private static MailServiceException Unsupported() => new("ეს ფუნქცია პირდაპირი MAPI/HTTP კავშირისთვის ჯერ განხორციელებული არ არის. / This operation is not implemented in the native MAPI/HTTP provider yet.");

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_addressBook != null) await _addressBook.DisposeAsync();
            await CloseSharedCalendarsAsync();
            await CloseSharedMailAsync();
            if (_session != null) await _session.DisposeAsync();
            _addressBook = null; _session = null; _logon = null;
        }
        finally { _gate.Release(); }
    }

    public async Task GoOfflineAsync()
    {
        await DisposeAsync();
        State = ConnectionState.Offline;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
