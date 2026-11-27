using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Posta.App.Infrastructure;
using Posta.Core.Models;

namespace Posta.App.Windows;

/// <summary>Common base for Office-style dialogs (white, Segoe UI, no icon, centered on owner).</summary>
public class DialogWindow : Window
{
    public DialogWindow()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI, Sylfaen");
        FontSize = 12;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; }
        };
    }

    protected static StackPanel ButtonRow(params Button[] buttons)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in buttons) sp.Children.Add(b);
        return sp;
    }

    protected Button MakeButton(string text, bool isDefault = false, bool isCancel = false, Action? onClick = null)
    {
        var b = new Button
        {
            Content = text, IsDefault = isDefault, IsCancel = isCancel,
            Style = (Style)Application.Current.FindResource(isDefault ? "PrimaryButton" : "DialogButton")
        };
        if (onClick != null) b.Click += (_, _) => onClick();
        return b;
    }
}

public sealed class MessageDialog : DialogWindow
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public MessageDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        Title = title;
        MaxWidth = 560;
        var (glyph, color) = image switch
        {
            MessageBoxImage.Error => (G.Cancel, "#C4314B"),
            MessageBoxImage.Warning => (G.Warning, "#C19C00"),
            MessageBoxImage.Question => (G.Help, "#0F6CBD"),
            _ => (G.Info, "#0F6CBD")
        };
        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 14) };
        var row = new DockPanel();
        var icon = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
            Background = GlyphFactory.ParseBrush(color, Brushes.SteelBlue),
            Child = Ui.GlyphText(glyph, 16, Brushes.White)
        };
        ((TextBlock)icon.Child).HorizontalAlignment = HorizontalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MinWidth = 260, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 });
        root.Children.Add(row);

        var btns = new List<Button>();
        switch (buttons)
        {
            case MessageBoxButton.OK:
                btns.Add(MakeButton(Loc.T("Ok"), true, true, () => Close(MessageBoxResult.OK)));
                break;
            case MessageBoxButton.OKCancel:
                btns.Add(MakeButton(Loc.T("Ok"), true, false, () => Close(MessageBoxResult.OK)));
                btns.Add(MakeButton(Loc.T("Cancel"), false, true, () => Close(MessageBoxResult.Cancel)));
                break;
            case MessageBoxButton.YesNo:
                btns.Add(MakeButton(Loc.T("Yes"), true, false, () => Close(MessageBoxResult.Yes)));
                btns.Add(MakeButton(Loc.T("No"), false, true, () => Close(MessageBoxResult.No)));
                break;
            case MessageBoxButton.YesNoCancel:
                btns.Add(MakeButton(Loc.T("Yes"), true, false, () => Close(MessageBoxResult.Yes)));
                btns.Add(MakeButton(Loc.T("No"), false, false, () => Close(MessageBoxResult.No)));
                btns.Add(MakeButton(Loc.T("Cancel"), false, true, () => Close(MessageBoxResult.Cancel)));
                break;
        }
        root.Children.Add(ButtonRow(btns.ToArray()));
        Content = root;
        Result = buttons is MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
    }

    private void Close(MessageBoxResult r)
    {
        Result = r;
        DialogResult = r is MessageBoxResult.OK or MessageBoxResult.Yes;
    }
}

public sealed class InputDialog : DialogWindow
{
    private readonly TextBox _box;
    public string Value => _box.Text.Trim();

    public InputDialog(string title, string label, string initial)
    {
        Title = title;
        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14), Width = 360 };
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        _box = new TextBox { Text = initial, Style = (Style)Application.Current.FindResource("FieldBox") };
        root.Children.Add(_box);
        root.Children.Add(ButtonRow(
            MakeButton(Loc.T("Ok"), true, false, () => { if (Value.Length > 0) DialogResult = true; }),
            MakeButton(Loc.T("Cancel"), false, true)));
        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }
}

public sealed class PasswordWindow : DialogWindow
{
    private readonly PasswordBox _pwd = new() { Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(3, 0, 3, 0) };
    private readonly CheckBox _remember = new() { IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };

    public string Password => _pwd.Password;
    public bool Remember => _remember.IsChecked == true;

    public PasswordWindow(AccountConfig account)
    {
        Title = Loc.T("PasswordTitle");
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Width = 380 };
        root.Children.Add(new TextBlock { Text = Loc.T("AppName"), FontSize = 18, Foreground = (Brush)Application.Current.FindResource("AccentBrush") });
        root.Children.Add(new TextBlock { Text = Loc.F("EnterPasswordFor", account.Email), Margin = new Thickness(0, 6, 0, 12), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = account.EffectiveUser, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(_pwd);
        _remember.Content = Loc.T("RememberPassword");
        root.Children.Add(_remember);
        root.Children.Add(ButtonRow(
            MakeButton(Loc.T("Ok"), true, false, () => DialogResult = true),
            MakeButton(Loc.T("Cancel"), false, true)));
        Content = root;
        Loaded += (_, _) => _pwd.Focus();
    }
}
