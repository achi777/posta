using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Posta.App.Controls;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Windows;

// =========================================================================================
// Options (File → Options)
// =========================================================================================
public sealed class OptionsWindow : DialogWindow
{
    private readonly AppSettings _s = AppServices.Current.Settings;
    private readonly ListBox _nav = new() { Width = 170, BorderThickness = new Thickness(0, 0, 1, 0), Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)) };
    private readonly ContentControl _page = new();
    private readonly List<Action> _apply = new();

    public OptionsWindow()
    {
        Title = Loc.T("OptionsTitle");
        ResizeMode = ResizeMode.CanResize;
        SizeToContent = SizeToContent.Manual;
        Width = 860;
        Height = 640;
        var root = new DockPanel();
        var btns = ButtonRow(MakeButton(Loc.T("Ok"), true, false, () => { foreach (var a in _apply) a(); AppServices.Current.SettingsService.Save(); DialogResult = true; }),
            MakeButton(Loc.T("Cancel"), false, true));
        btns.Margin = new Thickness(14, 10, 14, 12);
        var btnHost = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1, 0, 0), Child = btns };
        DockPanel.SetDock(btnHost, Dock.Bottom);
        root.Children.Add(btnHost);
        DockPanel.SetDock(_nav, Dock.Left);
        root.Children.Add(_nav);
        root.Children.Add(new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 16, 24, 16) });
        Content = root;

        var pages = new (string key, Func<FrameworkElement> build)[]
        {
            ("OptGeneral", General), ("OptMail", Mail), ("OptCalendar", CalendarPage), ("OptPeople", People), ("OptTasks", Tasks),
            ("OptLanguage", LanguagePage), ("OptAdvanced", Advanced)
        };
        var built = new Dictionary<string, FrameworkElement>();
        foreach (var (key, build) in pages)
        {
            var item = new ListBoxItem { Content = Loc.T(key), Padding = new Thickness(14, 7, 8, 7), Tag = key };
            _nav.Items.Add(item);
        }
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is not ListBoxItem { Tag: string key }) return;
            if (!built.TryGetValue(key, out var page)) built[key] = page = pages.First(p => p.key == key).build();
            _page.Content = page;
        };
        _nav.SelectedIndex = 0;
    }

    // ---------------------------------------------------------------- helpers

    private static StackPanel Page(string title, string? subtitle = null)
    {
        var sp = new StackPanel { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.Light, Margin = new Thickness(0, 0, 0, 4) });
        if (subtitle != null) sp.Children.Add(new TextBlock { Text = subtitle, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        return sp;
    }

    private static void Section(Panel p, string title)
    {
        p.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 2) });
        p.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD)), Margin = new Thickness(0, 0, 0, 8) });
    }

    private CheckBox Check(Panel p, string key, bool value, Action<bool> apply)
    {
        var cb = new CheckBox { Content = Loc.T(key), IsChecked = value, Margin = new Thickness(10, 3, 0, 3) };
        p.Children.Add(cb);
        _apply.Add(() => apply(cb.IsChecked == true));
        return cb;
    }

    private TextBox Number(Panel p, string key, int value, Action<int> apply)
    {
        var tb = new TextBox { Text = value.ToString(), Width = 60, Height = 22, VerticalContentAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = Loc.T(key), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        row.Children.Add(tb);
        p.Children.Add(row);
        _apply.Add(() => { if (int.TryParse(tb.Text, out var v) && v >= 0) apply(v); });
        return tb;
    }

    private ComboBox Combo<T>(Panel p, string key, IEnumerable<(T value, string text)> items, T current, Action<T> apply)
    {
        var cb = new ComboBox { Width = 220 };
        foreach (var (v, t) in items) cb.Items.Add(new ComboBoxItem { Content = t, Tag = v });
        cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, current)) ?? cb.Items[0];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = Loc.T(key), VerticalAlignment = VerticalAlignment.Center, Width = 240, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(cb);
        p.Children.Add(row);
        _apply.Add(() => apply((T)((ComboBoxItem)cb.SelectedItem).Tag));
        return cb;
    }

    private static IEnumerable<(TimeSpan, string)> Times()
    {
        for (int i = 0; i < 48; i++) { var t = TimeSpan.FromMinutes(i * 30); yield return (t, DateTime.Today.Add(t).ToString("t", Loc.Instance.Culture)); }
    }

    // ---------------------------------------------------------------- pages

    private FrameworkElement General()
    {
        var p = Page(Loc.T("GeneralHeader"));
        Section(p, Loc.T("UserInterfaceOptions"));
        Combo(p, "OfficeTheme", new[] { ("Colorful", Loc.T("ThemeColorful")), ("White", Loc.T("ThemeWhite")) }, _s.Theme, v => _s.Theme = v);
        Check(p, "ShowSenderPhotos", _s.ShowSenderPhotos, v => _s.ShowSenderPhotos = v);
        Section(p, Loc.T("StartupOptions"));
        Combo(p, "DisplayLanguage", new[] { ("ka-GE", Loc.T("Georgian")), ("en-US", Loc.T("English")) }, _s.Language.StartsWith("en") ? "en-US" : "ka-GE",
            v => { if (v != _s.Language) { _s.Language = v; Loc.Instance.SetLanguage(v); } });
        return p;
    }

    private FrameworkElement Mail()
    {
        var p = Page(Loc.T("OptMail"), Loc.Instance.IsGeorgian ? "შეცვალეთ ელფოსტის შექმნისა და მიღების პარამეტრები." : "Change the settings for messages you create and receive.");
        Section(p, Loc.T("ComposeMessages"));
        Combo(p, "ComposeIn", new[] { (ComposeFormat.Html, Loc.T("HTML")), (ComposeFormat.PlainText, Loc.T("PlainText")) }, _s.ComposeFormat, v => _s.ComposeFormat = v);
        Check(p, "AlwaysCheckSpelling", _s.CheckSpelling, v => _s.CheckSpelling = v);
        var sigRow = new DockPanel { Margin = new Thickness(10, 8, 0, 3) };
        var sigBtn = MakeButton(Loc.T("Signatures"), false, false, () => new SignaturesWindow { Owner = this }.ShowDialog());
        sigBtn.Margin = new Thickness(0);
        DockPanel.SetDock(sigBtn, Dock.Right);
        sigRow.Children.Add(sigBtn);
        sigRow.Children.Add(new TextBlock { Text = Loc.T("SignaturesDesc"), VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(sigRow);
        var statRow = new DockPanel { Margin = new Thickness(10, 3, 0, 3) };
        var statBtn = MakeButton(Loc.T("StationeryFonts"), false, false, () => new SignaturesWindow(stationery: true) { Owner = this }.ShowDialog());
        statBtn.Margin = new Thickness(0);
        DockPanel.SetDock(statBtn, Dock.Right);
        statRow.Children.Add(statBtn);
        statRow.Children.Add(new TextBlock { Text = Loc.T("ThemeStationery"), VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(statRow);

        Section(p, Loc.T("ReadingPane"));
        var markWhen = Check(p, "MarkReadWhenViewed", _s.MarkRead == MarkReadMode.AfterSeconds, _ => { });
        var secs = Number(p, "WaitSeconds", _s.MarkReadSeconds, v => _s.MarkReadSeconds = v);
        var onChange = Check(p, "MarkReadOnSelectionChange", _s.MarkRead == MarkReadMode.WhenSelectionChanges, _ => { });
        _apply.Add(() => _s.MarkRead = markWhen.IsChecked == true ? MarkReadMode.AfterSeconds : onChange.IsChecked == true ? MarkReadMode.WhenSelectionChanges : MarkReadMode.Never);
        markWhen.Checked += (_, _) => onChange.IsChecked = false;
        onChange.Checked += (_, _) => markWhen.IsChecked = false;

        Section(p, Loc.T("MessageArrival"));
        p.Children.Add(new TextBlock { Text = Loc.T("WhenNewMessages"), Margin = new Thickness(10, 0, 0, 2) });
        Check(p, "PlaySound", _s.PlaySound, v => _s.PlaySound = v);
        Check(p, "ShowEnvelope", _s.ShowEnvelopeInTaskbar, v => _s.ShowEnvelopeInTaskbar = v);
        Check(p, "DisplayDesktopAlert", _s.ShowDesktopAlert, v => _s.ShowDesktopAlert = v);

        Section(p, Loc.T("RepliesForwards"));
        Check(p, "CloseOriginalOnReply", _s.CloseOriginalOnReply, v => _s.CloseOriginalOnReply = v);

        Section(p, Loc.T("SaveMessages"));
        Number(p, "AutoSaveDrafts", _s.AutoSaveDraftMinutes, v => _s.AutoSaveDraftMinutes = v);
        Check(p, "SaveCopiesSent", _s.SaveSentItems, v => _s.SaveSentItems = v);

        Section(p, Loc.T("SendMessages"));
        Check(p, "AutoCompleteList", _s.AutoCompleteRecipients, v => _s.AutoCompleteRecipients = v);
        var empty = MakeButton(Loc.T("EmptyAutoComplete"), false, false, () => { _s.RecentRecipients.Clear(); });
        empty.HorizontalAlignment = HorizontalAlignment.Left;
        empty.Margin = new Thickness(30, 2, 0, 4);
        p.Children.Add(empty);
        Check(p, "WarnAttachment", _s.WarnMissingAttachment, v => _s.WarnMissingAttachment = v);

        Section(p, Loc.T("Tracking2"));
        Check(p, "AlwaysDeliveryReceipt", _s.DeliveryReceiptAlways, v => _s.DeliveryReceiptAlways = v);
        Check(p, "AlwaysReadReceipt", _s.ReadReceiptAlways, v => _s.ReadReceiptAlways = v);

        Section(p, Loc.T("Pictures2"));
        Check(p, "BlockPictures", _s.BlockExternalPictures, v => _s.BlockExternalPictures = v);
        return p;
    }

    private FrameworkElement CalendarPage()
    {
        var p = Page(Loc.T("OptCalendar"), Loc.Instance.IsGeorgian ? "შეცვალეთ კალენდრის, შეხვედრებისა და დროის სარტყლების პარამეტრები." : "Change the settings for calendars, meetings, and time zones.");
        var c = Loc.Instance.Culture;
        Section(p, Loc.T("WorkTime"));
        Combo(p, "StartTime2", Times(), _s.WorkStart, v => _s.WorkStart = v);
        Combo(p, "EndTime2", Times(), _s.WorkEnd, v => _s.WorkEnd = v);
        p.Children.Add(new TextBlock { Text = Loc.T("WorkWeekDays"), Margin = new Thickness(10, 6, 0, 2) });
        var days = new WrapPanel { Margin = new Thickness(10, 0, 0, 4) };
        var boxes = new Dictionary<DayOfWeek, CheckBox>();
        foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek)))
        {
            var cb = new CheckBox { Content = c.DateTimeFormat.GetAbbreviatedDayName(d), IsChecked = _s.WorkDays.Contains(d), Margin = new Thickness(0, 0, 12, 0) };
            boxes[d] = cb;
            days.Children.Add(cb);
        }
        p.Children.Add(days);
        _apply.Add(() => _s.WorkDays = boxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList());
        Combo(p, "FirstDayOfWeek", Enum.GetValues(typeof(DayOfWeek)).Cast<DayOfWeek>().Select(d => (d, c.DateTimeFormat.GetDayName(d))), _s.FirstDayOfWeek, v => _s.FirstDayOfWeek = v);

        Section(p, Loc.T("CalendarOptions"));
        var rem = Check(p, "DefaultReminders", _s.DefaultRemindersOn, v => _s.DefaultRemindersOn = v);
        Combo(p, "Reminder", new[] { 0, 5, 10, 15, 30, 60, 120, 1440 }.Select(m => (m, m == 0 ? Loc.T("AtStart") : m < 60 ? Loc.F("ReminderMinutes", m) : m < 1440 ? Loc.F("ReminderHours", m / 60) : Loc.F("ReminderDays", m / 1440))),
            _s.DefaultReminderMinutes, v => _s.DefaultReminderMinutes = v);
        Check(p, "AllowProposeTimes", _s.AllowAttendeesProposeNewTimes, v => _s.AllowAttendeesProposeNewTimes = v);
        Number(p, "DefaultMeetingDuration", _s.DefaultMeetingMinutes, v => _s.DefaultMeetingMinutes = Math.Max(5, v));

        Section(p, Loc.T("DisplayOptions"));
        Combo(p, "TimeScale", new[] { 5, 6, 10, 15, 30, 60 }.Select(m => (m, Loc.F("Minutes", m))), _s.TimeScaleMinutes, v => _s.TimeScaleMinutes = v);
        Check(p, "ShowWeekNumbers", _s.ShowWeekNumbers, v => _s.ShowWeekNumbers = v);

        Section(p, Loc.T("Reminders").Split(':')[0]);
        Check(p, "ShowReminders", _s.ShowReminders, v => _s.ShowReminders = v);
        Check(p, "PlayReminderSound", _s.PlayReminderSound, v => _s.PlayReminderSound = v);
        return p;
    }

    private FrameworkElement People()
    {
        var p = Page(Loc.T("OptPeople"));
        Section(p, Loc.T("NamesAndFiling"));
        Combo(p, "DefaultFileAs", new[] { ("LastFirst", Loc.T("LastFirst")), ("FirstLast", Loc.T("FirstLast")) }, _s.ContactFileAs, v => _s.ContactFileAs = v);
        return p;
    }

    private FrameworkElement Tasks()
    {
        var p = Page(Loc.T("OptTasks"));
        Section(p, Loc.T("TaskOptions"));
        Check(p, "ShowCompleted", _s.ShowCompletedTasks, v => _s.ShowCompletedTasks = v);
        return p;
    }

    private FrameworkElement LanguagePage()
    {
        var p = Page(Loc.T("OptLanguage"), Loc.T("OptLanguageDesc"));
        Section(p, Loc.T("DisplayLanguage"));
        var ka = new RadioButton { Content = "ქართული (Georgian)", GroupName = "lang", IsChecked = Loc.Instance.IsGeorgian, Margin = new Thickness(10, 3, 0, 3) };
        var en = new RadioButton { Content = "English", GroupName = "lang", IsChecked = !Loc.Instance.IsGeorgian, Margin = new Thickness(10, 3, 0, 3) };
        p.Children.Add(ka);
        p.Children.Add(en);
        p.Children.Add(new TextBlock { Text = Loc.T("LanguageRestartHint"), Foreground = Brushes.DimGray, Margin = new Thickness(10, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        _apply.Add(() =>
        {
            var code = ka.IsChecked == true ? "ka-GE" : "en-US";
            if (code != _s.Language) { _s.Language = code; Loc.Instance.SetLanguage(code); }
        });
        return p;
    }

    private FrameworkElement Advanced()
    {
        var p = Page(Loc.T("OptAdvanced"));
        Section(p, Loc.T("GrpSendReceive"));
        Number(p, "SendReceiveEvery", _s.SendReceiveIntervalMinutes, v => _s.SendReceiveIntervalMinutes = Math.Max(1, v));
        Section(p, Loc.T("ReadingPane"));
        Combo(p, "ReadingPane", new[] { (ReadingPanePosition.Right, Loc.T("Right")), (ReadingPanePosition.Bottom, Loc.T("Bottom")), (ReadingPanePosition.Off, Loc.T("Off")) }, _s.ReadingPane, v => _s.ReadingPane = v);
        var folder = new TextBlock { Text = SettingsService.DataFolder, Foreground = Brushes.DimGray, Margin = new Thickness(10, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        p.Children.Add(folder);
        return p;
    }
}

// =========================================================================================
// Signatures and Stationery
// =========================================================================================
public sealed class SignaturesWindow : DialogWindow
{
    private readonly AppSettings _s = AppServices.Current.Settings;
    private readonly List<Signature> _sigs;
    private readonly ListBox _list = new() { Height = 130, ItemContainerStyle = (Style)Application.Current.FindResource("PlainListItem") };
    private readonly HtmlEditor _editor = new() { Height = 230 };
    private readonly ComboBox _account = new() { Width = 260 }, _newSig = new() { Width = 260 }, _replySig = new() { Width = 260 };
    private readonly ComboBox _font = new() { Width = 160, IsEditable = true }, _size = new() { Width = 60, IsEditable = true };
    private readonly Dictionary<string, AccountSignatureDefaults> _defaults = new();
    private Signature? _current;
    private bool _loading;

    public SignaturesWindow(bool stationery = false)
    {
        Title = Loc.T("SignaturesTitle");
        _sigs = _s.Signatures.Select(x => new Signature { Name = x.Name, Html = x.Html }).ToList();
        foreach (var a in AppServices.Current.Accounts)
        {
            var d = AppServices.Current.SettingsService.GetSignatureDefaults(a.Account.Id);
            _defaults[a.Account.Id] = new AccountSignatureDefaults { AccountId = d.AccountId, NewMessages = d.NewMessages, RepliesForwards = d.RepliesForwards };
        }

        var tabs = new TabControl { Margin = new Thickness(12, 12, 12, 0), Width = 820 };
        tabs.Items.Add(new TabItem { Header = Loc.T("EmailSignature"), Content = SignatureTab() });
        tabs.Items.Add(new TabItem { Header = Loc.T("PersonalStationery"), Content = StationeryTab() });
        if (stationery) tabs.SelectedIndex = 1;
        var root = new StackPanel();
        root.Children.Add(tabs);
        var btns = ButtonRow(MakeButton(Loc.T("Ok"), true, false, async () => { await SaveAsync(); DialogResult = true; }), MakeButton(Loc.T("Cancel"), false, true));
        btns.Margin = new Thickness(12, 10, 12, 12);
        root.Children.Add(btns);
        Content = root;
        RefreshList();
    }

    private FrameworkElement SignatureTab()
    {
        var root = new StackPanel { Margin = new Thickness(10) };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = Loc.T("SelectSignatureToEdit"), Margin = new Thickness(0, 0, 0, 4) });
        left.Children.Add(_list);
        var lb = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Button B(string t, Action a) { var b = MakeButton(t, false, false, a); b.Margin = new Thickness(0, 0, 6, 4); b.MinWidth = 0; b.Padding = new Thickness(10, 0, 10, 0); return b; }
        lb.Children.Add(B(Loc.T("Delete"), Delete));
        lb.Children.Add(B(Loc.T("New"), New));
        lb.Children.Add(B(Loc.T("SaveSig"), async () => await CommitCurrentAsync()));
        lb.Children.Add(B(Loc.T("Rename"), Rename));
        left.Children.Add(lb);
        top.Children.Add(left);

        var right = new StackPanel();
        right.Children.Add(new TextBlock { Text = Loc.T("ChooseDefaultSignature"), Margin = new Thickness(0, 0, 0, 6) });
        void Row(string label, ComboBox cb)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(new TextBlock { Text = Loc.T(label), Width = 150, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(cb);
            right.Children.Add(sp);
        }
        Row("EmailAccount", _account);
        Row("NewMessagesSig", _newSig);
        Row("RepliesForwardsSig", _replySig);
        foreach (var a in AppServices.Current.Accounts) _account.Items.Add(new ComboBoxItem { Content = a.Account.Email, Tag = a.Account.Id });
        _account.SelectionChanged += (_, _) => ShowDefaults();
        _newSig.SelectionChanged += (_, _) => StoreDefaults();
        _replySig.SelectionChanged += (_, _) => StoreDefaults();
        Grid.SetColumn(right, 2);
        top.Children.Add(right);
        root.Children.Add(top);

        root.Children.Add(new TextBlock { Text = Loc.T("EditSignature"), Margin = new Thickness(0, 12, 0, 4) });
        var tb = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var f in new[] { "Aptos", "Calibri", "Arial", "Segoe UI", "Sylfaen", "Times New Roman", "Georgia", "Verdana", "Tahoma" }) _font.Items.Add(f);
        foreach (var z in new[] { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24" }) _size.Items.Add(z);
        _font.Text = (_s.ComposeFontFamily ?? "Aptos").Split(',')[0].Trim();
        _size.Text = _s.ComposeFontSizePt.ToString();
        _font.SelectionChanged += async (_, _) => { if (_font.SelectedItem is string f) await _editor.Command("fontName", f); };
        _size.SelectionChanged += async (_, _) => { if (_size.SelectedItem is string z && double.TryParse(z, out var pt)) await _editor.SetFontSize(pt); };
        tb.Children.Add(_font);
        tb.Children.Add(_size);
        Button T(string glyph, string tip, Func<Task> a)
        {
            var b = new Button { Style = (Style)FindResource("FlatButton"), Content = Ui.GlyphText(glyph, 14, GlyphFactory.DefaultBrush), ToolTip = tip, Margin = new Thickness(2, 0, 0, 0) };
            b.Click += async (_, _) => await a();
            return b;
        }
        tb.Children.Add(T(G.Bold, Loc.T("Bold"), () => _editor.Command("bold")));
        tb.Children.Add(T(G.Italic, Loc.T("Italic"), () => _editor.Command("italic")));
        tb.Children.Add(T(G.Underline, Loc.T("UnderlineTip"), () => _editor.Command("underline")));
        tb.Children.Add(T(G.FontColor, Loc.T("FontColor"), async () =>
        {
            var c = Ui.Prompt(Loc.T("FontColor"), "#RRGGBB", "#0F6CBD", this);
            if (!string.IsNullOrWhiteSpace(c)) await _editor.Command("foreColor", c);
        }));
        tb.Children.Add(T(G.AlignLeft, Loc.T("AlignLeft"), () => _editor.Command("justifyLeft")));
        tb.Children.Add(T(G.AlignCenter, Loc.T("Center"), () => _editor.Command("justifyCenter")));
        tb.Children.Add(T(G.AlignRight, Loc.T("AlignRight"), () => _editor.Command("justifyRight")));
        tb.Children.Add(T(G.Picture, Loc.T("InsertImage"), InsertPictureAsync));
        tb.Children.Add(T(G.Link, Loc.T("Link"), async () =>
        {
            var url = Ui.Prompt(Loc.T("Link"), Loc.T("InsertLinkPrompt"), "https://", this);
            if (!string.IsNullOrWhiteSpace(url)) await _editor.Command("createLink", url);
        }));
        tb.Children.Add(T(G.Contact, Loc.T("BusinessCardBtn"), InsertBusinessCardAsync));
        root.Children.Add(tb);
        root.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Child = _editor });

        _list.SelectionChanged += async (_, _) =>
        {
            await CommitCurrentAsync();
            _current = _list.SelectedItem as Signature;
            _editor.IsEnabled = _current != null;
            await _editor.SetHtmlAsync(_current == null ? "" : HtmlUtil.BodyContent(_current.Html));
        };
        _list.DisplayMemberPath = nameof(Signature.Name);
        if (_account.Items.Count > 0) _account.SelectedIndex = 0;
        return root;
    }

    private FrameworkElement StationeryTab()
    {
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = Loc.T("NewMailMessages"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var fontBox = new ComboBox { Width = 220, IsEditable = true, Text = _s.ComposeFontFamily.Split(',')[0].Trim() };
        foreach (var f in new[] { "Aptos", "Calibri", "Arial", "Segoe UI", "Sylfaen", "Times New Roman", "Georgia", "Verdana", "Tahoma", "Consolas" }) fontBox.Items.Add(f);
        var sizeBox = new ComboBox { Width = 70, IsEditable = true, Text = _s.ComposeFontSizePt.ToString() };
        foreach (var z in new[] { 8, 9, 10, 11, 12, 14, 16, 18 }) sizeBox.Items.Add(z.ToString());
        var colorBox = new TextBox { Width = 90, Height = 24, Text = _s.ComposeFontColor, VerticalContentAlignment = VerticalAlignment.Center };
        var sample = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 12, 0, 0), Height = 60 };
        var sampleText = new TextBlock { Text = Loc.T("SampleText") + " — ფოსტა Mail 123" };
        sample.Child = sampleText;
        void UpdateSample()
        {
            try { sampleText.FontFamily = new FontFamily(fontBox.Text + ", Sylfaen"); } catch { }
            if (double.TryParse(sizeBox.Text, out var pt)) sampleText.FontSize = pt * 96 / 72;
            sampleText.Foreground = GlyphFactory.ParseBrush(colorBox.Text, Brushes.Black);
        }
        fontBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateSample);
        fontBox.LostFocus += (_, _) => UpdateSample();
        sizeBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateSample);
        colorBox.TextChanged += (_, _) => UpdateSample();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = Loc.T("DefaultFont"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(fontBox);
        row.Children.Add(new TextBlock { Text = Loc.T("FontSize"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 0) });
        row.Children.Add(sizeBox);
        row.Children.Add(new TextBlock { Text = Loc.T("Color"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 0) });
        row.Children.Add(colorBox);
        root.Children.Add(row);
        root.Children.Add(sample);
        UpdateSample();
        _saveStationery = () =>
        {
            if (!string.IsNullOrWhiteSpace(fontBox.Text)) _s.ComposeFontFamily = $"{fontBox.Text.Trim()}, 'Segoe UI', Sylfaen, sans-serif";
            if (int.TryParse(sizeBox.Text, out var pt) && pt is >= 6 and <= 72) _s.ComposeFontSizePt = pt;
            if (colorBox.Text.StartsWith('#')) _s.ComposeFontColor = colorBox.Text.Trim();
        };
        return root;
    }

    private Action? _saveStationery;

    private void RefreshList()
    {
        var sel = (_list.SelectedItem as Signature)?.Name;
        _list.ItemsSource = null;
        _list.ItemsSource = _sigs;
        _list.SelectedItem = _sigs.FirstOrDefault(x => x.Name == sel) ?? _sigs.FirstOrDefault();
        _loading = true;
        foreach (var cb in new[] { _newSig, _replySig })
        {
            cb.Items.Clear();
            cb.Items.Add(new ComboBoxItem { Content = Loc.T("SignatureNone"), Tag = null });
            foreach (var s in _sigs) cb.Items.Add(new ComboBoxItem { Content = s.Name, Tag = s.Name });
        }
        _loading = false;
        ShowDefaults();
        _editor.IsEnabled = _sigs.Count > 0;
    }

    private string? SelectedAccountId => (_account.SelectedItem as ComboBoxItem)?.Tag as string;

    private void ShowDefaults()
    {
        var id = SelectedAccountId;
        if (id == null || !_defaults.TryGetValue(id, out var d) || _newSig.Items.Count == 0 || _replySig.Items.Count == 0) return;
        _loading = true;
        _newSig.SelectedItem = _newSig.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == d.NewMessages) ?? _newSig.Items[0];
        _replySig.SelectedItem = _replySig.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == d.RepliesForwards) ?? _replySig.Items[0];
        _loading = false;
    }

    private void StoreDefaults()
    {
        if (_loading) return;
        var id = SelectedAccountId;
        if (id == null || !_defaults.TryGetValue(id, out var d)) return;
        d.NewMessages = (_newSig.SelectedItem as ComboBoxItem)?.Tag as string;
        d.RepliesForwards = (_replySig.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    private async Task CommitCurrentAsync()
    {
        if (_current == null) return;
        _current.Html = $"<html><body>{await _editor.GetHtmlAsync()}</body></html>";
    }

    private async void New()
    {
        await CommitCurrentAsync();
        var name = Ui.Prompt(Loc.T("NewSignatureTitle"), Loc.T("NewSignaturePrompt"), "", this);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_sigs.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        var me = AppServices.Current.DefaultAccount;
        var sig = new Signature
        {
            Name = name,
            Html = $"<html><body><div style=\"font-family:{_s.ComposeFontFamily.Replace("\"", "'")};font-size:{_s.ComposeFontSizePt}pt\">" +
                   $"<div>{(Loc.Instance.IsGeorgian ? "პატივისცემით," : "Best regards,")}</div><div><b>{HtmlUtil.Encode(me?.UserDisplayName ?? me?.Account.DisplayName ?? "")}</b></div>" +
                   $"<div style=\"color:#605E5C\">{HtmlUtil.Encode(me?.Account.Email ?? "")}</div></div></body></html>"
        };
        _sigs.Add(sig);
        _current = null;
        RefreshList();
        _list.SelectedItem = sig;
    }

    private void Delete()
    {
        if (_list.SelectedItem is not Signature s) return;
        if (!Ui.Confirm(Loc.F("DeleteSignatureConfirm", s.Name), this)) return;
        _current = null;
        _sigs.Remove(s);
        foreach (var d in _defaults.Values)
        {
            if (d.NewMessages == s.Name) d.NewMessages = null;
            if (d.RepliesForwards == s.Name) d.RepliesForwards = null;
        }
        RefreshList();
    }

    private void Rename()
    {
        if (_list.SelectedItem is not Signature s) return;
        var name = Ui.Prompt(Loc.T("Rename"), Loc.T("NewSignaturePrompt"), s.Name, this);
        if (string.IsNullOrWhiteSpace(name) || name == s.Name) return;
        foreach (var d in _defaults.Values)
        {
            if (d.NewMessages == s.Name) d.NewMessages = name;
            if (d.RepliesForwards == s.Name) d.RepliesForwards = name;
        }
        s.Name = name;
        RefreshList();
    }

    private async Task InsertPictureAsync()
    {
        var dlg = new OpenFileDialog { Filter = Loc.T("ImageFilter") };
        if (dlg.ShowDialog(this) != true) return;
        var bytes = await File.ReadAllBytesAsync(dlg.FileName);
        var ext = Path.GetExtension(dlg.FileName).TrimStart('.').ToLowerInvariant();
        var mime = ext is "jpg" or "jpeg" ? "image/jpeg" : ext == "gif" ? "image/gif" : "image/png";
        await _editor.InsertHtml($"<img src=\"data:{mime};base64,{Convert.ToBase64String(bytes)}\" style=\"max-width:240px\">");
    }

    private async Task InsertBusinessCardAsync()
    {
        var me = AppServices.Current.DefaultAccount;
        if (me == null) return;
        ResolvedName? info = null;
        try { info = (await me.ResolveNamesAsync(me.Account.Email, true)).FirstOrDefault(); } catch { }
        var html = $"<table style=\"border-left:3px solid #0F6CBD;padding-left:8px\"><tr><td><b>{HtmlUtil.Encode(info?.Name ?? me.Account.DisplayName)}</b><br>" +
                   $"{HtmlUtil.Encode(string.Join(", ", new[] { info?.Title, info?.Department, info?.Company }.Where(s => !string.IsNullOrWhiteSpace(s))))}<br>" +
                   $"{HtmlUtil.Encode(info?.Phone ?? "")}<br><a href=\"mailto:{me.Account.Email}\">{me.Account.Email}</a></td></tr></table>";
        await _editor.InsertHtml(html);
    }

    private async Task SaveAsync()
    {
        await CommitCurrentAsync();
        _saveStationery?.Invoke();
        foreach (var d in _defaults.Values)
        {
            var target = AppServices.Current.SettingsService.GetSignatureDefaults(d.AccountId);
            target.NewMessages = d.NewMessages;
            target.RepliesForwards = d.RepliesForwards;
        }
        AppServices.Current.SettingsService.SaveSignatures(_sigs);
    }
}
