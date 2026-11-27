using Posta.Core.Models;
using Posta.Core.Services;
using Ews = Microsoft.Exchange.WebServices.Data;

namespace Posta.Exchange;

public sealed partial class EwsMailService
{
    // =====================================================================================
    // Directory
    // =====================================================================================

    public Task<IReadOnlyList<ResolvedName>> ResolveNamesAsync(string query, bool directoryOnly = false, CancellationToken ct = default) => Run(async () =>
    {
        var result = new List<ResolvedName>();
        // An empty query lists the first entries of the Global Address List ("SMTP:" prefix
        // makes ResolveNames return up to 100 directory entries), like Outlook's address book.
        var browse = string.IsNullOrWhiteSpace(query);
        Ews.NameResolutionCollection res;
        try
        {
            res = await Svc.ResolveName(browse ? "SMTP:" : query.Trim(),
                browse || directoryOnly ? Ews.ResolveNameSearchLocation.DirectoryOnly : Ews.ResolveNameSearchLocation.ContactsThenDirectory, true);
        }
        catch (Ews.ServiceResponseException ex) when (ex.ErrorCode == Ews.ServiceError.ErrorNameResolutionNoResults)
        {
            return (IReadOnlyList<ResolvedName>)result;
        }
        foreach (var r in res)
        {
            var mb = r.Mailbox;
            if (mb == null) continue;
            var n = new ResolvedName
            {
                Name = mb.Name ?? mb.Address ?? "",
                Email = mb.Address ?? "",
                Kind = mb.MailboxType switch
                {
                    Ews.MailboxType.PublicGroup or Ews.MailboxType.ContactGroup => RecipientKind.DistributionList,
                    Ews.MailboxType.Contact => RecipientKind.Contact,
                    Ews.MailboxType.Mailbox => RecipientKind.Mailbox,
                    _ => RecipientKind.Unknown
                }
            };
            var c = r.Contact;
            if (c != null)
            {
                n.Title = TryGet<string?>(c, Ews.ContactSchema.JobTitle, null);
                n.Department = TryGet<string?>(c, Ews.ContactSchema.Department, null);
                n.Company = TryGet<string?>(c, Ews.ContactSchema.CompanyName, null);
                n.Office = TryGet<string?>(c, Ews.ContactSchema.OfficeLocation, null);
                n.Alias = TryGet<string?>(c, Ews.ContactSchema.Alias, null);
                var phones = TryGet<Ews.PhoneNumberDictionary?>(c, Ews.ContactSchema.PhoneNumbers, null);
                if (phones != null)
                {
                    if (phones.TryGetValue(Ews.PhoneNumberKey.BusinessPhone, out var bp)) n.Phone = bp;
                    if (phones.TryGetValue(Ews.PhoneNumberKey.MobilePhone, out var mp)) n.Mobile = mp;
                }
            }
            result.Add(n);
        }
        return (IReadOnlyList<ResolvedName>)result;
    });

    public Task<IReadOnlyList<Recipient>> ExpandGroupAsync(string email, CancellationToken ct = default) => Run(async () =>
    {
        var res = await Svc.ExpandGroup(email);
        return (IReadOnlyList<Recipient>)res.Members.Select(m => new Recipient(m.Name ?? "", m.Address ?? "")).ToList();
    });

    private readonly Dictionary<string, byte[]?> _photoCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<byte[]?> GetUserPhotoAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || _service == null) return null;
        lock (_photoCache)
            if (_photoCache.TryGetValue(email, out var cached)) return cached;
        byte[]? photo = null;
        try
        {
            var r = await Svc.GetUserPhoto(email, "HR96x96", "");
            if (r.Status == Ews.GetUserPhotoStatus.PhotoReturned) photo = r.Photo;
        }
        catch { }
        lock (_photoCache) _photoCache[email] = photo;
        return photo;
    }

    // =====================================================================================
    // Folder permissions (calendar sharing)
    // =====================================================================================

    public Task<IReadOnlyList<PermissionEntry>> GetFolderPermissionsAsync(string folderId, CancellationToken ct = default) => Run(async () =>
    {
        var f = await Ews.Folder.Bind(Svc, Fid(folderId), new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.FolderSchema.Permissions));
        var list = new List<PermissionEntry>();
        foreach (var p in f.Permissions)
        {
            var e = new PermissionEntry
            {
                StandardUser = p.UserId.StandardUser switch
                {
                    Ews.StandardUser.Default => "Default",
                    Ews.StandardUser.Anonymous => "Anonymous",
                    _ => null
                },
                Email = p.UserId.PrimarySmtpAddress ?? "",
                Name = p.UserId.DisplayName ?? p.UserId.PrimarySmtpAddress ?? "",
            };
            e.Level = (PermissionLevel)Enum.Parse(typeof(PermissionLevel), p.PermissionLevel.ToString());
            e.CanCreateItems = p.CanCreateItems;
            e.CanCreateSubFolders = p.CanCreateSubFolders;
            e.IsFolderOwner = p.IsFolderOwner;
            e.IsFolderVisible = p.IsFolderVisible;
            e.IsFolderContact = p.IsFolderContact;
            e.EditItems = (ItemAccessScope)(int)p.EditItems;
            e.DeleteItems = (ItemAccessScope)(int)p.DeleteItems;
            e.ReadItems = p.ReadItems switch
            {
                Ews.FolderPermissionReadAccess.FullDetails => ReadAccess.FullDetails,
                Ews.FolderPermissionReadAccess.TimeOnly => ReadAccess.TimeOnly,
                Ews.FolderPermissionReadAccess.TimeAndSubjectAndLocation => ReadAccess.TimeAndSubjectAndLocation,
                _ => ReadAccess.None
            };
            list.Add(e);
        }
        return (IReadOnlyList<PermissionEntry>)list;
    });

    public Task SetFolderPermissionsAsync(string folderId, IEnumerable<PermissionEntry> permissions, CancellationToken ct = default) => Run(async () =>
    {
        var f = await Ews.Folder.Bind(Svc, Fid(folderId), new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.FolderSchema.Permissions));
        f.Permissions.Clear();
        foreach (var e in permissions)
        {
            var level = (Ews.FolderPermissionLevel)Enum.Parse(typeof(Ews.FolderPermissionLevel), e.Level.ToString());
            Ews.FolderPermission p = e.StandardUser switch
            {
                "Default" => new Ews.FolderPermission(Ews.StandardUser.Default, level == Ews.FolderPermissionLevel.Custom ? Ews.FolderPermissionLevel.None : level),
                "Anonymous" => new Ews.FolderPermission(Ews.StandardUser.Anonymous, level == Ews.FolderPermissionLevel.Custom ? Ews.FolderPermissionLevel.None : level),
                _ => new Ews.FolderPermission(e.Email, level == Ews.FolderPermissionLevel.Custom ? Ews.FolderPermissionLevel.None : level)
            };
            if (level == Ews.FolderPermissionLevel.Custom)
            {
                p.CanCreateItems = e.CanCreateItems;
                p.CanCreateSubFolders = e.CanCreateSubFolders;
                p.IsFolderOwner = e.IsFolderOwner;
                p.IsFolderVisible = e.IsFolderVisible;
                p.IsFolderContact = e.IsFolderContact;
                p.EditItems = (Ews.PermissionScope)(int)e.EditItems;
                p.DeleteItems = (Ews.PermissionScope)(int)e.DeleteItems;
                p.ReadItems = e.ReadItems switch
                {
                    ReadAccess.FullDetails => Ews.FolderPermissionReadAccess.FullDetails,
                    ReadAccess.TimeOnly => Ews.FolderPermissionReadAccess.TimeOnly,
                    ReadAccess.TimeAndSubjectAndLocation => Ews.FolderPermissionReadAccess.TimeAndSubjectAndLocation,
                    _ => Ews.FolderPermissionReadAccess.None
                };
            }
            f.Permissions.Add(p);
        }
        await f.Update();
    });

    // =====================================================================================
    // Contacts
    // =====================================================================================

    private Ews.FolderId ContactsFolder(string folderId) =>
        string.IsNullOrEmpty(folderId) ? new Ews.FolderId(Ews.WellKnownFolderName.Contacts) : Fid(folderId);

    public Task<IReadOnlyList<ContactInfo>> GetContactsAsync(string folderId, string? search, CancellationToken ct = default) => Run(async () =>
    {
        var props = new Ews.PropertySet(Ews.BasePropertySet.IdOnly, Ews.ContactSchema.DisplayName, Ews.ContactSchema.FileAs,
            Ews.ContactSchema.GivenName, Ews.ContactSchema.Surname, Ews.ContactSchema.CompanyName, Ews.ContactSchema.JobTitle,
            Ews.ContactSchema.Department, Ews.ContactSchema.EmailAddress1, Ews.ContactSchema.EmailAddress2, Ews.ContactSchema.BusinessPhone,
            Ews.ContactSchema.MobilePhone, Ews.ContactSchema.HomePhone, Ews.ContactSchema.BusinessAddressCity, Ews.ContactSchema.OfficeLocation,
            Ews.ItemSchema.ItemClass, Ews.ItemSchema.Categories, Ews.ContactSchema.HasPicture);
        var result = new List<ContactInfo>();
        int offset = 0;
        while (true)
        {
            var view = new Ews.ItemView(500, offset) { PropertySet = props };
            view.OrderBy.Add(Ews.ContactSchema.FileAs, Ews.SortDirection.Ascending);
            var found = string.IsNullOrWhiteSpace(search)
                ? await Svc.FindItems(ContactsFolder(folderId), view)
                : await Svc.FindItems(ContactsFolder(folderId), search!, view);
            foreach (var item in found.Items)
            {
                var c = new ContactInfo
                {
                    Id = item.Id.UniqueId, AccountId = Account.Id, FolderId = folderId,
                    DisplayName = TryGet(item, Ews.ContactSchema.DisplayName, "") ?? "",
                    FileAs = TryGet(item, Ews.ContactSchema.FileAs, "") ?? "",
                    IsGroup = item is Ews.ContactGroup
                };
                if (item is Ews.Contact ct2)
                {
                    c.GivenName = TryGet(ct2, Ews.ContactSchema.GivenName, "") ?? "";
                    c.Surname = TryGet(ct2, Ews.ContactSchema.Surname, "") ?? "";
                    c.Company = TryGet(ct2, Ews.ContactSchema.CompanyName, "") ?? "";
                    c.JobTitle = TryGet(ct2, Ews.ContactSchema.JobTitle, "") ?? "";
                    c.Department = TryGet(ct2, Ews.ContactSchema.Department, "") ?? "";
                    c.Office = TryGet(ct2, Ews.ContactSchema.OfficeLocation, "") ?? "";
                    var emails = TryGet<Ews.EmailAddressDictionary?>(ct2, Ews.ContactSchema.EmailAddresses, null);
                    if (emails != null)
                    {
                        if (emails.TryGetValue(Ews.EmailAddressKey.EmailAddress1, out var e1)) { c.Email1 = e1.Address ?? ""; c.Email1Name = e1.Name ?? ""; }
                        if (emails.TryGetValue(Ews.EmailAddressKey.EmailAddress2, out var e2)) c.Email2 = e2.Address ?? "";
                    }
                    var phones = TryGet<Ews.PhoneNumberDictionary?>(ct2, Ews.ContactSchema.PhoneNumbers, null);
                    if (phones != null)
                    {
                        if (phones.TryGetValue(Ews.PhoneNumberKey.BusinessPhone, out var bp)) c.BusinessPhone = bp ?? "";
                        if (phones.TryGetValue(Ews.PhoneNumberKey.MobilePhone, out var mp)) c.MobilePhone = mp ?? "";
                        if (phones.TryGetValue(Ews.PhoneNumberKey.HomePhone, out var hp)) c.HomePhone = hp ?? "";
                    }
                    var addr = TryGet<Ews.PhysicalAddressDictionary?>(ct2, Ews.ContactSchema.PhysicalAddresses, null);
                    if (addr != null && addr.TryGetValue(Ews.PhysicalAddressKey.Business, out var ba)) c.BusinessAddress.City = ba.City ?? "";
                }
                if (item.TryGetProperty(Ews.ItemSchema.Categories, out Ews.StringList cats) && cats != null) c.Categories = cats.ToList();
                if (string.IsNullOrEmpty(c.FileAs)) c.FileAs = c.DisplayName;
                result.Add(c);
            }
            if (!found.MoreAvailable || result.Count >= 5000) break;
            offset = found.NextPageOffset ?? offset + 500;
        }
        return (IReadOnlyList<ContactInfo>)result;
    });

    public Task<ContactInfo> GetContactAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        var item = await Ews.Item.Bind(Svc, Iid(itemId), new Ews.PropertySet(Ews.BasePropertySet.FirstClassProperties) { RequestedBodyType = Ews.BodyType.Text });
        var c = new ContactInfo { Id = item.Id.UniqueId, AccountId = Account.Id };
        if (item is Ews.ContactGroup g)
        {
            c.IsGroup = true;
            c.DisplayName = g.DisplayName ?? "";
            c.FileAs = g.FileAs ?? c.DisplayName;
            c.Members = g.Members.Select(m => new Recipient(m.AddressInformation?.Name ?? "", m.AddressInformation?.Address ?? "")).ToList();
            c.Notes = TryGet<Ews.MessageBody?>(g, Ews.ItemSchema.Body, null)?.Text ?? "";
            return c;
        }
        if (item is not Ews.Contact x) throw new MailServiceException("Item is not a contact.");
        c.DisplayName = x.DisplayName ?? "";
        c.FileAs = x.FileAs ?? "";
        c.GivenName = x.GivenName ?? "";
        c.MiddleName = x.MiddleName ?? "";
        c.Surname = x.Surname ?? "";
        c.Nickname = TryGet(x, Ews.ContactSchema.NickName, "") ?? "";
        c.Company = x.CompanyName ?? "";
        c.Department = x.Department ?? "";
        c.JobTitle = x.JobTitle ?? "";
        c.Office = x.OfficeLocation ?? "";
        c.Manager = x.Manager ?? "";
        c.Assistant = x.AssistantName ?? "";
        c.WebPage = x.BusinessHomePage ?? "";
        c.SpouseName = x.SpouseName ?? "";
        c.Title = TryGet<Ews.CompleteName?>(x, Ews.ContactSchema.CompleteName, null)?.Title ?? "";
        c.Suffix = TryGet<Ews.CompleteName?>(x, Ews.ContactSchema.CompleteName, null)?.Suffix ?? "";
        c.Birthday = TryGet<DateTime?>(x, Ews.ContactSchema.Birthday, null);
        c.Anniversary = TryGet<DateTime?>(x, Ews.ContactSchema.WeddingAnniversary, null);
        c.Notes = TryGet<Ews.MessageBody?>(x, Ews.ItemSchema.Body, null)?.Text ?? "";
        if (x.TryGetProperty(Ews.ItemSchema.Categories, out Ews.StringList cats) && cats != null) c.Categories = cats.ToList();

        if (x.EmailAddresses.TryGetValue(Ews.EmailAddressKey.EmailAddress1, out var e1)) { c.Email1 = e1.Address ?? ""; c.Email1Name = e1.Name ?? ""; }
        if (x.EmailAddresses.TryGetValue(Ews.EmailAddressKey.EmailAddress2, out var e2)) c.Email2 = e2.Address ?? "";
        if (x.EmailAddresses.TryGetValue(Ews.EmailAddressKey.EmailAddress3, out var e3)) c.Email3 = e3.Address ?? "";

        string Ph(Ews.PhoneNumberKey k) => x.PhoneNumbers.TryGetValue(k, out var v) ? v ?? "" : "";
        c.BusinessPhone = Ph(Ews.PhoneNumberKey.BusinessPhone);
        c.BusinessPhone2 = Ph(Ews.PhoneNumberKey.BusinessPhone2);
        c.HomePhone = Ph(Ews.PhoneNumberKey.HomePhone);
        c.MobilePhone = Ph(Ews.PhoneNumberKey.MobilePhone);
        c.BusinessFax = Ph(Ews.PhoneNumberKey.BusinessFax);
        c.Pager = Ph(Ews.PhoneNumberKey.Pager);

        if (x.ImAddresses.TryGetValue(Ews.ImAddressKey.ImAddress1, out var im)) c.ImAddress = im ?? "";

        PostalAddress Addr(Ews.PhysicalAddressKey k)
        {
            if (!x.PhysicalAddresses.TryGetValue(k, out var a) || a == null) return new PostalAddress();
            return new PostalAddress { Street = a.Street ?? "", City = a.City ?? "", State = a.State ?? "", PostalCode = a.PostalCode ?? "", Country = a.CountryOrRegion ?? "" };
        }
        c.BusinessAddress = Addr(Ews.PhysicalAddressKey.Business);
        c.HomeAddress = Addr(Ews.PhysicalAddressKey.Home);
        c.OtherAddress = Addr(Ews.PhysicalAddressKey.Other);

        try
        {
            if (x.HasPicture)
            {
                var pic = x.GetContactPictureAttachment();
                if (pic != null) { await pic.Load(); c.Photo = pic.Content; }
            }
        }
        catch { }
        return c;
    });

    public Task<string> SaveContactAsync(string folderId, ContactInfo c, CancellationToken ct = default) => Run(async () =>
    {
        if (c.IsGroup)
        {
            Ews.ContactGroup g = string.IsNullOrEmpty(c.Id)
                ? new Ews.ContactGroup(Svc)
                : await Ews.ContactGroup.Bind(Svc, Iid(c.Id), new Ews.PropertySet(Ews.BasePropertySet.FirstClassProperties));
            g.DisplayName = c.DisplayName;
            g.Members.Clear();
            foreach (var m in c.Members.Where(m => !string.IsNullOrWhiteSpace(m.Address)))
                g.Members.Add(new Ews.GroupMember(m.Name, m.Address));
            g.Body = new Ews.MessageBody(Ews.BodyType.Text, c.Notes ?? "");
            if (string.IsNullOrEmpty(c.Id)) await g.Save(ContactsFolder(folderId));
            else await g.Update(Ews.ConflictResolutionMode.AutoResolve);
            return g.Id.UniqueId;
        }

        Ews.Contact x = string.IsNullOrEmpty(c.Id)
            ? new Ews.Contact(Svc)
            : await Ews.Contact.Bind(Svc, Iid(c.Id), new Ews.PropertySet(Ews.BasePropertySet.FirstClassProperties));
        bool isNew = string.IsNullOrEmpty(c.Id);

        x.GivenName = NullIfEmpty(c.GivenName);
        x.MiddleName = NullIfEmpty(c.MiddleName);
        x.Surname = NullIfEmpty(c.Surname);
        x.NickName = NullIfEmpty(c.Nickname);
        x.CompanyName = NullIfEmpty(c.Company);
        x.Department = NullIfEmpty(c.Department);
        x.JobTitle = NullIfEmpty(c.JobTitle);
        x.OfficeLocation = NullIfEmpty(c.Office);
        x.Manager = NullIfEmpty(c.Manager);
        x.AssistantName = NullIfEmpty(c.Assistant);
        x.BusinessHomePage = NullIfEmpty(c.WebPage);
        x.SpouseName = NullIfEmpty(c.SpouseName);
        x.FileAsMapping = Ews.FileAsMapping.SurnameCommaGivenName;
        if (string.IsNullOrWhiteSpace(c.GivenName) && string.IsNullOrWhiteSpace(c.Surname))
        {
            x.FileAsMapping = Ews.FileAsMapping.Company;
            if (string.IsNullOrWhiteSpace(c.Company)) { x.FileAsMapping = Ews.FileAsMapping.None; x.FileAs = c.DisplayName; }
        }
        if (!string.IsNullOrWhiteSpace(c.DisplayName)) x.DisplayName = c.DisplayName;
        if (c.Birthday.HasValue) x.Birthday = c.Birthday.Value; else if (!isNew) x.Birthday = null;
        if (c.Anniversary.HasValue) x.WeddingAnniversary = c.Anniversary.Value; else if (!isNew) x.WeddingAnniversary = null;
        x.Body = new Ews.MessageBody(Ews.BodyType.Text, c.Notes ?? "");
        x.Categories = new Ews.StringList(c.Categories);

        void Email(Ews.EmailAddressKey k, string addr, string? name)
        {
            if (!string.IsNullOrWhiteSpace(addr)) x.EmailAddresses[k] = new Ews.EmailAddress(string.IsNullOrWhiteSpace(name) ? c.DisplayName : name, addr);
            else if (!isNew && x.EmailAddresses.Contains(k)) x.EmailAddresses[k] = null!;
        }
        Email(Ews.EmailAddressKey.EmailAddress1, c.Email1, c.Email1Name);
        Email(Ews.EmailAddressKey.EmailAddress2, c.Email2, null);
        Email(Ews.EmailAddressKey.EmailAddress3, c.Email3, null);

        void Phone(Ews.PhoneNumberKey k, string v)
        {
            if (!string.IsNullOrWhiteSpace(v)) x.PhoneNumbers[k] = v;
            else if (!isNew && x.PhoneNumbers.Contains(k)) x.PhoneNumbers[k] = null!;
        }
        Phone(Ews.PhoneNumberKey.BusinessPhone, c.BusinessPhone);
        Phone(Ews.PhoneNumberKey.BusinessPhone2, c.BusinessPhone2);
        Phone(Ews.PhoneNumberKey.HomePhone, c.HomePhone);
        Phone(Ews.PhoneNumberKey.MobilePhone, c.MobilePhone);
        Phone(Ews.PhoneNumberKey.BusinessFax, c.BusinessFax);
        Phone(Ews.PhoneNumberKey.Pager, c.Pager);

        if (!string.IsNullOrWhiteSpace(c.ImAddress)) x.ImAddresses[Ews.ImAddressKey.ImAddress1] = c.ImAddress;

        void Addr(Ews.PhysicalAddressKey k, PostalAddress a)
        {
            if (a.IsEmpty)
            {
                if (!isNew && x.PhysicalAddresses.Contains(k)) x.PhysicalAddresses[k] = null!;
                return;
            }
            x.PhysicalAddresses[k] = new Ews.PhysicalAddressEntry { Street = a.Street, City = a.City, State = a.State, PostalCode = a.PostalCode, CountryOrRegion = a.Country };
        }
        Addr(Ews.PhysicalAddressKey.Business, c.BusinessAddress);
        Addr(Ews.PhysicalAddressKey.Home, c.HomeAddress);
        Addr(Ews.PhysicalAddressKey.Other, c.OtherAddress);

        if (c.Photo != null && c.Photo.Length > 0) x.SetContactPicture(c.Photo);

        if (isNew) await x.Save(ContactsFolder(folderId));
        else await x.Update(Ews.ConflictResolutionMode.AutoResolve);
        return x.Id.UniqueId;
    });

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public Task DeleteContactAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        var item = await Ews.Item.Bind(Svc, Iid(itemId), Ews.PropertySet.IdOnly);
        await item.Delete(Ews.DeleteMode.MoveToDeletedItems);
    });

    // =====================================================================================
    // Tasks
    // =====================================================================================

    private static readonly Ews.PropertySet TaskProps = new(Ews.BasePropertySet.IdOnly,
        Ews.ItemSchema.Subject, Ews.TaskSchema.StartDate, Ews.TaskSchema.DueDate, Ews.TaskSchema.Status, Ews.TaskSchema.PercentComplete,
        Ews.ItemSchema.Importance, Ews.ItemSchema.IsReminderSet, Ews.ItemSchema.ReminderDueBy, Ews.TaskSchema.CompleteDate,
        Ews.ItemSchema.Categories, Ews.TaskSchema.Owner);

    public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(string folderId, bool includeCompleted, CancellationToken ct = default) => Run(async () =>
    {
        var result = new List<TaskInfo>();
        var fid = string.IsNullOrEmpty(folderId) ? new Ews.FolderId(Ews.WellKnownFolderName.Tasks) : Fid(folderId);
        var view = new Ews.ItemView(1000) { PropertySet = TaskProps };
        view.OrderBy.Add(Ews.TaskSchema.DueDate, Ews.SortDirection.Ascending);
        var found = includeCompleted
            ? await Svc.FindItems(fid, view)
            : await Svc.FindItems(fid, new Ews.SearchFilter.IsNotEqualTo(Ews.TaskSchema.Status, Ews.TaskStatus.Completed), view);
        foreach (var item in found.Items.OfType<Ews.Task>())
        {
            result.Add(ToTask(item, folderId));
        }

        // Flagged e-mail appears in the To-Do list as well
        if (string.IsNullOrEmpty(folderId) || folderId == _tasksFolderId)
        {
            try
            {
                var mv = new Ews.ItemView(200) { PropertySet = SummaryProps };
                var flagged = await Svc.FindItems(Ews.WellKnownFolderName.Inbox, new Ews.SearchFilter.IsEqualTo(PidTagFlagStatus, 2), mv);
                foreach (var m in flagged.Items)
                {
                    var s = ToSummary(m, _inboxFolderId ?? "");
                    result.Add(new TaskInfo
                    {
                        Id = s.Id, AccountId = Account.Id, FolderId = s.FolderId, Subject = s.Subject, DueDate = s.FlagDue,
                        Importance = s.Importance, IsFlaggedMail = true, Status = TaskState.NotStarted, Categories = s.Categories
                    });
                }
            }
            catch { }
        }
        return (IReadOnlyList<TaskInfo>)result;
    });

    private TaskInfo ToTask(Ews.Task t, string folderId)
    {
        var info = new TaskInfo
        {
            Id = t.Id.UniqueId, AccountId = Account.Id, FolderId = folderId,
            Subject = TryGet(t, Ews.ItemSchema.Subject, ""),
            StartDate = TryGet<DateTime?>(t, Ews.TaskSchema.StartDate, null),
            DueDate = TryGet<DateTime?>(t, Ews.TaskSchema.DueDate, null),
            Status = TryGet(t, Ews.TaskSchema.Status, Ews.TaskStatus.NotStarted) switch
            {
                Ews.TaskStatus.InProgress => TaskState.InProgress,
                Ews.TaskStatus.Completed => TaskState.Completed,
                Ews.TaskStatus.WaitingOnOthers => TaskState.WaitingOnOthers,
                Ews.TaskStatus.Deferred => TaskState.Deferred,
                _ => TaskState.NotStarted
            },
            PercentComplete = (int)TryGet(t, Ews.TaskSchema.PercentComplete, 0.0),
            Importance = MapImportance(TryGet(t, Ews.ItemSchema.Importance, Ews.Importance.Normal)),
            ReminderSet = TryGet(t, Ews.ItemSchema.IsReminderSet, false),
            CompleteDate = TryGet<DateTime?>(t, Ews.TaskSchema.CompleteDate, null),
            Owner = TryGet<string?>(t, Ews.TaskSchema.Owner, null)
        };
        if (info.ReminderSet && t.TryGetProperty(Ews.ItemSchema.ReminderDueBy, out DateTime rd)) info.ReminderTime = rd;
        if (t.TryGetProperty(Ews.ItemSchema.Categories, out Ews.StringList cats) && cats != null) info.Categories = cats.ToList();
        return info;
    }

    public Task<TaskInfo> GetTaskAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        var item = await Ews.Item.Bind(Svc, Iid(itemId), new Ews.PropertySet(Ews.BasePropertySet.FirstClassProperties) { RequestedBodyType = Ews.BodyType.HTML });
        if (item is not Ews.Task t) throw new MailServiceException("Item is not a task.");
        var info = ToTask(t, "");
        var body = TryGet<Ews.MessageBody?>(t, Ews.ItemSchema.Body, null);
        info.BodyHtml = body == null ? "" : body.BodyType == Ews.BodyType.Text ? HtmlUtil.PlainTextToHtml(body.Text) : body.Text ?? "";
        return info;
    });

    public Task<string> SaveTaskAsync(string folderId, TaskInfo task, CancellationToken ct = default) => Run(async () =>
    {
        if (task.IsFlaggedMail)
        {
            await SetFlagAsync(new[] { task.Id }, task.IsComplete ? FlagState.Complete : FlagState.Flagged,
                task.StartDate ?? task.DueDate, task.DueDate, ct);
            return task.Id;
        }
        bool isNew = string.IsNullOrEmpty(task.Id);
        var t = isNew ? new Ews.Task(Svc) : await Ews.Task.Bind(Svc, Iid(task.Id), new Ews.PropertySet(Ews.BasePropertySet.FirstClassProperties));
        t.Subject = task.Subject;
        t.StartDate = task.StartDate;
        t.DueDate = task.DueDate;
        t.Status = task.Status switch
        {
            TaskState.InProgress => Ews.TaskStatus.InProgress,
            TaskState.Completed => Ews.TaskStatus.Completed,
            TaskState.WaitingOnOthers => Ews.TaskStatus.WaitingOnOthers,
            TaskState.Deferred => Ews.TaskStatus.Deferred,
            _ => Ews.TaskStatus.NotStarted
        };
        if (task.Status != TaskState.Completed) t.PercentComplete = Math.Clamp(task.PercentComplete, 0, 99);
        t.Importance = MapImportance(task.Importance);
        t.IsReminderSet = task.ReminderSet;
        if (task.ReminderSet && task.ReminderTime.HasValue) t.ReminderDueBy = task.ReminderTime.Value;
        t.Body = new Ews.MessageBody(Ews.BodyType.HTML, task.BodyHtml ?? "");
        t.Categories = new Ews.StringList(task.Categories);
        if (isNew) await t.Save(string.IsNullOrEmpty(folderId) ? new Ews.FolderId(Ews.WellKnownFolderName.Tasks) : Fid(folderId));
        else await t.Update(Ews.ConflictResolutionMode.AutoResolve);
        return t.Id.UniqueId;
    });

    public Task DeleteTaskAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        var item = await Ews.Item.Bind(Svc, Iid(itemId), Ews.PropertySet.IdOnly);
        await item.Delete(Ews.DeleteMode.MoveToDeletedItems);
    });

    // =====================================================================================
    // Automatic replies & rules
    // =====================================================================================

    public Task<OofSettingsInfo> GetOofAsync(CancellationToken ct = default) => Run(async () =>
    {
        var s = await Svc.GetUserOofSettings(Account.Email);
        var info = new OofSettingsInfo
        {
            State = s.State switch { Ews.OofState.Enabled => OofState.Enabled, Ews.OofState.Scheduled => OofState.Scheduled, _ => OofState.Disabled },
            InternalReplyHtml = s.InternalReply?.Message ?? "",
            ExternalReplyHtml = s.ExternalReply?.Message ?? "",
            ExternalAudience = s.ExternalAudience switch { Ews.OofExternalAudience.Known => OofExternalAudience.Known, Ews.OofExternalAudience.All => OofExternalAudience.All, _ => OofExternalAudience.None }
        };
        if (s.Duration != null && s.Duration.EndTime > s.Duration.StartTime)
        {
            info.Start = s.Duration.StartTime.ToLocalTime();
            info.End = s.Duration.EndTime.ToLocalTime();
        }
        return info;
    });

    public Task SetOofAsync(OofSettingsInfo info, CancellationToken ct = default) => Run(async () =>
    {
        var s = new Ews.OofSettings
        {
            State = info.State switch { OofState.Enabled => Ews.OofState.Enabled, OofState.Scheduled => Ews.OofState.Scheduled, _ => Ews.OofState.Disabled },
            InternalReply = new Ews.OofReply(info.InternalReplyHtml ?? ""),
            ExternalReply = new Ews.OofReply(info.ExternalReplyHtml ?? ""),
            ExternalAudience = info.ExternalAudience switch { OofExternalAudience.Known => Ews.OofExternalAudience.Known, OofExternalAudience.All => Ews.OofExternalAudience.All, _ => Ews.OofExternalAudience.None },
            Duration = new Ews.TimeWindow(info.Start.ToUniversalTime(), info.End.ToUniversalTime())
        };
        await Svc.SetUserOofSettings(Account.Email, s);
    });

    public Task<IReadOnlyList<RuleInfo>> GetRulesAsync(CancellationToken ct = default) => Run(async () =>
    {
        var rules = await Svc.GetInboxRules();
        var list = new List<RuleInfo>();
        foreach (var r in rules)
        {
            var c = r.Conditions;
            var a = r.Actions;
            var info = new RuleInfo
            {
                Id = r.Id, Name = r.DisplayName ?? "", IsEnabled = r.IsEnabled, Priority = r.Priority,
                HasUnsupportedParts = r.IsNotSupported,
                FromAddresses = c.FromAddresses.Select(x => x.Address ?? x.Name ?? "").ToList(),
                SentToAddresses = c.SentToAddresses.Select(x => x.Address ?? x.Name ?? "").ToList(),
                SubjectContains = c.ContainsSubjectStrings.ToList(),
                BodyContains = c.ContainsBodyStrings.ToList(),
                SenderContains = c.ContainsSenderStrings.ToList(),
                SentOnlyToMe = c.SentOnlyToMe,
                MyNameInCc = c.SentCcMe,
                HasAttachments = c.HasAttachments,
                WithImportance = c.Importance.HasValue ? MapImportance(c.Importance.Value) : null,
                MoveToFolderId = a.MoveToFolder?.UniqueId,
                CopyToFolderId = a.CopyToFolder?.UniqueId,
                Delete = a.Delete,
                PermanentDelete = a.PermanentDelete,
                MarkAsRead = a.MarkAsRead,
                ForwardTo = a.ForwardToRecipients.Select(x => x.Address ?? "").ToList(),
                RedirectTo = a.RedirectToRecipients.Select(x => x.Address ?? "").ToList(),
                AssignCategories = a.AssignCategories.ToList(),
                MarkImportance = a.MarkImportance.HasValue ? MapImportance(a.MarkImportance.Value) : null,
                StopProcessing = a.StopProcessingRules
            };
            list.Add(info);
        }
        return (IReadOnlyList<RuleInfo>)list.OrderBy(r => r.Priority).ToList();
    });

    public Task SaveRulesAsync(IReadOnlyList<RuleInfo> rules, IReadOnlyList<string> deletedRuleIds, CancellationToken ct = default) => Run(async () =>
    {
        var ops = new List<Ews.RuleOperation>();
        foreach (var id in deletedRuleIds) ops.Add(new Ews.DeleteRuleOperation(id));
        int priority = 1;
        foreach (var info in rules)
        {
            var r = new Ews.Rule { DisplayName = info.Name, IsEnabled = info.IsEnabled, Priority = priority++ };
            if (!string.IsNullOrEmpty(info.Id)) r.Id = info.Id;
            var c = r.Conditions;
            foreach (var x in info.FromAddresses.Where(s => !string.IsNullOrWhiteSpace(s))) c.FromAddresses.Add(new Ews.EmailAddress(x));
            foreach (var x in info.SentToAddresses.Where(s => !string.IsNullOrWhiteSpace(s))) c.SentToAddresses.Add(new Ews.EmailAddress(x));
            foreach (var x in info.SubjectContains.Where(s => !string.IsNullOrWhiteSpace(s))) c.ContainsSubjectStrings.Add(x);
            foreach (var x in info.BodyContains.Where(s => !string.IsNullOrWhiteSpace(s))) c.ContainsBodyStrings.Add(x);
            foreach (var x in info.SenderContains.Where(s => !string.IsNullOrWhiteSpace(s))) c.ContainsSenderStrings.Add(x);
            if (info.SentOnlyToMe) c.SentOnlyToMe = true;
            if (info.MyNameInCc) c.SentCcMe = true;
            if (info.HasAttachments) c.HasAttachments = true;
            if (info.WithImportance.HasValue) c.Importance = MapImportance(info.WithImportance.Value);

            var a = r.Actions;
            if (!string.IsNullOrEmpty(info.MoveToFolderId)) a.MoveToFolder = Fid(info.MoveToFolderId);
            if (!string.IsNullOrEmpty(info.CopyToFolderId)) a.CopyToFolder = Fid(info.CopyToFolderId);
            if (info.Delete) a.Delete = true;
            if (info.PermanentDelete) a.PermanentDelete = true;
            if (info.MarkAsRead) a.MarkAsRead = true;
            foreach (var x in info.ForwardTo.Where(s => !string.IsNullOrWhiteSpace(s))) a.ForwardToRecipients.Add(new Ews.EmailAddress(x));
            foreach (var x in info.RedirectTo.Where(s => !string.IsNullOrWhiteSpace(s))) a.RedirectToRecipients.Add(new Ews.EmailAddress(x));
            foreach (var x in info.AssignCategories.Where(s => !string.IsNullOrWhiteSpace(s))) a.AssignCategories.Add(x);
            if (info.MarkImportance.HasValue) a.MarkImportance = MapImportance(info.MarkImportance.Value);
            if (info.StopProcessing) a.StopProcessingRules = true;

            if (info.HasUnsupportedParts && !string.IsNullOrEmpty(info.Id)) continue; // never overwrite rules we cannot represent
            ops.Add(string.IsNullOrEmpty(info.Id) ? new Ews.CreateRuleOperation(r) : new Ews.SetRuleOperation(r));
        }
        if (ops.Count > 0) await Svc.UpdateInboxRules(ops, true);
    });
}
