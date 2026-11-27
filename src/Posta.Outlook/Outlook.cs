namespace Posta.Outlook;

/// <summary>Numeric constants from the Outlook object model (used with late-bound dynamic COM).</summary>
internal static class OL
{
    // OlDefaultFolders
    public const int DeletedItems = 3, Outbox = 4, SentMail = 5, Inbox = 6, Calendar = 9,
        Contacts = 10, Journal = 11, Notes = 12, Tasks = 13, Drafts = 16, Junk = 23, Conflicts = 19;

    // OlItemType
    public const int MailItem = 0, AppointmentItem = 1, ContactItem = 2, TaskItem = 3,
        JournalItem = 4, NoteItem = 5, DistributionListItem = 7;

    // OlImportance
    public const int ImportanceLow = 0, ImportanceNormal = 1, ImportanceHigh = 2;

    // OlSensitivity
    public const int Normal = 0, Personal = 1, Private = 2, Confidential = 3;

    // OlBodyFormat
    public const int FormatUnspecified = 0, FormatPlain = 1, FormatHTML = 2, FormatRichText = 3;

    // OlFlagStatus
    public const int NoFlag = 0, FlagComplete = 1, FlagMarked = 2;

    // OlBusyStatus
    public const int Free = 0, Tentative = 1, Busy = 2, OutOfOffice = 3, WorkingElsewhere = 4;

    // OlMeetingStatus
    public const int NonMeeting = 0, Meeting = 1, MeetingReceived = 3, MeetingCanceled = 5, MeetingReceivedAndCanceled = 7;

    // OlMeetingResponse
    public const int RespTentative = 2, RespAccepted = 3, RespDeclined = 4;

    // OlResponseStatus
    public const int ResponseNone = 0, ResponseOrganized = 1, ResponseTentative = 2, ResponseAccepted = 3,
        ResponseDeclined = 4, ResponseNotResponded = 5;

    // OlTaskStatus
    public const int TaskNotStarted = 0, TaskInProgress = 1, TaskComplete = 2, TaskWaiting = 3, TaskDeferred = 4;

    // OlRecurrenceType
    public const int RecursDaily = 0, RecursWeekly = 1, RecursMonthly = 2, RecursMonthNth = 3, RecursYearly = 5, RecursYearNth = 6;

    // OlDaysOfWeek (bit flags)
    public const int Sunday = 1, Monday = 2, Tuesday = 4, Wednesday = 8, Thursday = 16, Friday = 32, Saturday = 64;

    // OlMailRecipientType
    public const int To = 1, CC = 2, BCC = 3;

    // OlSaveAsType
    public const int SaveMSG = 3, SaveHTML = 5, SaveMSGUnicode = 9;

    // OlSortOrder
    public const int Ascending = 1, Descending = 2;

    // OlAttachmentType
    public const int ByValue = 1, ByReference = 4, EmbeddedItem = 5, OLE = 6;
}
