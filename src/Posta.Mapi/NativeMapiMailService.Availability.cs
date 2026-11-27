using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.Mapi;

public sealed partial class NativeMapiMailService
{
    public async Task<IReadOnlyList<AttendeeAvailability>> GetAvailabilityAsync(IEnumerable<string> emails, DateTime start, DateTime end, CancellationToken ct = default)
    {
        if (end <= start) throw new MailServiceException("Availability end must be after its start.");
        var result = new List<AttendeeAvailability>();
        foreach (var email in emails.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var availability = new AttendeeAvailability { Email = email };
            try
            {
                var calendar = string.Equals(email, Account.Email, StringComparison.OrdinalIgnoreCase)
                    ? (await GetOwnCalendarsAsync(ct)).Single(c => c.IsDefault)
                    : await OpenSharedCalendarAsync(email, ct);
                availability.Name = calendar.OwnerName;
                var events = await GetEventsAsync(calendar, start, end, ct);
                availability.Slots = events.Where(e => !e.IsCancelled && e.ShowAs != ShowAs.Free)
                    .Select(e => new BusySlot
                    {
                        Start = e.Start < start ? start : e.Start,
                        End = e.End > end ? end : e.End,
                        Status = e.ShowAs
                    }).ToList();
            }
            catch (MailServiceException)
            {
                // The calendar may grant free/busy only, which uses a different Exchange API.
                availability.HasError = true;
            }
            result.Add(availability);
        }
        return result;
    }
}
