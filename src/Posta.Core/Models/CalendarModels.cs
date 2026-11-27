using CommunityToolkit.Mvvm.ComponentModel;

namespace Posta.Core.Models;

public enum ShowAs { Free, Tentative, Busy, OutOfOffice, WorkingElsewhere, NoData }

public enum RecurrenceType { None, Daily, Weekly, MonthlyAbsolute, MonthlyRelative, YearlyAbsolute, YearlyRelative }
public enum RecurrenceEnd { NoEnd, AfterOccurrences, ByDate }
public enum WeekIndex { First, Second, Third, Fourth, Last }
public enum EditScope { Single, Series }

public sealed class RecurrenceInfo
{
    public RecurrenceType Type { get; set; } = RecurrenceType.None;
    public int Interval { get; set; } = 1;
    /// <summary>Daily: every weekday instead of every N days.</summary>
    public bool EveryWeekday { get; set; }
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();
    public int DayOfMonth { get; set; } = 1;
    public int Month { get; set; } = 1;
    public WeekIndex WeekIndex { get; set; } = WeekIndex.First;
    public DayOfWeek RelativeDay { get; set; } = DayOfWeek.Monday;
    public DateTime StartDate { get; set; } = DateTime.Today;
    public RecurrenceEnd EndType { get; set; } = RecurrenceEnd.NoEnd;
    public int Occurrences { get; set; } = 10;
    public DateTime EndDate { get; set; } = DateTime.Today.AddMonths(3);

    public RecurrenceInfo Clone()
    {
        var r = (RecurrenceInfo)MemberwiseClone();
        r.DaysOfWeek = new List<DayOfWeek>(DaysOfWeek);
        return r;
    }
}

public sealed class Attendee
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public MeetingResponse Response { get; set; } = MeetingResponse.NoResponseReceived;
    public AttendeeType Type { get; set; } = AttendeeType.Required;
    public string Display => string.IsNullOrWhiteSpace(Name) ? Email : Name;
    public Recipient ToRecipient() => new(Name, Email);
}

public enum AttendeeType { Organizer, Required, Optional, Resource }

public partial class CalendarEvent : ObservableObject
{
    public string Id { get; set; } = "";
    public string? ChangeKey { get; set; }
    public bool MeetingSubmissionNeedsReview { get; set; }
    public string CalendarKey { get; set; } = "";
    public string? SeriesMasterId { get; set; }

    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private DateTime _start;
    [ObservableProperty] private DateTime _end;
    [ObservableProperty] private bool _isAllDay;
    [ObservableProperty] private ShowAs _showAs = ShowAs.Busy;

    public Recipient? Organizer { get; set; }
    public List<Attendee> Attendees { get; set; } = new();
    public string BodyHtml { get; set; } = "";
    public bool ReminderSet { get; set; } = true;
    public int ReminderMinutes { get; set; } = 15;
    public DateTime? ReminderDueBy { get; set; }
    public Sensitivity Sensitivity { get; set; }
    public Importance Importance { get; set; } = Importance.Normal;
    public bool IsMeeting { get; set; }
    public bool IsOrganizer { get; set; } = true;
    public bool IsCancelled { get; set; }
    public bool IsRecurring { get; set; }
    public bool IsOccurrence { get; set; }
    public bool IsException { get; set; }
    public RecurrenceInfo? Recurrence { get; set; }
    public MeetingResponse MyResponse { get; set; }
    public List<string> Categories { get; set; } = new();
    public bool AllowNewTimeProposal { get; set; } = true;
    public bool ResponseRequested { get; set; } = true;
    public string? TimeZoneId { get; set; }
    public List<AttachmentInfo> Attachments { get; set; } = new();
    public string? OnlineMeetingUrl { get; set; }

    /// <summary>Only free/busy information is available (limited permissions).</summary>
    public bool IsFreeBusyOnly { get; set; }
    public bool? CanEditItem { get; set; }
    public bool? CanDeleteItem { get; set; }

    public bool IsPrivate => Sensitivity == Sensitivity.Private;
    public TimeSpan Duration => End - Start;
    public bool IsMultiDay => IsAllDay ? (End - Start).TotalDays > 1 : End.Date > Start.Date && !(End.TimeOfDay == TimeSpan.Zero && End.Date == Start.Date.AddDays(1));

    public CalendarEvent CloneShallow()
    {
        var c = (CalendarEvent)MemberwiseClone();
        c.Attendees = Attendees.Select(a => new Attendee { Name = a.Name, Email = a.Email, Response = a.Response, Type = a.Type }).ToList();
        c.Categories = new List<string>(Categories);
        c.Attachments = new List<AttachmentInfo>(Attachments);
        return c;
    }
}

public enum CalendarGroupKind { MyCalendars, SharedCalendars, OtherCalendars, PeoplesCalendars }

public enum CalendarAccess { None, FreeBusyTime, FreeBusyDetails, ReadAll, Author, Editor, Owner }

public partial class CalendarSource : ObservableObject
{
    /// <summary>Unique key: accountId|mailbox|folderId</summary>
    public string Key { get; set; } = "";
    public string AccountId { get; set; } = "";
    /// <summary>Folder id. For other users' default calendars this may be empty, and <see cref="OwnerEmail"/> is used.</summary>
    public string FolderId { get; set; } = "";
    public string? OwnerEmail { get; set; }
    public string OwnerName { get; set; } = "";
    public bool IsShared { get; set; }
    public bool IsDefault { get; set; }
    public CalendarGroupKind Group { get; set; }
    public CalendarAccess Access { get; set; } = CalendarAccess.Owner;

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string _color = "#A4C2F4";

    public bool CanCreate => Access >= CalendarAccess.Author;
    public bool CanEditAll => Access >= CalendarAccess.Editor;
    public bool CanReadDetails => Access >= CalendarAccess.ReadAll;
}

public sealed class BusySlot
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public ShowAs Status { get; set; }
    public string? Subject { get; set; }
    public string? Location { get; set; }
}

public sealed class AttendeeAvailability
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public bool HasError { get; set; }
    public List<BusySlot> Slots { get; set; } = new();
}

public sealed class ReminderInfo
{
    public string Key { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Location { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime DueBy { get; set; }
    public bool IsTask { get; set; }
}
