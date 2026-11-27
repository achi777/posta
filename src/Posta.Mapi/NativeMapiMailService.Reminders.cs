using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    public async Task<IReadOnlyList<ReminderInfo>> GetUpcomingRemindersAsync(DateTime until, CancellationToken ct = default)
    {
        var result = new List<ReminderInfo>();
        foreach (var calendar in await GetOwnCalendarsAsync(ct))
        {
            var events = await GetEventsAsync(calendar, until.AddDays(-30), until.AddDays(30), ct);
            foreach (var item in events.Where(e => e.ReminderSet && !e.IsCancelled))
            {
                var due = item.Start.AddMinutes(-item.ReminderMinutes);
                if (due > until) continue;
                result.Add(new ReminderInfo
                {
                    AccountId = Account.Id, ItemId = item.Id, Key = $"{Account.Id}|{item.Id}|{due.ToUniversalTime().Ticks}",
                    Subject = item.Subject, Location = item.Location, Start = item.Start, DueBy = due
                });
            }
        }
        var folders = await GetFolderTreeAsync(ct: ct);
        foreach (var folder in folders.Where(f => f.IsTaskFolder))
        {
            foreach (var task in await GetTasksAsync(folder.Id, false, ct))
            {
                if (!task.ReminderSet || task.ReminderTime is not DateTime due || due > until) continue;
                result.Add(new ReminderInfo
                {
                    AccountId = Account.Id, ItemId = task.Id, Key = $"{Account.Id}|{task.Id}|{due.ToUniversalTime().Ticks}",
                    Subject = task.Subject, Start = task.DueDate ?? due, DueBy = due, IsTask = true
                });
            }
        }
        return result.OrderBy(r => r.DueBy).ToList();
    }

    public Task DismissReminderAsync(string itemId, CancellationToken ct = default) => Run(async () =>
    {
        var (master, original) = CalendarItemId(itemId);
        if (original.HasValue) return true; // AppServices persists the dismissal for this occurrence.
        var (folder, message) = ParseMessage(master);
        var tag = (await Session.ResolvePropertyTagsAsync(Logon, [new(CommonSet, 0x8503, 0xb)], false, ct))[0];
        if (tag == 0) return true;
        var properties = await Session.ReadMessagePropertiesAsync(Logon, folder, message, [0x001a001f, tag], ct);
        if (Text(properties, 0x001a001f) is not ("IPM.Appointment" or "IPM.Task"))
            throw new MailServiceException("Only appointment and task reminders can be dismissed here.");
        if (properties.GetValueOrDefault(tag) is true)
            await Session.SetMessagePropertiesAsync(Logon, folder, message, new Dictionary<uint, object> { [tag] = false }, ct);
        return true;
    }, ct);
}
