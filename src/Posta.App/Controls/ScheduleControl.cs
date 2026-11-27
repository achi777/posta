using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.Core.Models;

namespace Posta.App.Controls;

/// <summary>Schedule view: one horizontal timeline row per calendar (useful for comparing shared calendars).</summary>
public sealed class ScheduleControl : Grid
{
    private const double NameWidth = 190;
    private const double RowHeight = 60;
    private const double MinHourWidth = 90;
    // Stretch the day across the available width (at least MinHourWidth per hour).
    private double HourWidth => Math.Max(MinHourWidth, Math.Floor((_scroll.ActualWidth > 0 ? _scroll.ActualWidth - 20 : ActualWidth - NameWidth - 20) / 24));

    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Canvas _canvas = new() { Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly StackPanel _names = new() { Background = Brushes.White };
    private DateTime _day;
    private List<CalendarSource> _sources = new();
    private List<EventItem> _items = new();

    public event EventHandler<EventItem>? OpenRequested;
    public event EventHandler<TimeRangeEventArgs>? NewRequested;
    public EventItem? SelectedItem { get; private set; }

    public ScheduleControl()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameWidth) });
        ColumnDefinitions.Add(new ColumnDefinition());
        var nameHost = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD)), BorderThickness = new Thickness(0, 0, 1, 0), Child = _names };
        Children.Add(nameHost);
        _scroll.Content = _canvas;
        SetColumn(_scroll, 1);
        Children.Add(_scroll);
        Background = Brushes.White;
        _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
        double lastViewport = 0;
        _scroll.SizeChanged += (_, _) => { if (Math.Abs(_scroll.ActualWidth - lastViewport) > 1 && _sources.Count > 0) { lastViewport = _scroll.ActualWidth; Render(); } };
    }

    public void SetData(DateTime day, IEnumerable<CalendarSource> sources, IEnumerable<EventItem> items)
    {
        _day = day.Date;
        _sources = sources.ToList();
        _items = items.ToList();
        Render();
        var s = AppServices.Current.Settings;
        _scroll.ScrollToHorizontalOffset(Math.Max(0, s.WorkStart.TotalHours * HourWidth - 20));
    }

    private void Render()
    {
        _canvas.Children.Clear();
        _names.Children.Clear();
        var c = Loc.Instance.Culture;
        var s = AppServices.Current.Settings;
        _canvas.Width = 24 * HourWidth;
        _canvas.Height = 30 + _sources.Count * RowHeight;
        var line = new SolidColorBrush(Color.FromRgb(0xE1, 0xE1, 0xE1));
        var off = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB));

        _names.Children.Add(new Border { Height = 30, BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new TextBlock { Text = _day.ToString("D", c), Margin = new Thickness(8, 6, 0, 0), FontWeight = FontWeights.SemiBold } });
        foreach (var src in _sources)
        {
            var sp = new DockPanel { Margin = new Thickness(8, 0, 4, 0) };
            var sw = new Border { Width = 12, Height = 12, Background = CalendarPalette.Fill(src.Color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(sw);
            sp.Children.Add(new TextBlock { Text = src.DisplayName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            _names.Children.Add(new Border { Height = RowHeight, BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1), Child = sp });
        }

        // off hours shading
        var ws = s.WorkStart.TotalHours * HourWidth;
        var we = s.WorkEnd.TotalHours * HourWidth;
        AddRect(0, 30, ws, _canvas.Height - 30, off);
        AddRect(we, 30, _canvas.Width - we, _canvas.Height - 30, off);

        for (int h = 0; h < 24; h++)
        {
            var x = h * HourWidth;
            _canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = _canvas.Height, Stroke = line, IsHitTestVisible = false });
            var t = new TextBlock { Text = _day.AddHours(h).ToString("t", c), Foreground = Brushes.DimGray, FontSize = 11 };
            Canvas.SetLeft(t, x + 4);
            Canvas.SetTop(t, 8);
            _canvas.Children.Add(t);
        }
        for (int r = 0; r <= _sources.Count; r++)
        {
            var y = 30 + r * RowHeight;
            _canvas.Children.Add(new Line { X1 = 0, X2 = _canvas.Width, Y1 = y, Y2 = y, Stroke = line, IsHitTestVisible = false });
        }

        for (int r = 0; r < _sources.Count; r++)
        {
            var src = _sources[r];
            var rowTop = 30 + r * RowHeight;
            var dayItems = _items.Where(i => i.Source.Key == src.Key && i.Event.Start < _day.AddDays(1) && i.Event.End > _day).ToList();
            // Like Outlook: all-day items form a thin strip at the top of the row,
            // timed items fill the rest, split into lanes when they overlap.
            var allDay = dayItems.Where(i => i.Event.IsAllDay).ToList();
            var timed = dayItems.Where(i => !i.Event.IsAllDay).OrderBy(i => i.Event.Start).ToList();
            const double stripH = 20;
            double y0 = rowTop + 3, avail = RowHeight - 6;
            if (allDay.Count > 0 && timed.Count > 0) { y0 += stripH + 2; avail -= stripH + 2; }

            void Place(EventItem item, double top, double height)
            {
                var start = item.Event.Start < _day ? _day : item.Event.Start;
                var end = item.Event.End > _day.AddDays(1) ? _day.AddDays(1) : item.Event.End;
                var block = CalendarPalette.EventBlock(item, true);
                block.Width = Math.Max(6, (end - start).TotalHours * HourWidth - 2);
                block.Height = Math.Max(4, height);
                Canvas.SetLeft(block, (start - _day).TotalHours * HourWidth + 1);
                Canvas.SetTop(block, top);
                if (SelectedItem?.Event.Id == item.Event.Id) block.BorderBrush = Brushes.Black;
                _canvas.Children.Add(block);
            }

            foreach (var item in allDay)
                Place(item, rowTop + 3, timed.Count > 0 ? stripH : RowHeight - 6);

            var laneEnds = new List<DateTime>();
            var lanes = new List<(EventItem Item, int Lane)>();
            foreach (var item in timed)
            {
                var lane = laneEnds.FindIndex(e => e <= item.Event.Start);
                if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(item.Event.End); }
                else laneEnds[lane] = item.Event.End;
                lanes.Add((item, lane));
            }
            var laneH = laneEnds.Count == 0 ? avail : avail / laneEnds.Count;
            foreach (var (item, lane) in lanes) Place(item, y0 + lane * laneH, laneH - 1);
        }

        if (_day == DateTime.Today)
        {
            var x = DateTime.Now.TimeOfDay.TotalHours * HourWidth;
            _canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = _canvas.Height, Stroke = new SolidColorBrush(Color.FromRgb(0xC4, 0x31, 0x4B)), StrokeThickness = 1.5, IsHitTestVisible = false });
        }
    }

    private void AddRect(double x, double y, double w, double h, Brush b)
    {
        if (w <= 0 || h <= 0) return;
        var r = new Rectangle { Width = w, Height = h, Fill = b, IsHitTestVisible = false };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        _canvas.Children.Add(r);
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep != _canvas)
        {
            if (dep is Border { Tag: EventItem item })
            {
                SelectedItem = item;
                if (e.ClickCount == 2) OpenRequested?.Invoke(this, item);
                else Render();
                return;
            }
            dep = Ui.ParentOf(dep);
        }
        if (e.ClickCount == 2)
        {
            var p = e.GetPosition(_canvas);
            var row = (int)((p.Y - 30) / RowHeight);
            if (row < 0 || row >= _sources.Count) return;
            var hour = Math.Floor(p.X / HourWidth * 2) / 2;
            var start = _day.AddHours(hour);
            NewRequested?.Invoke(this, new TimeRangeEventArgs { Start = start, End = start.AddMinutes(30), Source = _sources[row] });
        }
    }
}
