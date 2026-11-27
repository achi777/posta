using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Windows;

// =========================================================================================
// Appointment recurrence
// =========================================================================================
public sealed class RecurrenceWindow : DialogWindow
{
    private readonly ComboBox _start = new() { Width = 90, IsEditable = true };
    private readonly ComboBox _end = new() { Width = 90, IsEditable = true };
    private readonly RadioButton _daily = new(), _weekly = new(), _monthly = new(), _yearly = new();
    private readonly ContentControl _patternHost = new() { MinHeight = 110, Margin = new Thickness(14, 0, 0, 0) };
    private readonly DatePicker _rangeStart = new() { Width = 150 };
    private readonly RadioButton _noEnd = new(), _endAfter = new(), _endBy = new();
    private readonly TextBox _count = new() { Width = 50, Height = 22 };
    private readonly DatePicker _endDate = new() { Width = 150 };

    // pattern controls
    private readonly RadioButton _dEvery = new() { IsChecked = true }, _dWeekday = new();
    private readonly TextBox _dInterval = new() { Width = 40, Text = "1", Height = 22 };
    private readonly TextBox _wInterval = new() { Width = 40, Text = "1", Height = 22 };
    private readonly Dictionary<DayOfWeek, CheckBox> _wDays = new();
    private readonly RadioButton _mDay = new() { IsChecked = true }, _mRel = new();
    private readonly TextBox _mDayNum = new() { Width = 40, Height = 22 }, _mInterval = new() { Width = 40, Text = "1", Height = 22 }, _mInterval2 = new() { Width = 40, Text = "1", Height = 22 };
    private readonly ComboBox _mWeekIdx = new() { Width = 90 }, _mWeekDay = new() { Width = 120 };
    private readonly RadioButton _yDay = new() { IsChecked = true }, _yRel = new();
    private readonly ComboBox _yMonth = new() { Width = 110 }, _yMonth2 = new() { Width = 110 }, _yWeekIdx = new() { Width = 90 }, _yWeekDay = new() { Width = 120 };
    private readonly TextBox _yDayNum = new() { Width = 40, Height = 22 };

    public RecurrenceInfo? Result { get; private set; }
    public DateTime StartTime { get; private set; }
    public DateTime EndTime { get; private set; }

    public RecurrenceWindow(RecurrenceInfo? current, DateTime start, DateTime end, bool allDay)
    {
        Title = Loc.T("AppointmentRecurrence");
        var c = Loc.Instance.Culture;
        var r = current?.Clone() ?? new RecurrenceInfo { Type = RecurrenceType.Weekly, DaysOfWeek = { start.DayOfWeek }, StartDate = start.Date, DayOfMonth = start.Day, Month = start.Month };
        for (int i = 0; i < 48; i++) { var t = DateTime.Today.AddMinutes(i * 30).ToString("t", c); _start.Items.Add(t); _end.Items.Add(t); }
        _start.Text = start.ToString("t", c);
        _end.Text = end.ToString("t", c);

        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12), Width = 560 };
        root.Children.Add(Section(Loc.T("AppointmentTime")));
        var tr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 6) };
        tr.Children.Add(Label(Loc.T("Start") + ":", 60)); tr.Children.Add(_start);
        tr.Children.Add(Label(Loc.T("End") + ":", 60, 20)); tr.Children.Add(_end);
        if (allDay) tr.IsEnabled = false;
        root.Children.Add(tr);

        root.Children.Add(Section(Loc.T("RecurrencePattern")));
        var pat = new DockPanel();
        var types = new StackPanel { MinWidth = 140 };
        _daily.Content = Loc.T("Daily"); _weekly.Content = Loc.T("Weekly"); _monthly.Content = Loc.T("Monthly"); _yearly.Content = Loc.T("Yearly");
        foreach (var rb in new[] { _daily, _weekly, _monthly, _yearly }) { rb.GroupName = "type"; rb.Margin = new Thickness(14, 2, 0, 4); rb.Checked += (_, _) => ShowPattern(); types.Children.Add(rb); }
        DockPanel.SetDock(types, Dock.Left);
        pat.Children.Add(types);
        pat.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1, 0, 0, 0), Child = _patternHost });
        root.Children.Add(pat);

        // weekday combos
        var idxNames = new[] { "First", "Second", "Third", "Fourth", "Last" };
        foreach (var n in idxNames) { _mWeekIdx.Items.Add(Loc.T(n)); _yWeekIdx.Items.Add(Loc.T(n)); }
        foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek))) { _mWeekDay.Items.Add(c.DateTimeFormat.GetDayName(d)); _yWeekDay.Items.Add(c.DateTimeFormat.GetDayName(d)); }
        for (int m = 1; m <= 12; m++) { _yMonth.Items.Add(c.DateTimeFormat.GetMonthName(m)); _yMonth2.Items.Add(c.DateTimeFormat.GetMonthName(m)); }
        var first = AppServices.Current.Settings.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            var d = (DayOfWeek)(((int)first + i) % 7);
            _wDays[d] = new CheckBox { Content = c.DateTimeFormat.GetDayName(d), Margin = new Thickness(0, 2, 10, 2), Width = 110 };
        }

        // range
        root.Children.Add(Section(Loc.T("RangeOfRecurrence")));
        var range = new Grid { Margin = new Thickness(14, 0, 0, 0) };
        range.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        range.ColumnDefinitions.Add(new ColumnDefinition());
        var rs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        rs.Children.Add(Label(Loc.T("Start") + ":", 60)); rs.Children.Add(_rangeStart);
        range.Children.Add(rs);
        var ends = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
        _noEnd.Content = Loc.T("NoEndDate"); _noEnd.GroupName = "end";
        var ea = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        _endAfter.Content = Loc.T("EndAfter"); _endAfter.GroupName = "end"; _endAfter.VerticalAlignment = VerticalAlignment.Center;
        ea.Children.Add(_endAfter); ea.Children.Add(_count); ea.Children.Add(Label(Loc.T("Occurrences"), 0, 6));
        var eb = new StackPanel { Orientation = Orientation.Horizontal };
        _endBy.Content = Loc.T("EndBy"); _endBy.GroupName = "end"; _endBy.VerticalAlignment = VerticalAlignment.Center;
        eb.Children.Add(_endBy); eb.Children.Add(_endDate);
        ends.Children.Add(_noEnd); ends.Children.Add(ea); ends.Children.Add(eb);
        Grid.SetColumn(ends, 1);
        range.Children.Add(ends);
        root.Children.Add(range);

        root.Children.Add(ButtonRow(
            MakeButton(Loc.T("Ok"), true, false, Accept),
            MakeButton(Loc.T("Cancel"), false, true),
            MakeButton(Loc.T("RemoveRecurrence"), false, false, () => { Result = null; StartTime = start; EndTime = end; DialogResult = true; })));
        Content = root;

        // populate
        _dInterval.Text = r.Interval.ToString();
        _dWeekday.IsChecked = r.EveryWeekday;
        _wInterval.Text = r.Interval.ToString();
        foreach (var d in r.DaysOfWeek) if (_wDays.TryGetValue(d, out var cb)) cb.IsChecked = true;
        _mDayNum.Text = r.DayOfMonth.ToString();
        _mInterval.Text = _mInterval2.Text = r.Interval.ToString();
        _mWeekIdx.SelectedIndex = _yWeekIdx.SelectedIndex = (int)r.WeekIndex;
        _mWeekDay.SelectedIndex = _yWeekDay.SelectedIndex = (int)r.RelativeDay;
        _yMonth.SelectedIndex = _yMonth2.SelectedIndex = Math.Clamp(r.Month - 1, 0, 11);
        _yDayNum.Text = r.DayOfMonth.ToString();
        _mRel.IsChecked = r.Type == RecurrenceType.MonthlyRelative;
        _yRel.IsChecked = r.Type == RecurrenceType.YearlyRelative;
        _rangeStart.SelectedDate = start.Date;
        _count.Text = r.Occurrences.ToString();
        _endDate.SelectedDate = r.EndDate;
        (r.EndType switch { RecurrenceEnd.AfterOccurrences => _endAfter, RecurrenceEnd.ByDate => _endBy, _ => _noEnd }).IsChecked = true;
        (r.Type switch
        {
            RecurrenceType.Daily => _daily,
            RecurrenceType.MonthlyAbsolute or RecurrenceType.MonthlyRelative => _monthly,
            RecurrenceType.YearlyAbsolute or RecurrenceType.YearlyRelative => _yearly,
            _ => _weekly
        }).IsChecked = true;
        _baseStart = start;
        _baseEnd = end;
        _allDay = allDay;
    }

    private readonly DateTime _baseStart, _baseEnd;
    private readonly bool _allDay;

    private static TextBlock Section(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) };
    private static TextBlock Label(string t, double width, double left = 0) => new() { Text = t, Width = width > 0 ? width : double.NaN, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 6, 0) };

    private static StackPanel Row(params UIElement[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }

    private void ShowPattern()
    {
        StackPanel p = new();
        if (_daily.IsChecked == true)
        {
            _dEvery.GroupName = _dWeekday.GroupName = "d";
            _dEvery.Content = Loc.T("Every"); _dWeekday.Content = Loc.T("EveryWeekday");
            p.Children.Add(Row(_dEvery, _dInterval, Label(Loc.T("Days"), 0, 6)));
            p.Children.Add(_dWeekday);
        }
        else if (_weekly.IsChecked == true)
        {
            p.Children.Add(Row(Label(Loc.T("RecurEvery"), 0), _wInterval, Label(Loc.T("WeeksOn"), 0, 6)));
            var wrap = new WrapPanel { Width = 420 };
            foreach (var cb in _wDays.Values) { if (cb.Parent is Panel old) old.Children.Remove(cb); wrap.Children.Add(cb); }
            p.Children.Add(wrap);
        }
        else if (_monthly.IsChecked == true)
        {
            _mDay.GroupName = _mRel.GroupName = "m";
            _mDay.Content = Loc.T("Day"); _mRel.Content = Loc.T("The");
            p.Children.Add(Row(_mDay, _mDayNum, Label(Loc.T("DayOfEvery"), 0, 6), _mInterval, Label(Loc.T("Months"), 0, 6)));
            p.Children.Add(Row(_mRel, _mWeekIdx, _mWeekDay, Label(Loc.T("DayOfEvery"), 0, 6), _mInterval2, Label(Loc.T("Months"), 0, 6)));
        }
        else
        {
            _yDay.GroupName = _yRel.GroupName = "y";
            _yDay.Content = ""; _yRel.Content = Loc.T("The");
            p.Children.Add(Row(_yDay, _yMonth, _yDayNum));
            p.Children.Add(Row(_yRel, _yWeekIdx, _yWeekDay, Label(Loc.T("Of"), 0, 6), _yMonth2));
        }
        foreach (var child in new UIElement[] { _dEvery, _dInterval, _dWeekday, _wInterval, _mDay, _mDayNum, _mInterval, _mRel, _mWeekIdx, _mWeekDay, _mInterval2, _yDay, _yMonth, _yDayNum, _yRel, _yWeekIdx, _yWeekDay, _yMonth2 })
        {
            if (child is FrameworkElement fe && fe.Parent is Panel parent && !IsUnder(parent, p)) parent.Children.Remove(fe);
        }
        _patternHost.Content = p;
    }

    private static bool IsUnder(DependencyObject child, DependencyObject root)
    {
        while (child != null)
        {
            if (child == root) return true;
            child = LogicalTreeHelper.GetParent(child);
        }
        return false;
    }

    private static int Int(TextBox t, int def) => int.TryParse(t.Text, out var v) && v > 0 ? v : def;

    private void Accept()
    {
        var c = Loc.Instance.Culture;
        var r = new RecurrenceInfo { StartDate = (_rangeStart.SelectedDate ?? DateTime.Today).Date };
        if (_daily.IsChecked == true)
        {
            r.Type = RecurrenceType.Daily;
            r.EveryWeekday = _dWeekday.IsChecked == true;
            r.Interval = Int(_dInterval, 1);
        }
        else if (_weekly.IsChecked == true)
        {
            r.Type = RecurrenceType.Weekly;
            r.Interval = Int(_wInterval, 1);
            r.DaysOfWeek = _wDays.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
            if (r.DaysOfWeek.Count == 0) r.DaysOfWeek.Add(r.StartDate.DayOfWeek);
        }
        else if (_monthly.IsChecked == true)
        {
            if (_mRel.IsChecked == true)
            {
                r.Type = RecurrenceType.MonthlyRelative;
                r.Interval = Int(_mInterval2, 1);
                r.WeekIndex = (WeekIndex)Math.Max(0, _mWeekIdx.SelectedIndex);
                r.RelativeDay = (DayOfWeek)Math.Max(0, _mWeekDay.SelectedIndex);
            }
            else
            {
                r.Type = RecurrenceType.MonthlyAbsolute;
                r.Interval = Int(_mInterval, 1);
                r.DayOfMonth = Math.Clamp(Int(_mDayNum, 1), 1, 31);
            }
        }
        else
        {
            if (_yRel.IsChecked == true)
            {
                r.Type = RecurrenceType.YearlyRelative;
                r.Month = _yMonth2.SelectedIndex + 1;
                r.WeekIndex = (WeekIndex)Math.Max(0, _yWeekIdx.SelectedIndex);
                r.RelativeDay = (DayOfWeek)Math.Max(0, _yWeekDay.SelectedIndex);
            }
            else
            {
                r.Type = RecurrenceType.YearlyAbsolute;
                r.Month = _yMonth.SelectedIndex + 1;
                r.DayOfMonth = Math.Clamp(Int(_yDayNum, 1), 1, 31);
            }
        }
        r.EndType = _endAfter.IsChecked == true ? RecurrenceEnd.AfterOccurrences : _endBy.IsChecked == true ? RecurrenceEnd.ByDate : RecurrenceEnd.NoEnd;
        r.Occurrences = Int(_count, 10);
        r.EndDate = _endDate.SelectedDate ?? r.StartDate.AddMonths(3);
        Result = r;
        if (_allDay)
        {
            StartTime = r.StartDate;
            EndTime = r.StartDate + (_baseEnd - _baseStart);
        }
        else
        {
            DateTime.TryParse(_start.Text, c, System.Globalization.DateTimeStyles.NoCurrentDateDefault, out var st);
            DateTime.TryParse(_end.Text, c, System.Globalization.DateTimeStyles.NoCurrentDateDefault, out var en);
            StartTime = r.StartDate + st.TimeOfDay;
            EndTime = r.StartDate + en.TimeOfDay;
            if (EndTime <= StartTime) EndTime = EndTime.AddDays(1);
        }
        DialogResult = true;
    }

    public static string Describe(RecurrenceInfo r, DateTime start, DateTime end)
    {
        var c = Loc.Instance.Culture;
        var ka = Loc.Instance.IsGeorgian;
        string pattern = r.Type switch
        {
            RecurrenceType.Daily when r.EveryWeekday => ka ? "ყოველ სამუშაო დღეს" : "every weekday",
            RecurrenceType.Daily => r.Interval == 1 ? (ka ? "ყოველდღე" : "every day") : (ka ? $"ყოველ {r.Interval} დღეში" : $"every {r.Interval} days"),
            RecurrenceType.Weekly => (r.Interval == 1 ? (ka ? "ყოველ კვირა: " : "every ") : (ka ? $"ყოველ {r.Interval} კვირაში: " : $"every {r.Interval} weeks on ")) +
                                     string.Join(", ", r.DaysOfWeek.Select(d => c.DateTimeFormat.GetDayName(d))),
            RecurrenceType.MonthlyAbsolute => ka ? $"ყოველი თვის {r.DayOfMonth} რიცხვში" : $"day {r.DayOfMonth} of every {(r.Interval == 1 ? "" : r.Interval + " ")}month",
            RecurrenceType.MonthlyRelative => ka ? $"ყოველი თვის {Loc.T(r.WeekIndex.ToString())} {c.DateTimeFormat.GetDayName(r.RelativeDay)}" : $"the {Loc.T(r.WeekIndex.ToString())} {c.DateTimeFormat.GetDayName(r.RelativeDay)} of every month",
            RecurrenceType.YearlyAbsolute => ka ? $"ყოველწლიურად {r.DayOfMonth} {c.DateTimeFormat.GetMonthName(r.Month)}" : $"every {c.DateTimeFormat.GetMonthName(r.Month)} {r.DayOfMonth}",
            RecurrenceType.YearlyRelative => ka ? $"ყოველწლიურად {c.DateTimeFormat.GetMonthName(r.Month)}-ის {Loc.T(r.WeekIndex.ToString())} {c.DateTimeFormat.GetDayName(r.RelativeDay)}" : $"the {Loc.T(r.WeekIndex.ToString())} {c.DateTimeFormat.GetDayName(r.RelativeDay)} of {c.DateTimeFormat.GetMonthName(r.Month)}",
            _ => ""
        };
        var from = r.StartDate.ToString("d", c);
        var until = r.EndType switch
        {
            RecurrenceEnd.ByDate => (ka ? " – " : " until ") + r.EndDate.ToString("d", c),
            RecurrenceEnd.AfterOccurrences => ka ? $" ({r.Occurrences}-ჯერ)" : $" ({r.Occurrences} times)",
            _ => ""
        };
        return Loc.F("Occurs", $"{pattern}, {(ka ? "დაწყება" : "effective")} {from}{until} {start.ToString("t", c)}–{end.ToString("t", c)}");
    }
}

// =========================================================================================
// Reminders window
// =========================================================================================
public sealed class RemindersWindow : DialogWindow
{
    private readonly ListView _list = new() { Height = 200 };
    private readonly ComboBox _snooze = new() { Width = 170 };
    private readonly TextBlock _header = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sub = new() { Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 8) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };

    public RemindersWindow()
    {
        Topmost = true;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new StackPanel { Margin = new Thickness(14, 12, 14, 12), Width = 520 };
        var head = new DockPanel();
        var icon = Ui.GlyphText(G.Ringer, 26, GlyphFactory.Blue);
        icon.Margin = new Thickness(0, 0, 12, 0);
        head.Children.Add(icon);
        var hs = new StackPanel();
        hs.Children.Add(_header);
        hs.Children.Add(_sub);
        head.Children.Add(hs);
        root.Children.Add(head);
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("Subject"), DisplayMemberBinding = new System.Windows.Data.Binding("Subject"), Width = 290 });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("DueIn"), DisplayMemberBinding = new System.Windows.Data.Binding("DueText"), Width = 190 });
        _list.View = gv;
        _list.SelectionChanged += (_, _) => UpdateHeader();
        _list.MouseDoubleClick += (_, _) => OpenSelected();
        root.Children.Add(_list);
        var actions = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var dismissAll = MakeButton(Loc.T("DismissAll"), false, false, async () => { foreach (var r in AppServices.Current.ActiveReminders.ToList()) await AppServices.Current.DismissReminderAsync(r); Refresh(); });
        dismissAll.Margin = new Thickness(0);
        actions.Children.Add(dismissAll);
        var open = MakeButton(Loc.T("OpenItem"), false, false, OpenSelected);
        var dismiss = MakeButton(Loc.T("Dismiss"), true, false, async () => { if (Current != null) await AppServices.Current.DismissReminderAsync(Current); Refresh(); });
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(open); right.Children.Add(dismiss);
        actions.Children.Add(right);
        root.Children.Add(actions);
        root.Children.Add(new TextBlock { Text = Loc.T("ClickSnoozeAgain"), Margin = new Thickness(0, 12, 0, 4) });
        foreach (var (minutes, label) in new[] { (5, Loc.F("SnoozeMinutes", 5)), (10, Loc.F("SnoozeMinutes", 10)),
            (15, Loc.F("SnoozeMinutes", 15)), (30, Loc.F("SnoozeMinutes", 30)),
            (60, Loc.T("SnoozeOneHour")), (120, Loc.T("SnoozeTwoHours")), (1440, Loc.T("SnoozeOneDay")) })
            _snooze.Items.Add(new ComboBoxItem { Content = label, Tag = minutes });
        _snooze.SelectedIndex = 0;
        var sn = new DockPanel();
        var snoozeBtn = MakeButton(Loc.T("Snooze"), false, false, () =>
        {
            if (Current == null) return;
            AppServices.Current.SnoozeReminder(Current, TimeSpan.FromMinutes((int)((ComboBoxItem)_snooze.SelectedItem).Tag));
            Refresh();
        });
        DockPanel.SetDock(snoozeBtn, Dock.Right);
        sn.Children.Add(snoozeBtn);
        sn.Children.Add(_snooze);
        root.Children.Add(sn);
        Content = root;
        _tick.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _tick.Start(); };
        Closed += (_, _) => _tick.Stop();
    }

    private sealed class Row
    {
        public ReminderInfo Info { get; init; } = null!;
        public string Subject => Info.Subject;
        public string DueText
        {
            get
            {
                var diff = Info.Start - DateTime.Now;
                if (diff.TotalMinutes <= 0) return diff.TotalMinutes > -1 ? Loc.T("Now") : $"{Loc.T("Overdue")} {Format(-diff)}";
                return Format(diff);
            }
        }
        private static string Format(TimeSpan t) => t.TotalDays >= 1 ? Loc.F("ReminderDays", Math.Round(t.TotalDays)) :
            t.TotalHours >= 1 ? Loc.F("ReminderHours", Math.Round(t.TotalHours)) : Loc.F("ReminderMinutes", Math.Max(1, Math.Round(t.TotalMinutes)));
    }

    private ReminderInfo? Current => (_list.SelectedItem as Row)?.Info;

    public void Refresh()
    {
        var selected = Current?.Key;
        var rows = AppServices.Current.ActiveReminders.OrderBy(r => r.Start).Select(r => new Row { Info = r }).ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(r => r.Info.Key == selected) ?? rows.FirstOrDefault();
        Title = Loc.F("ReminderWindowTitle", rows.Count);
        if (rows.Count == 0) Close();
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        var r = Current;
        _header.Text = r?.Subject ?? "";
        _sub.Text = r == null ? "" : $"{Loc.T("Start")}: {r.Start.ToString("f", Loc.Instance.Culture)}" + (string.IsNullOrWhiteSpace(r.Location) ? "" : $"   {Loc.T("Location")}: {r.Location}");
    }

    private void OpenSelected()
    {
        var r = Current;
        if (r == null) return;
        var svc = AppServices.Current.GetAccount(r.AccountId);
        if (svc != null) _ = AppointmentWindow.OpenExistingAsync(svc, null, r.ItemId, EditScope.Single, false);
    }
}

// =========================================================================================
// Desktop alert (new mail toast)
// =========================================================================================
public sealed class DesktopAlertWindow : Window
{
    private static readonly List<DesktopAlertWindow> Open = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(7) };

    private DesktopAlertWindow(NewMailNotification n)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 380;
        Height = 104;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Segoe UI, Sylfaen");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var avatar = new Grid { Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        avatar.Children.Add(new System.Windows.Shapes.Ellipse { Fill = InitialsBrushConverter.For(n.From) });
        avatar.Children.Add(new TextBlock { Text = new Recipient(n.From, "").Initials, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(avatar);
        var sp = new StackPanel();
        var close = new Button { Style = (Style)Application.Current.FindResource("FlatButton"), Content = Ui.GlyphText(G.Cancel, 9), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(4, 2, 4, 2) };
        close.Click += (_, e) => { e.Handled = true; Close(); };
        var head = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = n.From, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = n.Subject, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock { Text = n.Preview, Foreground = Brushes.DimGray, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11.5 });
        Grid.SetColumn(sp, 1);
        grid.Children.Add(sp);
        Content = new Border
        {
            Background = Brushes.White, BorderBrush = (Brush)Application.Current.FindResource("BorderStrongBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 8, 10), Child = grid, Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.3 },
            Cursor = Cursors.Hand
        };
        MouseLeftButtonUp += async (_, _) =>
        {
            Close();
            var svc = AppServices.Current.GetAccount(n.AccountId);
            if (svc != null) await MailActions.OpenItemAsync(svc, new MailItemSummary { Id = n.ItemId, AccountId = n.AccountId });
        };
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => _timer.Start();
        _timer.Tick += (_, _) => Close();
        Closed += (_, _) => { Open.Remove(this); _timer.Stop(); };
    }

    public static void Show(NewMailNotification n, Window owner)
    {
        var w = new DesktopAlertWindow(n);
        var area = SystemParameters.WorkArea;
        w.Left = area.Right - w.Width - 6;
        w.Top = area.Bottom - (w.Height - 4) * (Open.Count + 1) - 6;
        Open.Add(w);
        w.Opacity = 0;
        w.Show();
        w.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        w._timer.Start();
    }
}
