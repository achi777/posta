using CommunityToolkit.Mvvm.ComponentModel;

namespace Posta.Core.Models;

public sealed class PostalAddress
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";

    public bool IsEmpty => string.IsNullOrWhiteSpace(Street) && string.IsNullOrWhiteSpace(City) &&
                           string.IsNullOrWhiteSpace(State) && string.IsNullOrWhiteSpace(PostalCode) && string.IsNullOrWhiteSpace(Country);

    public override string ToString() =>
        string.Join(Environment.NewLine, new[] { Street, string.Join(" ", new[] { City, State, PostalCode }.Where(s => !string.IsNullOrWhiteSpace(s))), Country }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
}

public partial class ContactInfo : ObservableObject
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string FolderId { get; set; } = "";
    public bool IsGroup { get; set; }

    [ObservableProperty] private string _displayName = "";
    public string FileAs { get; set; } = "";
    public string GivenName { get; set; } = "";
    public string MiddleName { get; set; } = "";
    public string Surname { get; set; } = "";
    public string Title { get; set; } = "";
    public string Suffix { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Company { get; set; } = "";
    public string Department { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public string Office { get; set; } = "";
    public string Manager { get; set; } = "";
    public string Assistant { get; set; } = "";
    public string Email1 { get; set; } = "";
    public string Email1Name { get; set; } = "";
    public string Email2 { get; set; } = "";
    public string Email3 { get; set; } = "";
    public string BusinessPhone { get; set; } = "";
    public string BusinessPhone2 { get; set; } = "";
    public string HomePhone { get; set; } = "";
    public string MobilePhone { get; set; } = "";
    public string BusinessFax { get; set; } = "";
    public string Pager { get; set; } = "";
    public string WebPage { get; set; } = "";
    public string ImAddress { get; set; } = "";
    public PostalAddress BusinessAddress { get; set; } = new();
    public PostalAddress HomeAddress { get; set; } = new();
    public PostalAddress OtherAddress { get; set; } = new();
    public DateTime? Birthday { get; set; }
    public DateTime? Anniversary { get; set; }
    public string SpouseName { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Categories { get; set; } = new();
    public byte[]? Photo { get; set; }
    public List<Recipient> Members { get; set; } = new();

    public string PrimaryEmail => !string.IsNullOrWhiteSpace(Email1) ? Email1 : !string.IsNullOrWhiteSpace(Email2) ? Email2 : Email3;

    public string Initials
    {
        get
        {
            var g = GivenName.Trim();
            var s = Surname.Trim();
            if (g.Length > 0 && s.Length > 0) return (g[..1] + s[..1]).ToUpperInvariant();
            return new Recipient(DisplayName, PrimaryEmail).Initials;
        }
    }

    public string SortKey => string.IsNullOrWhiteSpace(FileAs) ? DisplayName : FileAs;
    public string IndexLetter => string.IsNullOrEmpty(SortKey) ? "#" : char.ToUpperInvariant(SortKey[0]).ToString();
}

public enum TaskState { NotStarted, InProgress, Completed, WaitingOnOthers, Deferred }

public partial class TaskInfo : ObservableObject
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string FolderId { get; set; } = "";

    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private DateTime? _dueDate;
    [ObservableProperty] private TaskState _status;
    [ObservableProperty] private int _percentComplete;
    [ObservableProperty] private Importance _importance = Importance.Normal;
    [ObservableProperty] private bool _reminderSet;
    [ObservableProperty] private DateTime? _reminderTime;
    [ObservableProperty] private DateTime? _completeDate;

    public string BodyHtml { get; set; } = "";
    public List<string> Categories { get; set; } = new();
    public bool IsFlaggedMail { get; set; }
    public string? Owner { get; set; }

    public bool IsComplete => Status == TaskState.Completed;
    public bool IsOverdue => !IsComplete && DueDate.HasValue && DueDate.Value.Date < DateTime.Today;
}

public enum PermissionLevel
{
    None, Owner, PublishingEditor, Editor, PublishingAuthor, Author, NoneditingAuthor, Reviewer, Contributor,
    FreeBusyTimeOnly, FreeBusyTimeAndSubjectAndLocation, Custom
}

public enum ItemAccessScope { None, Owned, All }
public enum ReadAccess { None, TimeOnly, TimeAndSubjectAndLocation, FullDetails }

public partial class PermissionEntry : ObservableObject
{
    /// <summary>"Default" or "Anonymous" for standard users, otherwise empty.</summary>
    public string? StandardUser { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";

    [ObservableProperty] private PermissionLevel _level;
    [ObservableProperty] private bool _canCreateItems;
    [ObservableProperty] private bool _canCreateSubFolders;
    [ObservableProperty] private bool _isFolderOwner;
    [ObservableProperty] private bool _isFolderVisible;
    [ObservableProperty] private bool _isFolderContact;
    [ObservableProperty] private ItemAccessScope _editItems;
    [ObservableProperty] private ItemAccessScope _deleteItems;
    [ObservableProperty] private ReadAccess _readItems;

    public string Display => StandardUser ?? (string.IsNullOrWhiteSpace(Name) ? Email : Name);
    public bool IsStandard => StandardUser != null;

    public void ApplyLevel(PermissionLevel level)
    {
        Level = level;
        switch (level)
        {
            case PermissionLevel.Owner: Set(true, true, true, true, true, ItemAccessScope.All, ItemAccessScope.All, ReadAccess.FullDetails); break;
            case PermissionLevel.PublishingEditor: Set(true, true, false, true, false, ItemAccessScope.All, ItemAccessScope.All, ReadAccess.FullDetails); break;
            case PermissionLevel.Editor: Set(true, false, false, true, false, ItemAccessScope.All, ItemAccessScope.All, ReadAccess.FullDetails); break;
            case PermissionLevel.PublishingAuthor: Set(true, true, false, true, false, ItemAccessScope.Owned, ItemAccessScope.Owned, ReadAccess.FullDetails); break;
            case PermissionLevel.Author: Set(true, false, false, true, false, ItemAccessScope.Owned, ItemAccessScope.Owned, ReadAccess.FullDetails); break;
            case PermissionLevel.NoneditingAuthor: Set(true, false, false, true, false, ItemAccessScope.None, ItemAccessScope.Owned, ReadAccess.FullDetails); break;
            case PermissionLevel.Reviewer: Set(false, false, false, true, false, ItemAccessScope.None, ItemAccessScope.None, ReadAccess.FullDetails); break;
            case PermissionLevel.Contributor: Set(true, false, false, true, false, ItemAccessScope.None, ItemAccessScope.None, ReadAccess.None); break;
            case PermissionLevel.FreeBusyTimeOnly: Set(false, false, false, false, false, ItemAccessScope.None, ItemAccessScope.None, ReadAccess.TimeOnly); break;
            case PermissionLevel.FreeBusyTimeAndSubjectAndLocation: Set(false, false, false, false, false, ItemAccessScope.None, ItemAccessScope.None, ReadAccess.TimeAndSubjectAndLocation); break;
            case PermissionLevel.None: Set(false, false, false, false, false, ItemAccessScope.None, ItemAccessScope.None, ReadAccess.None); break;
        }
    }

    private void Set(bool create, bool sub, bool owner, bool visible, bool contact, ItemAccessScope edit, ItemAccessScope del, ReadAccess read)
    {
        CanCreateItems = create; CanCreateSubFolders = sub; IsFolderOwner = owner; IsFolderVisible = visible; IsFolderContact = contact;
        EditItems = edit; DeleteItems = del; ReadItems = read;
    }
}

public enum OofState { Disabled, Enabled, Scheduled }
public enum OofExternalAudience { None, Known, All }

public sealed class OofSettingsInfo
{
    public OofState State { get; set; }
    public DateTime Start { get; set; } = DateTime.Today.AddHours(DateTime.Now.Hour + 1);
    public DateTime End { get; set; } = DateTime.Today.AddDays(1).AddHours(DateTime.Now.Hour + 1);
    public string InternalReplyHtml { get; set; } = "";
    public string ExternalReplyHtml { get; set; } = "";
    public OofExternalAudience ExternalAudience { get; set; } = OofExternalAudience.All;
}

public partial class RuleInfo : ObservableObject
{
    public string? Id { get; set; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isEnabled = true;
    public int Priority { get; set; }

    // Conditions
    public List<string> FromAddresses { get; set; } = new();
    public List<string> SentToAddresses { get; set; } = new();
    public List<string> SubjectContains { get; set; } = new();
    public List<string> BodyContains { get; set; } = new();
    public List<string> SenderContains { get; set; } = new();
    public bool SentOnlyToMe { get; set; }
    public bool MyNameInCc { get; set; }
    public bool HasAttachments { get; set; }
    public Importance? WithImportance { get; set; }

    // Actions
    public string? MoveToFolderId { get; set; }
    public string? MoveToFolderName { get; set; }
    public string? CopyToFolderId { get; set; }
    public string? CopyToFolderName { get; set; }
    public bool Delete { get; set; }
    public bool PermanentDelete { get; set; }
    public bool MarkAsRead { get; set; }
    public List<string> ForwardTo { get; set; } = new();
    public List<string> RedirectTo { get; set; } = new();
    public List<string> AssignCategories { get; set; } = new();
    public Importance? MarkImportance { get; set; }
    public bool StopProcessing { get; set; }

    /// <summary>Rule contains conditions/actions not understood by this client.</summary>
    public bool HasUnsupportedParts { get; set; }
}
