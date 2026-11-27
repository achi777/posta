using System.Text;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    private static readonly Guid TaskSet = new("00062003-0000-0000-c000-000000000046");
    private static readonly Guid CommonSet = new("00062008-0000-0000-c000-000000000046");
    private static readonly MapiPropertyName[] TaskNames =
    [new(TaskSet, 0x8101, 3), new(TaskSet, 0x8102, 5), new(TaskSet, 0x8104, 0x40), new(TaskSet, 0x8105, 0x40),
     new(TaskSet, 0x811c, 0xb), new(TaskSet, 0x810f, 0x40), new(CommonSet, 0x8503, 0xb), new(CommonSet, 0x8502, 0x40)];

    private static uint[] TaskColumns(uint[] named) => [0x674a0014, 0x001a001f, 0x0037001f, 0x00170003, 0x1000001f, .. named.Where(t => t != 0)];
    public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(string folderId, bool includeCompleted, CancellationToken ct = default)
        => string.IsNullOrEmpty(folderId) ? AllTasksAsync(includeCompleted, ct) : ReadTasksAsync(folderId, includeCompleted, ct);

    private async Task<IReadOnlyList<TaskInfo>> AllTasksAsync(bool includeCompleted, CancellationToken ct)
    {
        var folders = await GetFolderTreeAsync(ct: ct); var result = new List<TaskInfo>();
        foreach (var folder in folders.Where(f => f.Kind == FolderKind.Tasks)) result.AddRange(await ReadTasksAsync(folder.Id, includeCompleted, ct));
        foreach (var folder in folders.Where(f => f.IsMailFolder && f.Kind is not (FolderKind.Root or FolderKind.DeletedItems or FolderKind.JunkEmail or FolderKind.SyncIssues)))
        {
            result.AddRange(await ReadFlaggedTasksAsync(folder.Id, includeCompleted, ct));
        }
        return result;
    }

    private Task<IReadOnlyList<TaskInfo>> ReadFlaggedTasksAsync(string folderId, bool includeCompleted, CancellationToken ct) => Run<IReadOnlyList<TaskInfo>>(async () =>
    {
        var n = await Session.ResolvePropertyTagsAsync(Logon, TaskNames, false, ct);
        var columns = TaskColumns(n).Concat(new uint[] { 0x10900003 }).ToArray();
        var restriction = MapiRestrictions.Property(0x10900003, 2u);
        if (includeCompleted) restriction = MapiRestrictions.Or(restriction, MapiRestrictions.Property(0x10900003, 1u));
        var table = await Session.ReadTableAsync(Logon, ParseFolder(folderId), false, columns, 5000, ct, restriction);
        if (table.Total > table.Rows.Count) throw new MailServiceException("Flagged task results exceed the current 5000-item limit.");
        return table.Rows.Where(row => IsFlaggedTask(Number(row, 0x10900003), includeCompleted)).Select(row =>
        {
            var task = MapTask(folderId, (ulong)row[0x674a0014u]!, row, n); task.IsFlaggedMail = true;
            if (Number(row, 0x10900003) == 1) { task.Status = TaskState.Completed; task.PercentComplete = 100; }
            return task;
        }).ToList();
    }, ct);

    internal static bool IsFlaggedTask(uint flag, bool includeCompleted) => flag == 2 || (includeCompleted && flag == 1);

    private Task<IReadOnlyList<TaskInfo>> ReadTasksAsync(string folderId, bool includeCompleted, CancellationToken ct) => Run<IReadOnlyList<TaskInfo>>(async () =>
    {
        var fid = ParseFolder(folderId); var named = await Session.ResolvePropertyTagsAsync(Logon, TaskNames, false, ct);
        var table = await Session.ReadTableAsync(Logon, fid, false, TaskColumns(named), 5000, ct);
        if (table.Total > table.Rows.Count) throw new MailServiceException("Task folder exceeds the current 5000-item limit.");
        return table.Rows.Where(r => Text(r, 0x001a001f).StartsWith("IPM.Task", StringComparison.OrdinalIgnoreCase))
            .Select(r => MapTask(folderId, (ulong)r[0x674a0014u]!, r, named)).Where(t => includeCompleted || !t.IsComplete).ToList();
    }, ct);

    public Task<TaskInfo> GetTaskAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        var (fid, mid) = ParseMessage(itemId); var named = await Session.ResolvePropertyTagsAsync(Logon, TaskNames, false, ct);
        var row = await Session.ReadMessagePropertiesAsync(Logon, fid, mid, TaskColumns(named), ct);
        if (!Text(row, 0x001a001f).StartsWith("IPM.Task", StringComparison.OrdinalIgnoreCase)) throw new MailServiceException("This item is not a task.");
        var result = MapTask(FolderId(fid), mid, row, named);
        result.BodyHtml = PimBody(await Session.ReadMessageAsync(Logon, fid, mid, ct));
        return result;
    }, ct);

    private TaskInfo MapTask(string folder, ulong mid, Dictionary<uint, object?> row, uint[] n) => new()
    {
        Id = $"mapi-m:{ParseFolder(folder):X16}:{mid:X16}", FolderId = folder, AccountId = Account.Id,
        Subject = Text(row, 0x0037001f), Importance = (Importance)Math.Min(2, Number(row, 0x00170003)),
        Status = (TaskState)Math.Min(4, Number(row, n[0])),
        PercentComplete = row.GetValueOrDefault(n[1]) is double fraction ? (int)Math.Clamp(Math.Round(fraction * 100), 0, 100) : 0,
        StartDate = NullableDate(row, n[2]), DueDate = NullableDate(row, n[3]), CompleteDate = NullableDate(row, n[5]),
        ReminderSet = row.GetValueOrDefault(n[6]) is true, ReminderTime = NullableDate(row, n[7]),
        BodyHtml = HtmlUtil.PlainTextToHtml(Text(row, 0x1000001f))
    };

    private static DateTime? NullableDate(Dictionary<uint, object?> row, uint tag) => row.GetValueOrDefault(tag) is DateTime date ? date.ToLocalTime() : null;

    public Task<string> SaveTaskAsync(string folderId, TaskInfo task, CancellationToken ct = default) => Run(async () =>
    {
        if (task.IsFlaggedMail || task.Categories.Count > 0) throw new MailServiceException("Flagged-mail tasks and task categories are not editable in native MAPI yet.");
        if (!Enum.IsDefined(task.Status) || task.PercentComplete is < 0 or > 100 || !Enum.IsDefined(task.Importance)) throw new MailServiceException("Invalid task state/progress.");
        if (task.ReminderSet && task.ReminderTime == null) throw new MailServiceException("Specify the reminder time.");
        if (string.IsNullOrEmpty(folderId)) folderId = (await DefaultFolderAsync(FolderKind.Tasks, ct)).Id;
        var fid = ParseFolder(folderId); ulong? mid = null;
        if (!string.IsNullOrEmpty(task.Id))
        {
            var existing = ParseMessage(task.Id); if (existing.folder != fid) throw new MailServiceException("Task folder mismatch."); mid = existing.message;
            var current = await Session.ReadMessagePropertiesAsync(Logon, fid, mid.Value, [0x001a001f], ct);
            if (Text(current, 0x001a001f) != "IPM.Task") throw new MailServiceException("Only a task can be edited here.");
        }
        var n = await Session.ResolvePropertyTagsAsync(Logon, TaskNames, true, ct);
        var props = new Dictionary<uint, object> { [0x001a001f] = "IPM.Task", [0x0037001f] = task.Subject,
            [0x0e070003] = 1u, [0x00170003] = (uint)task.Importance, [0x3fde0003] = 65001u, [0x1000001f] = HtmlUtil.ToPlainText(task.BodyHtml),
            [n[0]] = (uint)task.Status, [n[1]] = task.IsComplete ? 1d : task.PercentComplete / 100d,
            [n[4]] = task.IsComplete, [n[6]] = task.ReminderSet };
        var deleted = new List<uint>();
        void DateValue(uint tag, DateTime? date) { if (date.HasValue) props[tag] = date.Value; else deleted.Add(tag); }
        DateValue(n[2], task.StartDate); DateValue(n[3], task.DueDate);
        DateValue(n[5], task.IsComplete ? task.CompleteDate ?? DateTime.Today : null);
        DateValue(n[7], task.ReminderSet ? task.ReminderTime : null);
        var saved = await Session.WriteMessageAsync(Logon, fid, mid, props, Encoding.UTF8.GetBytes(task.BodyHtml), [], [], [], false, ct, deleted.ToArray());
        return task.Id = $"mapi-m:{fid:X16}:{saved.Id:X16}";
    }, ct);

    public Task DeleteTaskAsync(string itemId, CancellationToken ct = default) => DeleteItemsAsync([itemId], false, ct);
}
