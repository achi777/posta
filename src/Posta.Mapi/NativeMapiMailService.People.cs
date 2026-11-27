using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    // Upper bound on how much of the directory is cached for name resolution (one-time per session).
    private const int DirectoryCap = 20000;
    private IReadOnlyList<Dictionary<uint, object?>>? _directory;

    private MapiAddressBook AddressBook()
    {
        if (_addressBookUri == null || _credentials == null)
            throw new MailServiceException("Autodiscover did not advertise a MAPI/HTTP address book endpoint on this server, so the directory (GAL) is unavailable.");
        return _addressBook ??= new MapiAddressBook(_addressBookUri, _credentials);
    }

    private async Task<IReadOnlyList<Dictionary<uint, object?>>> DirectoryAsync(CancellationToken ct)
        => _directory ??= await AddressBook().LoadDirectoryAsync(DirectoryCap, ct);

    private static string? Opt(Dictionary<uint, object?> row, uint tag) => row.GetValueOrDefault(tag) is string s && s.Length > 0 ? s : null;

    public Task<IReadOnlyList<Recipient>> ExpandGroupAsync(string email, CancellationToken ct = default)
        => Run<IReadOnlyList<Recipient>>(async () =>
        {
            var directory = await DirectoryAsync(ct);
            var matches = directory.Where(row =>
                string.Equals(Text(row, 0x39fe001f), email, StringComparison.OrdinalIgnoreCase) &&
                (Number(row, 0x0ffe0003) == 8 || Number(row, 0x39000003) == 1)).Take(2).ToArray();
            if (matches.Length != 1 || string.IsNullOrWhiteSpace(Text(matches[0], 0x3003001f)))
                throw new MailServiceException("The distribution group could not be uniquely resolved in Exchange.");
            var rows = await AddressBook().ReadGroupMembersAsync(Text(matches[0], 0x3003001f), ct);
            var recipients = rows.Select(row => new Recipient(Text(row, 0x3001001f), Text(row, 0x39fe001f))).ToList();
            if (recipients.Any(recipient => !System.Net.Mail.MailAddress.TryCreate(recipient.Address, out _)))
                throw new MailServiceException("Exchange returned a group member without an SMTP address. Expansion would omit recipients.");
            return recipients.DistinctBy(r => r.Address, StringComparer.OrdinalIgnoreCase).ToList();
        }, ct);

    public Task<IReadOnlyList<ResolvedName>> ResolveNamesAsync(string query, bool directoryOnly = false, CancellationToken ct = default) => Run<IReadOnlyList<ResolvedName>>(async () =>
    {
        var directory = await DirectoryAsync(ct);
        IEnumerable<Dictionary<uint, object?>> rows = directory;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            bool Has(Dictionary<uint, object?> r, uint tag) => r.GetValueOrDefault(tag) is string s && s.Contains(q, StringComparison.OrdinalIgnoreCase);
            rows = directory.Where(r => Has(r, 0x3001001f) || Has(r, 0x3a00001f) || Has(r, 0x39fe001f) || Has(r, 0x3003001f));
        }
        var result = new List<ResolvedName>();
        foreach (var row in rows.Take(200))
        {
            var smtp = Text(row, 0x39fe001f);
            var email = smtp.Length > 0 ? smtp : Text(row, 0x3003001f);
            var name = Text(row, 0x3001001f);
            if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(name)) continue;
            var displayType = Number(row, 0x39000003); var objectType = Number(row, 0x0ffe0003);
            result.Add(new ResolvedName
            {
                Name = name.Length > 0 ? name : email, Email = email,
                Kind = objectType == 8 || displayType == 1 ? RecipientKind.DistributionList
                    : displayType == 7 ? RecipientKind.Room : RecipientKind.Mailbox,
                Title = Opt(row, 0x3a17001f), Department = Opt(row, 0x3a18001f), Company = Opt(row, 0x3a16001f),
                Office = Opt(row, 0x3a19001f), Phone = Opt(row, 0x3a08001f), Mobile = Opt(row, 0x3a1c001f), Alias = Opt(row, 0x3a00001f)
            });
        }
        return result;
    }, ct);
}
