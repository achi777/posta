using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Posta.App.Controls;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.App.Windows;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Views;

public partial class CalendarView : UserControl
{
    public ObservableCollection<CalendarSource> Sources { get; } = new();
    private readonly Dictionary<string, List<CalendarEvent>> _events = new();
    private readonly List<FrameworkElement> _surfaces = new();
    private int? _customDays;
    private bool _initialized;
    private bool _initializing;
    private CancellationTokenSource? _loadCts;
    private string _search = "";
    private CalendarSource? _selectedSource;

    public DateTime CurrentDate { get; private set; } = DateTime.Today;
    public CalendarViewMode ViewMode { get; private set; }
    public int ItemCount => _events.Values.Sum(v => v.Count);

    public event EventHandler? StatusChanged;
    public event EventHandler? DateChanged;
    public event EventHandler? SourcesChanged;

    private static AppSettings S => AppServices.Current.Settings;

    public CalendarView()
    {
        InitializeComponent();
        ViewMode = S.CalendarView;
        SearchBox.Tag = Loc.Instance.IsGeorgian ? "კალენდარში ძიება" : "Search Calendar";
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            SearchBox.Tag = Loc.Instance.IsGeorgian ? "კალენდარში ძიება" : "Search Calendar";
            foreach (var s in Sources.Where(s => s.IsDefault)) s.DisplayName = Loc.T("F_Calendar");
            if (_initialized) Render();
        };
        AppServices.Current.DataChanged += async (_, what) => { if (what == "calendar" && _initialized) await ReloadAsync(); };
        AppServices.Current.SyncTick += async (_, _) => { if (IsVisible && _initialized) await ReloadAsync(true); };
    }

    // =====================================================================================
    // Sources
    // =====================================================================================

    public async Task InitializeAsync()
    {
        if (_initializing) return;
        _initializing = true;
        try
        {
            var list = new List<CalendarSource>();
            var colorIdx = 0;
            foreach (var svc in AppServices.Current.Accounts.ToList())
            {
                if (svc.State != ConnectionState.Connected) continue;
                try
                {
                    var own = await svc.GetOwnCalendarsAsync();
                    foreach (var c in own)
                    {
                        if (c.IsDefault)
                        {
                            c.DisplayName = AppServices.Current.Accounts.Count > 1 ? $"{Loc.T("F_Calendar")} – {svc.Account.Email}" : Loc.T("F_Calendar");
                        }
                        c.Color = S.CalendarColors.TryGetValue(c.Key, out var col) ? col : CalendarPalette.Colors[colorIdx++ % CalendarPalette.Colors.Length];
                        c.IsChecked = S.CalendarChecked.TryGetValue(c.Key, out var chk) ? chk : c.IsDefault && svc == AppServices.Current.DefaultAccount;
                        list.Add(c);
                    }
                }
                catch { }

                foreach (var r in svc.Account.SharedCalendars.ToList())
                {
                    CalendarSource src;
                    try { src = await svc.OpenSharedCalendarAsync(r.OwnerEmail); }
                    catch
                    {
                        src = new CalendarSource
                        {
                            Key = $"{svc.Account.Id}|{r.OwnerEmail.ToLowerInvariant()}|calendar", AccountId = svc.Account.Id, OwnerEmail = r.OwnerEmail,
                            OwnerName = r.OwnerName, IsShared = true, Access = CalendarAccess.None
                        };
                    }
                    src.DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? src.OwnerName : r.DisplayName;
                    src.Group = r.Group;
                    src.Color = r.Color;
                    src.IsChecked = r.IsChecked;
                    list.Add(src);
                }
            }
            foreach (var s in Sources) s.PropertyChanged -= Source_PropertyChanged;
            Sources.Clear();
            foreach (var s in list)
            {
                s.PropertyChanged += Source_PropertyChanged;
                Sources.Add(s);
            }
            if (!Sources.Any(s => s.IsChecked) && Sources.Count > 0) Sources[0].IsChecked = true;
            _initialized = true;
            SourcesChanged?.Invoke(this, EventArgs.Empty);
            if (IsVisible) await ReloadAsync();
        }
        finally
        {
            _initializing = false;
        }
    }

    private async void Source_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not CalendarSource src) return;
        if (e.PropertyName == nameof(CalendarSource.IsChecked))
        {
            PersistSource(src);
            await ReloadAsync();
        }
        else if (e.PropertyName == nameof(CalendarSource.Color))
        {
            PersistSource(src);
            Render();
        }
    }

    public void PersistSource(CalendarSource src)
    {
        if (src.IsShared)
        {
            var svc = AppServices.Current.GetAccount(src.AccountId);
            var r = svc?.Account.SharedCalendars.FirstOrDefault(x => string.Equals(x.OwnerEmail, src.OwnerEmail, StringComparison.OrdinalIgnoreCase));
            if (r != null)
            {
                r.IsChecked = src.IsChecked;
                r.Color = src.Color;
                r.DisplayName = src.DisplayName;
                r.Group = src.Group;
                AppServices.Current.SettingsService.SaveAccounts();
            }
        }
        else
        {
            S.CalendarChecked[src.Key] = src.IsChecked;
            S.CalendarColors[src.Key] = src.Color;
            AppServices.Current.SettingsService.Save();
        }
    }

    public async Task AddSharedSourceAsync(IMailService svc, CalendarSource src)
    {
        if (Sources.Any(s => s.Key == src.Key))
        {
            Sources.First(s => s.Key == src.Key).IsChecked = true;
            return;
        }
        src.Color = CalendarPalette.Colors[(Sources.Count + 1) % CalendarPalette.Colors.Length];
        src.IsChecked = true;
        svc.Account.SharedCalendars.Add(new SharedCalendarRef
        {
            OwnerEmail = src.OwnerEmail ?? "", OwnerName = src.OwnerName, DisplayName = src.DisplayName, FolderId = src.FolderId, Color = src.Color, IsChecked = true, Group = src.Group
        });
        AppServices.Current.SettingsService.SaveAccounts();
        src.PropertyChanged += Source_PropertyChanged;
        Sources.Add(src);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
        await ReloadAsync();
    }

    public async Task RemoveSourceAsync(CalendarSource src)
    {
        if (!src.IsShared) return;
        var svc = AppServices.Current.GetAccount(src.AccountId);
        svc?.Account.SharedCalendars.RemoveAll(x => string.Equals(x.OwnerEmail, src.OwnerEmail, StringComparison.OrdinalIgnoreCase));
        AppServices.Current.SettingsService.SaveAccounts();
        src.PropertyChanged -= Source_PropertyChanged;
        Sources.Remove(src);
        _events.Remove(src.Key);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
        await ReloadAsync();
    }

    public CalendarSource? DefaultSource => Sources.FirstOrDefault(s => s.IsDefault && s.AccountId == AppServices.Current.DefaultAccount?.Account.Id) ?? Sources.FirstOrDefault(s => s.IsDefault);

    /// <summary>The calendar new items are created in / sharing commands act on.</summary>
    public CalendarSource? SelectedSource
    {
        get => _selectedSource ?? SelectedItem?.Source ?? DefaultSource;
        set => _selectedSource = value;
    }

    // =====================================================================================
    // Navigation
    // =====================================================================================

    public async Task EnsureLoadedAsync()
    {
        if (!_initialized) await InitializeAsync();
        else await ReloadAsync();
    }

    public void NavigateTo(DateTime date)
    {
        CurrentDate = date.Date;
        if (ViewMode == CalendarViewMode.Month && _customDays == null) { }
        _ = ReloadAsync();
        DateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void GoToday()
    {
        _customDays = null;
        NavigateTo(DateTime.Today);
    }

    public void ShowNextDays(int n)
    {
        _customDays = n;
        ViewMode = CalendarViewMode.Day;
        NavigateTo(DateTime.Today);
    }

    public void SetViewMode(CalendarViewMode m)
    {
        _customDays = null;
        ViewMode = m;
        S.CalendarView = m;
        AppServices.Current.SettingsService.Save();
        _ = ReloadAsync();
    }

    public void SetDays(IReadOnlyList<DateTime> days)
    {
        // Selecting several days in the date navigator shows exactly those days
        if (days.Count <= 1) { _customDays = null; NavigateTo(days.FirstOrDefault(DateTime.Today)); return; }
        ViewMode = CalendarViewMode.Day;
        _customDays = Math.Min(14, (days.Max() - days.Min()).Days + 1);
        NavigateTo(days.Min());
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Step(int dir)
    {
        CurrentDate = ViewMode switch
        {
            CalendarViewMode.Month => CurrentDate.AddMonths(dir),
            CalendarViewMode.Week or CalendarViewMode.WorkWeek => CurrentDate.AddDays(7 * dir),
            _ => CurrentDate.AddDays((_customDays ?? 1) * dir)
        };
        _ = ReloadAsync();
        DateChanged?.Invoke(this, EventArgs.Empty);
    }

    private DateTime WeekStart(DateTime d) => d.AddDays(-(((int)d.DayOfWeek - (int)S.FirstDayOfWeek + 7) % 7)).Date;

    public List<DateTime> VisibleDays()
    {
        switch (ViewMode)
        {
            case CalendarViewMode.WorkWeek:
            {
                var ws = WeekStart(CurrentDate);
                var days = Enumerable.Range(0, 7).Select(i => ws.AddDays(i)).Where(d => S.WorkDays.Contains(d.DayOfWeek)).ToList();
                return days.Count > 0 ? days : Enumerable.Range(0, 5).Select(i => ws.AddDays(i)).ToList();
            }
            case CalendarViewMode.Week:
            {
                var ws = WeekStart(CurrentDate);
                return Enumerable.Range(0, 7).Select(i => ws.AddDays(i)).ToList();
            }
            case CalendarViewMode.Month:
            {
                var first = new DateTime(CurrentDate.Year, CurrentDate.Month, 1);
                var gs = WeekStart(first);
                var weeks = (int)Math.Ceiling(((first - gs).Days + DateTime.DaysInMonth(first.Year, first.Month)) / 7.0);
                return Enumerable.Range(0, weeks * 7).Select(i => gs.AddDays(i)).ToList();
            }
            default:
                return Enumerable.Range(0, _customDays ?? 1).Select(i => CurrentDate.AddDays(i)).ToList();
        }
    }

    private string RangeText(List<DateTime> days)
    {
        var c = Loc.Instance.Culture;
        if (ViewMode == CalendarViewMode.Month) return CurrentDate.ToString("MMMM yyyy", c);
        if (days.Count == 1) return days[0].ToString("D", c);
        var a = days[0];
        var b = days[^1];
        if (a.Month == b.Month) return $"{a.Day} – {b.ToString("d MMMM yyyy", c)}";
        if (a.Year == b.Year) return $"{a.ToString("d MMMM", c)} – {b.ToString("d MMMM yyyy", c)}";
        return $"{a.ToString("d MMMM yyyy", c)} – {b.ToString("d MMMM yyyy", c)}";
    }

    // =====================================================================================
    // Loading
    // =====================================================================================

    public async Task ReloadAsync(bool silent = false)
    {
        if (!_initialized) return;
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var days = VisibleDays();
        var start = days[0];
        var end = days[^1].AddDays(1);
        if (!silent) LoadingBar.Visibility = Visibility.Visible;
        RangeTitle.Text = RangeText(days);

        var checkedSources = Sources.Where(s => s.IsChecked).ToList();
        var tasks = checkedSources.Select(async src =>
        {
            var svc = AppServices.Current.GetAccount(src.AccountId);
            if (svc == null || svc.State != ConnectionState.Connected) return (src, (IReadOnlyList<CalendarEvent>?)null, (string?)null);
            try { return (src, await svc.GetEventsAsync(src, start, end, cts.Token), (string?)null); }
            catch (Exception ex) { return (src, (IReadOnlyList<CalendarEvent>?)null, ex.Message); }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        if (cts.IsCancellationRequested) return;

        string? error = null;
        foreach (var (src, list, err) in results)
        {
            if (list != null) _events[src.Key] = list.ToList();
            else if (err != null) { error ??= $"{src.DisplayName}: {err}"; _events.Remove(src.Key); }
        }
        foreach (var k in _events.Keys.Where(k => checkedSources.All(s => s.Key != k)).ToList()) _events.Remove(k);
        LoadingBar.Visibility = Visibility.Collapsed;
        Render();
        InfoText.Visibility = error != null ? Visibility.Visible : Visibility.Collapsed;
        InfoText.Text = error ?? "";
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private IEnumerable<EventItem> ItemsFor(IEnumerable<CalendarSource> sources)
    {
        foreach (var src in sources)
        {
            if (!_events.TryGetValue(src.Key, out var list)) continue;
            foreach (var ev in list)
            {
                if (_search.Length > 0 && !(ev.Subject.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ||
                                            ev.Location.Contains(_search, StringComparison.CurrentCultureIgnoreCase))) continue;
                yield return new EventItem(ev, src);
            }
        }
    }

    // =====================================================================================
    // Rendering
    // =====================================================================================

    private void Render()
    {
        Host.Children.Clear();
        Host.ColumnDefinitions.Clear();
        _surfaces.Clear();
        var days = VisibleDays();
        RangeTitle.Text = RangeText(days);
        var shown = Sources.Where(s => s.IsChecked).ToList();
        if (shown.Count == 0) return;

        if (ViewMode == CalendarViewMode.Schedule)
        {
            var sc = new ScheduleControl();
            sc.SetData(CurrentDate, shown, ItemsFor(shown));
            sc.OpenRequested += (_, item) => _ = OpenItemAsync(item);
            sc.NewRequested += (_, r) => NewFromRange(r, false);
            Host.Children.Add(sc);
            _surfaces.Add(sc);
            return;
        }

        var overlay = S.CalendarOverlay || shown.Count == 1;
        var panels = overlay ? new List<List<CalendarSource>> { shown } : shown.Select(s => new List<CalendarSource> { s }).ToList();
        for (int i = 0; i < panels.Count; i++)
        {
            var group = panels[i];
            var primary = group.FirstOrDefault(s => s.IsDefault && s.CanCreate) ?? group.First();
            Host.ColumnDefinitions.Add(new ColumnDefinition());
            var panel = new DockPanel { Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) };
            if (shown.Count > 1) panel.Children.Add(TabHeader(group, overlay));
            FrameworkElement surface;
            if (ViewMode == CalendarViewMode.Month)
            {
                var mc = new MonthControl { DefaultSource = primary };
                mc.SetData(CurrentDate, ItemsFor(group), !primary.CanCreate);
                mc.NewRequested += (_, r) => NewFromRange(r, false);
                mc.OpenRequested += (_, item) => _ = OpenItemAsync(item);
                mc.EventMoved += async (_, a) => await MoveEventAsync(a);
                mc.DayOpenRequested += (_, d) => { ViewMode = CalendarViewMode.Day; _customDays = null; NavigateTo(d); };
                mc.ContextMenuRequested += (_, a) => ShowContextMenu(a.item, a.range, a.target);
                surface = mc;
            }
            else
            {
                var dw = new DayWeekControl { DefaultSource = primary };
                dw.SetData(days, ItemsFor(group), i == 0, !primary.CanCreate);
                dw.NewRequested += (_, r) => NewFromRange(r, false);
                dw.OpenRequested += (_, item) => _ = OpenItemAsync(item);
                dw.EventMoved += async (_, a) => await MoveEventAsync(a);
                dw.DayHeaderDoubleClicked += (_, d) => { ViewMode = CalendarViewMode.Day; _customDays = null; NavigateTo(d); };
                dw.ContextMenuRequested += (_, a) => ShowContextMenu(a.item, a.range, a.target);
                dw.SelectedItemChanged += (_, item) => { if (item != null) foreach (var o in _surfaces.OfType<DayWeekControl>().Where(o => o != dw)) o.ClearSelection(); };
                dw.HookScroll();
                dw.Scrolled += (_, off) => { foreach (var o in _surfaces.OfType<DayWeekControl>().Where(o => o != dw)) if (Math.Abs(o.VerticalOffset - off) > 1) o.VerticalOffset = off; };
                surface = dw;
            }
            panel.Children.Add(surface);
            _surfaces.Add(surface);
            Grid.SetColumn(panel, i);
            Host.Children.Add(panel);
        }
    }

    private FrameworkElement TabHeader(List<CalendarSource> group, bool overlay)
    {
        var src = group[0];
        var title = overlay ? string.Join(", ", group.Select(g => g.DisplayName)) : src.DisplayName;
        var dp = new DockPanel { Margin = new Thickness(8, 0, 4, 0) };
        var toggle = new Button { Style = (Style)FindResource("FlatButton"), Padding = new Thickness(4, 0, 4, 0), ToolTip = Loc.T(overlay ? "ViewSideBySide" : "ViewInOverlayMode"),
            Content = Ui.GlyphText(overlay ? G.ChevronRight : G.ChevronLeft, 10, Brushes.Black) };
        toggle.Click += (_, _) => { S.CalendarOverlay = !S.CalendarOverlay; AppServices.Current.SettingsService.Save(); Render(); };
        DockPanel.SetDock(toggle, Dock.Left);
        dp.Children.Add(toggle);
        if (!overlay && src.IsShared)
        {
            var close = new Button { Style = (Style)FindResource("FlatButton"), Padding = new Thickness(4, 0, 4, 0), Content = Ui.GlyphText(G.Cancel, 9, Brushes.Black) };
            close.Click += (_, _) => src.IsChecked = false;
            DockPanel.SetDock(close, Dock.Right);
            dp.Children.Add(close);
        }
        dp.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 0) });
        var tab = new Border
        {
            Height = 26, Background = overlay ? Brushes.White : CalendarPalette.Fill(src.Color), CornerRadius = new CornerRadius(3, 3, 0, 0),
            BorderBrush = CalendarPalette.Dark(src.Color), BorderThickness = new Thickness(0, 0, 0, 2), Child = dp, Margin = new Thickness(0, 0, 0, 0)
        };
        if (!overlay && src.Access < CalendarAccess.ReadAll && src.IsShared)
            tab.ToolTip = Loc.T("FreeBusyOnly");
        DockPanel.SetDock(tab, Dock.Top);
        tab.MouseLeftButtonDown += (_, _) => _selectedSource = src;
        return tab;
    }

    // =====================================================================================
    // Item operations
    // =====================================================================================

    public EventItem? SelectedItem =>
        _surfaces.Select(s => s switch
        {
            DayWeekControl d => d.SelectedItem,
            MonthControl m => m.SelectedItem,
            ScheduleControl sc => sc.SelectedItem,
            _ => null
        }).FirstOrDefault(i => i != null);

    private TimeRangeEventArgs? SelectedRange =>
        _surfaces.Select(s => s switch { DayWeekControl d => d.SelectedRange, MonthControl m => m.SelectedRange, _ => null }).FirstOrDefault(r => r != null);

    public void NewAppointment(bool meeting, bool allDay)
    {
        var range = SelectedRange;
        var start = range?.Start ?? DateTime.Now.Date.AddHours(DateTime.Now.Hour + 1);
        var end = range?.End ?? start.AddMinutes(meeting ? S.DefaultMeetingMinutes : 30);
        if (allDay) { start = start.Date; end = start.AddDays(1); }
        NewFromRange(new TimeRangeEventArgs { Start = start, End = end, AllDay = allDay, Source = range?.Source }, meeting);
    }

    private void NewFromRange(TimeRangeEventArgs r, bool meeting)
    {
        if (r.Source is { CanCreate: false }) return;
        var src = r.Source is { CanCreate: true } ? r.Source : (SelectedSource is { CanCreate: true } ? SelectedSource : DefaultSource);
        var svc = src != null ? AppServices.Current.GetAccount(src.AccountId) : AppServices.Current.DefaultAccount;
        if (svc == null) return;
        var ev = new CalendarEvent
        {
            Start = r.Start, End = r.End <= r.Start ? r.Start.AddMinutes(30) : r.End, IsAllDay = r.AllDay,
            ShowAs = r.AllDay ? ShowAs.Free : ShowAs.Busy, ReminderSet = S.DefaultRemindersOn, ReminderMinutes = r.AllDay ? 18 * 60 : S.DefaultReminderMinutes,
            AllowNewTimeProposal = S.AllowAttendeesProposeNewTimes, IsMeeting = meeting
        };
        AppointmentWindow.OpenNew(svc, src, ev, meeting);
    }

    public async Task OpenItemAsync(EventItem item)
    {
        var svc = AppServices.Current.GetAccount(item.Source.AccountId);
        if (svc == null) return;
        if (item.Event.IsFreeBusyOnly)
        {
            Ui.Info(CalendarPalette.Tooltip(item) + "\n\n" + Loc.T("FreeBusyOnly"), Window.GetWindow(this));
            return;
        }
        var scope = EditScope.Single;
        if (item.Event.IsOccurrence && item.CanEdit)
        {
            var w = new SeriesChoiceWindow(Loc.T("SeriesOrOccurrence"), Loc.T("JustThisOne"), Loc.T("EntireSeries")) { Owner = Window.GetWindow(this) };
            if (w.ShowDialog() != true) return;
            scope = w.FirstChosen ? EditScope.Single : EditScope.Series;
        }
        await AppointmentWindow.OpenExistingAsync(svc, item.Source, item.Event.Id, scope, !item.CanEdit);
    }

    public Task DeleteSelectedAsync() => DeleteItemAsync(SelectedItem);

    private async Task DeleteItemAsync(EventItem? item)
    {
        if (item == null || !item.CanDelete) return;
        var svc = AppServices.Current.GetAccount(item.Source.AccountId);
        if (svc == null) return;
        var ev = item.Event;
        var scope = EditScope.Single;
        var owner = Window.GetWindow(this);
        if (ev.IsOccurrence)
        {
            var w = new SeriesChoiceWindow(Loc.T("DeleteSeriesPrompt"), Loc.T("DeleteOccurrence"), Loc.T("DeleteSeries")) { Owner = owner };
            if (w.ShowDialog() != true) return;
            scope = w.FirstChosen ? EditScope.Single : EditScope.Series;
        }
        var notify = false;
        if (ev.IsMeeting)
        {
            var r = Ui.YesNoCancel(ev.IsOrganizer ? Loc.T("CancelMeetingPrompt") : Loc.T("DeleteMeetingResponse"), owner);
            if (r == MessageBoxResult.Cancel) return;
            notify = r == MessageBoxResult.Yes;
        }
        if (await Ui.RunAsync(() => svc.DeleteEventAsync(ev.Id, notify, null, scope), owner))
            await ReloadAsync();
    }

    private async Task MoveEventAsync(EventMovedEventArgs a)
    {
        var svc = AppServices.Current.GetAccount(a.Item.Source.AccountId);
        if (svc == null) return;
        var owner = Window.GetWindow(this);
        await Ui.RunAsync(async () =>
        {
            var full = await svc.GetEventAsync(a.Item.Event.Id);
            if (a.Copy)
            {
                full.Id = "";
                full.Recurrence = null;
                full.IsOccurrence = false;
            }
            full.Start = a.NewStart;
            full.End = a.NewEnd;
            var send = full.IsMeeting && full.IsOrganizer && full.Attendees.Any() &&
                       Ui.Confirm(Loc.Instance.IsGeorgian ? "გავუგზავნო განახლება მონაწილეებს?" : "Send an update to the attendees?", owner);
            await svc.SaveEventAsync(a.Item.Source, full, send, EditScope.Single);
        }, owner);
        await ReloadAsync(true);
    }

    private void ShowContextMenu(EventItem? item, TimeRangeEventArgs? range, UIElement target)
    {
        var m = new ContextMenu();
        if (item != null)
        {
            var ev = item.Event;
            m.Items.Add(Ui.Menu(Loc.T("Open"), () => _ = OpenItemAsync(item), G.Open));
            if (ev.IsMeeting && !ev.IsOrganizer && item.CanEdit)
            {
                m.Items.Add(new Separator());
                m.Items.Add(Ui.Menu(Loc.T("Accept"), () => _ = RespondAsync(item, MeetingResponse.Accept), G.Accept));
                m.Items.Add(Ui.Menu(Loc.T("Tentative"), () => _ = RespondAsync(item, MeetingResponse.Tentative), G.Tentative));
                m.Items.Add(Ui.Menu(Loc.T("Decline"), () => _ = RespondAsync(item, MeetingResponse.Decline), G.Decline));
            }
            if (item.CanEdit)
            {
                m.Items.Add(new Separator());
                var showAs = new MenuItem { Header = Loc.T("ShowAs") };
                foreach (var (sa, key) in new[] { (ShowAs.Free, "Free"), (ShowAs.WorkingElsewhere, "WorkingElsewhere"), (ShowAs.Tentative, "Tentative"), (ShowAs.Busy, "Busy"), (ShowAs.OutOfOffice, "OutOfOffice") })
                {
                    var mi = Ui.Menu(Loc.T(key), () => _ = QuickUpdateAsync(item, e => e.ShowAs = sa));
                    mi.IsCheckable = true;
                    mi.IsChecked = ev.ShowAs == sa;
                    showAs.Items.Add(mi);
                }
                m.Items.Add(showAs);
                var cat = new MenuItem { Header = Loc.T("Categorize"), Icon = Ui.GlyphText(G.Tag, 14, GlyphFactory.DefaultBrush) };
                foreach (var c in S.Categories)
                {
                    var name = c.Name;
                    var mi = new MenuItem
                    {
                        Header = Loc.T(name), IsCheckable = true, IsChecked = ev.Categories.Contains(name),
                        Icon = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(2), Background = GlyphFactory.ParseBrush(c.Color, Brushes.Gray) }
                    };
                    mi.Click += (_, _) => _ = QuickUpdateAsync(item, e =>
                    {
                        if (e.Categories.Contains(name)) e.Categories.Remove(name); else e.Categories.Add(name);
                    });
                    cat.Items.Add(mi);
                }
                m.Items.Add(cat);
                var priv = Ui.Menu(Loc.T("Private"), () => _ = QuickUpdateAsync(item, e => e.Sensitivity = e.IsPrivate ? Sensitivity.Normal : Sensitivity.Private), G.Lock);
                priv.IsCheckable = true;
                priv.IsChecked = ev.IsPrivate;
                m.Items.Add(priv);
            }
            if (item.CanDelete)
            {
                m.Items.Add(new Separator());
                m.Items.Add(Ui.Menu(Loc.T("Delete"), () => _ = DeleteItemAsync(item), G.Delete));
            }
            if (item.Source.IsShared && !item.Event.IsFreeBusyOnly)
            {
                m.Items.Add(new Separator());
                m.Items.Add(Ui.Menu(Loc.Instance.IsGeorgian ? "ჩემს კალენდარში კოპირება" : "Copy to My Calendar", () => _ = CopyToMyCalendarAsync(item), G.Copy));
            }
        }
        else
        {
            var src = SelectedSource;
            var canCreate = range?.Source?.CanCreate ?? src?.CanCreate ?? true;
            m.Items.Add(Ui.Menu(Loc.T("NewAppointment"), () => NewFromRange(range ?? new TimeRangeEventArgs { Start = DateTime.Now }, false), G.Calendar, canCreate));
            m.Items.Add(Ui.Menu(Loc.T("NewMeeting"), () => NewFromRange(range ?? new TimeRangeEventArgs { Start = DateTime.Now }, true), G.People, canCreate));
            m.Items.Add(Ui.Menu(Loc.T("NewAllDayEvent"), () =>
            {
                var s = (range?.Start ?? DateTime.Today).Date;
                NewFromRange(new TimeRangeEventArgs { Start = s, End = s.AddDays(1), AllDay = true, Source = range?.Source }, false);
            }, G.CalendarDay, canCreate));
            m.Items.Add(Ui.Menu(Loc.T("NewRecurringAppointment"), () => NewRecurring(range, false), G.Repeat, canCreate));
            m.Items.Add(Ui.Menu(Loc.T("NewRecurringMeeting"), () => NewRecurring(range, true), G.Repeat, canCreate));
            m.Items.Add(new Separator());
            m.Items.Add(Ui.Menu(Loc.T("Today"), GoToday, G.Today));
            m.Items.Add(Ui.Menu(Loc.T("Day"), () => SetViewMode(CalendarViewMode.Day), G.CalendarDay));
            m.Items.Add(Ui.Menu(Loc.T("WorkWeek"), () => SetViewMode(CalendarViewMode.WorkWeek), G.CalendarWeek));
            m.Items.Add(Ui.Menu(Loc.T("Week"), () => SetViewMode(CalendarViewMode.Week), G.CalendarWeek));
            m.Items.Add(Ui.Menu(Loc.T("Month"), () => SetViewMode(CalendarViewMode.Month), G.Calendar));
        }
        m.PlacementTarget = target;
        m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        m.IsOpen = true;
    }

    private void NewRecurring(TimeRangeEventArgs? range, bool meeting)
    {
        var src = SelectedSource is { CanCreate: true } ? SelectedSource : DefaultSource;
        var svc = src != null ? AppServices.Current.GetAccount(src.AccountId) : AppServices.Current.DefaultAccount;
        if (svc == null) return;
        var start = range?.Start ?? DateTime.Now.Date.AddHours(DateTime.Now.Hour + 1);
        var ev = new CalendarEvent
        {
            Start = start, End = range?.End ?? start.AddMinutes(30), ReminderSet = S.DefaultRemindersOn, ReminderMinutes = S.DefaultReminderMinutes,
            Recurrence = new RecurrenceInfo { Type = RecurrenceType.Weekly, DaysOfWeek = { start.DayOfWeek }, StartDate = start.Date }
        };
        AppointmentWindow.OpenNew(svc, src, ev, meeting, openRecurrence: true);
    }

    private async Task RespondAsync(EventItem item, MeetingResponse r)
    {
        var svc = AppServices.Current.GetAccount(item.Source.AccountId);
        if (svc == null) return;
        if (await MailActions.RespondToMeetingAsync(svc, item.Event.Id, item.Event.Subject, r, MeetingResponseMode.SendNow, Window.GetWindow(this)))
            await ReloadAsync(true);
    }

    private async Task QuickUpdateAsync(EventItem item, Action<CalendarEvent> change)
    {
        var svc = AppServices.Current.GetAccount(item.Source.AccountId);
        if (svc == null) return;
        await Ui.RunAsync(async () =>
        {
            var full = await svc.GetEventAsync(item.Event.Id);
            change(full);
            await svc.SaveEventAsync(item.Source, full, false, EditScope.Single);
        }, Window.GetWindow(this));
        await ReloadAsync(true);
    }

    private async Task CopyToMyCalendarAsync(EventItem item)
    {
        var src = DefaultSource;
        if (src == null) return;
        var svcFrom = AppServices.Current.GetAccount(item.Source.AccountId);
        var svcTo = AppServices.Current.GetAccount(src.AccountId);
        if (svcFrom == null || svcTo == null) return;
        await Ui.RunAsync(async () =>
        {
            var full = await svcFrom.GetEventAsync(item.Event.Id);
            full.Id = "";
            full.Attendees.Clear();
            full.Recurrence = null;
            full.IsOccurrence = false;
            await svcTo.SaveEventAsync(src, full, false, EditScope.Single);
        }, Window.GetWindow(this));
        await ReloadAsync(true);
    }

    // =====================================================================================
    // Search, e-mail calendar, print
    // =====================================================================================

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        Render();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) SearchBox.Text = "";
    }

    public void EmailCalendar()
    {
        var src = SelectedSource ?? DefaultSource;
        if (src == null) return;
        var days = VisibleDays();
        var c = Loc.Instance.Culture;
        var sb = new System.Text.StringBuilder();
        sb.Append($"<div style=\"font-family:Calibri,'Segoe UI',Sylfaen,sans-serif\"><h3 style=\"margin:0 0 6px 0\">{HtmlUtil.Encode(src.DisplayName)}</h3>");
        sb.Append($"<div style=\"color:#605E5C;margin-bottom:10px\">{HtmlUtil.Encode(RangeText(days))}</div>");
        var items = _events.TryGetValue(src.Key, out var list) ? list.OrderBy(e => e.Start).ToList() : new List<CalendarEvent>();
        foreach (var g in items.GroupBy(e => e.Start.Date))
        {
            sb.Append($"<div style=\"background:#F3F2F1;padding:4px 8px;font-weight:bold;margin-top:8px\">{HtmlUtil.Encode(g.Key.ToString("D", c))}</div><table style=\"border-collapse:collapse\">");
            foreach (var e in g)
            {
                var when = e.IsAllDay ? Loc.T("AllDay") : $"{e.Start.ToString("t", c)} – {e.End.ToString("t", c)}";
                var subj = e.IsPrivate ? Loc.T("Private") : e.Subject;
                sb.Append($"<tr><td style=\"padding:3px 16px 3px 8px;color:#605E5C;white-space:nowrap\">{HtmlUtil.Encode(when)}</td><td style=\"padding:3px\"><b>{HtmlUtil.Encode(subj)}</b>{(string.IsNullOrWhiteSpace(e.Location) || e.IsPrivate ? "" : " – " + HtmlUtil.Encode(e.Location))}</td></tr>");
            }
            sb.Append("</table>");
        }
        sb.Append("</div>");
        var svc = AppServices.Current.GetAccount(src.AccountId);
        MailActions.ComposeNew(svc, null, $"{src.DisplayName}: {RangeText(days)}", sb.ToString());
    }

    public void Print()
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;
        var size = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
        var visual = new Grid { Width = size.Width, Height = size.Height, Background = Brushes.White };
        var title = new TextBlock { Text = RangeTitle.Text, FontSize = 18, Margin = new Thickness(10) };
        visual.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        visual.RowDefinitions.Add(new RowDefinition());
        visual.Children.Add(title);
        FrameworkElement body;
        var shown = Sources.Where(s => s.IsChecked).ToList();
        if (ViewMode == CalendarViewMode.Month)
        {
            var mc = new MonthControl();
            mc.SetData(CurrentDate, ItemsFor(shown), true);
            body = mc;
        }
        else
        {
            var dw = new DayWeekControl();
            dw.SetData(VisibleDays(), ItemsFor(shown), true, true);
            body = dw;
        }
        Grid.SetRow(body, 1);
        visual.Children.Add(body);
        visual.Measure(size);
        visual.Arrange(new Rect(size));
        visual.UpdateLayout();
        dlg.PrintVisual(visual, Loc.T("PrintCalendar"));
    }
}
