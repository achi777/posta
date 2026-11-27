using Posta.Core.Models;

namespace Posta.Core.Services;

/// <summary>
/// One connected mailbox account. Implemented by the EWS provider (Exchange 2013–2019) and by the demo provider.
/// </summary>
public interface IMailService
{
    AccountConfig Account { get; }
    ConnectionState State { get; }
    string? LastError { get; }
    string? ServerVersion { get; }
    string? UserDisplayName { get; }
    event EventHandler? StateChanged;

    Task ConnectAsync(string? password, CancellationToken ct = default);

    // ---------- Folders ----------
    Task<IReadOnlyList<MailFolderInfo>> GetFolderTreeAsync(string? mailboxEmail = null, CancellationToken ct = default);
    Task<MailFolderInfo?> GetWellKnownFolderAsync(FolderKind kind, string? mailboxEmail = null, CancellationToken ct = default);
    Task<(int unread, int total)> GetFolderCountsAsync(string folderId, CancellationToken ct = default);
    Task<string> CreateFolderAsync(string parentFolderId, string name, string folderClass, CancellationToken ct = default);
    Task RenameFolderAsync(string folderId, string newName, CancellationToken ct = default);
    Task DeleteFolderAsync(string folderId, bool permanent, CancellationToken ct = default);
    Task MoveFolderAsync(string folderId, string newParentId, CancellationToken ct = default);
    Task CopyFolderAsync(string folderId, string newParentId, CancellationToken ct = default);
    Task MarkAllReadAsync(string folderId, CancellationToken ct = default);
    Task EmptyFolderAsync(string folderId, bool deleteSubFolders, CancellationToken ct = default);

    // ---------- Mail ----------
    Task<Paged<MailItemSummary>> GetItemsAsync(MailQuery query, CancellationToken ct = default);
    Task<MailMessageDetail> GetMessageAsync(string itemId, CancellationToken ct = default);
    Task<byte[]> GetAttachmentContentAsync(string itemId, string attachmentId, CancellationToken ct = default);
    Task<byte[]> GetMimeContentAsync(string itemId, CancellationToken ct = default);
    Task SetReadAsync(IEnumerable<string> itemIds, bool isRead, CancellationToken ct = default);
    Task SetFlagAsync(IEnumerable<string> itemIds, FlagState state, DateTime? start, DateTime? due, CancellationToken ct = default);
    Task SetCategoriesAsync(string itemId, IEnumerable<string> categories, CancellationToken ct = default);
    Task SetImportanceAsync(string itemId, Importance importance, CancellationToken ct = default);
    Task DeleteItemsAsync(IEnumerable<string> itemIds, bool permanent, CancellationToken ct = default);
    Task MoveItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default);
    Task CopyItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default);
    Task MarkAsJunkAsync(IEnumerable<string> itemIds, bool isJunk, CancellationToken ct = default);
    Task SendAsync(OutgoingMessage message, CancellationToken ct = default);
    Task<string> SaveDraftAsync(OutgoingMessage message, CancellationToken ct = default);
    Task<OutgoingMessage> LoadDraftAsync(string draftId, CancellationToken ct = default);
    Task<IReadOnlyList<NewMailNotification>> GetNewMailSinceAsync(DateTime sinceUtc, CancellationToken ct = default);
    Task IgnoreConversationAsync(string itemId, CancellationToken ct = default);

    // ---------- Directory ----------
    Task<IReadOnlyList<ResolvedName>> ResolveNamesAsync(string query, bool directoryOnly = false, CancellationToken ct = default);
    Task<IReadOnlyList<Recipient>> ExpandGroupAsync(string email, CancellationToken ct = default);
    Task<byte[]?> GetUserPhotoAsync(string email, CancellationToken ct = default);

    // ---------- Calendar ----------
    Task<IReadOnlyList<CalendarSource>> GetOwnCalendarsAsync(CancellationToken ct = default);
    Task<CalendarSource> OpenSharedCalendarAsync(string ownerEmail, CancellationToken ct = default);
    Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(CalendarSource source, DateTime start, DateTime end, CancellationToken ct = default);
    Task<CalendarEvent> GetEventAsync(string itemId, CancellationToken ct = default);
    Task<string> SaveEventAsync(CalendarSource source, CalendarEvent ev, bool sendInvitations, EditScope scope, CancellationToken ct = default);
    Task DeleteEventAsync(string itemId, bool notifyAttendees, string? cancellationText, EditScope scope, CancellationToken ct = default);
    Task RespondToMeetingAsync(string itemId, MeetingResponse response, bool sendResponse, string? comment, CancellationToken ct = default);
    Task<IReadOnlyList<AttendeeAvailability>> GetAvailabilityAsync(IEnumerable<string> emails, DateTime start, DateTime end, CancellationToken ct = default);
    Task DismissReminderAsync(string itemId, CancellationToken ct = default);
    Task<IReadOnlyList<ReminderInfo>> GetUpcomingRemindersAsync(DateTime until, CancellationToken ct = default);

    // ---------- Permissions / sharing ----------
    Task<IReadOnlyList<PermissionEntry>> GetFolderPermissionsAsync(string folderId, CancellationToken ct = default);
    Task SetFolderPermissionsAsync(string folderId, IEnumerable<PermissionEntry> permissions, CancellationToken ct = default);

    // ---------- Contacts ----------
    Task<IReadOnlyList<ContactInfo>> GetContactsAsync(string folderId, string? search, CancellationToken ct = default);
    Task<ContactInfo> GetContactAsync(string itemId, CancellationToken ct = default);
    Task<string> SaveContactAsync(string folderId, ContactInfo contact, CancellationToken ct = default);
    Task DeleteContactAsync(string itemId, CancellationToken ct = default);

    // ---------- Tasks ----------
    Task<IReadOnlyList<TaskInfo>> GetTasksAsync(string folderId, bool includeCompleted, CancellationToken ct = default);
    Task<TaskInfo> GetTaskAsync(string itemId, CancellationToken ct = default);
    Task<string> SaveTaskAsync(string folderId, TaskInfo task, CancellationToken ct = default);
    Task DeleteTaskAsync(string itemId, CancellationToken ct = default);

    // ---------- Mailbox settings ----------
    Task<OofSettingsInfo> GetOofAsync(CancellationToken ct = default);
    Task SetOofAsync(OofSettingsInfo settings, CancellationToken ct = default);
    Task<IReadOnlyList<RuleInfo>> GetRulesAsync(CancellationToken ct = default);
    Task SaveRulesAsync(IReadOnlyList<RuleInfo> rules, IReadOnlyList<string> deletedRuleIds, CancellationToken ct = default);
}

public sealed class MailServiceException : Exception
{
    public MailServiceException(string message, Exception? inner = null) : base(message, inner) { }
}
