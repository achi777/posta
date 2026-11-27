using CommunityToolkit.Mvvm.ComponentModel;

namespace Posta.Core.Models;

public enum FolderKind
{
    Generic, Root, Inbox, Drafts, SentItems, DeletedItems, JunkEmail, Outbox, Archive,
    Calendar, Contacts, Tasks, Notes, Journal, ConversationHistory, SyncIssues, SearchFolder, RssFeeds
}

public enum Importance { Low, Normal, High }
public enum Sensitivity { Normal, Personal, Private, Confidential }
public enum FlagState { NotFlagged, Flagged, Complete }
public enum MailFilter { All, Unread, Flagged, HasAttachments, Important, ToMe, ThisWeek, Mentioned }
public enum ComposeMode { New, Reply, ReplyAll, Forward, EditDraft, ForwardAsAttachment }

public sealed class Recipient
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";

    public Recipient() { }
    public Recipient(string name, string address) { Name = name; Address = address; }

    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : Name;
    public string FullDisplay => string.IsNullOrWhiteSpace(Name) || Name == Address ? Address : $"{Name} <{Address}>";

    public string Initials
    {
        get
        {
            var src = string.IsNullOrWhiteSpace(Name) ? Address : Name;
            if (string.IsNullOrWhiteSpace(src)) return "?";
            var parts = src.Split(new[] { ' ', '.', '_', '@' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && !src.Contains('@')) return (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
            return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        }
    }

    public override string ToString() => FullDisplay;
}

public partial class MailFolderInfo : ObservableObject
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; }
    public string AccountId { get; set; } = "";
    /// <summary>Owner mailbox SMTP address for delegated/shared mailboxes; null for the primary mailbox.</summary>
    public string? MailboxEmail { get; set; }
    public FolderKind Kind { get; set; }
    public string FolderClass { get; set; } = "IPF.Note";
    public int ChildFolderCount { get; set; }

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private int _unreadCount;
    [ObservableProperty] private int _totalCount;

    public bool IsMailFolder => FolderClass.StartsWith("IPF.Note", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(FolderClass);
    public bool IsCalendarFolder => FolderClass.StartsWith("IPF.Appointment", StringComparison.OrdinalIgnoreCase);
    public bool IsContactFolder => FolderClass.StartsWith("IPF.Contact", StringComparison.OrdinalIgnoreCase);
    public bool IsTaskFolder => FolderClass.StartsWith("IPF.Task", StringComparison.OrdinalIgnoreCase);
    public bool IsSpecial => Kind != FolderKind.Generic;
}

public partial class MailItemSummary : ObservableObject
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string FolderId { get; set; } = "";
    public bool IsReadOnly { get; set; }
    public string ItemClass { get; set; } = "IPM.Note";
    public string? ConversationId { get; set; }
    public Recipient From { get; set; } = new();
    public string DisplayTo { get; set; } = "";
    public DateTime Received { get; set; }
    public DateTime Sent { get; set; }
    public int Size { get; set; }
    public bool HasAttachments { get; set; }

    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private bool _isRead;
    [ObservableProperty] private Importance _importance = Importance.Normal;
    [ObservableProperty] private FlagState _flag;
    [ObservableProperty] private DateTime? _flagDue;
    [ObservableProperty] private List<string> _categories = new();
    [ObservableProperty] private bool _isDraft;
    /// <summary>Name shown in the message list (sender, or recipients in Sent Items/Drafts).</summary>
    [ObservableProperty] private string _listName = "";

    public bool IsMeetingRequest => ItemClass.StartsWith("IPM.Schedule.Meeting.Request", StringComparison.OrdinalIgnoreCase);
    public bool IsMeetingResponse => ItemClass.StartsWith("IPM.Schedule.Meeting.Resp", StringComparison.OrdinalIgnoreCase);
    public bool IsMeetingCancel => ItemClass.StartsWith("IPM.Schedule.Meeting.Canceled", StringComparison.OrdinalIgnoreCase);
    public bool IsMeetingItem => ItemClass.StartsWith("IPM.Schedule.Meeting", StringComparison.OrdinalIgnoreCase);
}

public sealed class AttachmentInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public bool IsInline { get; set; }
    public string? ContentId { get; set; }
    public bool IsItemAttachment { get; set; }
    /// <summary>Local file path (for outgoing attachments).</summary>
    public string? LocalPath { get; set; }
    public byte[]? Content { get; set; }

    public string SizeDisplay => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0} KB",
        _ => $"{Size / (1024.0 * 1024):0.0} MB"
    };

    public string Extension => Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
}

public enum MeetingResponse { None, Organizer, Tentative, Accept, Decline, NoResponseReceived, Unknown }

public sealed class MailMessageDetail
{
    /// <summary>False when the provider could not resolve every recipient. Reply-all and draft editing must refuse truncation.</summary>
    public bool RecipientsComplete { get; set; } = true;
    public bool ReplyRecipientsComplete { get; set; } = true;
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string FolderId { get; set; } = "";
    public string ItemClass { get; set; } = "IPM.Note";
    public string Subject { get; set; } = "";
    public Recipient From { get; set; } = new();
    public Recipient? Sender { get; set; }
    public List<Recipient> To { get; set; } = new();
    public List<Recipient> Cc { get; set; } = new();
    public List<Recipient> Bcc { get; set; } = new();
    public List<Recipient> ReplyTo { get; set; } = new();
    public DateTime Sent { get; set; }
    public DateTime Received { get; set; }
    public string BodyHtml { get; set; } = "";
    public bool BodyIsHtml { get; set; } = true;
    public List<AttachmentInfo> Attachments { get; set; } = new();
    public Importance Importance { get; set; }
    public Sensitivity Sensitivity { get; set; }
    public bool IsRead { get; set; }
    public bool IsDraft { get; set; }
    public FlagState Flag { get; set; }
    public List<string> Categories { get; set; } = new();
    public string? InternetMessageId { get; set; }
    public bool ReadReceiptRequested { get; set; }

    // Meeting request information
    public bool IsMeetingRequest => ItemClass.StartsWith("IPM.Schedule.Meeting.Request", StringComparison.OrdinalIgnoreCase);
    public bool IsMeetingItem => ItemClass.StartsWith("IPM.Schedule.Meeting", StringComparison.OrdinalIgnoreCase);
    public DateTime? MeetingStart { get; set; }
    public DateTime? MeetingEnd { get; set; }
    public string? MeetingLocation { get; set; }
    public MeetingResponse MyResponse { get; set; }
    public string? AssociatedAppointmentId { get; set; }
    public bool MeetingIsOutOfDate { get; set; }
    public bool? CanRespondToMeeting { get; set; }
}

public sealed class OutgoingMessage
{
    public bool SubmissionOutcomeUnknown { get; set; }
    public string AccountId { get; set; } = "";
    /// <summary>Send on behalf / from shared mailbox.</summary>
    public string? FromAddress { get; set; }
    public List<Recipient> To { get; set; } = new();
    public List<Recipient> Cc { get; set; } = new();
    public List<Recipient> Bcc { get; set; } = new();
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public List<AttachmentInfo> Attachments { get; set; } = new();
    public List<string> RemovedAttachmentIds { get; set; } = new();
    public Importance Importance { get; set; } = Importance.Normal;
    public Sensitivity Sensitivity { get; set; } = Sensitivity.Normal;
    public bool RequestReadReceipt { get; set; }
    public bool RequestDeliveryReceipt { get; set; }
    public List<string> Categories { get; set; } = new();
    public FlagState Flag { get; set; }
    public ComposeMode Mode { get; set; } = ComposeMode.New;
    /// <summary>The message being replied to / forwarded.</summary>
    public string? SourceItemId { get; set; }
    /// <summary>The existing draft item (if editing/saving a draft).</summary>
    public string? DraftId { get; set; }
    public DateTime? DeferredDelivery { get; set; }
    public bool SaveSentCopy { get; set; } = true;
}

public sealed class Paged<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int TotalCount { get; init; }
    public bool MoreAvailable { get; init; }
}

public enum SortField { Date, From, To, Subject, Size, Importance, Attachments, Categories, Flag }

public sealed class MailQuery
{
    public string FolderId { get; set; } = "";
    public int Offset { get; set; }
    public int PageSize { get; set; } = 100;
    public MailFilter Filter { get; set; } = MailFilter.All;
    public string? Search { get; set; }
    public SortField Sort { get; set; } = SortField.Date;
    public bool Descending { get; set; } = true;
}

public enum RecipientKind { Mailbox, Contact, DistributionList, Room, Unknown }

public sealed class ResolvedName
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public RecipientKind Kind { get; set; }
    public string? Title { get; set; }
    public string? Department { get; set; }
    public string? Company { get; set; }
    public string? Office { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? Alias { get; set; }

    public Recipient ToRecipient() => new(Name, Email);
    public override string ToString() => string.IsNullOrEmpty(Name) ? Email : $"{Name} <{Email}>";
}

public sealed class NewMailNotification
{
    public string AccountId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string From { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Preview { get; set; } = "";
    public DateTime Received { get; set; }
}
