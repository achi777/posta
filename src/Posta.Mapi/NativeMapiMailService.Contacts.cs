using System.Text;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    private static readonly Guid AddressSet = new("00062004-0000-0000-c000-000000000046");
    private static readonly Dictionary<uint, string> ContactStrings = new()
    {
        [0x3001001f] = nameof(ContactInfo.DisplayName), [0x3a06001f] = nameof(ContactInfo.GivenName),
        [0x3a11001f] = nameof(ContactInfo.Surname), [0x3a44001f] = nameof(ContactInfo.MiddleName),
        [0x3a45001f] = nameof(ContactInfo.Title), [0x3a05001f] = nameof(ContactInfo.Suffix),
        [0x3a4f001f] = nameof(ContactInfo.Nickname), [0x3a16001f] = nameof(ContactInfo.Company),
        [0x3a18001f] = nameof(ContactInfo.Department), [0x3a17001f] = nameof(ContactInfo.JobTitle),
        [0x3a19001f] = nameof(ContactInfo.Office), [0x3a4e001f] = nameof(ContactInfo.Manager),
        [0x3a30001f] = nameof(ContactInfo.Assistant), [0x3a08001f] = nameof(ContactInfo.BusinessPhone),
        [0x3a1b001f] = nameof(ContactInfo.BusinessPhone2), [0x3a09001f] = nameof(ContactInfo.HomePhone),
        [0x3a1c001f] = nameof(ContactInfo.MobilePhone), [0x3a24001f] = nameof(ContactInfo.BusinessFax),
        [0x3a21001f] = nameof(ContactInfo.Pager), [0x3a51001f] = nameof(ContactInfo.WebPage),
        [0x3a48001f] = nameof(ContactInfo.SpouseName), [0x1000001f] = nameof(ContactInfo.Notes)
    };
    private static readonly MapiPropertyName[] ContactNames =
    [new(AddressSet, 0x8005, 0x1f), new(AddressSet, 0x8083, 0x1f), new(AddressSet, 0x8093, 0x1f),
     new(AddressSet, 0x80a3, 0x1f), new(AddressSet, 0x8080, 0x1f), new(AddressSet, 0x8082, 0x1f),
     new(AddressSet, 0x8084, 0x1f), new(AddressSet, 0x8085, 0x102), new(AddressSet, 0x8092, 0x1f),
     new(AddressSet, 0x8095, 0x102), new(AddressSet, 0x80a2, 0x1f), new(AddressSet, 0x80a5, 0x102),
     new(AddressSet, 0x8062, 0x1f)];
    private static readonly uint[][] PostalTags =
    [ [0x3a29001f, 0x3a27001f, 0x3a28001f, 0x3a2a001f, 0x3a26001f],
      [0x3a5d001f, 0x3a59001f, 0x3a5c001f, 0x3a5b001f, 0x3a5a001f],
      [0x3a63001f, 0x3a5f001f, 0x3a62001f, 0x3a61001f, 0x3a60001f] ];

    public Task<IReadOnlyList<ContactInfo>> GetContactsAsync(string folderId, string? search, CancellationToken ct = default) => Run<IReadOnlyList<ContactInfo>>(async () =>
    {
        if (string.IsNullOrEmpty(folderId)) folderId = (await DefaultFolderAsync(FolderKind.Contacts, ct)).Id;
        var fid = ParseFolder(folderId); var named = await Session.ResolvePropertyTagsAsync(Logon, ContactNames, false, ct);
        var tags = ContactStrings.Keys.Concat(PostalTags.SelectMany(x => x)).Concat(named.Where(t => t != 0)).Concat(new uint[] { 0x674a0014, 0x001a001f, 0x3a420040, 0x3a410040 }).ToArray();
        var table = await Session.ReadTableAsync(Logon, fid, false, tags, 5000, ct);
        if (table.Total > table.Rows.Count) throw new MailServiceException("Contact folder exceeds the current 5000-item limit.");
        var contacts = table.Rows.Select(row => MapContact(folderId, (ulong)row[0x674a0014u]!, row, named)).ToList();
        return contacts.Where(c => string.IsNullOrWhiteSpace(search) ||
            string.Join(" ", c.DisplayName, c.PrimaryEmail, c.Company).Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }, ct);

    public Task<ContactInfo> GetContactAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        var (fid, mid) = ParseMessage(itemId); var named = await Session.ResolvePropertyTagsAsync(Logon, ContactNames, false, ct);
        var tags = ContactStrings.Keys.Concat(PostalTags.SelectMany(x => x)).Concat(named.Where(t => t != 0)).Concat(new uint[] { 0x001a001f, 0x3a420040, 0x3a410040 }).ToArray();
        var row = await Session.ReadMessagePropertiesAsync(Logon, fid, mid, tags, ct);
        var body = await Session.ReadMessageAsync(Logon, fid, mid, ct);
        row[0x1000001f] = HtmlUtil.ToPlainText(PimBody(body));
        return MapContact(FolderId(fid), mid, row, named);
    }, ct);

    private ContactInfo MapContact(string folder, ulong mid, Dictionary<uint, object?> row, uint[] named)
    {
        var contact = new ContactInfo { Id = $"mapi-m:{ParseFolder(folder):X16}:{mid:X16}", AccountId = Account.Id, FolderId = folder,
            IsGroup = Text(row, 0x001a001f).StartsWith("IPM.DistList", StringComparison.OrdinalIgnoreCase),
            FileAs = Text(row, named[0]), Email1 = Text(row, named[1]), Email2 = Text(row, named[2]), Email3 = Text(row, named[3]),
            Email1Name = Text(row, named[4]), ImAddress = Text(row, named[12]),
            Birthday = row.GetValueOrDefault(0x3a420040u) is DateTime birthday ? birthday.ToLocalTime() : null,
            Anniversary = row.GetValueOrDefault(0x3a410040u) is DateTime anniversary ? anniversary.ToLocalTime() : null };
        foreach (var (tag, name) in ContactStrings) typeof(ContactInfo).GetProperty(name)!.SetValue(contact, Text(row, tag));
        var addresses = new[] { contact.BusinessAddress, contact.HomeAddress, contact.OtherAddress };
        for (int i = 0; i < addresses.Length; i++)
        {
            var a = addresses[i]; var t = PostalTags[i];
            a.Street = Text(row, t[0]); a.City = Text(row, t[1]); a.State = Text(row, t[2]); a.PostalCode = Text(row, t[3]); a.Country = Text(row, t[4]);
        }
        return contact;
    }

    public Task<string> SaveContactAsync(string folderId, ContactInfo contact, CancellationToken ct = default) => Run(async () =>
    {
        // Do not discard unimplemented structured data during an update.
        if (contact.IsGroup || contact.Members.Count > 0 || contact.Photo != null || contact.Categories.Count > 0)
            throw new MailServiceException("Contact groups, photos and contact categories are not implemented in native MAPI yet.");
        foreach (var email in new[] { contact.Email1, contact.Email2, contact.Email3 }.Where(s => !string.IsNullOrEmpty(s)))
            if (!System.Net.Mail.MailAddress.TryCreate(email, out _)) throw new MailServiceException("Enter a valid SMTP email address for the contact.");
        if (string.IsNullOrEmpty(folderId)) folderId = (await DefaultFolderAsync(FolderKind.Contacts, ct)).Id;
        var fid = ParseFolder(folderId); ulong? mid = string.IsNullOrEmpty(contact.Id) ? null : ParseMessage(contact.Id).message;
        if (mid != null && ParseMessage(contact.Id).folder != fid) throw new MailServiceException("Contact folder mismatch.");
        if (mid != null)
        {
            var current = await Session.ReadMessagePropertiesAsync(Logon, fid, mid.Value, [0x001a001f], ct);
            if (Text(current, 0x001a001f) != "IPM.Contact") throw new MailServiceException("Only a personal contact can be edited here.");
        }
        var named = await Session.ResolvePropertyTagsAsync(Logon, ContactNames, true, ct);
        var props = ContactStrings.ToDictionary(x => x.Key, x => (object)(string)typeof(ContactInfo).GetProperty(x.Value)!.GetValue(contact)!);
        props[0x001a001f] = "IPM.Contact"; props[0x0e070003] = 1u; props[0x3fde0003] = 65001u;
        props[named[0]] = contact.FileAs; props[named[1]] = contact.Email1; props[named[2]] = contact.Email2; props[named[3]] = contact.Email3;
        props[named[4]] = contact.Email1Name; props[named[5]] = "SMTP"; props[named[6]] = contact.Email1;
        props[named[7]] = OneOff(contact.Email1Name, contact.Email1); props[named[8]] = "SMTP"; props[named[9]] = OneOff(contact.DisplayName, contact.Email2);
        props[named[10]] = "SMTP"; props[named[11]] = OneOff(contact.DisplayName, contact.Email3); props[named[12]] = contact.ImAddress;
        var addresses = new[] { contact.BusinessAddress, contact.HomeAddress, contact.OtherAddress };
        for (int i = 0; i < addresses.Length; i++)
        {
            var a = addresses[i]; var values = new[] { a.Street, a.City, a.State, a.PostalCode, a.Country };
            for (int j = 0; j < values.Length; j++) props[PostalTags[i][j]] = values[j];
        }
        var deleted = new List<uint>();
        if (contact.Birthday.HasValue) props[0x3a420040] = contact.Birthday.Value; else deleted.Add(0x3a420040);
        if (contact.Anniversary.HasValue) props[0x3a410040] = contact.Anniversary.Value; else deleted.Add(0x3a410040);
        var saved = await Session.WriteMessageAsync(Logon, fid, mid, props, Encoding.UTF8.GetBytes(HtmlUtil.PlainTextToHtml(contact.Notes)), [], [], [], false, ct, deleted.ToArray());
        return contact.Id = $"mapi-m:{fid:X16}:{saved.Id:X16}";
    }, ct);

    internal static byte[] OneOff(string name, string email) => MapiWire.Write(w =>
    {
        w.Write(0u); w.Write(Convert.FromHexString("812B1FA4BEA310199D6E00DD010F5402"));
        w.Write((ushort)0); w.Write(new byte[] { 1, 0x80 });
        MapiWriteWire.UnicodeZ(w, name); MapiWriteWire.UnicodeZ(w, "SMTP"); MapiWriteWire.UnicodeZ(w, email);
    });

    private static string PimBody(MapiMessageData data)
    {
        if (data.Html is { Length: > 0 })
        {
            var codePage = Number(data.Properties, 0x3fde0003);
            if (codePage == 0) throw new MailServiceException("The item body has no declared HTML encoding.");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)codePage).GetString(data.Html);
        }
        return data.Text != null ? HtmlUtil.PlainTextToHtml(Encoding.Unicode.GetString(data.Text)) : "";
    }

    public Task DeleteContactAsync(string itemId, CancellationToken ct = default) => DeleteItemsAsync([itemId], false, ct);
}
