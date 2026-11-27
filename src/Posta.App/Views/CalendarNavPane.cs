using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Posta.App.Controls;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.App.Windows;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Views;

/// <summary>Calendar module folder pane: date navigator and the calendar list (My / Shared / Other calendars).</summary>
public sealed class CalendarNavPane : UserControl
{
    private readonly System.Windows.Controls.Calendar _navigator = new()
    {
        SelectionMode = CalendarSelectionMode.SingleRange,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Margin = new Thickness(4, 0, 4, 4),
        HorizontalAlignment = HorizontalAlignment.Center,
        IsTodayHighlighted = true
    };
    private readonly StackPanel _list = new() { Margin = new Thickness(0, 4, 0, 8) };
    private CalendarView? _view;
    private bool _syncing;

    public CalendarNavPane()
    {
        Background = (Brush)Application.Current.FindResource("FolderPaneBrush");
        var root = new DockPanel();
        DockPanel.SetDock(_navigator, Dock.Top);
        root.Children.Add(_navigator);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        _navigator.SelectedDatesChanged += Navigator_SelectedDatesChanged;
        _navigator.PreviewMouseUp += (_, _) => { if (Mouse.Captured is CalendarItem) Mouse.Capture(null); };
        Loc.Instance.LanguageChanged += (_, _) => BuildList();
    }

    public void Attach(CalendarView view)
    {
        _view = view;
        view.SourcesChanged += (_, _) => BuildList();
        view.DateChanged += (_, _) =>
        {
            _syncing = true;
            _navigator.DisplayDate = view.CurrentDate;
            _navigator.SelectedDate = view.CurrentDate;
            _syncing = false;
        };
        _navigator.FirstDayOfWeek = AppServices.Current.Settings.FirstDayOfWeek;
        _navigator.IsTodayHighlighted = true;
        BuildList();
    }

    private void Navigator_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _view == null || _navigator.SelectedDates.Count == 0) return;
        _view.SetDays(_navigator.SelectedDates.OrderBy(d => d).ToList());
    }

    private void BuildList()
    {
        _list.Children.Clear();
        if (_view == null) return;
        foreach (var group in new[] { CalendarGroupKind.MyCalendars, CalendarGroupKind.SharedCalendars, CalendarGroupKind.OtherCalendars, CalendarGroupKind.PeoplesCalendars })
        {
            var items = _view.Sources.Where(s => s.Group == group).ToList();
            if (items.Count == 0 && group is not (CalendarGroupKind.MyCalendars or CalendarGroupKind.SharedCalendars)) continue;
            var header = new Expander
            {
                IsExpanded = true,
                Header = new TextBlock { Text = Loc.T(group.ToString()), FontWeight = FontWeights.SemiBold, FontSize = 13 },
                Margin = new Thickness(6, 4, 4, 0)
            };
            var sp = new StackPanel { Margin = new Thickness(14, 2, 0, 4) };
            foreach (var src in items) sp.Children.Add(Row(src));
            if (group == CalendarGroupKind.SharedCalendars && items.Count == 0)
            {
                var open = new Button
                {
                    Content = Loc.T("OpenSharedCalendar"),
                    Style = (Style)FindResource("FlatButton"),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                open.Click += async (_, _) => await OpenSharedCalendarAsync(false);
                sp.Children.Add(open);
            }
            header.Content = sp;
            header.ContextMenu = GroupMenu(group);
            _list.Children.Add(header);
        }
    }

    private FrameworkElement Row(CalendarSource src)
    {
        var cb = new CheckBox { IsChecked = src.IsChecked, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        cb.Checked += (_, _) => src.IsChecked = true;
        cb.Unchecked += (_, _) => src.IsChecked = false;
        var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = CalendarPalette.Fill(src.Color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = src.DisplayName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        if (src.IsShared && src.Access > CalendarAccess.None && src.Access < CalendarAccess.ReadAll) name.ToolTip = Loc.T("FreeBusyOnly");
        else if (src.IsShared && src.Access == CalendarAccess.ReadAll) name.ToolTip = Loc.T("ReadOnlyCalendar");
        var dp = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 2, 6, 2) };
        dp.Children.Add(cb);
        dp.Children.Add(swatch);
        dp.Children.Add(name);
        var row = new Border { Child = dp, Padding = new Thickness(2, 2, 2, 2), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromRgb(0xEB, 0xEB, 0xEB));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && Ui.FindAncestor<CheckBox>(d) != null) return;
            if (_view != null) _view.SelectedSource = src;
            src.IsChecked = !src.IsChecked;
            cb.IsChecked = src.IsChecked;
        };
        src.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CalendarSource.IsChecked)) cb.IsChecked = src.IsChecked;
            if (e.PropertyName == nameof(CalendarSource.Color)) swatch.Background = CalendarPalette.Fill(src.Color);
            if (e.PropertyName == nameof(CalendarSource.DisplayName)) name.Text = src.DisplayName;
        };
        row.ContextMenu = SourceMenu(src);
        return row;
    }

    private ContextMenu SourceMenu(CalendarSource src)
    {
        var m = new ContextMenu();
        m.Opened += (_, _) => { if (_view != null) _view.SelectedSource = src; };
        var color = new MenuItem { Header = Loc.T("CalendarColor"), Icon = Ui.GlyphText("", 14, GlyphFactory.DefaultBrush) };
        foreach (var c in CalendarPalette.Colors)
        {
            var hex = c;
            var mi = new MenuItem { Header = new Border { Width = 60, Height = 14, Background = CalendarPalette.Fill(hex), CornerRadius = new CornerRadius(2) } };
            mi.Click += (_, _) => src.Color = hex;
            color.Items.Add(mi);
        }
        m.Items.Add(color);
        m.Items.Add(new Separator());
        if (!src.IsShared)
        {
            m.Items.Add(Ui.Menu(Loc.T("ShareCalendar"), () => Share(src), G.Share));
            m.Items.Add(Ui.Menu(Loc.T("CalendarPermissions"), () => Permissions(src), G.Lock));
            if (!src.IsDefault)
            {
                m.Items.Add(new Separator());
                m.Items.Add(Ui.Menu(Loc.T("Rename"), () => _ = RenameAsync(src), G.Rename));
                m.Items.Add(Ui.Menu(Loc.T("DeleteCalendar"), () => _ = DeleteCalendarAsync(src), G.Delete));
            }
        }
        else
        {
            m.Items.Add(Ui.Menu(Loc.T("Rename"), () =>
            {
                var name = Ui.Prompt(Loc.T("Rename"), Loc.T("NamePrompt"), src.DisplayName);
                if (string.IsNullOrWhiteSpace(name)) return;
                src.DisplayName = name;
                _view?.PersistSource(src);
            }, G.Rename));
            var move = new MenuItem { Header = Loc.Instance.IsGeorgian ? "ჯგუფში გადატანა" : "Move to group" };
            foreach (var g in new[] { CalendarGroupKind.SharedCalendars, CalendarGroupKind.OtherCalendars, CalendarGroupKind.PeoplesCalendars })
            {
                var grp = g;
                move.Items.Add(Ui.Menu(Loc.T(g.ToString()), () => { src.Group = grp; _view?.PersistSource(src); BuildList(); }));
            }
            m.Items.Add(move);
            m.Items.Add(Ui.Menu(Loc.T("RemoveCalendar"), () => _ = _view?.RemoveSourceAsync(src), G.Remove));
        }
        m.Items.Add(new Separator());
        var overlay = Ui.Menu(Loc.T("Overlay"), () =>
        {
            AppServices.Current.Settings.CalendarOverlay = !AppServices.Current.Settings.CalendarOverlay;
            AppServices.Current.SettingsService.Save();
            _ = _view?.ReloadAsync();
        });
        overlay.IsCheckable = true;
        overlay.IsChecked = AppServices.Current.Settings.CalendarOverlay;
        m.Items.Add(overlay);
        return m;
    }

    private ContextMenu GroupMenu(CalendarGroupKind group)
    {
        var m = new ContextMenu();
        m.Items.Add(Ui.Menu(Loc.T("OpenSharedCalendar"), () => _ = OpenSharedCalendarAsync(false, group), G.Calendar));
        m.Items.Add(Ui.Menu(Loc.T("FromAddressBook"), () => _ = OpenSharedCalendarAsync(true, group), G.ContactInfo));
        if (group == CalendarGroupKind.MyCalendars)
            m.Items.Add(Ui.Menu(Loc.T("CreateNewBlankCalendar"), () => _ = CreateCalendarAsync(), G.Add));
        return m;
    }

    public void ShowGroupsMenu(UIElement? target)
    {
        var m = GroupMenu(CalendarGroupKind.SharedCalendars);
        m.PlacementTarget = target;
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    // =====================================================================================
    // Commands
    // =====================================================================================

    public async Task OpenSharedCalendarAsync(bool fromAddressBook, CalendarGroupKind group = CalendarGroupKind.SharedCalendars)
    {
        if (_view == null) return;
        var svc = AppServices.Current.DefaultAccount;
        if (svc == null) return;
        var owner = Window.GetWindow(this);
        string? email;
        if (fromAddressBook)
        {
            var pick = AddressBookWindow.PickOne(svc, owner, null);
            email = pick?.Email;
        }
        else
        {
            var w = new OpenCalendarWindow(svc) { Owner = owner };
            if (w.ShowDialog() != true) return;
            email = w.SelectedEmail;
        }
        if (string.IsNullOrWhiteSpace(email)) return;
        CalendarSource? src = null;
        if (!await Ui.RunAsync(async () => src = await svc.OpenSharedCalendarAsync(email), owner)) return;
        src!.Group = group;
        if (src.Access <= CalendarAccess.FreeBusyDetails)
            Ui.Info($"{src.DisplayName}\n\n{Loc.T("FreeBusyOnly")}", owner);
        await _view.AddSharedSourceAsync(svc, src);
    }

    public async Task CreateCalendarAsync()
    {
        if (_view == null) return;
        var svc = AppServices.Current.DefaultAccount;
        var def = _view.DefaultSource;
        if (svc == null || def == null) return;
        var name = Ui.Prompt(Loc.T("CreateNewBlankCalendar"), Loc.T("NamePrompt"), "", Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name)) return;
        if (await Ui.RunAsync(() => svc.CreateFolderAsync(def.FolderId, name, "IPF.Appointment")))
            await _view.InitializeAsync();
    }

    private async Task RenameAsync(CalendarSource src)
    {
        var svc = AppServices.Current.GetAccount(src.AccountId);
        if (svc == null) return;
        var name = Ui.Prompt(Loc.T("Rename"), Loc.T("NamePrompt"), src.DisplayName, Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name)) return;
        if (await Ui.RunAsync(() => svc.RenameFolderAsync(src.FolderId, name))) src.DisplayName = name;
    }

    private async Task DeleteCalendarAsync(CalendarSource src)
    {
        var svc = AppServices.Current.GetAccount(src.AccountId);
        if (svc == null || _view == null) return;
        if (!Ui.Confirm(Loc.F("DeleteFolderConfirm", src.DisplayName), Window.GetWindow(this), MessageBoxImage.Warning)) return;
        if (await Ui.RunAsync(() => svc.DeleteFolderAsync(src.FolderId, false))) await _view.InitializeAsync();
    }

    public void ShareSelectedCalendar()
    {
        var src = _view?.SelectedSource;
        if (src == null || src.IsShared) src = _view?.DefaultSource;
        if (src != null) Share(src);
    }

    public void ShowPermissionsForSelected()
    {
        var src = _view?.SelectedSource;
        if (src == null || src.IsShared) src = _view?.DefaultSource;
        if (src != null) Permissions(src);
    }

    private void Share(CalendarSource src)
    {
        var svc = AppServices.Current.GetAccount(src.AccountId);
        if (svc == null) return;
        new ShareCalendarWindow(svc, src) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void Permissions(CalendarSource src)
    {
        var svc = AppServices.Current.GetAccount(src.AccountId);
        if (svc == null) return;
        PermissionsWindow.ShowFor(svc, src.FolderId, src.DisplayName, Window.GetWindow(this), isCalendar: true);
    }
}
