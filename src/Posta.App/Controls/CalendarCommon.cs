using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Posta.App.Infrastructure;
using Posta.Core.Models;

namespace Posta.App.Controls;

/// <summary>An event placed on a calendar surface together with the calendar it belongs to.</summary>
public sealed class EventItem
{
    public EventItem(CalendarEvent ev, CalendarSource source) { Event = ev; Source = source; }
    public CalendarEvent Event { get; }
    public CalendarSource Source { get; }
    public bool CanEdit => !Event.IsFreeBusyOnly && (Event.CanEditItem ?? Source.CanCreate);
    public bool CanDelete => !Event.IsFreeBusyOnly && (Event.CanDeleteItem ?? CanEdit);
}

public sealed class TimeRangeEventArgs : EventArgs
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool AllDay { get; init; }
    public CalendarSource? Source { get; init; }
}

public sealed class EventMovedEventArgs : EventArgs
{
    public EventItem Item { get; init; } = null!;
    public DateTime NewStart { get; init; }
    public DateTime NewEnd { get; init; }
    public bool Copy { get; init; }
}

public static class CalendarPalette
{
    // Calendar colors (the classic desktop client's calendar color set)
    public static readonly string[] Colors =
    {
        "#A4C2F4", "#C0E4A8", "#F4B9B9", "#D4C2F0", "#FCE29F", "#9ED9D5", "#F7C9A1", "#C5C5C5", "#B7D6F2", "#E7B6D9", "#A9DEB1", "#F2D59A"
    };

    public static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Color.FromRgb(0xA4, 0xC2, 0xF4); }
    }

    public static Brush Fill(string hex, double alpha = 1)
    {
        var c = Parse(hex);
        var b = new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    public static Brush Light(string hex)
    {
        var c = Parse(hex);
        var b = new SolidColorBrush(Color.FromRgb((byte)(c.R + (255 - c.R) * 0.55), (byte)(c.G + (255 - c.G) * 0.55), (byte)(c.B + (255 - c.B) * 0.55)));
        b.Freeze();
        return b;
    }

    public static Brush Dark(string hex)
    {
        var c = Parse(hex);
        var b = new SolidColorBrush(Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.55)));
        b.Freeze();
        return b;
    }

    public static Brush Hatch(string hex)
    {
        var c = Parse(hex);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(6, 6), MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat };
        var light = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.6), (byte)(c.G + (255 - c.G) * 0.6), (byte)(c.B + (255 - c.B) * 0.6));
        brush.GradientStops.Add(new GradientStop(Colors2.White, 0));
        brush.GradientStops.Add(new GradientStop(Colors2.White, 0.5));
        brush.GradientStops.Add(new GradientStop(light, 0.5));
        brush.GradientStops.Add(new GradientStop(light, 1));
        brush.Freeze();
        return brush;
    }

    private static class Colors2
    {
        public static readonly Color White = System.Windows.Media.Colors.White;
    }

    /// <summary>Visual for a single event block (used by day/week, month and schedule surfaces).</summary>
    public static Border EventBlock(EventItem item, bool compact, bool overlayColorsBySource = true)
    {
        var ev = item.Event;
        var color = item.Source.Color;
        Brush fill = ev.ShowAs switch
        {
            ShowAs.Free => Brushes.White,
            ShowAs.Tentative => Hatch(color),
            _ => Light(color)
        };
        Brush bar = ev.ShowAs switch
        {
            ShowAs.OutOfOffice => new SolidColorBrush(Color.FromRgb(0x80, 0x00, 0x80)),
            ShowAs.WorkingElsewhere => new SolidColorBrush(Color.FromRgb(0x2B, 0x88, 0xD8)),
            ShowAs.Free => Brushes.White,
            _ => Dark(color)
        };
        var text = new TextBlock { TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = Brushes.Black };
        if (ev.IsPrivate) text.Inlines.Add(new System.Windows.Documents.Run(" ") { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10 });
        if (ev.IsRecurring) text.Inlines.Add(new System.Windows.Documents.Run(" ") { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10 });
        if (ev.IsCancelled) text.Inlines.Add(new System.Windows.Documents.Run(Loc.Instance.IsGeorgian ? "გაუქმებულია: " : "Canceled: ") { Foreground = Brushes.DarkRed });
        var subject = string.IsNullOrWhiteSpace(ev.Subject) ? Loc.T("Untitled Event") : ev.Subject;
        text.Inlines.Add(new System.Windows.Documents.Run(subject) { FontWeight = FontWeights.SemiBold });
        if (!compact && !string.IsNullOrWhiteSpace(ev.Location))
        {
            text.Inlines.Add(new System.Windows.Documents.LineBreak());
            text.Inlines.Add(new System.Windows.Documents.Run(ev.Location));
        }
        if (!compact && ev.Organizer != null && ev.IsMeeting && !ev.IsOrganizer)
        {
            text.Inlines.Add(new System.Windows.Documents.LineBreak());
            text.Inlines.Add(new System.Windows.Documents.Run(ev.Organizer.Display) { Foreground = Brushes.DimGray });
        }
        var inner = new Border { Padding = new Thickness(5, 2, 4, 2), Child = text };
        var outer = new Border
        {
            Background = fill,
            BorderBrush = bar,
            BorderThickness = new Thickness(4, 0, 0, 0),
            Child = inner,
            Tag = item,
            SnapsToDevicePixels = true,
            ClipToBounds = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Tooltip(item)
        };
        if (ev.ShowAs == ShowAs.Free) { outer.BorderBrush = Dark(color); outer.BorderThickness = new Thickness(4, 1, 1, 1); }
        return outer;
    }

    public static string Tooltip(EventItem item)
    {
        var ev = item.Event;
        var c = Loc.Instance.Culture;
        var when = ev.IsAllDay
            ? ev.Start.ToString("d", c) + (ev.End.Date > ev.Start.Date.AddDays(1) ? " – " + ev.End.AddDays(-1).ToString("d", c) : "")
            : $"{ev.Start.ToString("t", c)} – {ev.End.ToString("t", c)}";
        var lines = new List<string> { when, string.IsNullOrWhiteSpace(ev.Subject) ? Loc.T("Untitled Event") : ev.Subject };
        if (!string.IsNullOrWhiteSpace(ev.Location)) lines.Add(ev.Location);
        if (ev.Organizer != null && ev.IsMeeting) lines.Add(Loc.T("Organizer") + ": " + ev.Organizer.Display);
        lines.Add(item.Source.DisplayName);
        return string.Join("\n", lines);
    }
}
