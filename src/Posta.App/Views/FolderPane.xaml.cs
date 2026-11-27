using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.App.ViewModels;
using Posta.App.Windows;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Views;

public partial class FolderPane : UserControl
{
    public ObservableCollection<FolderNode> Roots { get; } = new();
    private readonly Dictionary<string, IReadOnlyList<MailFolderInfo>> _folders = new();
    private string? _selectedFolderId;
    private bool _loading;
    private Point _dragStart;

    public event EventHandler<FolderNode>? FolderSelected;
    public event EventHandler<DragPayload>? ItemsDropped;

    public FolderNode? SelectedNode => Tree.SelectedItem as FolderNode;

    public FolderPane()
    {
        InitializeComponent();
        Tree.ItemsSource = Roots;
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            foreach (var r in Roots)
            {
                if (r.NodeKind == NodeKind.FavoritesHeader) r.Title = Loc.T("Favorites");
                foreach (var d in r.Descendants()) d.RefreshDisplay();
            }
        };
        AppServices.Current.DataChanged += async (_, what) => { if (what == "folders") await LoadAsync(); };
    }

    /// <summary>All folders known for an account (all opened mailboxes).</summary>
    public IEnumerable<MailFolderInfo> FoldersOf(IMailService svc) =>
        _folders.Where(kv => kv.Key.StartsWith(svc.Account.Id + "|")).SelectMany(kv => kv.Value);

    public IEnumerable<FolderNode> AllNodes() => Roots.SelectMany(r => new[] { r }.Concat(r.Descendants()));

    public async Task LoadAsync(bool useCacheFirst = false)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            if (useCacheFirst)
            {
                foreach (var svc in AppServices.Current.Accounts)
                {
                    var cached = AppServices.Current.Cache.GetFolders(svc.Account.Id, "");
                    if (cached != null) _folders[svc.Account.Id + "|"] = cached;
                }
                Rebuild();
            }

            foreach (var svc in AppServices.Current.Accounts.ToList())
            {
                if (svc.State != ConnectionState.Connected) continue;
                var mailboxes = new List<string?> { null };
                mailboxes.AddRange(svc.Account.AdditionalMailboxes.Where(m => !string.IsNullOrWhiteSpace(m)));
                foreach (var mb in mailboxes)
                {
                    try
                    {
                        var list = await svc.GetFolderTreeAsync(mb);
                        _folders[svc.Account.Id + "|" + (mb ?? "")] = list;
                        if (mb == null) AppServices.Current.Cache.SaveFolders(svc.Account.Id, "", list);
                    }
                    catch (Exception ex)
                    {
                        if (mb != null) _folders.Remove(svc.Account.Id + "|" + mb);
                        System.Diagnostics.Debug.WriteLine(ex.Message);
                    }
                }
            }
            Rebuild();
        }
        finally
        {
            _loading = false;
        }
    }

    private void Rebuild()
    {
        var settings = AppServices.Current.Settings;
        var accountRoots = new List<FolderNode>();
        foreach (var svc in AppServices.Current.Accounts)
        {
            if (_folders.TryGetValue(svc.Account.Id + "|", out var main))
                accountRoots.Add(FolderTreeBuilder.BuildAccount(svc, main, null, svc.Account.Email, settings));
            else
                accountRoots.Add(new FolderNode { NodeKind = NodeKind.AccountRoot, Service = svc, Title = svc.Account.Email, IsExpanded = true });

            foreach (var mb in svc.Account.AdditionalMailboxes)
            {
                if (_folders.TryGetValue(svc.Account.Id + "|" + mb, out var other))
                    accountRoots.Add(FolderTreeBuilder.BuildAccount(svc, other, mb, mb, settings));
            }
        }
        Roots.Clear();
        Roots.Add(FolderTreeBuilder.BuildFavorites(accountRoots, settings));
        foreach (var r in accountRoots) Roots.Add(r);

        foreach (var n in AllNodes()) n.PropertyChanged += Node_PropertyChanged;

        // restore selection (prefer the favorite node if that was selected)
        var target = _selectedFolderId == null ? null : AllNodes().FirstOrDefault(n => n.Folder?.Id == _selectedFolderId);
        if (target != null)
        {
            ExpandTo(target);
            target.IsSelected = true;
        }
    }

    private void Node_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderNode.IsExpanded) && sender is FolderNode n)
        {
            AppServices.Current.Settings.ExpandedFolders[n.ExpandKey] = n.IsExpanded;
        }
    }

    private static void ExpandTo(FolderNode n)
    {
        var p = n.Parent;
        while (p != null) { p.IsExpanded = true; p = p.Parent; }
    }

    public void SelectFolder(Func<FolderNode, bool> predicate)
    {
        var n = AllNodes().FirstOrDefault(x => x.Folder != null && !x.IsFavorite && predicate(x)) ?? AllNodes().FirstOrDefault(x => x.Folder != null && predicate(x));
        if (n == null) return;
        ExpandTo(n);
        n.IsSelected = true;
    }

    public void SelectInbox(string? accountId = null) =>
        SelectFolder(n => n.Folder!.Kind == FolderKind.Inbox && n.MailboxEmail == null && (accountId == null || n.Service?.Account.Id == accountId));

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode n && n.Folder != null)
        {
            _selectedFolderId = n.Folder.Id;
            FolderSelected?.Invoke(this, n);
        }
    }

    // ------------------------------------------------------------------ Context menu

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = Ui.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not FolderNode node) return;
        e.Handled = true;
        var menu = BuildMenu(node);
        if (menu == null) return;
        menu.PlacementTarget = item;
        menu.IsOpen = true;
    }

    private ContextMenu? BuildMenu(FolderNode node)
    {
        var m = new ContextMenu();
        var owner = Window.GetWindow(this);
        if (node.NodeKind == NodeKind.FavoritesHeader) return null;
        if (node.Folder == null)
        {
            if (node.Service == null) return null;
            m.Items.Add(Ui.Menu(Loc.T("NewFolder"), () => _ = NewFolderAsync(node), G.NewFolder));
            m.Items.Add(new Separator());
            m.Items.Add(Ui.Menu(Loc.T("AccountSettings"), () => new AccountSettingsWindow { Owner = owner }.ShowDialog(), G.Settings));
            if (node.NodeKind == NodeKind.MailboxRoot)
                m.Items.Add(Ui.Menu(Loc.T("Remove"), () => RemoveMailbox(node), G.Remove));
            return m;
        }
        var f = node.Folder;
        var special = f.Kind != FolderKind.Generic;
        m.Items.Add(Ui.Menu(Loc.T("NewFolder"), () => _ = NewFolderAsync(node), G.NewFolder));
        m.Items.Add(Ui.Menu(Loc.T("RenameFolder"), () => _ = RenameAsync(node), G.Rename, !special));
        m.Items.Add(Ui.Menu(Loc.T("CopyFolder"), () => _ = MoveFolderAsync(node, true), G.Copy, !special));
        m.Items.Add(Ui.Menu(Loc.T("MoveFolder"), () => _ = MoveFolderAsync(node, false), G.Move, !special));
        m.Items.Add(Ui.Menu(Loc.T("DeleteFolder"), () => _ = DeleteFolderAsync(node), G.Delete, !special));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Menu(Loc.T("MarkAllAsRead"), () => _ = MarkAllReadAsync(node), G.Read));
        m.Items.Add(Ui.Menu(Loc.T("CleanUpFolder"), () => CleanUpRequested?.Invoke(this, node), G.Clean));
        m.Items.Add(Ui.Menu(Loc.T("DeleteAll"), () => _ = EmptyAsync(node), G.Delete));
        m.Items.Add(new Separator());
        var isFav = AppServices.Current.Settings.Favorites.Contains(f.Id) || node.IsFavorite;
        m.Items.Add(Ui.Menu(Loc.T(isFav ? "RemoveFromFavorites" : "ShowInFavorites"), () => ToggleFavorite(node), G.Star));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Menu(Loc.T("FolderPermissions"), () => PermissionsWindow.ShowFor(node.Service!, f.Id, FolderVisuals.LocalizedName(f), owner), G.Lock));
        return m;
    }

    public event EventHandler<FolderNode>? CleanUpRequested;

    public async Task NewFolderAsync(FolderNode? parent)
    {
        parent ??= SelectedNode;
        if (parent?.Service == null) return;
        var svc = parent.Service;
        var w = new NewFolderWindow(svc, FoldersOf(svc).ToList(), parent.Folder) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() != true) return;
        var parentId = w.ParentFolder?.Id;
        if (parentId == null)
        {
            var root = await svc.GetWellKnownFolderAsync(FolderKind.Root, parent.MailboxEmail);
            parentId = root?.Id;
        }
        if (parentId == null) return;
        if (await Ui.RunAsync(() => svc.CreateFolderAsync(parentId, w.FolderName, w.FolderClass)))
        {
            if (parent.Folder != null) AppServices.Current.Settings.ExpandedFolders["f:" + parentId] = true;
            await LoadAsync();
        }
    }

    public async Task RenameAsync(FolderNode? node)
    {
        node ??= SelectedNode;
        if (node?.Folder == null || node.Service == null || node.Folder.Kind != FolderKind.Generic) return;
        var name = Ui.Prompt(Loc.T("RenameFolder"), Loc.T("NewFolderPrompt"), node.Folder.DisplayName, Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name) || name == node.Folder.DisplayName) return;
        if (await Ui.RunAsync(() => node.Service.RenameFolderAsync(node.Folder.Id, name)))
        {
            node.Folder.DisplayName = name;
            node.RefreshDisplay();
        }
    }

    public async Task MoveFolderAsync(FolderNode? node, bool copy)
    {
        node ??= SelectedNode;
        if (node?.Folder == null || node.Service == null) return;
        var w = new FolderPickerWindow(node.Service, Loc.T(copy ? "CopyFolder" : "MoveFolder"), FoldersOf(node.Service).ToList(), allowRoot: true) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() != true) return;
        var targetId = w.SelectedFolder?.Id;
        if (targetId == null)
        {
            var root = await node.Service.GetWellKnownFolderAsync(FolderKind.Root, node.MailboxEmail);
            targetId = root?.Id;
        }
        if (targetId == null || targetId == node.Folder.Id) return;
        if (await Ui.RunAsync(() => copy ? node.Service.CopyFolderAsync(node.Folder.Id, targetId) : node.Service.MoveFolderAsync(node.Folder.Id, targetId)))
            await LoadAsync();
    }

    public async Task DeleteFolderAsync(FolderNode? node)
    {
        node ??= SelectedNode;
        if (node?.Folder == null || node.Service == null || node.Folder.Kind != FolderKind.Generic) return;
        if (!Ui.Confirm(Loc.F("DeleteFolderConfirm", node.Folder.DisplayName), Window.GetWindow(this), MessageBoxImage.Warning)) return;
        if (await Ui.RunAsync(() => node.Service.DeleteFolderAsync(node.Folder.Id, false)))
        {
            AppServices.Current.Settings.Favorites.Remove(node.Folder.Id);
            if (_selectedFolderId == node.Folder.Id) _selectedFolderId = null;
            await LoadAsync();
            if (SelectedNode == null) SelectInbox(node.Service.Account.Id);
        }
    }

    public async Task MarkAllReadAsync(FolderNode? node)
    {
        node ??= SelectedNode;
        if (node?.Folder == null || node.Service == null) return;
        if (await Ui.RunAsync(() => node.Service.MarkAllReadAsync(node.Folder.Id)))
        {
            node.Folder.UnreadCount = 0;
            AppServices.Current.RaiseDataChanged("items:" + node.Folder.Id);
        }
    }

    public async Task EmptyAsync(FolderNode? node)
    {
        node ??= SelectedNode;
        if (node?.Folder == null || node.Service == null) return;
        if (!Ui.Confirm(Loc.F("EmptyFolderConfirm", FolderVisuals.LocalizedName(node.Folder)), Window.GetWindow(this), MessageBoxImage.Warning)) return;
        if (await Ui.RunAsync(() => node.Service.EmptyFolderAsync(node.Folder.Id, node.Folder.Kind == FolderKind.DeletedItems)))
        {
            node.Folder.UnreadCount = 0;
            node.Folder.TotalCount = 0;
            AppServices.Current.Cache.ReplaceItems(node.Service.Account.Id, node.Folder.Id, Array.Empty<MailItemSummary>());
            AppServices.Current.RaiseDataChanged("items:" + node.Folder.Id);
            if (node.Folder.Kind == FolderKind.DeletedItems) await LoadAsync();
        }
    }

    public void ToggleFavorite(FolderNode? node)
    {
        node ??= SelectedNode;
        if (node?.Folder == null) return;
        var s = AppServices.Current.Settings;
        if (s.Favorites.Count == 0)
        {
            // materialize the default favorites before changing them
            var fav = Roots.FirstOrDefault(r => r.NodeKind == NodeKind.FavoritesHeader);
            if (fav != null) s.Favorites.AddRange(fav.Children.Select(c => c.Folder!.Id));
        }
        if (s.Favorites.Contains(node.Folder.Id)) s.Favorites.Remove(node.Folder.Id);
        else s.Favorites.Add(node.Folder.Id);
        AppServices.Current.SettingsService.Save();
        Rebuild();
    }

    private void RemoveMailbox(FolderNode node)
    {
        if (node.Service == null || node.MailboxEmail == null) return;
        node.Service.Account.AdditionalMailboxes.Remove(node.MailboxEmail);
        AppServices.Current.NotifyAccountsChanged();
        _folders.Remove(node.Service.Account.Id + "|" + node.MailboxEmail);
        Rebuild();
    }

    // ------------------------------------------------------------------ Drag & drop

    private FolderNode? _dropHighlight;

    private void SetDropHighlight(FolderNode? n)
    {
        if (_dropHighlight == n) return;
        if (_dropHighlight != null) _dropHighlight.IsDropTarget = false;
        _dropHighlight = n;
        if (n != null) n.IsDropTarget = true;
    }

    private FolderNode? NodeAt(DragEventArgs e) => Ui.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as FolderNode;

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        var node = NodeAt(e);
        var ok = node?.Folder != null;
        if (ok && e.Data.GetData("PostaItems") is DragPayload p)
            ok = p.AccountId == node!.Service?.Account.Id && p.SourceFolderId != node.Folder!.Id;
        else if (ok && e.Data.GetData("PostaFolder") is FolderNode src)
            ok = src != node && src.Service == node!.Service && src.Folder?.Kind == FolderKind.Generic;
        else ok = false;
        e.Effects = ok ? (e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) ? DragDropEffects.Copy : DragDropEffects.Move) : DragDropEffects.None;
        SetDropHighlight(ok ? node : null);
        if (ok && node is { IsExpanded: false, Children.Count: > 0 }) node.IsExpanded = true;
        e.Handled = true;
    }

    private void Tree_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(null);

    private async void Tree_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(null);
        var node = NodeAt(e);
        if (node?.Folder == null || node.Service == null) return;
        var copy = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);
        if (e.Data.GetData("PostaItems") is DragPayload p)
        {
            if (p.AccountId != node.Service.Account.Id) return;
            var ok = await Ui.RunAsync(() => copy ? node.Service.CopyItemsAsync(p.ItemIds, node.Folder.Id) : node.Service.MoveItemsAsync(p.ItemIds, node.Folder.Id), null, false);
            if (ok)
            {
                ItemsDropped?.Invoke(this, p);
                try
                {
                    var (unread, total) = await node.Service.GetFolderCountsAsync(node.Folder.Id);
                    node.Folder.UnreadCount = unread;
                    node.Folder.TotalCount = total;
                }
                catch { }
            }
        }
        else if (e.Data.GetData("PostaFolder") is FolderNode src && src.Folder != null && src.Service == node.Service)
        {
            if (await Ui.RunAsync(() => copy ? node.Service.CopyFolderAsync(src.Folder.Id, node.Folder.Id) : node.Service.MoveFolderAsync(src.Folder.Id, node.Folder.Id)))
                await LoadAsync();
        }
    }

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < 10 && Math.Abs(pos.Y - _dragStart.Y) < 10) return;
        var item = Ui.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not FolderNode { Folder.Kind: FolderKind.Generic, IsFavorite: false } node) return;
        DragDrop.DoDragDrop(Tree, new DataObject("PostaFolder", node), DragDropEffects.Move | DragDropEffects.Copy);
    }
}
