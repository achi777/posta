using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.ViewModels;

public enum NodeKind { FavoritesHeader, AccountRoot, MailboxRoot, Folder }

public partial class FolderNode : ObservableObject
{
    public NodeKind NodeKind { get; init; }
    public MailFolderInfo? Folder { get; init; }
    public IMailService? Service { get; init; }
    public string? MailboxEmail { get; init; }
    public bool IsFavorite { get; init; }
    public int Level { get; init; }
    public FolderNode? Parent { get; init; }
    public ObservableCollection<FolderNode> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _isDropTarget;

    public Thickness Indent => new(Level * 14, 0, 0, 0);
    public bool IsHeader => NodeKind != NodeKind.Folder;
    public string Glyph => Folder != null ? FolderVisuals.Glyph(Folder.Kind, Folder.FolderClass) : "";

    public string ExpandKey => NodeKind switch
    {
        NodeKind.FavoritesHeader => "fav",
        NodeKind.AccountRoot => "acc:" + Service?.Account.Id,
        NodeKind.MailboxRoot => "mbx:" + Service?.Account.Id + ":" + MailboxEmail,
        _ => "f:" + Folder?.Id
    };

    /// <summary>Drafts / Outbox show total count in brackets, other folders the unread count.</summary>
    public string CountText
    {
        get
        {
            if (Folder == null) return "";
            if (Folder.Kind is FolderKind.Drafts or FolderKind.Outbox) return Folder.TotalCount > 0 ? $"[{Folder.TotalCount}]" : "";
            if (Folder.Kind == FolderKind.JunkEmail) return Folder.UnreadCount > 0 ? $"[{Folder.UnreadCount}]" : "";
            return Folder.UnreadCount > 0 ? Folder.UnreadCount.ToString() : "";
        }
    }

    public bool IsBold => NodeKind != NodeKind.Folder || (Folder != null && Folder.UnreadCount > 0 && Folder.Kind is not (FolderKind.Drafts or FolderKind.DeletedItems or FolderKind.Outbox));

    public bool CountIsTotal => Folder?.Kind is FolderKind.Drafts or FolderKind.Outbox or FolderKind.JunkEmail;

    public void RefreshDisplay()
    {
        if (Folder != null) Title = FolderVisuals.LocalizedName(Folder);
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsBold));
        OnPropertyChanged(nameof(CountIsTotal));
    }

    public FolderNode()
    {
    }

    public void AttachFolder()
    {
        if (Folder != null)
        {
            Folder.PropertyChanged += (_, _) => RefreshDisplay();
            RefreshDisplay();
        }
    }

    public IEnumerable<FolderNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }
}

/// <summary>Builds the folder pane (Favorites + one tree per mailbox) for the mail module.</summary>
public sealed class FolderTreeBuilder
{
    public static FolderNode BuildAccount(IMailService svc, IReadOnlyList<MailFolderInfo> folders, string? mailboxEmail, string title, AppSettings settings)
    {
        var root = new FolderNode
        {
            NodeKind = mailboxEmail == null ? NodeKind.AccountRoot : NodeKind.MailboxRoot,
            Service = svc,
            MailboxEmail = mailboxEmail,
            Title = title,
            Level = 0
        };
        root.IsExpanded = !settings.ExpandedFolders.TryGetValue(root.ExpandKey, out var ex) || ex;

        // Mail module shows mail-type folders (and the special non-mail ones the desktop client shows).
        var visible = folders.Where(f => f.IsMailFolder || f.Kind is FolderKind.ConversationHistory or FolderKind.SyncIssues or FolderKind.RssFeeds).ToList();
        var byParent = visible.ToLookup(f => f.ParentId ?? "");
        AddChildren(root, "", byParent, svc, mailboxEmail, 1, settings);
        return root;
    }

    private static void AddChildren(FolderNode parent, string parentId, ILookup<string, MailFolderInfo> byParent, IMailService svc, string? mailbox, int level, AppSettings settings)
    {
        var children = byParent[parentId]
            .OrderBy(f => level == 1 ? Rank(f.Kind) : 10)
            .ThenBy(f => FolderVisuals.LocalizedName(f), StringComparer.CurrentCultureIgnoreCase);
        foreach (var f in children)
        {
            var node = new FolderNode { NodeKind = NodeKind.Folder, Folder = f, Service = svc, MailboxEmail = mailbox, Level = level, Parent = parent };
            node.AttachFolder();
            node.IsExpanded = settings.ExpandedFolders.TryGetValue(node.ExpandKey, out var ex) && ex;
            parent.Children.Add(node);
            AddChildren(node, f.Id, byParent, svc, mailbox, level + 1, settings);
        }
    }

    private static int Rank(FolderKind k) => k switch
    {
        FolderKind.Inbox => 0, FolderKind.Drafts => 1, FolderKind.SentItems => 2, FolderKind.DeletedItems => 3, FolderKind.Archive => 4, _ => 10
    };

    public static FolderNode BuildFavorites(IEnumerable<FolderNode> accountRoots, AppSettings settings)
    {
        var fav = new FolderNode { NodeKind = NodeKind.FavoritesHeader, Title = Loc.T("Favorites"), Level = 0 };
        fav.IsExpanded = !settings.ExpandedFolders.TryGetValue("fav", out var ex) || ex;
        var all = accountRoots.SelectMany(r => r.Descendants()).Where(n => n.Folder != null).ToList();

        IEnumerable<FolderNode> picks;
        if (settings.Favorites.Count > 0)
        {
            picks = settings.Favorites.Select(id => all.FirstOrDefault(n => n.Folder!.Id == id)).Where(n => n != null)!;
        }
        else
        {
            var first = accountRoots.FirstOrDefault(r => r.NodeKind == NodeKind.AccountRoot);
            picks = first == null ? Enumerable.Empty<FolderNode>() :
                first.Children.Where(n => n.Folder!.Kind is FolderKind.Inbox or FolderKind.SentItems or FolderKind.DeletedItems)
                    .OrderBy(n => Rank(n.Folder!.Kind));
        }
        foreach (var n in picks)
        {
            var f = new FolderNode { NodeKind = NodeKind.Folder, Folder = n.Folder, Service = n.Service, MailboxEmail = n.MailboxEmail, Level = 1, IsFavorite = true, Parent = fav };
            f.AttachFolder();
            fav.Children.Add(f);
        }
        return fav;
    }
}
