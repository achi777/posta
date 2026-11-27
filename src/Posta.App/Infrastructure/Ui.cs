using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Posta.Core.Services;

namespace Posta.App.Infrastructure;

public static class Ui
{
    public static Window? ActiveWindow =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public static void Error(string message, Window? owner = null) =>
        Show(message, Loc.T("Error"), MessageBoxButton.OK, MessageBoxImage.Error, owner);

    public static void Info(string message, Window? owner = null) =>
        Show(message, Loc.T("AppName"), MessageBoxButton.OK, MessageBoxImage.Information, owner);

    public static bool Confirm(string message, Window? owner = null, MessageBoxImage image = MessageBoxImage.Question) =>
        Show(message, Loc.T("AppName"), MessageBoxButton.YesNo, image, owner) == MessageBoxResult.Yes;

    public static MessageBoxResult YesNoCancel(string message, Window? owner = null) =>
        Show(message, Loc.T("AppName"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question, owner);

    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image, Window? owner = null)
    {
        var dlg = new Windows.MessageDialog(message, title, buttons, image) { Owner = owner ?? ActiveWindow };
        if (dlg.Owner == null) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return dlg.Result;
    }

    public static string? Prompt(string title, string label, string initial = "", Window? owner = null)
    {
        var dlg = new Windows.InputDialog(title, label, initial) { Owner = owner ?? ActiveWindow };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    /// <summary>Runs an async operation, showing errors in an Office-style dialog and a wait cursor meanwhile.</summary>
    public static async Task<bool> RunAsync(Func<Task> action, Window? owner = null, bool waitCursor = true)
    {
        var prev = Mouse.OverrideCursor;
        try
        {
            if (waitCursor) Mouse.OverrideCursor = Cursors.AppStarting;
            await action();
            return true;
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = prev;
            Error(ex is MailServiceException ? ex.Message : ex.Message + (ex.InnerException != null ? "\n" + ex.InnerException.Message : ""), owner);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = prev;
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = ParentOf(d);
        }
        return null;
    }

    public static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var c in FindChildren<T>(child)) yield return c;
        }
    }

    public static TextBlock GlyphText(string glyph, double size = 14, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = size,
        Foreground = brush ?? Brushes.DimGray,
        VerticalAlignment = VerticalAlignment.Center
    };

    public static MenuItem Menu(string header, Action onClick, string? glyph = null, bool isEnabled = true, string? gesture = null)
    {
        var mi = new MenuItem { Header = header, IsEnabled = isEnabled, InputGestureText = gesture ?? "" };
        if (glyph != null) mi.Icon = GlyphText(glyph, 14, GlyphFactory.DefaultBrush);
        mi.Click += (_, _) => onClick();
        return mi;
    }

    public static bool IsValidEmail(string s)
    {
        s = s.Trim();
        var at = s.IndexOf('@');
        return at > 0 && at < s.Length - 3 && s.IndexOf('.', at) > at + 1 && !s.Contains(' ');
    }
}
