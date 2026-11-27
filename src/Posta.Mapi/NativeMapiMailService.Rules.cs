using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    private const uint RuleId = 0x66740014, RuleSequence = 0x66760003, RuleState = 0x66770003, RuleName = 0x6682001f;

    public Task SaveRulesAsync(IReadOnlyList<RuleInfo> rules, IReadOnlyList<string> deletedRuleIds, CancellationToken ct = default)
        => Run(async () =>
        {
            var inbox = Logon.FolderIds[4];
            var current = (await Session.ReadRulesAsync(Logon, inbox, ct))
                .ToDictionary(row => (ulong)(row[RuleId] ?? throw new MapiException("Missing rule ID.")));
            var seen = new HashSet<ulong>();
            var changes = new List<(byte Action, Dictionary<uint, object> Values)>();
            var currentOrder = current.OrderBy(p => Number(p.Value, RuleSequence)).Select(p => p.Key).ToArray();
            var ordered = rules.Where(r => r.Id != null).Select(r => ParseRule(r.Id!)).ToArray();
            var deleted = deletedRuleIds.Select(ParseRule).ToHashSet();
            if (!current.Keys.ToHashSet().SetEquals(ordered.Concat(deleted)))
                throw new MailServiceException("Inbox rules changed on Exchange. Reopen the rules window before saving.");
            var renumber = rules.Take(ordered.Length).Any(r => r.Id == null) ||
                !ordered.SequenceEqual(currentOrder.Where(id => !deleted.Contains(id)));
            var lastSequence = current.Count == 0 ? 0u : current.Values.Max(row => Number(row, RuleSequence));
            for (var i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (string.IsNullOrWhiteSpace(rule.Name)) throw new MailServiceException("Enter a rule name.");
                var sequence = checked((uint)(i + 1));
                if (rule.Id == null)
                {
                    var ruleFolders = rule.MoveToFolderId != null || rule.CopyToFolderId != null ? await Folders(ct) : [];
                    changes.Add((1, CreateStandardRule(rule, renumber ? sequence : checked(++lastSequence), ruleFolders)));
                    continue;
                }
                var id = ParseRule(rule.Id);
                if (!seen.Add(id) || deleted.Contains(id)) throw new MailServiceException("Duplicate Inbox rule ID.");
                var previous = current[id];
                var changesForRule = new Dictionary<uint, object> { [RuleId] = id };
                if (rule.Name != Text(previous, RuleName))
                    throw new MailServiceException("This connection does not support renaming existing Inbox rules.");
                var state = Number(previous, RuleState);
                if (((state & 1) != 0) != rule.IsEnabled) changesForRule[RuleState] = (state & ~1u) | (rule.IsEnabled ? 1u : 0u);
                if (renumber && Number(previous, RuleSequence) != sequence) changesForRule[RuleSequence] = sequence;
                if (changesForRule.Count > 1) changes.Add((2, changesForRule));
            }
            foreach (var id in deleted) changes.Add((4, new() { [RuleId] = id }));
            await Session.ModifyRulesAsync(Logon, inbox, changes, ct);
            return true;
        }, ct);

    private static ulong ParseRule(string id) =>
        id.StartsWith("mapi-rule:", StringComparison.Ordinal) &&
        ulong.TryParse(id.AsSpan(10), System.Globalization.NumberStyles.HexNumber, null, out var parsed)
            ? parsed : throw new MailServiceException("Invalid native Inbox rule ID.");

    private static Dictionary<uint, object> CreateStandardRule(RuleInfo rule, uint sequence, IReadOnlyList<MailFolderInfo> folders)
    {
        if (rule.FromAddresses.Count > 0 || rule.SentToAddresses.Count > 0 || rule.SentOnlyToMe || rule.MyNameInCc ||
            rule.PermanentDelete || rule.ForwardTo.Count > 0 || rule.RedirectTo.Count > 0 || rule.AssignCategories.Count > 0)
            throw new MailServiceException("This rule uses a condition or action that is unavailable over the direct connection.");
        var conditions = new List<byte[]>();
        AddTerms(rule.SubjectContains, 0x0037001f);
        AddTerms(rule.BodyContains, 0x1000001f);
        AddTerms(rule.SenderContains, 0x5d01001f);
        if (rule.HasAttachments) conditions.Add(MapiRestrictions.Property(0x0e1b000b, true));
        if (rule.WithImportance is { } importance) conditions.Add(MapiRestrictions.Property(0x00170003, (uint)importance));
        if (conditions.Count == 0) throw new MailServiceException("Select at least one condition for a new Exchange rule.");

        var actions = new List<byte[]>();
        if (rule.MarkAsRead) actions.Add(RuleAction(0x0b));
        if (rule.MarkImportance is { } setImportance)
            actions.Add(RuleAction(0x09, w => MapiWriteWire.Value(w, 0x00170003, (uint)setImportance)));
        if (rule.CopyToFolderId != null) actions.Add(FolderAction(0x02, rule.CopyToFolderId, folders));
        if (rule.MoveToFolderId != null) actions.Add(FolderAction(0x01, rule.MoveToFolderId, folders));
        if (rule.Delete) actions.Add(RuleAction(0x0a));
        if (actions.Count == 0) throw new MailServiceException("Select an action for the Exchange rule.");

        var actionValue = MapiWire.Write(w =>
        {
            w.Write(checked((ushort)actions.Count));
            foreach (var action in actions)
            {
                w.Write(checked((ushort)action.Length)); w.Write(action);
            }
        });
        return new()
        {
            [RuleName] = rule.Name, [RuleSequence] = sequence,
            [RuleState] = (rule.IsEnabled ? 1u : 0u) | (rule.StopProcessing ? 0x10u : 0u),
            [0x6681001f] = "Posta", [0x667900fd] = MapiRestrictions.Combine(false, conditions),
            [0x668000fe] = actionValue
        };

        void AddTerms(List<string> terms, uint tag)
        {
            if (terms.Any(string.IsNullOrWhiteSpace)) throw new MailServiceException("Rule search terms cannot be blank.");
            if (terms.Count != 0) conditions.Add(MapiRestrictions.Combine(true, terms.Select(term => MapiRestrictions.Contains(tag, term)).ToArray()));
        }
    }

    private static byte[] RuleAction(byte type, Action<BinaryWriter>? write = null) => MapiWire.Write(w =>
    { w.Write(type); w.Write(0u); w.Write(0u); write?.Invoke(w); });

    private static byte[] FolderAction(byte type, string folderId, IReadOnlyList<MailFolderInfo> folders)
    {
        if (!folders.Any(folder => folder.Id == folderId && folder.IsMailFolder && folder.Kind != FolderKind.Root))
            throw new MailServiceException("Select a mail folder in the same Exchange mailbox for this rule.");
        return RuleAction(type, w =>
        {
            w.Write((byte)1); // Same mailbox. The standard rule ignores the nonempty StoreEID.
            w.Write((ushort)1); w.Write((byte)0);
            w.Write((ushort)21); w.Write((byte)1); w.Write(ParseFolder(folderId)); w.Write(0ul); w.Write(0u);
        });
    }

    public Task<IReadOnlyList<RuleInfo>> GetRulesAsync(CancellationToken ct = default)
        => Run<IReadOnlyList<RuleInfo>>(async () =>
        {
            var rows = await Session.ReadRulesAsync(Logon, Logon.FolderIds[4], ct);
            return rows.Select(row =>
            {
                if (row.GetValueOrDefault(0x66740014u) is not ulong id ||
                    row.GetValueOrDefault(0x66770003u) is not uint state)
                    throw new MailServiceException("Exchange returned an incomplete inbox rule.");
                return new RuleInfo
                {
                    Id = $"mapi-rule:{id:X16}", Name = Text(row, 0x6682001f),
                    Priority = checked((int)Number(row, 0x66760003)), IsEnabled = (state & 1) != 0,
                    StopProcessing = (state & 0x10) != 0, HasUnsupportedParts = true
                };
            }).OrderBy(rule => rule.Priority).ToList();
        }, ct);
}
