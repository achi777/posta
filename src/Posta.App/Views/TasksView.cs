using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Posta.App.Controls;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.App.Windows;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Views;

/// <summary>Tasks module: To-Do list (tasks + flagged mail) grouped by due date, quick add, details pane.</summary>
public sealed class TasksView : UserControl
{
    private readonly ObservableCollection<TaskInfo> _tasks = new();
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), ItemContainerStyle = (Style)Application.Current.FindResource("OutlookListItem"), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _quickAdd = new() { Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(10, 8, 10, 6), BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC6, 0xC4)) };
    private readonly ContentControl _details = new();
    private readonly Grid _root = new();
    private bool _todo = true;
    private bool _loaded;

    public event EventHandler? StatusChanged;
    public int ItemCount => _tasks.Count;
    private static AppSettings S => AppServices.Current.Settings;

    public TasksView()
    {
        Background = Brushes.White;
        _list.ItemsSource = _tasks;
        _list.ItemTemplate = TaskTemplate();
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_tasks);
        view.GroupDescriptions.Add(new PropertyGroupDescription(null, new DueGroupConverter()));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(TaskInfo.IsComplete), System.ComponentModel.ListSortDirection.Ascending));
        _list.GroupStyle.Add(new GroupStyle
        {
            HeaderTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Border BorderBrush="#EDEBE9" BorderThickness="0,0,0,1" Padding="10,8,8,4"><TextBlock Text="{Binding Name}" FontWeight="SemiBold" /></Border>
</DataTemplate>
""")
        });
        _list.SelectionChanged += (_, _) => ShowDetails(_list.SelectedItem as TaskInfo);
        _list.MouseDoubleClick += (_, e) => { if (Ui.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) != null) _ = OpenSelectedAsync(); };
        _list.ContextMenuOpening += (_, _) => _list.ContextMenu = Menu();
        _list.ContextMenu = new ContextMenu();
        _list.KeyDown += async (_, e) => { if (e.Key == Key.Delete) await DeleteSelectedAsync(); if (e.Key == Key.Enter) await OpenSelectedAsync(); };
        _list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnItemButton));

        _quickAdd.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await QuickAddAsync(); };
        SetQuickAddHint();
        Loc.Instance.LanguageChanged += (_, _) => { SetQuickAddHint(); CollectionViewSource.GetDefaultView(_tasks).Refresh(); };
        AppServices.Current.DataChanged += async (_, what) => { if (what is "tasks" or "calendar" && _loaded) await ReloadAsync(); };

        Content = _root;
        ApplyLayout();
    }

    private void SetQuickAddHint()
    {
        _quickAdd.Tag = Loc.T("TypeNewTask");
        _quickAdd.ToolTip = Loc.T("TypeNewTask");
        if (string.IsNullOrEmpty(_quickAdd.Text))
        {
            _quickAdd.Background = new VisualBrush(new TextBlock { Text = "  " + Loc.T("TypeNewTask"), Foreground = Brushes.Gray, FontStyle = FontStyles.Italic })
            { Stretch = Stretch.None, AlignmentX = AlignmentX.Left };
        }
        _quickAdd.TextChanged -= QuickAdd_TextChanged;
        _quickAdd.TextChanged += QuickAdd_TextChanged;
    }

    private void QuickAdd_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_quickAdd.Text.Length > 0) _quickAdd.Background = Brushes.White;
        else SetQuickAddHint();
    }

    public void ApplyLayout()
    {
        _root.Children.Clear();
        _root.ColumnDefinitions.Clear();
        foreach (var c in new FrameworkElement[] { _list, _quickAdd, _details }) if (c.Parent is Panel p) p.Children.Remove(c);
        var left = new DockPanel();
        DockPanel.SetDock(_quickAdd, Dock.Top);
        left.Children.Add(_quickAdd);
        left.Children.Add(_list);
        if (S.ReadingPane == ReadingPanePosition.Off)
        {
            _root.Children.Add(left);
            return;
        }
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition());
        _root.Children.Add(left);
        var split = new GridSplitter { Style = (Style)Application.Current.FindResource("GridSplitterV") };
        Grid.SetColumn(split, 1);
        _root.Children.Add(split);
        var scroll = new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 2);
        _root.Children.Add(scroll);
    }

    private static DataTemplate TaskTemplate() => (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Grid Margin="8,6,8,6" Background="Transparent">
    <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
    <Button x:Name="Check" Tag="complete" Width="22" Height="22" Margin="0,0,10,0" Cursor="Hand" VerticalAlignment="Center" Focusable="False">
      <Button.Template>
        <ControlTemplate TargetType="Button">
          <Grid Background="Transparent">
            <Ellipse x:Name="C" Width="18" Height="18" Stroke="#605E5C" StrokeThickness="1.3"/>
            <TextBlock x:Name="M" Text="&#xE73E;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="10" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="#0F6CBD" Visibility="Collapsed"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="M" Property="Visibility" Value="Visible"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Button.Template>
    </Button>
    <StackPanel Grid.Column="1" VerticalAlignment="Center">
      <TextBlock x:Name="Subj" Text="{Binding Subject}" FontSize="13.5" TextTrimming="CharacterEllipsis"/>
      <TextBlock x:Name="Due" FontSize="11.5" Foreground="#605E5C" Text="{Binding DueDate, StringFormat=d}"/>
    </StackPanel>
    <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
      <TextBlock x:Name="Imp" Text="!" Foreground="#C4314B" FontWeight="Bold" Margin="0,0,8,0" Visibility="Collapsed"/>
      <TextBlock x:Name="MailIcon" Text="&#xE715;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" Foreground="#605E5C" Margin="0,0,8,0" Visibility="Collapsed"/>
      <TextBlock x:Name="Flag" Text="&#xE7C1;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" Foreground="#D13438"/>
    </StackPanel>
  </Grid>
  <DataTemplate.Triggers>
    <DataTrigger Binding="{Binding IsComplete}" Value="True">
      <Setter TargetName="Subj" Property="TextDecorations" Value="Strikethrough"/>
      <Setter TargetName="Subj" Property="Foreground" Value="#8A8886"/>
      <Setter TargetName="Flag" Property="Text" Value="&#xE73E;"/>
      <Setter TargetName="Flag" Property="Foreground" Value="#107C10"/>
    </DataTrigger>
    <DataTrigger Binding="{Binding IsOverdue}" Value="True">
      <Setter TargetName="Subj" Property="Foreground" Value="#C4314B"/>
      <Setter TargetName="Due" Property="Foreground" Value="#C4314B"/>
    </DataTrigger>
    <DataTrigger Binding="{Binding Importance}" Value="High"><Setter TargetName="Imp" Property="Visibility" Value="Visible"/></DataTrigger>
    <DataTrigger Binding="{Binding IsFlaggedMail}" Value="True"><Setter TargetName="MailIcon" Property="Visibility" Value="Visible"/></DataTrigger>
  </DataTemplate.Triggers>
</DataTemplate>
""");

    private sealed class DueGroupConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not TaskInfo t) return "";
            if (t.IsComplete) return Loc.T("CompletedTasks");
            if (t.DueDate == null) return Loc.T("NoDate");
            var d = t.DueDate.Value.Date;
            var today = DateTime.Today;
            if (d < today) return Loc.T("Overdue2");
            if (d == today) return Loc.T("FlagToday");
            if (d == today.AddDays(1)) return Loc.T("FlagTomorrow");
            if (d <= today.AddDays(7 - ((int)today.DayOfWeek + 6) % 7 - 1)) return Loc.T("FlagThisWeek");
            if (d <= today.AddDays(14)) return Loc.T("FlagNextWeek");
            return Loc.Instance.IsGeorgian ? "მოგვიანებით" : "Later";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : ReloadAsync();

    public async Task ShowAsync(bool todo)
    {
        _todo = todo;
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        var all = new List<TaskInfo>();
        foreach (var svc in AppServices.Current.Accounts.ToList())
        {
            if (svc.State != ConnectionState.Connected) continue;
            try
            {
                var list = await svc.GetTasksAsync("", S.ShowCompletedTasks || !_todo);
                all.AddRange(_todo ? list : list.Where(t => !t.IsFlaggedMail));
            }
            catch { }
        }
        var selected = (_list.SelectedItem as TaskInfo)?.Id;
        _tasks.Clear();
        foreach (var t in all.OrderBy(t => t.IsComplete).ThenBy(t => t.DueDate ?? DateTime.MaxValue)) _tasks.Add(t);
        _loaded = true;
        _list.SelectedItem = (selected != null ? _tasks.FirstOrDefault(t => t.Id == selected) : null) ?? _tasks.FirstOrDefault();
        ShowDetails(_list.SelectedItem as TaskInfo);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowDetails(TaskInfo? t)
    {
        if (t == null) { _details.Content = null; return; }
        var sp = new StackPanel { Margin = new Thickness(28, 20, 28, 20) };
        sp.Children.Add(new TextBlock { Text = t.Subject, FontSize = 20, FontWeight = FontWeights.Light, TextWrapping = TextWrapping.Wrap });
        void Line(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var dp = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            dp.Children.Add(new TextBlock { Text = label, Width = 150, Foreground = Brushes.DimGray });
            dp.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(dp);
        }
        var c = Loc.Instance.Culture;
        Line(Loc.T("StartDate"), t.StartDate?.ToString("D", c));
        Line(Loc.T("DueDate"), t.DueDate?.ToString("D", c));
        Line(Loc.T("Status"), Loc.T("TS_" + t.Status));
        Line(Loc.T("PercentComplete"), t.PercentComplete + "%");
        Line(Loc.T("Priority"), Loc.T(t.Importance switch { Importance.High => "High", Importance.Low => "Low", _ => "NormalPriority" }));
        if (t.IsFlaggedMail) Line(Loc.T("Type"), Loc.T("FlaggedMail"));
        var open = new Button { Style = (Style)Application.Current.FindResource("DialogButton"), Content = Loc.T("Open"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        open.Click += async (_, _) => await OpenSelectedAsync();
        sp.Children.Add(open);
        _details.Content = sp;
    }

    private async void OnItemButton(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button { Tag: "complete", DataContext: TaskInfo t })
        {
            e.Handled = true;
            await ToggleCompleteAsync(t);
        }
    }

    private async Task ToggleCompleteAsync(TaskInfo t)
    {
        var svc = AppServices.Current.GetAccount(t.AccountId);
        if (svc == null) return;
        if (!t.IsFlaggedMail && string.IsNullOrEmpty(t.BodyHtml))
        {
            try { var full = await svc.GetTaskAsync(t.Id); t.BodyHtml = full.BodyHtml; } catch { }
        }
        t.Status = t.IsComplete ? TaskState.NotStarted : TaskState.Completed;
        t.PercentComplete = t.IsComplete ? 100 : 0;
        if (await Ui.RunAsync(() => svc.SaveTaskAsync(t.FolderId, t))) await ReloadAsync();
    }

    private async Task QuickAddAsync()
    {
        var text = _quickAdd.Text.Trim();
        var svc = AppServices.Current.DefaultAccount;
        if (text.Length == 0 || svc == null) return;
        var t = new TaskInfo { Subject = text, DueDate = DateTime.Today, StartDate = DateTime.Today };
        if (await Ui.RunAsync(() => svc.SaveTaskAsync("", t)))
        {
            _quickAdd.Text = "";
            await ReloadAsync();
        }
    }

    private ContextMenu Menu()
    {
        var m = new ContextMenu();
        var t = _list.SelectedItem as TaskInfo;
        m.Items.Add(Ui.Menu(Loc.T("Open"), () => _ = OpenSelectedAsync(), G.Open, t != null));
        m.Items.Add(Ui.Menu(Loc.T("MarkComplete"), () => _ = MarkCompleteAsync(), G.CheckMark, t != null));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Menu(Loc.T("FlagToday"), () => _ = SetDueAsync(0), G.Flag, t != null));
        m.Items.Add(Ui.Menu(Loc.T("FlagTomorrow"), () => _ = SetDueAsync(1), G.Flag, t != null));
        m.Items.Add(Ui.Menu(Loc.T("FlagNextWeek"), () => _ = SetDueAsync(7), G.Flag, t != null));
        m.Items.Add(new Separator());
        var showCompleted = Ui.Menu(Loc.T("ShowCompleted"), async () => { S.ShowCompletedTasks = !S.ShowCompletedTasks; AppServices.Current.SettingsService.Save(); await ReloadAsync(); });
        showCompleted.IsCheckable = true;
        showCompleted.IsChecked = S.ShowCompletedTasks;
        m.Items.Add(showCompleted);
        m.Items.Add(Ui.Menu(Loc.T("Delete"), () => _ = DeleteSelectedAsync(), G.Delete, t != null));
        return m;
    }

    public void NewTask()
    {
        var svc = AppServices.Current.DefaultAccount;
        if (svc == null) return;
        new TaskWindow(svc, null).Show();
    }

    public async Task OpenSelectedAsync()
    {
        if (_list.SelectedItem is not TaskInfo t) return;
        var svc = AppServices.Current.GetAccount(t.AccountId);
        if (svc == null) return;
        if (t.IsFlaggedMail)
        {
            await MailActions.OpenItemAsync(svc, new MailItemSummary { Id = t.Id, AccountId = t.AccountId });
            return;
        }
        TaskInfo? full = null;
        if (!await Ui.RunAsync(async () => full = await svc.GetTaskAsync(t.Id))) return;
        full!.AccountId = t.AccountId;
        full.FolderId = t.FolderId;
        new TaskWindow(svc, full).Show();
    }

    public async Task DeleteSelectedAsync()
    {
        if (_list.SelectedItem is not TaskInfo t) return;
        var svc = AppServices.Current.GetAccount(t.AccountId);
        if (svc == null) return;
        var ok = t.IsFlaggedMail
            ? await Ui.RunAsync(() => svc.SetFlagAsync(new[] { t.Id }, FlagState.NotFlagged, null, null))
            : await Ui.RunAsync(() => svc.DeleteTaskAsync(t.Id));
        if (ok)
        {
            var idx = _list.SelectedIndex;
            _tasks.Remove(t);
            // Like Outlook: keep a selection after deleting.
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(Math.Max(idx, 0), _list.Items.Count - 1);
            ShowDetails(_list.SelectedItem as TaskInfo);
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task MarkCompleteAsync()
    {
        if (_list.SelectedItem is TaskInfo t && !t.IsComplete) await ToggleCompleteAsync(t);
    }

    public async Task SetDueAsync(int days)
    {
        if (_list.SelectedItem is not TaskInfo t) return;
        var svc = AppServices.Current.GetAccount(t.AccountId);
        if (svc == null) return;
        if (!t.IsFlaggedMail && string.IsNullOrEmpty(t.BodyHtml))
        {
            try { var full = await svc.GetTaskAsync(t.Id); t.BodyHtml = full.BodyHtml; } catch { }
        }
        t.DueDate = DateTime.Today.AddDays(days);
        t.StartDate = t.DueDate;
        if (await Ui.RunAsync(() => svc.SaveTaskAsync(t.FolderId, t))) await ReloadAsync();
    }
}

/// <summary>To-Do bar (right side): upcoming appointments + tasks.</summary>
public sealed class ToDoBar : UserControl
{
    private readonly StackPanel _panel = new() { Margin = new Thickness(10, 8, 10, 8) };

    public ToDoBar()
    {
        Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AppServices.Current.DataChanged += async (_, what) => { if (IsVisible && what is "calendar" or "tasks") await LoadAsync(); };
    }

    public async Task LoadAsync()
    {
        _panel.Children.Clear();
        var c = Loc.Instance.Culture;
        var nav = new System.Windows.Controls.Calendar { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center, FirstDayOfWeek = AppServices.Current.Settings.FirstDayOfWeek };
        nav.SelectedDatesChanged += (_, _) => { if (nav.SelectedDate.HasValue) MailActions.ShowCalendarAt(nav.SelectedDate.Value); };
        _panel.Children.Add(nav);
        _panel.Children.Add(new TextBlock { Text = Loc.T("UpcomingEvents"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        var svc = AppServices.Current.DefaultAccount;
        if (svc == null || svc.State != ConnectionState.Connected) return;
        try
        {
            var cals = await svc.GetOwnCalendarsAsync();
            var def = cals.FirstOrDefault(x => x.IsDefault);
            if (def != null)
            {
                var events = (await svc.GetEventsAsync(def, DateTime.Today, DateTime.Today.AddDays(7))).Where(e => e.End > DateTime.Now).OrderBy(e => e.Start).Take(8).ToList();
                if (events.Count == 0) _panel.Children.Add(new TextBlock { Text = Loc.T("NoUpcomingEvents"), Foreground = Brushes.Gray });
                foreach (var e in events)
                {
                    var item = new EventItem(e, def);
                    var b = new Border { BorderBrush = CalendarPalette.Dark(def.Color), BorderThickness = new Thickness(3, 0, 0, 0), Background = Brushes.White, Padding = new Thickness(8, 4, 6, 4), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand, ToolTip = CalendarPalette.Tooltip(item) };
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = e.Subject, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    sp.Children.Add(new TextBlock { Text = (e.Start.Date == DateTime.Today ? "" : e.Start.ToString("ddd ", c)) + (e.IsAllDay ? Loc.T("AllDay") : $"{e.Start.ToString("t", c)} – {e.End.ToString("t", c)}"), Foreground = Brushes.DimGray, FontSize = 11 });
                    if (!string.IsNullOrWhiteSpace(e.Location)) sp.Children.Add(new TextBlock { Text = e.Location, Foreground = Brushes.DimGray, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
                    b.Child = sp;
                    b.MouseLeftButtonDown += async (_, ev) => { if (ev.ClickCount == 2) await AppointmentWindow.OpenExistingAsync(svc, def, e.Id, EditScope.Single, false); };
                    _panel.Children.Add(b);
                }
            }
            _panel.Children.Add(new TextBlock { Text = Loc.T("ToDoList"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            var tasks = (await svc.GetTasksAsync("", false)).OrderBy(t => t.DueDate ?? DateTime.MaxValue).Take(10);
            foreach (var t in tasks)
            {
                var tb = new TextBlock { Text = "•  " + t.Subject, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 2), Foreground = t.IsOverdue ? Brushes.Firebrick : Brushes.Black };
                _panel.Children.Add(tb);
            }
        }
        catch { }
    }
}
