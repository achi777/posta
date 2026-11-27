using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Posta.Core.Models;

namespace Posta.App.Infrastructure;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility v && (v == Visibility.Visible) != Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var has = value != null && !(value is string s && string.IsNullOrWhiteSpace(s));
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EqualsToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value != null && parameter != null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter != null)
        {
            if (targetType.IsEnum) return Enum.Parse(targetType, parameter.ToString()!);
            return parameter;
        }
        return Binding.DoNothing;
    }
}

public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value != null && parameter != null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FontWeightUnreadConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool isRead && !isRead ? FontWeights.SemiBold : FontWeights.Normal;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LocalizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Loc.T((parameter as string ?? "") + value);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Deterministic pastel background for initials avatars (like the desktop client).</summary>
public sealed class InitialsBrushConverter : IValueConverter
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xC2, 0x39, 0xB3), Color.FromRgb(0x0F, 0x6C, 0xBD), Color.FromRgb(0x10, 0x7C, 0x10), Color.FromRgb(0xCA, 0x50, 0x10),
        Color.FromRgb(0x8E, 0x56, 0x2E), Color.FromRgb(0x5C, 0x2E, 0x91), Color.FromRgb(0x03, 0x83, 0x87), Color.FromRgb(0xA4, 0x26, 0x2C),
        Color.FromRgb(0x49, 0x82, 0x05), Color.FromRgb(0x00, 0x5B, 0x70), Color.FromRgb(0x88, 0x17, 0x98), Color.FromRgb(0x69, 0x79, 0x7E),
    };
    private static readonly Dictionary<int, Brush> Cache = new();

    public static Brush For(string? key)
    {
        var h = 0;
        foreach (var c in (key ?? "").ToLowerInvariant()) h = unchecked(h * 31 + c);
        var idx = Math.Abs(h % Palette.Length);
        lock (Cache)
        {
            if (!Cache.TryGetValue(idx, out var b))
            {
                b = new SolidColorBrush(Palette[idx]);
                b.Freeze();
                Cache[idx] = b;
            }
            return b;
        }
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => For(value?.ToString());
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Message list date: time for today, weekday + time this week, short date otherwise.</summary>
public sealed class ListDateConverter : IValueConverter
{
    public static string Format(DateTime d)
    {
        var c = Loc.Instance.Culture;
        var today = DateTime.Today;
        if (d.Date == today) return d.ToString("t", c);
        if (d.Date > today.AddDays(-7) && d.Date < today) return d.ToString("ddd", c) + " " + d.ToString("t", c);
        return d.ToString("d", c);
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime d && d > DateTime.MinValue ? Format(d) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ColorStringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        GlyphFactory.ParseBrush(value as string, Brushes.LightSteelBlue);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CategoryColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        GlyphFactory.ParseBrush(Services.AppServices.Current?.CategoryColor(value as string), Brushes.LightGray);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BytesToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0) return null;
        try
        {
            var img = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.DecodePixelWidth = 128;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class FolderVisuals
{
    public static string Glyph(FolderKind k, string folderClass) => k switch
    {
        FolderKind.Inbox => G.Inbox,
        FolderKind.Drafts => G.Drafts,
        FolderKind.SentItems => G.Sent,
        FolderKind.DeletedItems => G.Deleted,
        FolderKind.JunkEmail => G.Junk,
        FolderKind.Outbox => G.Outbox,
        FolderKind.Archive => G.Archive,
        FolderKind.Calendar => G.Calendar,
        FolderKind.Contacts => G.People,
        FolderKind.Tasks => G.Tasks,
        FolderKind.Notes => G.Note,
        FolderKind.ConversationHistory => G.Message,
        FolderKind.RssFeeds => G.Feed,
        FolderKind.SyncIssues => G.Sync,
        FolderKind.SearchFolder => G.Find,
        _ when folderClass.StartsWith("IPF.Appointment") => G.Calendar,
        _ when folderClass.StartsWith("IPF.Contact") => G.People,
        _ when folderClass.StartsWith("IPF.Task") => G.Tasks,
        _ => G.Folder
    };

    public static string LocalizedName(MailFolderInfo f) => f.Kind switch
    {
        FolderKind.Generic or FolderKind.Root or FolderKind.SearchFolder => f.DisplayName,
        _ => Loc.T("F_" + f.Kind)
    };

    public static int SortRank(FolderKind k) => k switch
    {
        FolderKind.Inbox => 0, FolderKind.Drafts => 1, FolderKind.SentItems => 2, FolderKind.DeletedItems => 3,
        FolderKind.Archive => 4, FolderKind.ConversationHistory => 5, FolderKind.JunkEmail => 6, FolderKind.Outbox => 7,
        FolderKind.RssFeeds => 8, FolderKind.SyncIssues => 9, FolderKind.SearchFolder => 10,
        FolderKind.Generic => 5, _ => 20
    };
}
