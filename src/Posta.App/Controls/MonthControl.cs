using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.Core.Models;

namespace Posta.App.Controls;

/// <summary>Month view: 5–6 week rows × 7 days with event chips, overflow counter, drag between days.</summary>
public sealed class MonthControl : Grid
{
    private readonly Grid _header = new() { Height = 26 };
    private readonly Grid _cells = new();
    private DateTime _month;
    private DateTime _gridStart;
    private int _weeks;
    private List<EventItem> _items = new();
    private bool _readOnly;
    private DateTime? _selectedDay;
    private EventItem? _dragItem;
    private Point _dragStart;
    private bool _dragging;

    public EventItem? SelectedItem { get; private set; }
    public CalendarSource? DefaultSource { get; set; }

    public event EventHandler<TimeRangeEventArgs>? NewRequested;
    public event EventHandler<EventItem>? OpenRequested;
    public event EventHandler<EventMovedEventArgs>? EventMoved;
    public event EventHandler<DateTime>? DayOpenRequested;
    public event EventHandler<(EventItem? item, TimeRangeEventArgs? range, UIElement target)>? ContextMenuRequested;

    public MonthControl()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Background = Brushes.White;
        Children.Add(_header);
        SetRow(_cells, 1);
        Children.Add(_cells);
        SizeChanged += (_, e) => { if (Math.Abs(e.PreviousSize.Height - e.NewSize.Height) > 20) Render(); };
    }

    public TimeRangeEventArgs? SelectedRange => _selectedDay == null ? null : new TimeRangeEventArgs
    {
        Start = _selectedDay.Value.AddHours(DateTime.Now.Hour + 1), End = _selectedDay.Value.AddHours(DateTime.Now.Hour + 1).AddMinutes(30), Source = DefaultSource
    };

    public void SetData(DateTime month, IEnumerable<EventItem> items, bool readOnly)
    {
        _month = new DateTime(month.Year, month.Month, 1);
        _items = items.ToList();
        _readOnly = readOnly;
        var first = AppServices.Current.Settings.FirstDayOfWeek;
        var offset = ((int)_month.DayOfWeek - (int)first + 7) % 7;
        _gridStart = _month.AddDays(-offset);
        var days = offset + DateTime.DaysInMonth(_month.Year, _month.Month);
        _weeks = (int)Math.Ceiling(days / 7.0);
        Render();
    }

    public DateTime GridStart => _gridStart;
    public DateTime GridEnd => _gridStart.AddDays(_weeks * 7);

    private void Render()
    {
        var c = Loc.Instance.Culture;
        var s = AppServices.Current.Settings;
        _header.Children.Clear();
        _header.ColumnDefinitions.Clear();
        var weekCol = s.ShowWeekNumbers ? 34 : 0;
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weekCol) });
        for (int i = 0; i < 7; i++)
        {
            _header.ColumnDefinitions.Add(new ColumnDefinition());
            var dow = (DayOfWeek)(((int)s.FirstDayOfWeek + i) % 7);
            var t = new TextBlock { Text = c.DateTimeFormat.GetDayName(dow), Margin = new Thickness(8, 4, 0, 4), Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x5E, 0x5C)) };
            SetColumn(t, i + 1);
            _header.Children.Add(t);
        }

        _cells.Children.Clear();
        _cells.RowDefinitions.Clear();
        _cells.ColumnDefinitions.Clear();
        _cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weekCol) });
        for (int i = 0; i < 7; i++) _cells.ColumnDefinitions.Add(new ColumnDefinition());
        for (int w = 0; w < _weeks; w++) _cells.RowDefinitions.Add(new RowDefinition());

        var cellHeight = Math.Max(60, (ActualHeight - 26) / Math.Max(1, _weeks));
        var maxChips = Math.Max(1, (int)((cellHeight - 24) / 19));
        var gridBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD));

        for (int w = 0; w < _weeks; w++)
        {
            if (s.ShowWeekNumbers)
            {
                var wk = c.Calendar.GetWeekOfYear(_gridStart.AddDays(w * 7), CalendarWeekRule.FirstFourDayWeek, s.FirstDayOfWeek);
                var wt = new TextBlock { Text = wk.ToString(), Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(6, 6, 0, 0) };
                SetRow(wt, w);
                _cells.Children.Add(wt);
            }
            for (int d = 0; d < 7; d++)
            {
                var day = _gridStart.AddDays(w * 7 + d);
                var inMonth = day.Month == _month.Month;
                var isToday = day == DateTime.Today;
                var panel = new StackPanel { Margin = new Thickness(2, 2, 2, 0) };
                var label = new TextBlock
                {
                    Text = day.Day == 1 ? day.ToString("d MMM", c) : day.Day.ToString(),
                    Margin = new Thickness(6, 2, 0, 3),
                    FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = isToday ? Brushes.White : inMonth ? Brushes.Black : Brushes.Gray
                };
                var labelHost = new Border { Child = label, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Padding = new Thickness(0, 0, 6, 0) };
                if (isToday) labelHost.Background = GlyphFactory.Blue;
                panel.Children.Add(labelHost);

                var dayItems = _items.Where(i => i.Event.Start < day.AddDays(1) && (i.Event.End > day || (i.Event.End == i.Event.Start && i.Event.Start.Date == day)))
                    .OrderByDescending(i => i.Event.IsAllDay || (i.Event.End - i.Event.Start).TotalHours >= 24)
                    .ThenBy(i => i.Event.Start).ToList();
                var shown = 0;
                foreach (var item in dayItems)
                {
                    if (shown >= maxChips) break;
                    panel.Children.Add(Chip(item, day));
                    shown++;
                }
                if (dayItems.Count > shown)
                {
                    var more = new TextBlock { Text = Loc.F("MoreItems", dayItems.Count - shown), Foreground = GlyphFactory.Blue, Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand, FontSize = 11 };
                    var dd = day;
                    more.MouseLeftButtonDown += (_, e) => { DayOpenRequested?.Invoke(this, dd); e.Handled = true; };
                    panel.Children.Add(more);
                }

                var cell = new Border
                {
                    BorderBrush = gridBrush,
                    BorderThickness = new Thickness(d == 0 ? 0 : 1, 0, 0, 1),
                    Background = _selectedDay == day ? new SolidColorBrush(Color.FromRgb(0xCC, 0xE4, 0xF7)) : inMonth ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)),
                    Child = panel,
                    Tag = day,
                    AllowDrop = true,
                    ClipToBounds = true
                };
                var cellDay = day;
                cell.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.Handled) return;
                    _selectedDay = cellDay;
                    SelectedItem = null;
                    if (e.ClickCount == 2 && !_readOnly)
                        NewRequested?.Invoke(this, new TimeRangeEventArgs { Start = cellDay, End = cellDay.AddDays(1), AllDay = true, Source = DefaultSource });
                    else Render();
                };
                cell.MouseRightButtonUp += (_, e) =>
                {
                    if (e.Handled) return;
                    _selectedDay = cellDay;
                    Render();
                    ContextMenuRequested?.Invoke(this, (null, new TimeRangeEventArgs { Start = cellDay.AddHours(9), End = cellDay.AddHours(9.5), Source = DefaultSource }, this));
                };
                cell.MouseLeftButtonUp += (_, _) => FinishDrag(cellDay);
                SetRow(cell, w);
                SetColumn(cell, d + 1);
                _cells.Children.Add(cell);
            }
        }
    }

    private FrameworkElement Chip(EventItem item, DateTime day)
    {
        var ev = item.Event;
        var allDayLike = ev.IsAllDay || (ev.End - ev.Start).TotalHours >= 24;
        Border chip;
        if (allDayLike)
        {
            chip = CalendarPalette.EventBlock(item, true);
            chip.Height = 18;
        }
        else
        {
            var tb = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
            tb.Inlines.Add(new System.Windows.Documents.Run(ev.Start.ToString("t", Loc.Instance.Culture) + " ") { Foreground = Brushes.DimGray });
            tb.Inlines.Add(new System.Windows.Documents.Run(string.IsNullOrWhiteSpace(ev.Subject) ? Loc.T("Untitled Event") : ev.Subject));
            var dot = new Border { Width = 4, Background = CalendarPalette.Dark(item.Source.Color), Margin = new Thickness(0, 1, 5, 1) };
            var dp = new DockPanel();
            DockPanel.SetDock(dot, Dock.Left);
            dp.Children.Add(dot);
            dp.Children.Add(tb);
            chip = new Border { Child = dp, Height = 18, Tag = item, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = CalendarPalette.Tooltip(item), Padding = new Thickness(2, 0, 2, 0) };
        }
        chip.Margin = new Thickness(0, 0, 0, 1);
        if (SelectedItem?.Event.Id == item.Event.Id)
        {
            chip.Background = new SolidColorBrush(Color.FromRgb(0xCC, 0xE4, 0xF7));
            chip.BorderBrush = Brushes.Black;
        }
        chip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            SelectedItem = item;
            _selectedDay = null;
            if (e.ClickCount == 2) { OpenRequested?.Invoke(this, item); return; }
            if (!_readOnly && item.CanEdit) { _dragItem = item; _dragStart = e.GetPosition(this); _dragging = false; CaptureMouse(); }
            Render();
        };
        chip.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            SelectedItem = item;
            ContextMenuRequested?.Invoke(this, (item, null, chip));
        };
        return chip;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragItem != null && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) > 6 || Math.Abs(p.Y - _dragStart.Y) > 6) { _dragging = true; Cursor = Cursors.SizeAll; }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragItem == null) return;
        ReleaseMouseCapture();
        Cursor = null;
        if (_dragging)
        {
            var hit = InputHitTest(e.GetPosition(this)) as DependencyObject;
            while (hit != null && !(hit is Border { Tag: DateTime })) hit = Ui.ParentOf(hit);
            if (hit is Border { Tag: DateTime day }) FinishDrag(day);
        }
        _dragItem = null;
        _dragging = false;
    }

    private void FinishDrag(DateTime day)
    {
        if (_dragItem == null || !_dragging) return;
        var ev = _dragItem.Event;
        var shift = day - ev.Start.Date;
        if (shift != TimeSpan.Zero)
            EventMoved?.Invoke(this, new EventMovedEventArgs { Item = _dragItem, NewStart = ev.Start + shift, NewEnd = ev.End + shift, Copy = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) });
        _dragItem = null;
        _dragging = false;
    }
}

internal static class CalendarWeekRule
{
    public const System.Globalization.CalendarWeekRule FirstFourDayWeek = System.Globalization.CalendarWeekRule.FirstFourDayWeek;
}
