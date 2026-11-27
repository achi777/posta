namespace Posta.Core.Models;

public enum ReadingPanePosition { Right, Bottom, Off }
public enum FolderPaneMode { Normal, Minimized, Off }
public enum CalendarViewMode { Day, WorkWeek, Week, Month, Schedule }
public enum PeopleViewMode { People, BusinessCard, Card, Phone, List }
public enum ComposeFormat { Html, PlainText }
public enum MarkReadMode { WhenSelectionChanges, AfterSeconds, Never }

public sealed class Signature
{
    public string Name { get; set; } = "";
    public string Html { get; set; } = "";
}

public sealed class AccountSignatureDefaults
{
    public string AccountId { get; set; } = "";
    public string? NewMessages { get; set; }
    public string? RepliesForwards { get; set; }
}

public sealed class CategoryDefinition
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#E7A1A2";
    public string? Shortcut { get; set; }
}

public sealed class QuickStep
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Move"; // Move, ReplyDelete, ToManager, TeamEmail, Done
    public string? TargetFolderId { get; set; }
    public string? TargetAccountId { get; set; }
    public string? Recipients { get; set; }
}

public sealed class AppSettings
{
    // General
    public string Language { get; set; } = "ka-GE";
    public string Theme { get; set; } = "Colorful";
    public bool StartMinimizedToTray { get; set; }
    public string StartupFolder { get; set; } = "Inbox";
    public bool ShowDesktopAlert { get; set; } = true;
    public bool PlaySound { get; set; } = true;
    public bool ShowEnvelopeInTaskbar { get; set; } = true;
    public int SendReceiveIntervalMinutes { get; set; } = 1;
    public bool WorkOffline { get; set; }

    // Mail / view
    public ReadingPanePosition ReadingPane { get; set; } = ReadingPanePosition.Right;
    public FolderPaneMode FolderPane { get; set; } = FolderPaneMode.Normal;
    public double FolderPaneWidth { get; set; } = 250;
    public double MessageListWidth { get; set; } = 420;
    public double MessageListHeight { get; set; } = 300;
    public bool ShowAsConversations { get; set; }
    public int MessagePreviewLines { get; set; } = 1;
    public bool ShowSenderPhotos { get; set; } = true;
    public bool ShowToDoBar { get; set; }
    public MarkReadMode MarkRead { get; set; } = MarkReadMode.AfterSeconds;
    public int MarkReadSeconds { get; set; } = 5;
    public bool BlockExternalPictures { get; set; } = true;
    public double ReadingZoom { get; set; } = 1.0;
    public List<string> Favorites { get; set; } = new();
    public Dictionary<string, bool> ExpandedFolders { get; set; } = new();

    // Compose
    public ComposeFormat ComposeFormat { get; set; } = ComposeFormat.Html;
    public string ComposeFontFamily { get; set; } = "Aptos, Calibri, 'Segoe UI', Sylfaen, sans-serif";
    public int ComposeFontSizePt { get; set; } = 11;
    public string ComposeFontColor { get; set; } = "#000000";
    public bool CheckSpelling { get; set; } = true;
    public bool AutoCompleteRecipients { get; set; } = true;
    public bool SaveSentItems { get; set; } = true;
    public bool WarnMissingAttachment { get; set; } = true;
    public bool CloseOriginalOnReply { get; set; }
    public string ReplyStyle { get; set; } = "IncludeOriginal"; // IncludeOriginal, Attach, Indent, Prefix
    public int AutoSaveDraftMinutes { get; set; } = 3;
    public bool ReadReceiptAlways { get; set; }
    public bool DeliveryReceiptAlways { get; set; }
    public List<Signature> Signatures { get; set; } = new();
    public List<AccountSignatureDefaults> SignatureDefaults { get; set; } = new();
    public List<string> RecentRecipients { get; set; } = new();

    // Calendar
    public TimeSpan WorkStart { get; set; } = TimeSpan.FromHours(9);
    public TimeSpan WorkEnd { get; set; } = TimeSpan.FromHours(18);
    public List<DayOfWeek> WorkDays { get; set; } = new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
    public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Monday;
    public int DefaultReminderMinutes { get; set; } = 15;
    public bool DefaultRemindersOn { get; set; } = true;
    public int TimeScaleMinutes { get; set; } = 30;
    public CalendarViewMode CalendarView { get; set; } = CalendarViewMode.WorkWeek;
    public bool CalendarOverlay { get; set; }
    public bool ShowWeekNumbers { get; set; }
    public string DefaultCalendarColor { get; set; } = "#A4C2F4";
    public bool AllowAttendeesProposeNewTimes { get; set; } = true;
    public int DefaultMeetingMinutes { get; set; } = 30;
    public bool ShowReminders { get; set; } = true;
    public bool PlayReminderSound { get; set; } = true;
    public Dictionary<string, string> CalendarColors { get; set; } = new();
    public Dictionary<string, bool> CalendarChecked { get; set; } = new();

    // People / tasks
    public PeopleViewMode PeopleView { get; set; } = PeopleViewMode.People;
    public string ContactFileAs { get; set; } = "LastFirst";
    public bool ShowCompletedTasks { get; set; } = true;

    // Categories / quick steps
    public List<CategoryDefinition> Categories { get; set; } = new();
    public List<QuickStep> QuickSteps { get; set; } = new();

    // Window state
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; } = true;
    public bool RibbonMinimized { get; set; }
    public HashSet<string> DismissedReminders { get; set; } = new();
}
