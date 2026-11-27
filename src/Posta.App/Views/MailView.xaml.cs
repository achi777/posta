using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.App.ViewModels;
using Posta.App.Windows;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Views;

public partial class MailListOptions : ObservableObject
{
    [ObservableProperty] private bool _showAvatars = true;
    [ObservableProperty] private bool _showPreview = true;
}

public sealed class DragPayload
{
    public string AccountId { get; init; } = "";
    public List<string> ItemIds { get; init; } = new();
    public string SourceFolderId { get; init; } = "";
}

public partial class MailView : UserControl
{
    private readonly ObservableCollection<MailItemSummary> _items = new();
    private readonly ListCollectionView _view;
    private readonly MailListOptions _options = new();
    private readonly DispatcherTimer _markReadTimer = new();
    private readonly DispatcherTimer _selectTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _msgCts;
    private int _offset;
    private bool _more;
    private bool _loadingPage;
    private MailItemSummary? _pendingRead;
    private Point _dragStart;

    public FolderNode? Node { get; private set; }
    public IMailService? Service => Node?.Service;
    public MailFolderInfo? Folder => Node?.Folder;

    public MailFilter Filter { get; private set; } = MailFilter.All;
    public SortField Sort { get; private set; } = SortField.Date;
    public bool Descending { get; private set; } = true;
    public string? SearchText { get; private set; }

    public Func<IMailService, IEnumerable<MailFolderInfo>>? AllFoldersProvider { get; set; }

    public event EventHandler? StatusChanged;
    public event EventHandler? SelectionStateChanged;

    public int TotalCount { get; private set; }
    public int UnreadCount => Folder?.UnreadCount ?? _items.Count(i => !i.IsRead);
    public bool IsLoading => LoadingBar.Visibility == Visibility.Visible;

    public ReadingPane ReadingPane => Reading;

    public MailView()
    {
        InitializeComponent();
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_items);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(null, new GroupKeyConverter(this)));
        MessageList.ItemsSource = _view;
        MessageList.DataContext = _options;
        SearchBox.Tag = Loc.T("SearchCurrentMailbox");
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            SearchBox.Tag = Loc.T("SearchCurrentMailbox");
            UpdateSortText();
            _view.Refresh();
        };

        _markReadTimer.Tick += async (_, _) =>
        {
            _markReadTimer.Stop();
            if (_pendingRead != null && MessageList.SelectedItem == _pendingRead && !_pendingRead.IsRead)
                await SetReadAsync(new[] { _pendingRead }, true);
        };
        _selectTimer.Tick += async (_, _) =>
        {
            _selectTimer.Stop();
            await ShowSelectedAsync();
        };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await ApplySearchAsync();
        };

        Reading.ItemChanged += async (_, _) => await RefreshAsync(true);
        AppServices.Current.SyncTick += async (_, _) => { if (IsVisible && Node != null && string.IsNullOrEmpty(SearchText)) await RefreshAsync(true); };
        Loaded += (_, _) => ApplyLayout();
    }

    // =====================================================================================
    // Layout (reading pane position / widths)
    // =====================================================================================

    public void ApplyLayout()
    {
        var s = AppServices.Current.Settings;
        _options.ShowAvatars = s.ShowSenderPhotos;
        _options.ShowPreview = s.MessagePreviewLines > 0;
        Grid.SetRow(Splitter, 0); Grid.SetColumn(Splitter, 1);
        Grid.SetRow(Reading, 0); Grid.SetColumn(Reading, 2);
        Grid.SetColumnSpan(ListHost, 1);
        Grid.SetRowSpan(ListHost, 1);
        Reading.Zoom = s.ReadingZoom;
        switch (s.ReadingPane)
        {
            case ReadingPanePosition.Right:
                ListColumn.Width = new GridLength(Math.Max(260, s.MessageListWidth));
                SplitColumn.Width = GridLength.Auto;
                PaneColumn.Width = new GridLength(1, GridUnitType.Star);
                ListRow.Height = new GridLength(1, GridUnitType.Star);
                SplitRow.Height = new GridLength(0);
                PaneRow.Height = new GridLength(0);
                Splitter.Style = (Style)FindResource("GridSplitterV");
                Splitter.Visibility = Visibility.Visible;
                Reading.Visibility = Visibility.Visible;
                break;
            case ReadingPanePosition.Bottom:
                ListColumn.Width = new GridLength(1, GridUnitType.Star);
                SplitColumn.Width = new GridLength(0);
                PaneColumn.Width = new GridLength(0);
                ListRow.Height = new GridLength(Math.Max(160, s.MessageListHeight));
                SplitRow.Height = GridLength.Auto;
                PaneRow.Height = new GridLength(1, GridUnitType.Star);
                Grid.SetColumnSpan(ListHost, 3);
                Grid.SetRow(Splitter, 1); Grid.SetColumn(Splitter, 0); Grid.SetColumnSpan(Splitter, 3);
                Grid.SetRow(Reading, 2); Grid.SetColumn(Reading, 0); Grid.SetColumnSpan(Reading, 3);
                Splitter.Style = (Style)FindResource("GridSplitterH");
                Splitter.Visibility = Visibility.Visible;
                Reading.Visibility = Visibility.Visible;
                break;
            default:
                ListColumn.Width = new GridLength(1, GridUnitType.Star);
                SplitColumn.Width = new GridLength(0);
                PaneColumn.Width = new GridLength(0);
                ListRow.Height = new GridLength(1, GridUnitType.Star);
                SplitRow.Height = new GridLength(0);
                PaneRow.Height = new GridLength(0);
                Grid.SetColumnSpan(ListHost, 3);
                Splitter.Visibility = Visibility.Collapsed;
                Reading.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var s = AppServices.Current.Settings;
        if (s.ReadingPane == ReadingPanePosition.Right) s.MessageListWidth = ListColumn.ActualWidth;
        else if (s.ReadingPane == ReadingPanePosition.Bottom) s.MessageListHeight = ListRow.ActualHeight;
        AppServices.Current.SettingsService.Save();
    }

    // =====================================================================================
    // Loading
    // =====================================================================================

    public async Task ShowFolderAsync(FolderNode node)
    {
        if (node.Folder == null || node.Service == null) return;
        var changed = Node?.Folder?.Id != node.Folder.Id;
        Node = node;
        if (changed)
        {
            SearchBox.Text = "";
            SearchText = null;
            Reading.Clear();
            _items.Clear();
            // Show cached headers immediately
            var cached = AppServices.Current.Cache.GetItems(node.Service.Account.Id, node.Folder.Id);
            foreach (var c in cached) { Prepare(c); _items.Add(c); }
            TotalCount = node.Folder.TotalCount;
        }
        await LoadAsync(append: false);
    }

    public Task RefreshAsync(bool silent = false) => Node == null ? Task.CompletedTask : LoadAsync(false, silent);

    private void Prepare(MailItemSummary s)
    {
        var showTo = Folder?.Kind is FolderKind.SentItems or FolderKind.Drafts or FolderKind.Outbox;
        s.ListName = showTo ? (string.IsNullOrWhiteSpace(s.DisplayTo) ? "" : s.DisplayTo) : (string.IsNullOrWhiteSpace(s.From.Display) ? s.DisplayTo : s.From.Display);
        if (string.IsNullOrWhiteSpace(s.Subject)) s.Subject = Loc.T("NoSubject");
    }

    private async Task LoadAsync(bool append, bool silent = false)
    {
        var node = Node;
        if (node?.Service == null || node.Folder == null) return;
        if (AppServices.Current.Settings.WorkOffline) return;
        if (append && (_loadingPage || !_more)) return;

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _loadingPage = true;
        if (!silent) LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var svc = node.Service;
            var pageSize = append ? 100 : Math.Max(100, append ? 100 : _items.Count > 100 && silent ? Math.Min(_items.Count, 500) : 100);
            var offset = append ? _offset : 0;
            Paged<MailItemSummary> page;

            if (!string.IsNullOrWhiteSpace(SearchText) && ScopeBox.SelectedIndex == 1 && AllFoldersProvider != null)
            {
                // Search the whole mailbox: query each mail folder and merge.
                var results = new List<MailItemSummary>();
                foreach (var f in AllFoldersProvider(svc).Where(f => f.IsMailFolder && f.Kind != FolderKind.Root))
                {
                    if (cts.IsCancellationRequested) return;
                    try
                    {
                        var p = await svc.GetItemsAsync(new MailQuery { FolderId = f.Id, PageSize = 50, Filter = Filter, Search = SearchText, Sort = Sort, Descending = Descending }, cts.Token);
                        results.AddRange(p.Items);
                    }
                    catch { }
                }
                page = new Paged<MailItemSummary> { Items = results.OrderByDescending(r => r.Received).ToList(), TotalCount = results.Count };
            }
            else
            {
                page = await svc.GetItemsAsync(new MailQuery
                {
                    FolderId = node.Folder.Id, Offset = offset, PageSize = pageSize, Filter = Filter, Search = SearchText, Sort = Sort, Descending = Descending
                }, cts.Token);
            }
            if (cts.IsCancellationRequested || Node != node) return;

            foreach (var i in page.Items) Prepare(i);
            if (append)
            {
                foreach (var i in page.Items.Where(i => _items.All(x => x.Id != i.Id))) _items.Add(i);
            }
            else
            {
                MergeInto(page.Items);
                if (node.MailboxEmail == null && Filter == MailFilter.All && string.IsNullOrWhiteSpace(SearchText) && Sort == SortField.Date && Descending)
                    AppServices.Current.Cache.ReplaceItems(svc.Account.Id, node.Folder.Id, page.Items);
            }
            _offset = offset + page.Items.Count;
            _more = page.MoreAvailable;
            TotalCount = page.TotalCount;

            // keep folder counters in sync
            if (string.IsNullOrWhiteSpace(SearchText) && Filter == MailFilter.All)
            {
                try
                {
                    var (unread, total) = await svc.GetFolderCountsAsync(node.Folder.Id);
                    node.Folder.UnreadCount = unread;
                    node.Folder.TotalCount = total;
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!silent && _items.Count == 0) EmptyText.Text = ex.Message;
        }
        finally
        {
            if (_loadCts == cts)
            {
                _loadingPage = false;
                LoadingBar.Visibility = Visibility.Collapsed;
            }
            UpdateEmpty();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Merges a fresh first page into the list, preserving selection and object identity.</summary>
    private void MergeInto(IReadOnlyList<MailItemSummary> fresh)
    {
        var selectedIds = MessageList.SelectedItems.Cast<MailItemSummary>().Select(s => s.Id).ToHashSet();
        var map = _items.ToDictionary(i => i.Id);
        var freshIds = fresh.Select(f => f.Id).ToHashSet();
        var keepExtra = _items.Count > fresh.Count && _more;

        var result = new List<MailItemSummary>(fresh.Count);
        foreach (var f in fresh)
        {
            if (map.TryGetValue(f.Id, out var existing))
            {
                existing.IsRead = f.IsRead;
                existing.Flag = f.Flag;
                existing.FlagDue = f.FlagDue;
                existing.Categories = f.Categories;
                existing.Importance = f.Importance;
                existing.Subject = f.Subject;
                existing.Preview = f.Preview;
                existing.ListName = f.ListName;
                result.Add(existing);
            }
            else result.Add(f);
        }
        if (keepExtra) result.AddRange(_items.Skip(fresh.Count).Where(i => !freshIds.Contains(i.Id)));

        var same = result.Count == _items.Count && result.Select((r, i) => ReferenceEquals(r, _items[i])).All(x => x);
        if (same) return;

        _items.Clear();
        foreach (var r in result) _items.Add(r);
        foreach (var r in result.Where(r => selectedIds.Contains(r.Id))) MessageList.SelectedItems.Add(r);
    }

    private void UpdateEmpty()
    {
        EmptyText.Visibility = _items.Count == 0 && !_loadingPage ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Count == 0 && EmptyText.Text != Loc.T("NothingToShow") && !_loadingPage && string.IsNullOrEmpty(SearchText)) { }
        FilterBadge.Text = TotalCount > _items.Count && _more ? $"{_items.Count}/{TotalCount}" : "";
    }

    private ScrollViewer? _scroll;

    protected override void OnRender(System.Windows.Media.DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_scroll == null)
        {
            _scroll = Ui.FindChildren<ScrollViewer>(MessageList).FirstOrDefault();
            if (_scroll != null)
                _scroll.ScrollChanged += async (_, e) =>
                {
                    if (_more && !_loadingPage && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 300)
                        await LoadAsync(append: true, silent: true);
                };
        }
    }

    // =====================================================================================
    // Grouping
    // =====================================================================================

    private sealed class GroupKeyConverter : IValueConverter
    {
        private readonly MailView _owner;
        public GroupKeyConverter(MailView owner) => _owner = owner;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not MailItemSummary s) return "";
            switch (_owner.Sort)
            {
                case SortField.From: return string.IsNullOrWhiteSpace(s.ListName) ? Loc.T("GrpNone") : s.ListName;
                case SortField.To: return string.IsNullOrWhiteSpace(s.DisplayTo) ? Loc.T("GrpNone") : s.DisplayTo;
                case SortField.Subject: return string.IsNullOrWhiteSpace(s.Subject) ? Loc.T("NoSubject") : s.Subject.Replace("RE: ", "").Replace("FW: ", "");
                case SortField.Size:
                    return s.Size switch
                    {
                        < 10 * 1024 => Loc.Instance.IsGeorgian ? "პატარა (< 10 KB)" : "Tiny (< 10 KB)",
                        < 100 * 1024 => Loc.Instance.IsGeorgian ? "მცირე (10 – 100 KB)" : "Small (10 - 100 KB)",
                        < 1024 * 1024 => Loc.Instance.IsGeorgian ? "საშუალო (100 KB – 1 MB)" : "Medium (100 KB - 1 MB)",
                        < 5 * 1024 * 1024 => Loc.Instance.IsGeorgian ? "დიდი (1 – 5 MB)" : "Large (1 - 5 MB)",
                        _ => Loc.Instance.IsGeorgian ? "უზარმაზარი (> 5 MB)" : "Huge (> 5 MB)"
                    };
                case SortField.Importance: return Loc.T(s.Importance switch { Importance.High => "High", Importance.Low => "Low", _ => "NormalPriority" });
                case SortField.Attachments: return s.HasAttachments ? Loc.T("Attachments") : Loc.T("GrpNone");
                case SortField.Categories: return s.Categories.Count == 0 ? Loc.T("GrpNone") : Loc.T(s.Categories[0]);
                case SortField.Flag: return s.Flag switch { FlagState.Flagged => Loc.T("Flagged"), FlagState.Complete => Loc.T("TS_Completed"), _ => Loc.T("GrpNone") };
                default: return DateGroup(s.Received);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public static string DateGroup(DateTime d)
    {
        var today = DateTime.Today;
        var date = d.Date;
        if (date > today) return Loc.T("GrpFuture");
        if (date == today) return Loc.T("GrpToday");
        if (date == today.AddDays(-1)) return Loc.T("GrpYesterday");
        var firstDay = AppServices.Current.Settings.FirstDayOfWeek;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)firstDay + 7) % 7));
        if (date >= weekStart)
        {
            var name = Loc.Instance.Culture.DateTimeFormat.GetDayName(date.DayOfWeek);
            return char.ToUpper(name[0]) + name[1..];
        }
        if (date >= weekStart.AddDays(-7)) return Loc.T("GrpLastWeek");
        if (date >= weekStart.AddDays(-14)) return Loc.T("GrpTwoWeeksAgo");
        if (date >= weekStart.AddDays(-21)) return Loc.T("GrpThreeWeeksAgo");
        var monthStart = new DateTime(today.Year, today.Month, 1);
        if (date >= monthStart.AddMonths(-1)) return Loc.T("GrpLastMonth");
        return Loc.T("GrpOlder");
    }

    // =====================================================================================
    // Filter / sort / search
    // =====================================================================================

    private async void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Filter = (sender as FrameworkElement)?.Tag as string == "Unread" ? MailFilter.Unread : MailFilter.All;
        await ReloadFresh();
    }

    public async Task SetFilterAsync(MailFilter f)
    {
        Filter = f;
        if (f == MailFilter.All) FilterAll.IsChecked = true;
        else if (f == MailFilter.Unread) FilterUnread.IsChecked = true;
        else { FilterAll.IsChecked = false; FilterUnread.IsChecked = false; }
        await ReloadFresh();
    }

    public async Task SetSortAsync(SortField field, bool? descending = null)
    {
        Sort = field;
        Descending = descending ?? field is SortField.Date or SortField.Size or SortField.Importance or SortField.Attachments or SortField.Flag;
        UpdateSortText();
        await ReloadFresh();
    }

    public async Task ReverseSortAsync()
    {
        Descending = !Descending;
        UpdateSortText();
        await ReloadFresh();
    }

    private void UpdateSortText()
    {
        SortText.Text = Sort switch
        {
            SortField.Date => Loc.T("ByDate"),
            SortField.From => Loc.T("From"),
            SortField.To => Loc.T("To"),
            SortField.Subject => Loc.T("Subject"),
            SortField.Size => Loc.T("Size"),
            SortField.Importance => Loc.T("Importance"),
            SortField.Attachments => Loc.T("Attachments"),
            SortField.Categories => Loc.T("Categories"),
            SortField.Flag => Loc.T("FlagStatus"),
            _ => ""
        };
        SortArrow.Text = Descending ? G.Down : G.Up;
    }

    private async Task ReloadFresh()
    {
        _items.Clear();
        Reading.Clear();
        _offset = 0;
        _view.Refresh();
        await LoadAsync(false);
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (field, key) in new[] { (SortField.Date, "Date"), (SortField.From, "From"), (SortField.To, "To"), (SortField.Categories, "Categories"),
                     (SortField.Flag, "FlagStatus"), (SortField.Size, "Size"), (SortField.Subject, "Subject"), (SortField.Attachments, "Attachments"), (SortField.Importance, "Importance") })
        {
            var mi = Ui.Menu(Loc.T(key), async () => await SetSortAsync(field));
            mi.IsCheckable = true;
            mi.IsChecked = Sort == field;
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Menu(Loc.T("ReverseSort"), async () => await ReverseSortAsync(), Descending ? G.Up : G.Down));
        menu.PlacementTarget = SortButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        if (string.IsNullOrEmpty(SearchBox.Text) && !string.IsNullOrEmpty(SearchText)) _searchTimer.Start();
        else if (SearchBox.Text.Length >= 2) _searchTimer.Start();
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _searchTimer.Stop(); await ApplySearchAsync(); }
        else if (e.Key == Key.Escape) { SearchBox.Text = ""; _searchTimer.Stop(); await ApplySearchAsync(); MessageList.Focus(); }
    }

    private async void ScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !string.IsNullOrWhiteSpace(SearchText)) await ReloadFresh();
    }

    private async Task ApplySearchAsync()
    {
        var text = SearchBox.Text.Trim();
        if (text == (SearchText ?? "")) return;
        SearchText = string.IsNullOrEmpty(text) ? null : text;
        await ReloadFresh();
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public async Task SearchForAsync(string text)
    {
        SearchBox.Text = text;
        await ApplySearchAsync();
    }

    // =====================================================================================
    // Selection & reading
    // =====================================================================================

    public List<MailItemSummary> Selected => MessageList.SelectedItems.Cast<MailItemSummary>().ToList();
    public MailItemSummary? Current => MessageList.SelectedItem as MailItemSummary;

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var s = AppServices.Current.Settings;
        if (s.MarkRead == MarkReadMode.WhenSelectionChanges)
        {
            foreach (var removed in e.RemovedItems.OfType<MailItemSummary>().Where(r => !r.IsRead))
                _ = SetReadAsync(new[] { removed }, true);
        }
        _markReadTimer.Stop();
        _selectTimer.Stop();
        _selectTimer.Start();
        SelectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ShowSelectedAsync()
    {
        var sel = Selected;
        if (sel.Count != 1 || Service == null || AppServices.Current.Settings.ReadingPane == ReadingPanePosition.Off)
        {
            if (sel.Count != 1) Reading.Clear();
            return;
        }
        var item = sel[0];
        _msgCts?.Cancel();
        var cts = new CancellationTokenSource();
        _msgCts = cts;
        try
        {
            Reading.ShowLoading();
            var d = await OpenedMessageStore.ReadAsync(Service, item.Id, cts.Token, allowCache: !item.IsReadOnly);
            if (cts.IsCancellationRequested || Current != item) return;
            Reading.Show(d, Service);
            MailActions.ActiveReadingPane = Reading;

            var s = AppServices.Current.Settings;
            if (!s.WorkOffline && Service.State == ConnectionState.Connected && !item.IsReadOnly && !item.IsRead && s.MarkRead == MarkReadMode.AfterSeconds)
            {
                _pendingRead = item;
                _markReadTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.1, s.MarkReadSeconds));
                _markReadTimer.Start();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            Reading.Clear();
            StatusMessage?.Invoke(this, ex.Message);
        }
    }

    public event EventHandler<string>? StatusMessage;

    private async void MessageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Ui.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) == null) return;
        await OpenSelectedAsync();
    }

    public async Task OpenSelectedAsync()
    {
        if (Current == null || Service == null) return;
        var item = Current;
        await MailActions.OpenItemAsync(Service, item);
        if (!AppServices.Current.Settings.WorkOffline && Service.State == ConnectionState.Connected && !item.IsRead)
            await SetReadAsync(new[] { item }, true);
    }

    private async void MessageList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await OpenSelectedAsync(); }
        else if (e.Key == Key.Delete) { e.Handled = true; await DeleteAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); }
    }

    private async void HoverAction_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not MailItemSummary item) return;
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "delete": await DeleteItemsAsync(new[] { item }, false); break;
            case "read": await SetReadAsync(new[] { item }, !item.IsRead); break;
            case "flag": await FlagAsync(new[] { item }, item.Flag == FlagState.Flagged ? FlagKind.Complete : FlagKind.Today); break;
        }
    }

    // =====================================================================================
    // Drag & drop source
    // =====================================================================================

    private void MessageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void MessageList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || Service == null || Folder == null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        if (Ui.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) == null) return;
        if (Ui.FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) != null) return;
        var sel = Selected;
        if (sel.Count == 0) return;
        var data = new DataObject("PostaItems", new DragPayload { AccountId = Service.Account.Id, ItemIds = sel.Select(s => s.Id).ToList(), SourceFolderId = Folder.Id });
        DragDrop.DoDragDrop(MessageList, data, DragDropEffects.Move | DragDropEffects.Copy);
    }

    // =====================================================================================
    // Context menu
    // =====================================================================================

    private void MessageList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var sel = Selected;
        if (sel.Count == 0 || Service == null) { e.Handled = true; return; }
        MessageList.ContextMenu = BuildContextMenu(sel);
    }

    private ContextMenu BuildContextMenu(List<MailItemSummary> sel)
    {
        var m = new ContextMenu();
        var one = sel.Count == 1;
        m.Items.Add(Ui.Menu(Loc.T("Copy"), () => CopyToClipboard(sel), G.Copy, gesture: "Ctrl+C"));
        m.Items.Add(Ui.Menu(Loc.T("QuickPrint"), () => Reading.Print(), G.Print, one));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Menu(Loc.T("Reply"), () => _ = ReplyAsync(ComposeMode.Reply), G.Reply, one, "Ctrl+R"));
        m.Items.Add(Ui.Menu(Loc.T("ReplyAll"), () => _ = ReplyAsync(ComposeMode.ReplyAll), G.ReplyAll, one, "Ctrl+Shift+R"));
        m.Items.Add(Ui.Menu(Loc.T("Forward"), () => _ = ReplyAsync(ComposeMode.Forward), G.Forward, one, "Ctrl+F"));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Menu(Loc.T("MarkAsRead"), () => _ = SetReadAsync(sel, true), G.Read, sel.Any(s => !s.IsRead), "Ctrl+Q"));
        m.Items.Add(Ui.Menu(Loc.T("MarkAsUnread"), () => _ = SetReadAsync(sel, false), G.Mail, sel.Any(s => s.IsRead), "Ctrl+U"));
        m.Items.Add(CategorizeMenu(sel));
        m.Items.Add(FollowUpMenu(sel));
        m.Items.Add(new Separator());
        var rules = new MenuItem { Header = Loc.T("Rules"), Icon = Ui.GlyphText(G.Rules, 14, GlyphFactory.DefaultBrush) };
        if (one)
        {
            var from = sel[0].From;
            rules.Items.Add(Ui.Menu(Loc.F("AlwaysMoveFrom", from.Display), () => _ = CreateMoveRuleAsync(from)));
        }
        rules.Items.Add(Ui.Menu(Loc.T("CreateRule"), () => _ = CreateRuleAsync()));
        rules.Items.Add(Ui.Menu(Loc.T("ManageRulesAlerts"), () => RulesWindow.ShowFor(Service!, Window.GetWindow(this))));
        m.Items.Add(rules);
        m.Items.Add(MoveMenu(sel));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Menu(Loc.T("Ignore"), () => _ = IgnoreAsync(), G.Ignore, one));
        var junk = new MenuItem { Header = Loc.T("Junk"), Icon = Ui.GlyphText(G.Junk, 14, GlyphFactory.DefaultBrush) };
        junk.Items.Add(Ui.Menu(Loc.T("BlockSender"), () => _ = JunkAsync(true), G.Blocked));
        junk.Items.Add(Ui.Menu(Loc.T("NeverBlockSender"), () => _ = JunkAsync(false)));
        junk.Items.Add(Ui.Menu(Loc.T("NotJunk"), () => _ = JunkAsync(false), null, Folder?.Kind == FolderKind.JunkEmail));
        m.Items.Add(junk);
        m.Items.Add(Ui.Menu(Loc.T("Delete"), () => _ = DeleteAsync(false), G.Delete, true, "Del"));
        m.Items.Add(Ui.Menu(Loc.T("Archive"), () => _ = ArchiveAsync(), G.Archive));
        return m;
    }

    public MenuItem CategorizeMenu(List<MailItemSummary> sel)
    {
        var mi = new MenuItem { Header = Loc.T("Categorize"), Icon = Ui.GlyphText(G.Tag, 14, GlyphFactory.DefaultBrush) };
        foreach (var item in BuildCategoryItems(sel)) mi.Items.Add(item);
        return mi;
    }

    public IEnumerable<object> BuildCategoryItems(List<MailItemSummary> sel)
    {
        foreach (var c in AppServices.Current.Settings.Categories)
        {
            var name = c.Name;
            var item = new MenuItem
            {
                Header = Loc.T(name),
                IsCheckable = true,
                IsChecked = sel.Count > 0 && sel.All(s => s.Categories.Contains(name)),
                Icon = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(2), Background = GlyphFactory.ParseBrush(c.Color, System.Windows.Media.Brushes.Gray) }
            };
            item.Click += async (_, _) => await ToggleCategoryAsync(sel, name);
            yield return item;
        }
        yield return new Separator();
        yield return Ui.Menu(Loc.T("ClearAllCategories"), () => _ = ClearCategoriesAsync(sel));
        yield return Ui.Menu(Loc.T("AllCategories"), () =>
        {
            var w = new CategoriesWindow(sel.FirstOrDefault()?.Categories ?? new List<string>()) { Owner = Window.GetWindow(this) };
            if (w.ShowDialog() == true) _ = SetCategoriesAsync(sel, w.SelectedCategories);
        });
    }

    public MenuItem FollowUpMenu(List<MailItemSummary> sel)
    {
        var mi = new MenuItem { Header = Loc.T("FollowUp"), Icon = Ui.GlyphText(G.Flag, 14, GlyphFactory.ParseBrush("#D13438", System.Windows.Media.Brushes.Red)) };
        foreach (var item in BuildFollowUpItems(sel)) mi.Items.Add(item);
        return mi;
    }

    public IEnumerable<object> BuildFollowUpItems(List<MailItemSummary> sel)
    {
        yield return Ui.Menu(Loc.T("FlagToday"), () => _ = FlagAsync(sel, FlagKind.Today), G.Flag);
        yield return Ui.Menu(Loc.T("FlagTomorrow"), () => _ = FlagAsync(sel, FlagKind.Tomorrow), G.Flag);
        yield return Ui.Menu(Loc.T("FlagThisWeek"), () => _ = FlagAsync(sel, FlagKind.ThisWeek), G.Flag);
        yield return Ui.Menu(Loc.T("FlagNextWeek"), () => _ = FlagAsync(sel, FlagKind.NextWeek), G.Flag);
        yield return Ui.Menu(Loc.T("FlagNoDate"), () => _ = FlagAsync(sel, FlagKind.NoDate), G.Flag);
        yield return Ui.Menu(Loc.T("FlagCustom"), () => _ = FlagAsync(sel, FlagKind.Custom), G.Flag);
        yield return new Separator();
        yield return Ui.Menu(Loc.T("MarkComplete"), () => _ = FlagAsync(sel, FlagKind.Complete), G.CheckMark);
        yield return Ui.Menu(Loc.T("ClearFlag"), () => _ = FlagAsync(sel, FlagKind.Clear), G.Cancel);
    }

    private MenuItem MoveMenu(List<MailItemSummary> sel)
    {
        var mi = new MenuItem { Header = Loc.T("MoveToFolder"), Icon = Ui.GlyphText(G.Move, 14, GlyphFactory.DefaultBrush) };
        if (Service != null && AllFoldersProvider != null)
        {
            foreach (var f in AllFoldersProvider(Service).Where(f => f.Kind is FolderKind.Inbox or FolderKind.Archive or FolderKind.Generic && f.IsMailFolder && f.Id != Folder?.Id).Take(8))
            {
                var target = f;
                mi.Items.Add(Ui.Menu(FolderVisuals.LocalizedName(f), () => _ = MoveToAsync(target.Id), G.Folder));
            }
            mi.Items.Add(new Separator());
        }
        mi.Items.Add(Ui.Menu(Loc.T("OtherFolder"), () => _ = MoveToOtherAsync(false), G.Move, gesture: "Ctrl+Shift+V"));
        mi.Items.Add(Ui.Menu(Loc.T("CopyToFolder"), () => _ = MoveToOtherAsync(true), G.Copy));
        return mi;
    }

    private void CopyToClipboard(List<MailItemSummary> sel)
    {
        var text = string.Join(Environment.NewLine, sel.Select(s => $"{s.From.Display}\t{s.Subject}\t{s.Received:g}"));
        try { Clipboard.SetText(text); } catch { }
    }

    // =====================================================================================
    // Actions (also invoked from the ribbon)
    // =====================================================================================

    private void RemoveItems(IEnumerable<MailItemSummary> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        var idx = _items.IndexOf(list[0]);
        foreach (var i in list) _items.Remove(i);
        if (Service != null) AppServices.Current.Cache.RemoveItems(Service.Account.Id, list.Select(l => l.Id));
        if (_items.Count > 0)
        {
            var next = _items[Math.Clamp(idx, 0, _items.Count - 1)];
            MessageList.SelectedItem = next;
            MessageList.ScrollIntoView(next);
        }
        else Reading.Clear();
        UpdateEmpty();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(bool permanent) => await DeleteItemsAsync(Selected, permanent);

    public async Task DeleteItemsAsync(IEnumerable<MailItemSummary> items, bool permanent)
    {
        var list = items.ToList();
        if (list.Count == 0 || Service == null) return;
        var hard = permanent || Folder?.Kind == FolderKind.DeletedItems;
        if (hard && !Ui.Confirm(Loc.T("DeleteConfirm"), Window.GetWindow(this), MessageBoxImage.Warning)) return;
        RemoveItems(list);
        if (!await Ui.RunAsync(() => Service.DeleteItemsAsync(list.Select(i => i.Id), hard), Window.GetWindow(this), false))
            await RefreshAsync(true);
        else UpdateFolderCounts(list, -1);
    }

    private void UpdateFolderCounts(List<MailItemSummary> removed, int sign)
    {
        if (Folder == null) return;
        Folder.TotalCount = Math.Max(0, Folder.TotalCount + sign * removed.Count);
        Folder.UnreadCount = Math.Max(0, Folder.UnreadCount + sign * removed.Count(r => !r.IsRead));
    }

    public async Task MoveToAsync(string folderId, bool copy = false, IMailService? target = null)
    {
        var list = Selected;
        if (list.Count == 0 || Service == null) return;
        if (target != null && target.Account.Id != Service.Account.Id)
        {
            Ui.Info(Loc.Instance.IsGeorgian ? "სხვა ანგარიშში გადატანა არ არის მხარდაჭერილი." : "Moving items between different accounts is not supported.");
            return;
        }
        if (!copy) RemoveItems(list);
        var ok = await Ui.RunAsync(() => copy ? Service.CopyItemsAsync(list.Select(i => i.Id), folderId) : Service.MoveItemsAsync(list.Select(i => i.Id), folderId), Window.GetWindow(this), false);
        if (!ok) await RefreshAsync(true);
        else if (!copy) UpdateFolderCounts(list, -1);
        AppServices.Current.RaiseDataChanged("folders");
    }

    public async Task MoveToOtherAsync(bool copy)
    {
        if (Service == null || Selected.Count == 0) return;
        var w = new FolderPickerWindow(Service, Loc.T(copy ? "CopyItemsTo" : "MoveItemsTo"), AllFoldersProvider?.Invoke(Service).ToList()) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() == true && w.SelectedFolder != null) await MoveToAsync(w.SelectedFolder.Id, copy);
    }

    public async Task ArchiveAsync()
    {
        if (Service == null || Selected.Count == 0) return;
        var archive = AllFoldersProvider?.Invoke(Service).FirstOrDefault(f => f.Kind == FolderKind.Archive && f.MailboxEmail == Node?.MailboxEmail);
        string? id = archive?.Id;
        if (id == null)
        {
            var root = await Service.GetWellKnownFolderAsync(FolderKind.Root, Node?.MailboxEmail);
            if (root == null) return;
            if (!await Ui.RunAsync(async () => id = await Service.CreateFolderAsync(root.Id, Loc.Instance.IsGeorgian ? "არქივი" : "Archive", "IPF.Note"))) return;
            AppServices.Current.RaiseDataChanged("folders");
        }
        await MoveToAsync(id!);
    }

    public async Task SetReadAsync(IEnumerable<MailItemSummary> items, bool read)
    {
        if (AppServices.Current.Settings.WorkOffline) return;
        var list = items.Where(i => !i.IsReadOnly && i.IsRead != read).ToList();
        if (list.Count == 0 || Service == null) return;
        foreach (var i in list) i.IsRead = read;
        if (Folder != null) Folder.UnreadCount = Math.Max(0, Folder.UnreadCount + (read ? -list.Count : list.Count));
        StatusChanged?.Invoke(this, EventArgs.Empty);
        if (!await Ui.RunAsync(() => Service.SetReadAsync(list.Select(i => i.Id), read), Window.GetWindow(this), false))
            foreach (var i in list) i.IsRead = !read;
    }

    public Task MarkReadAsync(bool read) => SetReadAsync(Selected, read);

    public async Task ToggleReadAsync()
    {
        var sel = Selected;
        if (sel.Count == 0) return;
        await SetReadAsync(sel, sel.Any(s => !s.IsRead));
    }

    public enum FlagKind { Today, Tomorrow, ThisWeek, NextWeek, NoDate, Custom, Complete, Clear }

    public async Task FlagAsync(IEnumerable<MailItemSummary> items, FlagKind kind)
    {
        var list = items.ToList();
        if (list.Count == 0 || Service == null) return;
        var today = DateTime.Today;
        DateTime? start = null, due = null;
        var state = FlagState.Flagged;
        int DaysTo(DayOfWeek d) => ((int)d - (int)today.DayOfWeek + 7) % 7;
        switch (kind)
        {
            case FlagKind.Today: start = due = today; break;
            case FlagKind.Tomorrow: start = due = today.AddDays(1); break;
            case FlagKind.ThisWeek: start = today; due = today.AddDays(Math.Max(0, DaysTo(DayOfWeek.Friday))); break;
            case FlagKind.NextWeek: start = today.AddDays(DaysTo(DayOfWeek.Monday) == 0 ? 7 : DaysTo(DayOfWeek.Monday)); due = start.Value.AddDays(4); break;
            case FlagKind.NoDate: break;
            case FlagKind.Custom:
                var w = new CustomFlagWindow { Owner = Window.GetWindow(this) };
                if (w.ShowDialog() != true) return;
                start = w.StartDate; due = w.DueDate;
                break;
            case FlagKind.Complete: state = FlagState.Complete; break;
            case FlagKind.Clear: state = FlagState.NotFlagged; break;
        }
        foreach (var i in list) { i.Flag = state; i.FlagDue = due; }
        await Ui.RunAsync(() => Service.SetFlagAsync(list.Select(i => i.Id), state, start, due), Window.GetWindow(this), false);
        if (Reading.Message != null && list.Any(l => l.Id == Reading.Message.Id))
        {
            Reading.Message.Flag = state;
            Reading.Show(Reading.Message, Service);
        }
    }

    public Task FlagSelectedAsync(FlagKind kind) => FlagAsync(Selected, kind);

    private async Task ToggleCategoryAsync(List<MailItemSummary> sel, string name)
    {
        var add = !sel.All(s => s.Categories.Contains(name));
        foreach (var s in sel)
        {
            var cats = new List<string>(s.Categories);
            if (add && !cats.Contains(name)) cats.Add(name);
            if (!add) cats.Remove(name);
            s.Categories = cats;
            await Ui.RunAsync(() => Service!.SetCategoriesAsync(s.Id, cats), Window.GetWindow(this), false);
        }
        RefreshReadingCategories(sel);
    }

    private async Task ClearCategoriesAsync(List<MailItemSummary> sel) => await SetCategoriesAsync(sel, new List<string>());

    private async Task SetCategoriesAsync(List<MailItemSummary> sel, List<string> cats)
    {
        foreach (var s in sel)
        {
            s.Categories = new List<string>(cats);
            await Ui.RunAsync(() => Service!.SetCategoriesAsync(s.Id, cats), Window.GetWindow(this), false);
        }
        RefreshReadingCategories(sel);
    }

    private void RefreshReadingCategories(List<MailItemSummary> sel)
    {
        if (Reading.Message != null && Service != null)
        {
            var s = sel.FirstOrDefault(x => x.Id == Reading.Message.Id);
            if (s != null) { Reading.Message.Categories = s.Categories; Reading.Show(Reading.Message, Service); }
        }
    }

    public async Task ReplyAsync(ComposeMode mode)
    {
        if (Current == null || Service == null) return;
        var d = Reading.Message?.Id == Current.Id ? Reading.Message : null;
        if (d == null && !await Ui.RunAsync(async () => d = await Service.GetMessageAsync(Current.Id))) return;
        await MailActions.ReplyAsync(Service, d!, mode);
    }

    public async Task ForwardAsAttachmentAsync()
    {
        if (Current == null || Service == null) return;
        MailMessageDetail? d = null;
        if (!await Ui.RunAsync(async () => d = await Service.GetMessageAsync(Current.Id))) return;
        await MailActions.ForwardAsAttachmentAsync(Service, d!);
    }

    public async Task ReplyWithMeetingAsync()
    {
        if (Current == null || Service == null) return;
        MailMessageDetail? d = null;
        if (!await Ui.RunAsync(async () => d = await Service.GetMessageAsync(Current.Id))) return;
        MailActions.ReplyWithMeeting(Service, d!);
    }

    public async Task IgnoreAsync()
    {
        if (Current == null || Service == null) return;
        var item = Current;
        RemoveItems(new[] { item });
        await Ui.RunAsync(() => Service.IgnoreConversationAsync(item.Id));
    }

    public async Task JunkAsync(bool junk)
    {
        var list = Selected;
        if (list.Count == 0 || Service == null) return;
        RemoveItems(list);
        await Ui.RunAsync(() => Service.MarkAsJunkAsync(list.Select(l => l.Id), junk));
        AppServices.Current.RaiseDataChanged("folders");
    }

    /// <summary>Removes redundant (older, read, quoted) messages of the same conversations in this folder.</summary>
    public async Task CleanUpAsync(bool wholeFolder)
    {
        if (Service == null) return;
        var groups = (wholeFolder ? _items.ToList() : _items.Where(i => i.ConversationId == Current?.ConversationId).ToList())
            .Where(i => !string.IsNullOrEmpty(i.ConversationId)).GroupBy(i => i.ConversationId);
        var redundant = new List<MailItemSummary>();
        foreach (var g in groups)
        {
            var ordered = g.OrderByDescending(i => i.Received).ToList();
            redundant.AddRange(ordered.Skip(1).Where(i => i.IsRead && !i.HasAttachments && i.Flag != FlagState.Flagged));
        }
        if (redundant.Count == 0)
        {
            Ui.Info(Loc.Instance.IsGeorgian ? "გასასუფთავებელი შეტყობინებები არ მოიძებნა." : "No messages were cleaned up.");
            return;
        }
        if (!Ui.Confirm((Loc.Instance.IsGeorgian ? "წაიშლება ზედმეტი შეტყობინებები: " : "Redundant messages to move to Deleted Items: ") + redundant.Count, Window.GetWindow(this))) return;
        await DeleteItemsAsync(redundant, false);
    }

    private async Task CreateMoveRuleAsync(Recipient from)
    {
        if (Service == null) return;
        var w = new FolderPickerWindow(Service, Loc.F("AlwaysMoveFrom", from.Display), AllFoldersProvider?.Invoke(Service).ToList()) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() != true || w.SelectedFolder == null) return;
        await Ui.RunAsync(async () =>
        {
            var rules = (await Service.GetRulesAsync()).ToList();
            rules.Add(new RuleInfo
            {
                Name = from.Display, FromAddresses = { from.Address }, MoveToFolderId = w.SelectedFolder.Id, MoveToFolderName = w.SelectedFolder.DisplayName, StopProcessing = true
            });
            await Service.SaveRulesAsync(rules, Array.Empty<string>());
        });
    }

    private async Task CreateRuleAsync()
    {
        if (Service == null) return;
        var item = Current;
        var rule = new RuleInfo { Name = item?.Subject ?? "" };
        if (item != null) { rule.FromAddresses.Add(item.From.Address); }
        var w = new RuleEditWindow(Service, rule, AllFoldersProvider?.Invoke(Service).ToList() ?? new List<MailFolderInfo>()) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() != true) return;
        await Ui.RunAsync(async () =>
        {
            var rules = (await Service.GetRulesAsync()).ToList();
            rules.Insert(0, w.Rule);
            await Service.SaveRulesAsync(rules, Array.Empty<string>());
        });
    }

    public void Print() => Reading.Print();

    public async Task SaveAsAsync()
    {
        if (Reading.Message != null && Service != null) await MailActions.SaveMessageAsAsync(Service, Reading.Message, Window.GetWindow(this));
    }

    public void SelectAll() => MessageList.SelectAll();
    public void FocusList() => MessageList.Focus();
}
