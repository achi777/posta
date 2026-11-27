using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.Core.Models;

namespace Posta.App.Controls;

/// <summary>
/// Day / Work week / Week calendar grid: time ruler, all-day band, overlapping event layout,
/// selection of time ranges, drag-to-move and drag-to-resize.
/// </summary>
public sealed class DayWeekControl : Grid
{
    private const double RulerWidth = 58;
    private const double AllDayRowHeight = 22;
    private const double DragThreshold = 4;

    private readonly Grid _header = new() { Height = 46, Background = Brushes.White };
    private readonly Canvas _allDay = new() { Background = Brushes.White, ClipToBounds = true };
    private readonly Border _allDayBorder = new() { BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD)), BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private readonly Grid _body = new();
    private readonly Canvas _ruler = new() { Width = RulerWidth, Background = Brushes.White };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, ClipToBounds = true, Focusable = true };
    private readonly Rectangle _selection = new() { Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0xCC, 0xE4, 0xF7)), IsHitTestVisible = false };
    private readonly DispatcherTimer _nowTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    private List<DateTime> _days = new();
    private List<EventItem> _items = new();
    private int _slotMinutes = 30;
    private double _slotHeight = 20;
    private TimeSpan _workStart = TimeSpan.FromHours(9), _workEnd = TimeSpan.FromHours(18);
    private List<DayOfWeek> _workDays = new();
    private bool _readOnly;
    private bool _showRuler = true;
    private bool _scrolledOnce;

    // selection state
    private (int day, int slot)? _selAnchor;
    private (int day, int slot)? _selEnd;
    private bool _selecting;

    // drag state
    private Border? _dragBlock;
    private EventItem? _dragItem;
    private Point _dragStartPoint;
    private bool _dragging;
    private bool _resizing;
    private DateTime _dragNewStart, _dragNewEnd;
    private Border? _dragGhost;

    public EventItem? SelectedItem { get; private set; }
    public CalendarSource? DefaultSource { get; set; }

    public event EventHandler<TimeRangeEventArgs>? NewRequested;
    public event EventHandler<EventItem>? OpenRequested;
    public event EventHandler<EventMovedEventArgs>? EventMoved;
    public event EventHandler<EventItem?>? SelectedItemChanged;
    public event EventHandler<(EventItem? item, TimeRangeEventArgs? range, UIElement target)>? ContextMenuRequested;

    public DayWeekControl()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Background = Brushes.White;

        Children.Add(_header);
        _allDayBorder.Child = _allDay;
        SetRow(_allDayBorder, 1);
        Children.Add(_allDayBorder);

        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RulerWidth) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _body.Children.Add(_ruler);
        SetColumn(_canvas, 1);
        _body.Children.Add(_canvas);
        _scroll.Content = _body;
        SetRow(_scroll, 2);
        Children.Add(_scroll);

        _canvas.SizeChanged += (_, _) => Render();
        _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
        _canvas.MouseRightButtonUp += Canvas_MouseRightButtonUp;
        _canvas.KeyDown += Canvas_KeyDown;
        _allDay.MouseLeftButtonDown += AllDay_MouseLeftButtonDown;
        _allDay.MouseRightButtonUp += AllDay_MouseRightButtonUp;
        _allDay.SizeChanged += (_, _) => RenderAllDay();
        _nowTimer.Tick += (_, _) => Render();
        Loaded += (_, _) => _nowTimer.Start();
        Unloaded += (_, _) => _nowTimer.Stop();
    }

    public void SetData(IEnumerable<DateTime> days, IEnumerable<EventItem> items, bool showRuler, bool readOnly)
    {
        var s = AppServices.Current.Settings;
        _days = days.Select(d => d.Date).ToList();
        _items = items.ToList();
        _showRuler = showRuler;
        _readOnly = readOnly;
        _slotMinutes = Math.Clamp(s.TimeScaleMinutes, 5, 60);
        _slotHeight = _slotMinutes switch { <= 10 => 16, <= 15 => 18, <= 30 => 21, _ => 36 };
        _workStart = s.WorkStart;
        _workEnd = s.WorkEnd;
        _workDays = s.WorkDays;
        _body.ColumnDefinitions[0].Width = new GridLength(_showRuler ? RulerWidth : 0);
        if (SelectedItem != null) SelectedItem = _items.FirstOrDefault(i => i.Event.Id == SelectedItem.Event.Id && i.Event.Start == SelectedItem.Event.Start);
        RenderHeader();
        Render();
        RenderAllDay();
        if (!_scrolledOnce)
        {
            _scrolledOnce = true;
            Dispatcher.BeginInvoke(() => _scroll.ScrollToVerticalOffset(Math.Max(0, _workStart.TotalMinutes / _slotMinutes * _slotHeight - 10)), DispatcherPriority.Loaded);
        }
    }

    public double VerticalOffset
    {
        get => _scroll.VerticalOffset;
        set { _scrolledOnce = true; _scroll.ScrollToVerticalOffset(value); }
    }

    public event EventHandler<double>? Scrolled;

    public void HookScroll()
    {
        _scroll.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) Scrolled?.Invoke(this, e.VerticalOffset); };
    }

    private int SlotsPerDay => 24 * 60 / _slotMinutes;
    private double DayWidth => _days.Count == 0 ? 0 : Math.Max(1, _canvas.ActualWidth) / _days.Count;

    // =====================================================================================
    // Rendering
    // =====================================================================================

    private void RenderHeader()
    {
        _header.Children.Clear();
        _header.ColumnDefinitions.Clear();
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_showRuler ? RulerWidth : 0) });
        var c = Loc.Instance.Culture;
        for (int i = 0; i < _days.Count; i++)
        {
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var d = _days[i];
            var isToday = d == DateTime.Today;
            var sp = new StackPanel { Margin = new Thickness(8, 4, 4, 4) };
            var dayName = c.DateTimeFormat.GetDayName(d.DayOfWeek);
            sp.Children.Add(new TextBlock
            {
                Text = _days.Count > 5 ? c.DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek) : dayName,
                FontSize = 11.5,
                Foreground = isToday ? GlyphFactory.Blue : new SolidColorBrush(Color.FromRgb(0x60, 0x5E, 0x5C))
            });
            sp.Children.Add(new TextBlock
            {
                Text = d.Day == 1 || i == 0 ? d.ToString("d MMM", c) : d.Day.ToString(),
                FontSize = 17,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isToday ? GlyphFactory.Blue : Brushes.Black
            });
            var cell = new Border
            {
                Child = sp,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD)),
                BorderThickness = new Thickness(i == 0 ? 0 : 1, isToday ? 0 : 0, 0, 1),
                Background = isToday ? new SolidColorBrush(Color.FromRgb(0xF3, 0xF8, 0xFD)) : Brushes.White
            };
            if (isToday)
            {
                var g = new Grid();
                g.Children.Add(cell);
                g.Children.Add(new Border { Height = 3, VerticalAlignment = VerticalAlignment.Top, Background = GlyphFactory.Blue });
                SetColumn(g, i + 1);
                _header.Children.Add(g);
            }
            else
            {
                SetColumn(cell, i + 1);
                _header.Children.Add(cell);
            }
            var day = d;
            cell.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) DayHeaderDoubleClicked?.Invoke(this, day); };
        }
    }

    public event EventHandler<DateTime>? DayHeaderDoubleClicked;

    private void Render()
    {
        _canvas.Children.Clear();
        _ruler.Children.Clear();
        if (_days.Count == 0) return;
        var slots = SlotsPerDay;
        var totalHeight = slots * _slotHeight;
        _canvas.Height = totalHeight;
        _ruler.Height = totalHeight;
        var dw = DayWidth;
        var lineMain = new SolidColorBrush(Color.FromRgb(0xE1, 0xE1, 0xE1));
        var lineHalf = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        var offBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB));
        lineMain.Freeze(); lineHalf.Freeze(); offBrush.Freeze();

        // backgrounds
        for (int d = 0; d < _days.Count; d++)
        {
            var isWorkDay = _workDays.Contains(_days[d].DayOfWeek);
            var x = d * dw;
            if (!isWorkDay)
            {
                AddRect(x, 0, dw, totalHeight, offBrush);
            }
            else
            {
                var ws = _workStart.TotalMinutes / _slotMinutes * _slotHeight;
                var we = _workEnd.TotalMinutes / _slotMinutes * _slotHeight;
                AddRect(x, 0, dw, ws, offBrush);
                AddRect(x, we, dw, totalHeight - we, offBrush);
            }
            if (d > 0) AddLine(x, 0, x, totalHeight, lineMain);
        }
        // horizontal lines
        var slotsPerHour = 60 / _slotMinutes;
        for (int s = 1; s < slots; s++)
        {
            var y = s * _slotHeight;
            AddLine(0, y, _canvas.ActualWidth, y, s % Math.Max(1, slotsPerHour) == 0 ? lineMain : lineHalf);
        }
        // ruler labels
        if (_showRuler)
        {
            var c = Loc.Instance.Culture;
            for (int h = 0; h < 24; h++)
            {
                var y = h * 60.0 / _slotMinutes * _slotHeight;
                var label = new TextBlock
                {
                    Text = DateTime.Today.AddHours(h).ToString(c.DateTimeFormat.ShortTimePattern.Contains('H') ? "HH:mm" : "h tt", c),
                    FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x5E, 0x5C)), Width = RulerWidth - 8, TextAlignment = TextAlignment.Right
                };
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y + 2);
                _ruler.Children.Add(label);
                var tick = new Line { X1 = RulerWidth - 6, X2 = RulerWidth, Y1 = y, Y2 = y, Stroke = lineMain, StrokeThickness = 1 };
                _ruler.Children.Add(tick);
            }
        }

        // selection
        _canvas.Children.Add(_selection);
        UpdateSelectionVisual();

        // events
        for (int d = 0; d < _days.Count; d++)
        {
            var day = _days[d];
            var dayEvents = _items.Where(i => !IsAllDayLike(i.Event) && i.Event.Start < day.AddDays(1) && i.Event.End > day).OrderBy(i => i.Event.Start).ThenByDescending(i => i.Event.End).ToList();
            foreach (var (item, col, cols) in LayoutColumns(dayEvents, day))
            {
                var s = item.Event.Start < day ? day : item.Event.Start;
                var e = item.Event.End > day.AddDays(1) ? day.AddDays(1) : item.Event.End;
                var top = (s - day).TotalMinutes / _slotMinutes * _slotHeight;
                var height = Math.Max(_slotHeight - 2, (e - s).TotalMinutes / _slotMinutes * _slotHeight - 1);
                var colW = (dw - 8) / cols;
                var block = CalendarPalette.EventBlock(item, height < 30);
                block.Width = Math.Max(4, colW - 2);
                block.Height = height;
                Canvas.SetLeft(block, d * dw + 1 + col * colW);
                Canvas.SetTop(block, top + 1);
                if (SelectedItem != null && SelectedItem.Event.Id == item.Event.Id && SelectedItem.Event.Start == item.Event.Start)
                {
                    block.BorderBrush = Brushes.Black;
                    block.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.35 };
                }
                _canvas.Children.Add(block);
            }
        }

        // current time indicator
        var todayIdx = _days.IndexOf(DateTime.Today);
        if (todayIdx >= 0)
        {
            var y = DateTime.Now.TimeOfDay.TotalMinutes / _slotMinutes * _slotHeight;
            var brush = new SolidColorBrush(Color.FromRgb(0xC4, 0x31, 0x4B));
            AddLine(todayIdx * dw, y, todayIdx * dw + dw, y, brush, 1.5);
            var dot = new Ellipse { Width = 8, Height = 8, Fill = brush, IsHitTestVisible = false };
            Canvas.SetLeft(dot, todayIdx * dw - 4);
            Canvas.SetTop(dot, y - 4);
            _canvas.Children.Add(dot);
        }
    }

    private static bool IsAllDayLike(CalendarEvent e) => e.IsAllDay || (e.End - e.Start).TotalHours >= 24;

    private IEnumerable<(EventItem item, int col, int cols)> LayoutColumns(List<EventItem> events, DateTime day)
    {
        // Group into clusters of overlapping events, assign columns greedily.
        var result = new List<(EventItem, int, int)>();
        var cluster = new List<(EventItem item, int col)>();
        var columnsEnd = new List<DateTime>();
        DateTime clusterEnd = DateTime.MinValue;

        void Flush()
        {
            var cols = Math.Max(1, columnsEnd.Count);
            foreach (var (item, col) in cluster) result.Add((item, col, cols));
            cluster.Clear();
            columnsEnd.Clear();
        }

        foreach (var ev in events)
        {
            var start = ev.Event.Start < day ? day : ev.Event.Start;
            var end = ev.Event.End;
            if (end - start < TimeSpan.FromMinutes(_slotMinutes)) end = start.AddMinutes(_slotMinutes);
            if (cluster.Count > 0 && start >= clusterEnd) Flush();
            var col = columnsEnd.FindIndex(e => e <= start);
            if (col < 0) { col = columnsEnd.Count; columnsEnd.Add(end); }
            else columnsEnd[col] = end;
            cluster.Add((ev, col));
            if (end > clusterEnd || cluster.Count == 1) clusterEnd = end > clusterEnd ? end : clusterEnd;
        }
        Flush();
        return result;
    }

    private void AddRect(double x, double y, double w, double h, Brush fill)
    {
        if (w <= 0 || h <= 0) return;
        var r = new Rectangle { Width = w, Height = h, Fill = fill, IsHitTestVisible = false };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        _canvas.Children.Add(r);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush stroke, double thickness = 1)
    {
        _canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = thickness, IsHitTestVisible = false, SnapsToDevicePixels = true });
    }

    private void RenderAllDay()
    {
        _allDay.Children.Clear();
        if (_days.Count == 0) return;
        var width = Math.Max(1, _allDay.ActualWidth - (_showRuler ? RulerWidth : 0));
        var dw = width / _days.Count;
        var offset = _showRuler ? RulerWidth : 0;
        var first = _days[0];
        var last = _days[^1].AddDays(1);
        var allDay = _items.Where(i => IsAllDayLike(i.Event) && i.Event.Start < last && i.Event.End > first).OrderBy(i => i.Event.Start).ThenByDescending(i => i.Event.End - i.Event.Start).ToList();

        var rows = new List<List<(int s, int e)>>();
        foreach (var item in allDay)
        {
            var sIdx = Math.Max(0, (int)Math.Floor((item.Event.Start.Date - first).TotalDays));
            var endDate = item.Event.End.TimeOfDay == TimeSpan.Zero ? item.Event.End.Date : item.Event.End.Date.AddDays(1);
            var eIdx = Math.Min(_days.Count, (int)Math.Ceiling((endDate - first).TotalDays));
            if (eIdx <= sIdx) eIdx = sIdx + 1;
            var row = rows.FindIndex(r => r.All(x => x.e <= sIdx || x.s >= eIdx));
            if (row < 0) { rows.Add(new List<(int, int)>()); row = rows.Count - 1; }
            rows[row].Add((sIdx, eIdx));
            var block = CalendarPalette.EventBlock(item, true);
            block.Height = AllDayRowHeight - 2;
            block.Width = Math.Max(4, (eIdx - sIdx) * dw - 3);
            Canvas.SetLeft(block, offset + sIdx * dw + 1);
            Canvas.SetTop(block, 2 + row * AllDayRowHeight);
            if (SelectedItem?.Event.Id == item.Event.Id) block.BorderBrush = Brushes.Black;
            _allDay.Children.Add(block);
        }
        _allDay.Height = Math.Max(AllDayRowHeight + 4, rows.Count * AllDayRowHeight + 6);

        // day separators
        for (int d = 1; d < _days.Count; d++)
        {
            var x = offset + d * dw;
            _allDay.Children.Insert(0, new Line { X1 = x, X2 = x, Y1 = 0, Y2 = 400, Stroke = new SolidColorBrush(Color.FromRgb(0xE1, 0xE1, 0xE1)), IsHitTestVisible = false });
        }
        if (_showRuler)
        {
            var lbl = new TextBlock { Text = Loc.T("AllDay"), FontSize = 10, Foreground = Brushes.Gray, Width = RulerWidth - 8, TextAlignment = TextAlignment.Right };
            Canvas.SetTop(lbl, 4);
            _allDay.Children.Add(lbl);
        }
    }

    // =====================================================================================
    // Hit testing helpers
    // =====================================================================================

    private (int day, int slot) PointToSlot(Point p)
    {
        var day = Math.Clamp((int)(p.X / DayWidth), 0, Math.Max(0, _days.Count - 1));
        var slot = Math.Clamp((int)(p.Y / _slotHeight), 0, SlotsPerDay - 1);
        return (day, slot);
    }

    private DateTime SlotTime(int day, int slot) => _days[day].AddMinutes(slot * _slotMinutes);

    private Border? BlockAt(object source)
    {
        var dep = source as DependencyObject;
        while (dep != null && dep != _canvas && dep != _allDay)
        {
            if (dep is Border { Tag: EventItem } b) return b;
            dep = Ui.ParentOf(dep);
        }
        return null;
    }

    private void UpdateSelectionVisual()
    {
        if (_selAnchor == null || _selEnd == null || _days.Count == 0)
        {
            _selection.Visibility = Visibility.Collapsed;
            return;
        }
        var (d1, s1) = _selAnchor.Value;
        var (d2, s2) = _selEnd.Value;
        var day = d1;
        var from = d1 == d2 ? Math.Min(s1, s2) : s1;
        var to = d1 == d2 ? Math.Max(s1, s2) : s2;
        if (d1 != d2) { from = Math.Min(s1, s2); to = Math.Max(s1, s2); day = d2; }
        _selection.Visibility = Visibility.Visible;
        _selection.Width = Math.Max(1, DayWidth - 1);
        _selection.Height = (to - from + 1) * _slotHeight;
        Canvas.SetLeft(_selection, day * DayWidth + 1);
        Canvas.SetTop(_selection, from * _slotHeight);
    }

    public TimeRangeEventArgs? SelectedRange
    {
        get
        {
            if (_selAnchor == null || _selEnd == null || _days.Count == 0) return null;
            var day = _selEnd.Value.day;
            var a = Math.Min(_selAnchor.Value.slot, _selEnd.Value.slot);
            var b = Math.Max(_selAnchor.Value.slot, _selEnd.Value.slot);
            return new TimeRangeEventArgs { Start = SlotTime(day, a), End = SlotTime(day, b + 1), Source = DefaultSource };
        }
    }

    public void ClearSelection()
    {
        _selAnchor = _selEnd = null;
        UpdateSelectionVisual();
    }

    private void SelectItem(EventItem? item)
    {
        if (SelectedItem == item) return;
        SelectedItem = item;
        SelectedItemChanged?.Invoke(this, item);
        Render();
        RenderAllDay();
    }

    // =====================================================================================
    // Mouse
    // =====================================================================================

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _canvas.Focus();
        var block = BlockAt(e.OriginalSource);
        var p = e.GetPosition(_canvas);
        if (block?.Tag is EventItem item)
        {
            ClearSelection();
            SelectItem(item);
            if (e.ClickCount == 2)
            {
                OpenRequested?.Invoke(this, item);
                e.Handled = true;
                return;
            }
            if (!_readOnly && item.CanEdit)
            {
                _dragBlock = block;
                _dragItem = item;
                _dragStartPoint = p;
                var local = e.GetPosition(block);
                _resizing = local.Y > block.ActualHeight - 6;
                _canvas.CaptureMouse();
            }
            e.Handled = true;
            return;
        }

        SelectItem(null);
        var slot = PointToSlot(p);
        if (e.ClickCount == 2)
        {
            var range = SelectedRange;
            if (range != null && !_readOnly) NewRequested?.Invoke(this, range);
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _selAnchor != null) _selEnd = (_selAnchor.Value.day, slot.slot);
        else { _selAnchor = slot; _selEnd = slot; }
        _selecting = true;
        _canvas.CaptureMouse();
        UpdateSelectionVisual();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (_selecting && e.LeftButton == MouseButtonState.Pressed && _selAnchor != null)
        {
            var slot = PointToSlot(p);
            _selEnd = (_selAnchor.Value.day, slot.slot);
            UpdateSelectionVisual();
            return;
        }
        if (_dragItem != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging && (Math.Abs(p.X - _dragStartPoint.X) > DragThreshold || Math.Abs(p.Y - _dragStartPoint.Y) > DragThreshold))
            {
                _dragging = true;
                _dragGhost = CalendarPalette.EventBlock(_dragItem, true);
                _dragGhost.Opacity = 0.75;
                _dragGhost.IsHitTestVisible = false;
                _dragGhost.Width = _dragBlock!.ActualWidth;
                _canvas.Children.Add(_dragGhost);
            }
            if (!_dragging) return;
            var ev = _dragItem.Event;
            var minutesPerPx = _slotMinutes / _slotHeight;
            if (_resizing)
            {
                var endSlot = Math.Clamp((int)Math.Round(p.Y / _slotHeight), 1, SlotsPerDay);
                var dayIdx = Math.Max(0, _days.IndexOf(ev.Start.Date));
                _dragNewStart = ev.Start;
                _dragNewEnd = _days.Count > 0 ? SlotTime(Math.Min(dayIdx, _days.Count - 1), 0).AddMinutes(endSlot * _slotMinutes) : ev.End;
                if (_dragNewEnd <= _dragNewStart) _dragNewEnd = _dragNewStart.AddMinutes(_slotMinutes);
                Canvas.SetLeft(_dragGhost!, Canvas.GetLeft(_dragBlock!));
                Canvas.SetTop(_dragGhost!, Canvas.GetTop(_dragBlock!));
                _dragGhost!.Height = Math.Max(_slotHeight, (_dragNewEnd - _dragNewStart).TotalMinutes / minutesPerPx);
            }
            else
            {
                var deltaMinutes = Math.Round((p.Y - _dragStartPoint.Y) * minutesPerPx / _slotMinutes) * _slotMinutes;
                var startDay = PointToSlot(_dragStartPoint).day;
                var curDay = PointToSlot(p).day;
                var dayShift = curDay - startDay;
                _dragNewStart = ev.Start.AddDays(dayShift).AddMinutes(deltaMinutes);
                _dragNewEnd = _dragNewStart + ev.Duration;
                var dayIndex = Math.Clamp(_days.IndexOf(_dragNewStart.Date), 0, _days.Count - 1);
                Canvas.SetLeft(_dragGhost!, dayIndex * DayWidth + 1);
                Canvas.SetTop(_dragGhost!, _dragNewStart.TimeOfDay.TotalMinutes / minutesPerPx);
                _dragGhost!.Height = Math.Max(_slotHeight, ev.Duration.TotalMinutes / minutesPerPx);
                _dragGhost!.Width = DayWidth - 8;
            }
            return;
        }
        // cursor feedback for resize edge
        var block = BlockAt(e.OriginalSource);
        if (block != null && !_readOnly && block.Tag is EventItem { CanEdit: true })
            block.Cursor = e.GetPosition(block).Y > block.ActualHeight - 6 ? Cursors.SizeNS : Cursors.Hand;
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _canvas.ReleaseMouseCapture();
        if (_selecting)
        {
            _selecting = false;
            return;
        }
        if (_dragging && _dragItem != null)
        {
            var item = _dragItem;
            var copy = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (_dragGhost != null) _canvas.Children.Remove(_dragGhost);
            if (_dragNewStart != item.Event.Start || _dragNewEnd != item.Event.End)
                EventMoved?.Invoke(this, new EventMovedEventArgs { Item = item, NewStart = _dragNewStart, NewEnd = _dragNewEnd, Copy = copy });
        }
        _dragging = false;
        _dragItem = null;
        _dragBlock = null;
        _dragGhost = null;
        _resizing = false;
    }

    private void Canvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var block = BlockAt(e.OriginalSource);
        if (block?.Tag is EventItem item)
        {
            SelectItem(item);
            ContextMenuRequested?.Invoke(this, (item, null, block));
        }
        else
        {
            var slot = PointToSlot(e.GetPosition(_canvas));
            var range = SelectedRange;
            var inside = range != null && slot.day == _selEnd?.day && slot.slot >= Math.Min(_selAnchor!.Value.slot, _selEnd!.Value.slot) && slot.slot <= Math.Max(_selAnchor.Value.slot, _selEnd.Value.slot);
            if (!inside) { _selAnchor = _selEnd = slot; UpdateSelectionVisual(); }
            SelectItem(null);
            ContextMenuRequested?.Invoke(this, (null, SelectedRange, _canvas));
        }
        e.Handled = true;
    }

    private void Canvas_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SelectedItem != null) { OpenRequested?.Invoke(this, SelectedItem); e.Handled = true; }
        else if (e.Key == Key.Enter && SelectedRange != null && !_readOnly) { NewRequested?.Invoke(this, SelectedRange); e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down && _selEnd != null)
        {
            var (d, s) = _selEnd.Value;
            s = Math.Clamp(s + (e.Key == Key.Up ? -1 : 1), 0, SlotsPerDay - 1);
            _selEnd = (d, s);
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _selAnchor = _selEnd;
            UpdateSelectionVisual();
            e.Handled = true;
        }
    }

    private void AllDay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var block = BlockAt(e.OriginalSource);
        if (block?.Tag is EventItem item)
        {
            SelectItem(item);
            if (e.ClickCount == 2) OpenRequested?.Invoke(this, item);
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && !_readOnly && _days.Count > 0)
        {
            var offset = _showRuler ? RulerWidth : 0;
            var x = e.GetPosition(_allDay).X - offset;
            var dw = (Math.Max(1, _allDay.ActualWidth - offset)) / _days.Count;
            var idx = Math.Clamp((int)(x / dw), 0, _days.Count - 1);
            NewRequested?.Invoke(this, new TimeRangeEventArgs { Start = _days[idx], End = _days[idx].AddDays(1), AllDay = true, Source = DefaultSource });
        }
    }

    private void AllDay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var block = BlockAt(e.OriginalSource);
        if (block?.Tag is EventItem item)
        {
            SelectItem(item);
            ContextMenuRequested?.Invoke(this, (item, null, block));
            e.Handled = true;
        }
    }
}
