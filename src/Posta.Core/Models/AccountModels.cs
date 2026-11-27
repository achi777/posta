namespace Posta.Core.Models;

public sealed class AccountConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>Login user name (UPN, or DOMAIN\user, or user). Empty → e-mail address.</summary>
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";
    /// <summary>EWS endpoint, e.g. https://mail.contoso.com/EWS/Exchange.asmx. Empty → autodiscover.</summary>
    public string ServerUrl { get; set; } = "";
    public bool UseWindowsCredentials { get; set; }
    /// <summary>Connect through a locally installed Microsoft Outlook (classic) via COM, instead of EWS.</summary>
    public bool UseOutlookInterop { get; set; }
    /// <summary>Experimental direct MAPI/HTTP provider; does not require Outlook. See README for capability limits.</summary>
    public bool UseNativeMapi { get; set; }
    public bool AcceptInvalidCertificates { get; set; }
    /// <summary>Additional mailboxes (delegate / shared) opened together with this account.</summary>
    public List<string> AdditionalMailboxes { get; set; } = new();
    /// <summary>Other users' calendars opened in the Calendar module.</summary>
    public List<SharedCalendarRef> SharedCalendars { get; set; } = new();
    public int SortOrder { get; set; }
    public bool IsDemo { get; set; }

    public string EffectiveUser => string.IsNullOrWhiteSpace(UserName) ? Email : UserName;
}

public sealed class SharedCalendarRef
{
    public string OwnerEmail { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string? FolderId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Color { get; set; } = "#C0E4A8";
    public bool IsChecked { get; set; } = true;
    public CalendarGroupKind Group { get; set; } = CalendarGroupKind.SharedCalendars;
}

public enum ConnectionState { Disconnected, Connecting, Connected, Offline, Error, NeedsPassword }
