using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Posta.App.Infrastructure;
using Posta.App.Services;

namespace Posta.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        var demo = e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        AppServices.Initialize(demo);

        ApplyTheme();

        // App icon on every window (title bar / taskbar / Alt+Tab).
        var appIcon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/posta.ico"));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window w && w.Icon == null) w.Icon = appIcon;
        }));

        // Search boxes: the (x) button clears the text and behaves like pressing Esc.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.TextBox), System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, args) =>
        {
            if (s is not System.Windows.Controls.TextBox tb || args.OriginalSource is not FrameworkElement { Name: "SearchClear" }) return;
            args.Handled = true;
            tb.Focus();
            var src = PresentationSource.FromVisual(tb);
            if (src == null) { tb.Clear(); return; }
            tb.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, src, 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            tb.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, src, 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            if (tb.Text.Length > 0) tb.Clear();
        }));

        // Localize the DatePicker watermark ("Select a date") like Outlook's "None".
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.DatePicker), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is System.Windows.Controls.DatePicker dp && dp.Template?.FindName("PART_TextBox", dp) is System.Windows.Controls.Primitives.DatePickerTextBox tb
                && tb.Template?.FindName("PART_Watermark", tb) is System.Windows.Controls.ContentControl wm)
                wm.Content = Loc.T("DateNone");
        }));

        // Use the selected UI language for all WPF formatting (dates, numbers).
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Instance.LanguageCode)));

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    private static void ApplyTheme()
    {
        try
        {
            var accent = Color.FromRgb(0x0F, 0x6C, 0xBD);
            var theme = ControlzEx.Theming.RuntimeThemeGenerator.Current.GenerateRuntimeTheme("Light", accent);
            if (theme != null)
            {
                ControlzEx.Theming.ThemeManager.Current.AddTheme(theme);
                ControlzEx.Theming.ThemeManager.Current.ChangeTheme(Current, theme);
            }
            else
            {
                ControlzEx.Theming.ThemeManager.Current.ChangeTheme(Current, "Light.Blue");
            }
        }
        catch
        {
            try { ControlzEx.Theming.ThemeManager.Current.ChangeTheme(Current, "Light.Blue"); } catch { }
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(Core.Services.SettingsService.LocalFolder, "error.log"),
                $"[{DateTime.Now:O}] {e.Exception}\n\n");
        }
        catch { }
        Ui.Error(e.Exception.Message);
    }
}
