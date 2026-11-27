using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    private static readonly MapiPropertyName CategoriesName = new(new("00020329-0000-0000-c000-000000000046"), 0, 0x101f, "Keywords");
    private static readonly MapiPropertyName[] FlagNames = [new(TaskSet, 0x8104, 0x40), new(TaskSet, 0x8105, 0x40),
        new(TaskSet, 0x8101, 3), new(TaskSet, 0x811c, 0xb), new(TaskSet, 0x8102, 5), new(TaskSet, 0x810f, 0x40)];

    public Task SetCategoriesAsync(string itemId, IEnumerable<string> categories, CancellationToken ct = default) => Run(async () =>
    {
        var values = categories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tag = (await Session.ResolvePropertyTagsAsync(Logon, [CategoriesName], true, ct))[0];
        var (fid, mid) = ParseMessage(itemId);
        await Session.SetMessagePropertiesAsync(Logon, fid, mid, new Dictionary<uint, object> { [tag] = values }, ct);
        return true;
    }, ct);

    public Task SetFlagAsync(IEnumerable<string> itemIds, FlagState state, DateTime? start, DateTime? due, CancellationToken ct = default) => Run(async () =>
    {
        if (!Enum.IsDefined(state)) throw new ArgumentException("Invalid flag state.");
        var items = itemIds.Select(ParseMessage).ToArray();
        var n = await Session.ResolvePropertyTagsAsync(Logon, FlagNames, true, ct);
        var deleted = new List<uint>();
        var props = new Dictionary<uint, object> { [0x10900003] = state switch { FlagState.Flagged => 2u, FlagState.Complete => 1u, _ => 0u },
            [n[2]] = state == FlagState.Complete ? 2u : 0u, [n[3]] = state == FlagState.Complete, [n[4]] = state == FlagState.Complete ? 1d : 0d };
        void OptionalDate(uint tag, DateTime? date) { if (date.HasValue) props[tag] = date.Value; else deleted.Add(tag); }
        OptionalDate(n[0], state == FlagState.Flagged ? start : null); OptionalDate(n[1], state == FlagState.Flagged ? due : null);
        OptionalDate(n[5], state == FlagState.Complete ? DateTime.Now : null);
        foreach (var (fid, mid) in items) await Session.SetMessagePropertiesAsync(Logon, fid, mid, props, ct, deleted.ToArray());
        return true;
    }, ct);

    public async Task<IReadOnlyList<NewMailNotification>> GetNewMailSinceAsync(DateTime sinceUtc, CancellationToken ct = default)
    {
        var inbox = await GetWellKnownFolderAsync(FolderKind.Inbox, ct: ct) ?? throw new MailServiceException("Inbox unavailable.");
        var result = await GetItemsAsync(new() { FolderId = inbox.Id, PageSize = 5000 }, ct);
        return result.Items.Where(m => m.Received.ToUniversalTime() > sinceUtc.ToUniversalTime()).Select(m => new NewMailNotification
        { AccountId = Account.Id, ItemId = m.Id, From = m.From.Display, Subject = m.Subject, Received = m.Received }).ToList();
    }
}
