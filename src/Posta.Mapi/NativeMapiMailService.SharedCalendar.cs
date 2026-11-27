using System.Net.Mail;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    private const string SharedCalendarPrefix = "mapi-shared:";
    private const uint AccessTag = 0x0ff40003, ModifyAccess = 1, DeleteAccess = 4, CreateContentsAccess = 0x10;
    private sealed record DelegateMailbox(string Owner, MapiHttpSession Session, MapiLogon Logon);
    private sealed record SharedCalendar(string Owner, string Name, ulong Folder, MapiHttpSession Session, MapiLogon Logon, uint Access)
    {
        public string Prefix => SharedCalendarPrefix + Owner + "|";
        public string FolderId => Prefix + NativeMapiMailService.FolderId(Folder);
    }

    private readonly Dictionary<string, SharedCalendar> _sharedCalendars = new(StringComparer.OrdinalIgnoreCase);

    public Task<CalendarSource> OpenSharedCalendarAsync(string ownerEmail, CancellationToken ct = default) => Run(async () =>
    {
        var entry = await ConnectSharedCalendarAsync(ownerEmail, ct);
        return new CalendarSource
        {
            Key = $"{Account.Id}|{entry.Owner}|calendar",
            AccountId = Account.Id,
            FolderId = entry.FolderId,
            OwnerEmail = entry.Owner,
            OwnerName = entry.Name,
            DisplayName = entry.Name,
            Group = CalendarGroupKind.SharedCalendars,
            IsShared = true,
            IsChecked = true,
            Access = (entry.Access & CreateContentsAccess) != 0 ? CalendarAccess.Author : CalendarAccess.ReadAll
        };
    }, ct);

    private async Task<SharedCalendar> ConnectSharedCalendarAsync(string email, CancellationToken ct)
    {
        var owner = DelegateOwner(email);
        if (_sharedCalendars.TryGetValue(owner, out var existing))
        {
            if (existing.Session.IsUsable) return existing;
            _sharedCalendars.Remove(owner);
            await existing.Session.DisposeAsync();
        }
        var (mailboxOwner, session, logon) = await OpenDelegateMailboxAsync(owner, ct);
        try
        {
            var folder = await session.ReadDelegatorCalendarIdAsync(logon, ct);
            await session.ReadTableAsync(logon, folder, false, [0x674a0014], 1, ct);
            var properties = await session.ReadFolderPropertiesAsync(logon, folder, [AccessTag], ct);
            var entry = new SharedCalendar(mailboxOwner, mailboxOwner, folder, session, logon, Number(properties, AccessTag));
            _sharedCalendars.Add(owner, entry);
            return entry;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static string DelegateOwner(string email)
    {
        if (!MailAddress.TryCreate(email.Trim(), out var parsed) || parsed.Address.Contains('|'))
            throw new MailServiceException("Enter the shared mailbox owner's email address.");
        return parsed.Address.ToLowerInvariant();
    }

    private async Task<DelegateMailbox> OpenDelegateMailboxAsync(string owner, CancellationToken ct)
    {
        if (_discoveryUri == null || _primaryLegacyDn == null || _credentials == null || State != ConnectionState.Connected)
            throw new MailServiceException("Connect the Exchange account first.");
        if (owner.Equals(Account.Email, StringComparison.OrdinalIgnoreCase))
            throw new MailServiceException("This address is already the primary mailbox.");
        var endpoint = await MapiHttpSession.DiscoverAsync(_discoveryUri, owner, _credentials, ct);
        if (endpoint.MailStore.GetLeftPart(UriPartial.Authority) != _discoveryUri.GetLeftPart(UriPartial.Authority))
            throw new MailServiceException("Exchange advertised a shared mailbox on a different server. This connection accepts only the configured Exchange origin.");
        if (endpoint.LegacyDn.Equals(_primaryLegacyDn, StringComparison.OrdinalIgnoreCase))
            throw new MailServiceException("This address is an alias of the primary mailbox.");

        var session = new MapiHttpSession(endpoint.MailStore, _credentials);
        try
        {
            // Authenticate the existing account. Exchange evaluates delegated access.
            await session.ConnectAsync(_primaryLegacyDn, ct);
            var logon = await session.LogonDelegatorAsync(endpoint.LegacyDn, ct);
            if (logon.MailboxGuid == Logon.MailboxGuid)
                throw new MailServiceException("Exchange returned the primary mailbox instead of the shared mailbox.");
            return new(owner, session, logon);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task CloseSharedCalendarsAsync()
    {
        foreach (var entry in _sharedCalendars.Values)
            await entry.Session.DisposeAsync();
        _sharedCalendars.Clear();
    }

    private static (string Owner, string Id) SplitSharedCalendarId(string id)
    {
        var separator = id.IndexOf('|', SharedCalendarPrefix.Length);
        if (separator < 0) throw new MailServiceException("Invalid shared calendar identifier.");
        return (id[SharedCalendarPrefix.Length..separator], id[(separator + 1)..]);
    }
}
