using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Controls;

/// <summary>
/// Scheduling Assistant: free/busy grid of all attendees for a day with the proposed meeting time highlighted.
/// Click on the grid to move the meeting; AutoPick finds the next slot where everybody is free.
/// </summary>
public sealed class SchedulingAssistant : Grid
{
    private const double NameWidth = 230;
    private const double HourWidth = 96;
    private const double RowHeight = 28;

    private readonly TextBlock _dateText = new() { FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Canvas _canvas = new() { Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly StackPanel _names = new() { Background = Brushes.White };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ProgressBar _loading = new() { Height = 3, IsIndeterminate = true, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(0) };
    private List<Recipient> _attendees = new();
    private List<AttendeeAvailability> _availability = new();
    private DateTime _day;
    private DateTime _start, _end;

    public IMailService? Service { get; set; }
    public event EventHandler<(DateTime start, DateTime end)>? TimeChanged;

    public SchedulingAssistant()
    {
        Background = Brushes.White;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = new DockPanel { Margin = new Thickness(10, 8, 10, 8) };
        var prev = new Button { Style = (Style)Application.Current.FindResource("FlatButton"), Content = Ui.GlyphText(G.ChevronLeft, 11) };
        var next = new Button { Style = (Style)Application.Current.FindResource("FlatButton"), Content = Ui.GlyphText(G.ChevronRight, 11) };
        prev.Click += async (_, _) => { _day = _day.AddDays(-1); await ReloadAsync(); };
        next.Click += async (_, _) => { _day = _day.AddDays(1); await ReloadAsync(); };
        var auto = new Button { Style = (Style)Application.Current.FindResource("DialogButton"), Content = Loc.T("AutoPick") + "  ›" };
        auto.Click += async (_, _) => await AutoPickAsync();
        DockPanel.SetDock(auto, Dock.Right);
        bar.Children.Add(auto);
        bar.Children.Add(prev);
        bar.Children.Add(next);
        bar.Children.Add(_dateText);
        Children.Add(bar);
        SetRow(_loading, 1);
        Children.Add(_loading);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameWidth) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD)), BorderThickness = new Thickness(0, 0, 1, 0), Child = _names });
        _scroll.Content = _canvas;
        SetColumn(_scroll, 1);
        body.Children.Add(_scroll);
        SetRow(body, 2);
        Children.Add(body);

        var legend = new WrapPanel { Margin = new Thickness(10, 6, 10, 8) };
        void L(string color, string key, bool hatch = false)
        {
            var sw = new Rectangle { Width = 14, Height = 10, Fill = hatch ? CalendarPalette.Hatch(color) : GlyphFactory.ParseBrush(color, Brushes.Gray), Margin = new Thickness(0, 0, 4, 0) };
            legend.Children.Add(sw);
            legend.Children.Add(new TextBlock { Text = Loc.T(key), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        L("#0F6CBD", "FreeBusyLegendBusy");
        L("#0F6CBD", "FreeBusyLegendTentative", true);
        L("#800080", "FreeBusyLegendOOF");
        L("#2B88D8", "FreeBusyLegendElsewhere");
        L("#E1DFDD", "FreeBusyLegendNoInfo");
        SetRow(legend, 3);
        Children.Add(legend);
        _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
    }

    public async Task LoadAsync(IEnumerable<Recipient> attendees, DateTime start, DateTime end)
    {
        _attendees = attendees.Where(a => !string.IsNullOrWhiteSpace(a.Address)).GroupBy(a => a.Address.ToLowerInvariant()).Select(g => g.First()).ToList();
        _start = start;
        _end = end;
        _day = start.Date;
        await ReloadAsync();
        _scroll.ScrollToHorizontalOffset(Math.Max(0, (AppServices.Current.Settings.WorkStart.TotalHours - 1) * HourWidth));
    }

    private async Task ReloadAsync()
    {
        _dateText.Text = _day.ToString("D", Loc.Instance.Culture);
        _loading.Visibility = Visibility.Visible;
        try
        {
            if (Service != null && _attendees.Count > 0)
                _availability = (await Service.GetAvailabilityAsync(_attendees.Select(a => a.Address), _day, _day.AddDays(1))).ToList();
        }
        catch { _availability = new List<AttendeeAvailability>(); }
        _loading.Visibility = Visibility.Collapsed;
        Render();
    }

    private void Render()
    {
        _names.Children.Clear();
        _canvas.Children.Clear();
        var c = Loc.Instance.Culture;
        var s = AppServices.Current.Settings;
        var line = new SolidColorBrush(Color.FromRgb(0xE1, 0xE1, 0xE1));
        var rows = _attendees.Count + 1;
        _canvas.Width = 24 * HourWidth;
        _canvas.Height = 26 + rows * RowHeight;

        _names.Children.Add(new Border { Height = 26, BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1) });
        _names.Children.Add(NameRow(Loc.T("AllAttendees"), true));
        foreach (var a in _attendees) _names.Children.Add(NameRow(a.Display, false));

        var off = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB));
        AddRect(0, 26, s.WorkStart.TotalHours * HourWidth, _canvas.Height - 26, off);
        AddRect(s.WorkEnd.TotalHours * HourWidth, 26, (24 - s.WorkEnd.TotalHours) * HourWidth, _canvas.Height - 26, off);

        for (int h = 0; h < 24; h++)
        {
            var x = h * HourWidth;
            _canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = _canvas.Height, Stroke = line, IsHitTestVisible = false });
            _canvas.Children.Add(new Line { X1 = x + HourWidth / 2, X2 = x + HourWidth / 2, Y1 = 26, Y2 = _canvas.Height, Stroke = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), IsHitTestVisible = false });
            var t = new TextBlock { Text = _day.AddHours(h).ToString("t", c), FontSize = 11, Foreground = Brushes.DimGray };
            Canvas.SetLeft(t, x + 4);
            Canvas.SetTop(t, 5);
            _canvas.Children.Add(t);
        }
        for (int r = 0; r <= rows; r++)
            _canvas.Children.Add(new Line { X1 = 0, X2 = _canvas.Width, Y1 = 26 + r * RowHeight, Y2 = 26 + r * RowHeight, Stroke = line, IsHitTestVisible = false });

        // busy bars
        var merged = new List<BusySlot>();
        for (int i = 0; i < _attendees.Count; i++)
        {
            var av = _availability.FirstOrDefault(a => string.Equals(a.Email, _attendees[i].Address, StringComparison.OrdinalIgnoreCase));
            var y = 26 + (i + 1) * RowHeight + 6;
            if (av == null || av.HasError)
            {
                AddRect(0, y, _canvas.Width, RowHeight - 12, new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD)));
                continue;
            }
            foreach (var slot in av.Slots.Where(sl => sl.Status != ShowAs.Free && sl.End > _day && sl.Start < _day.AddDays(1)))
            {
                var st = slot.Start < _day ? _day : slot.Start;
                var en = slot.End > _day.AddDays(1) ? _day.AddDays(1) : slot.End;
                var rect = new Rectangle
                {
                    Width = Math.Max(2, (en - st).TotalHours * HourWidth), Height = RowHeight - 12,
                    Fill = slot.Status switch
                    {
                        ShowAs.Tentative => CalendarPalette.Hatch("#0F6CBD"),
                        ShowAs.OutOfOffice => new SolidColorBrush(Color.FromRgb(0x80, 0x00, 0x80)),
                        ShowAs.WorkingElsewhere => new SolidColorBrush(Color.FromRgb(0x2B, 0x88, 0xD8)),
                        _ => new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD))
                    },
                    ToolTip = string.Join("\n", new[] { $"{st.ToString("t", c)} – {en.ToString("t", c)}", slot.Subject, slot.Location }.Where(x => !string.IsNullOrWhiteSpace(x))),
                };
                Canvas.SetLeft(rect, (st - _day).TotalHours * HourWidth);
                Canvas.SetTop(rect, y);
                _canvas.Children.Add(rect);
                merged.Add(new BusySlot { Start = st, End = en, Status = slot.Status });
            }
        }
        foreach (var m in merged)
        {
            var rect = new Rectangle { Width = Math.Max(2, (m.End - m.Start).TotalHours * HourWidth), Height = RowHeight - 12, Fill = new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD)), Opacity = 0.75, IsHitTestVisible = false };
            Canvas.SetLeft(rect, (m.Start - _day).TotalHours * HourWidth);
            Canvas.SetTop(rect, 26 + 6);
            _canvas.Children.Add(rect);
        }

        // meeting time
        if (_start.Date <= _day && _end > _day)
        {
            var st = _start < _day ? _day : _start;
            var en = _end > _day.AddDays(1) ? _day.AddDays(1) : _end;
            var conflict = merged.Any(m => m.Start < en && m.End > st && m.Status != ShowAs.Free);
            var sel = new Rectangle
            {
                Width = Math.Max(2, (en - st).TotalHours * HourWidth), Height = _canvas.Height - 26,
                Stroke = conflict ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)), StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x10, 0x7C, 0x10)), IsHitTestVisible = false
            };
            Canvas.SetLeft(sel, (st - _day).TotalHours * HourWidth);
            Canvas.SetTop(sel, 26);
            _canvas.Children.Add(sel);
        }
    }

    private Border NameRow(string text, bool bold) => new()
    {
        Height = RowHeight, BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE1, 0xE1)), BorderThickness = new Thickness(0, 0, 0, 1),
        Child = new TextBlock { Text = text, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis }
    };

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
        var x = e.GetPosition(_canvas).X;
        var minutes = Math.Floor(x / HourWidth * 2) * 30;
        var duration = _end - _start;
        _start = _day.AddMinutes(minutes);
        _end = _start + duration;
        Render();
        TimeChanged?.Invoke(this, (_start, _end));
    }

    private async Task AutoPickAsync()
    {
        var duration = _end - _start;
        var s = AppServices.Current.Settings;
        var candidate = _start.AddMinutes(30);
        for (int guard = 0; guard < 14 * 48; guard++)
        {
            if (candidate.Date != _day)
            {
                _day = candidate.Date;
                await ReloadAsync();
            }
            var inWork = s.WorkDays.Contains(candidate.DayOfWeek) && candidate.TimeOfDay >= s.WorkStart && (candidate + duration).TimeOfDay <= s.WorkEnd && (candidate + duration).Date == candidate.Date;
            var free = _availability.All(a => a.HasError || !a.Slots.Any(sl => sl.Status != ShowAs.Free && sl.Start < candidate + duration && sl.End > candidate));
            if (inWork && free)
            {
                _start = candidate;
                _end = candidate + duration;
                Render();
                TimeChanged?.Invoke(this, (_start, _end));
                return;
            }
            candidate = candidate.AddMinutes(30);
        }
    }
}
