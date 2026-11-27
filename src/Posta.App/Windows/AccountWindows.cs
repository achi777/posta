using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Posta.App.Controls;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Windows;

// =========================================================================================
// Add / change account (Exchange)
// =========================================================================================
public sealed class AddAccountWindow : DialogWindow
{
    private readonly TextBox _name = Field(), _email = Field(), _user = Field(), _server = Field(), _domain = Field();
    private readonly PasswordBox _password = new() { Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(3, 0, 3, 0) };
    private readonly CheckBox _windows = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly CheckBox _outlook = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly CheckBox _native = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly CheckBox _remember = new() { IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Expander _advanced = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _status = new() { Foreground = Brushes.DimGray, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 3, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(0) };
    private readonly Button _connect;
    private readonly AccountConfig? _existing;

    private static TextBox Field() => new() { Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(3, 0, 3, 0) };

    public AddAccountWindow(AccountConfig? existing = null)
    {
        _existing = existing;
        Title = Loc.T("AddAccountTitle");
        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 18), Width = 460 };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var icon = Ui.GlyphText(G.Mail, 30, GlyphFactory.Blue);
        icon.Margin = new Thickness(0, 0, 14, 0);
        head.Children.Add(icon);
        var hs = new StackPanel();
        hs.Children.Add(new TextBlock { Text = Loc.T("AddAccountTitle"), FontSize = 20, FontWeight = FontWeights.Light });
        hs.Children.Add(new TextBlock { Text = Loc.T("AddAccountIntro"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
        head.Children.Add(hs);
        root.Children.Add(head);

        void Labeled(string label, Control c)
        {
            root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
            root.Children.Add(c);
        }
        Labeled(Loc.T("YourName"), _name);
        Labeled(Loc.T("EmailAddress"), _email);
        Labeled(Loc.T("Password"), _password);
        _windows.Content = Loc.T("UseWindowsCredentials");
        _windows.Checked += (_, _) => _password.IsEnabled = false;
        _windows.Unchecked += (_, _) => _password.IsEnabled = true;
        root.Children.Add(_windows);
        _remember.Content = Loc.T("RememberPassword");
        root.Children.Add(_remember);

        // Connect through a locally installed Outlook (classic) via COM, for servers that block EWS.
        _outlook.Content = Loc.T("UseOutlookInterop");
        _native.Content = Loc.T("UseNativeMapi");
        var outlookNote = new TextBlock { Text = Loc.T("UseOutlookInteropNote"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(22, 2, 0, 0), Visibility = Visibility.Collapsed };
        void ApplyOutlook(bool on)
        {
            _password.IsEnabled = !on && _windows.IsChecked != true;
            _windows.IsEnabled = !on;
            _remember.IsEnabled = !on;
            _server.IsEnabled = !on;
            _user.IsEnabled = !on;
            _domain.IsEnabled = !on;
            outlookNote.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        _outlook.Checked += (_, _) => { _native.IsChecked = false; ApplyOutlook(true); };
        _outlook.Unchecked += (_, _) => ApplyOutlook(false);
        root.Children.Add(_outlook);
        root.Children.Add(outlookNote);

        root.Children.Add(_native);

        var adv = new StackPanel { Margin = new Thickness(18, 4, 0, 0) };
        var serverLabel = new TextBlock { Text = Loc.T("ServerUrl"), Margin = new Thickness(0, 4, 0, 3) };
        adv.Children.Add(serverLabel);
        adv.Children.Add(_server);
        adv.Children.Add(new TextBlock { Text = Loc.T("UserNameLogin"), Margin = new Thickness(0, 8, 0, 3) });
        adv.Children.Add(_user);
        adv.Children.Add(new TextBlock { Text = "Domain:", Margin = new Thickness(0, 8, 0, 3) });
        adv.Children.Add(_domain);
        _advanced.Header = Loc.T("AdvancedSettings");
        _advanced.Content = adv;
        root.Children.Add(_advanced);
        _native.Checked += (_, _) =>
        {
            _outlook.IsChecked = false; ApplyOutlook(false);
            serverLabel.Text = Loc.T("MapiAutodiscoverServer");
            _advanced.IsExpanded = true;
        };
        _native.Unchecked += (_, _) => serverLabel.Text = Loc.T("ServerUrl");
        root.Children.Add(_progress);
        root.Children.Add(_status);
        _connect = MakeButton(Loc.T("Connect"), true, false, async () => await ConnectAsync());
        root.Children.Add(ButtonRow(_connect, MakeButton(Loc.T("Cancel"), false, true)));
        Content = root;

        if (existing != null)
        {
            _name.Text = existing.DisplayName;
            _email.Text = existing.Email;
            _email.IsReadOnly = true;
            _user.Text = existing.UserName;
            _server.Text = existing.ServerUrl;
            _domain.Text = existing.Domain;
            _windows.IsChecked = existing.UseWindowsCredentials;
            _outlook.IsChecked = existing.UseOutlookInterop;
            _native.IsChecked = existing.UseNativeMapi;
            // Switching an existing account's provider requires a new service and new cache IDs.
            // Keep this immutable until account migration is implemented.
            _outlook.IsEnabled = false; _native.IsEnabled = false;
            _advanced.IsExpanded = true;
        }
        Loaded += (_, _) => (existing == null ? _name : (Control)_password).Focus();
    }

    private async Task ConnectAsync()
    {
        var email = _email.Text.Trim();
        if (!Ui.IsValidEmail(email)) { Ui.Error(Loc.T("InvalidEmail"), this); return; }
        if (_existing == null && AppServices.Current.SettingsService.Accounts.Any(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Error(Loc.T("AccountExists"), this);
            return;
        }
        var cfg = _existing ?? new AccountConfig();
        cfg.DisplayName = _name.Text.Trim();
        cfg.Email = email;
        cfg.UserName = _user.Text.Trim();
        cfg.Domain = _domain.Text.Trim();
        cfg.ServerUrl = _server.Text.Trim();
        cfg.UseWindowsCredentials = _windows.IsChecked == true;
        cfg.UseOutlookInterop = _outlook.IsChecked == true;
        cfg.UseNativeMapi = _native.IsChecked == true;
        var pwd = _password.Password;
        bool noPwd = cfg.UseWindowsCredentials || cfg.UseOutlookInterop;

        _connect.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _status.Text = Loc.T("TestingConnection");
        try
        {
            if (_existing == null)
            {
                await AppServices.Current.AddAccountAsync(cfg, noPwd ? null : pwd, !noPwd && _remember.IsChecked == true);
            }
            else
            {
                var svc = AppServices.Current.GetAccount(cfg.Id);
                if (svc != null)
                {
                    await svc.ConnectAsync(noPwd ? null : pwd);
                    if (!noPwd && _remember.IsChecked == true) CredentialStore.Save(cfg.Id, cfg.EffectiveUser, pwd);
                    AppServices.Current.NotifyAccountsChanged();
                }
            }
            _status.Text = Loc.T("AccountAdded");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _status.Text = "✖ " + ex.Message;
            try
            {
                // Full exception chain (never the password) for troubleshooting.
                System.IO.File.AppendAllText(System.IO.Path.Combine(Posta.Core.Services.SettingsService.LocalFolder, "connection.log"),
                    $"[{DateTime.Now:O}] {cfg.Email} user={cfg.EffectiveUser} domain={cfg.Domain} server={cfg.ServerUrl}\n{ex}\n\n");
            }
            catch { }
            _status.Foreground = Brushes.Firebrick;
            _advanced.IsExpanded = true;
            if (!string.IsNullOrEmpty(cfg.ServerUrl) && string.IsNullOrEmpty(_server.Text)) _server.Text = cfg.ServerUrl;
        }
        finally
        {
            _connect.IsEnabled = true;
            _progress.Visibility = Visibility.Collapsed;
        }
    }
}

// =========================================================================================
// Account settings (list, add, change, remove, additional mailboxes)
// =========================================================================================
public sealed class AccountSettingsWindow : DialogWindow
{
    private readonly ListView _list = new() { Height = 220, Width = 600 };
    private readonly TextBox _mailboxes = new() { AcceptsReturn = true, Height = 70, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    public AccountSettingsWindow()
    {
        Title = Loc.T("AccountSettings");
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        root.Children.Add(new TextBlock { Text = Loc.T("AccountsList"), FontSize = 15, Margin = new Thickness(0, 0, 0, 8) });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        Button B(string text, string glyph, Action a)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Ui.GlyphText(glyph, 13, GlyphFactory.Blue));
            sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0) });
            var b = new Button { Style = (Style)FindResource("FlatButton"), Content = sp, Margin = new Thickness(0, 0, 4, 0) };
            b.Click += (_, _) => a();
            return b;
        }
        bar.Children.Add(B(Loc.T("New") + "...", G.Add, AddNew));
        bar.Children.Add(B(Loc.T("Repair"), G.Sync, Repair));
        bar.Children.Add(B(Loc.T("Change"), G.Edit, ChangeSelected));
        bar.Children.Add(B(Loc.T("SetAsDefault"), G.CheckMark, SetDefault));
        bar.Children.Add(B(Loc.T("Remove"), G.Cancel, RemoveSelected));
        root.Children.Add(bar);

        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("Name"), Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding("Account.Email") });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("Type"), Width = 280, DisplayMemberBinding = new System.Windows.Data.Binding("TypeText") });
        _list.View = gv;
        _list.SelectionChanged += (_, _) => ShowMailboxes();
        root.Children.Add(_list);

        root.Children.Add(new TextBlock { Text = Loc.T("AdditionalMailboxes"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
        root.Children.Add(new TextBlock { Text = Loc.T("AdditionalMailboxesHint"), Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(_mailboxes);

        root.Children.Add(ButtonRow(MakeButton(Loc.T("Close"), true, true, () => { SaveMailboxes(); DialogResult = true; })));
        Content = root;
        Refresh();
    }

    private sealed class Row
    {
        public IMailService Service { get; init; } = null!;
        public AccountConfig Account => Service.Account;
        public string TypeText => Loc.T("Exchange") + (Service.State == ConnectionState.Connected ? "" : " – " + Loc.T(Service.State == ConnectionState.NeedsPassword ? "NeedPassword" : "Disconnected")) +
                                  (AppServices.Current.DefaultAccount == Service ? "  " + Loc.T("DefaultAccount") : "");
    }

    private IMailService? Selected => (_list.SelectedItem as Row)?.Service;

    private void Refresh()
    {
        var sel = Selected;
        _list.ItemsSource = AppServices.Current.Accounts.Select(a => new Row { Service = a }).ToList();
        _list.SelectedItem = ((List<Row>)_list.ItemsSource).FirstOrDefault(r => r.Service == sel) ?? ((List<Row>)_list.ItemsSource).FirstOrDefault();
    }

    private IMailService? _mailboxOwner;

    private void ShowMailboxes()
    {
        SaveMailboxes();
        _mailboxOwner = Selected;
        _mailboxes.Text = _mailboxOwner == null ? "" : string.Join(Environment.NewLine, _mailboxOwner.Account.AdditionalMailboxes);
        _mailboxes.IsEnabled = _mailboxOwner != null && !_mailboxOwner.Account.IsDemo;
    }

    private void SaveMailboxes()
    {
        if (_mailboxOwner == null) return;
        var list = _mailboxes.Text.Split(new[] { '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(Ui.IsValidEmail).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!list.SequenceEqual(_mailboxOwner.Account.AdditionalMailboxes))
        {
            _mailboxOwner.Account.AdditionalMailboxes = list;
            AppServices.Current.NotifyAccountsChanged();
            AppServices.Current.RaiseDataChanged("folders");
        }
    }

    private void AddNew()
    {
        if (new AddAccountWindow { Owner = this }.ShowDialog() == true) Refresh();
    }

    private async void Repair()
    {
        var svc = Selected;
        if (svc == null) return;
        await AppServices.Current.ConnectAccountAsync(svc, this, true);
        Refresh();
    }

    private void ChangeSelected()
    {
        var svc = Selected;
        if (svc == null || svc.Account.IsDemo) return;
        if (new AddAccountWindow(svc.Account) { Owner = this }.ShowDialog() == true) Refresh();
    }

    private void SetDefault()
    {
        var svc = Selected;
        if (svc == null) return;
        var accounts = AppServices.Current.Accounts;
        accounts.Move(accounts.IndexOf(svc), 0);
        var cfgs = AppServices.Current.SettingsService.Accounts;
        for (int i = 0; i < accounts.Count; i++)
        {
            var c = cfgs.FirstOrDefault(x => x.Id == accounts[i].Account.Id);
            if (c != null) c.SortOrder = i;
        }
        AppServices.Current.NotifyAccountsChanged();
        Refresh();
    }

    private void RemoveSelected()
    {
        var svc = Selected;
        if (svc == null || svc.Account.IsDemo) return;
        if (!Ui.Confirm(Loc.F("RemoveAccountConfirm", svc.Account.Email), this, MessageBoxImage.Warning)) return;
        _mailboxOwner = null;
        AppServices.Current.RemoveAccount(svc);
        Refresh();
    }
}

// =========================================================================================
// Automatic replies (Out of office)
// =========================================================================================
public sealed class AutoRepliesWindow : DialogWindow
{
    private readonly IMailService _svc;
    private readonly RadioButton _off = new(), _on = new();
    private readonly CheckBox _range = new();
    private readonly DatePicker _startDate = new() { Width = 140 }, _endDate = new() { Width = 140 };
    private readonly TextBox _startTime = new() { Width = 60, Height = 24 }, _endTime = new() { Width = 60, Height = 24 };
    private readonly HtmlEditor _inside = new() { Height = 200 };
    private readonly HtmlEditor _outside = new() { Height = 200 };
    private readonly CheckBox _outsideEnabled = new();
    private readonly RadioButton _contactsOnly = new(), _anyone = new() { IsChecked = true };
    private readonly StackPanel _panel;

    public AutoRepliesWindow(IMailService svc)
    {
        _svc = svc;
        Title = $"{Loc.T("AutoRepliesTitle")} – {svc.Account.Email}";
        _panel = new StackPanel { Margin = new Thickness(16, 12, 16, 12), Width = 640, IsEnabled = false };
        _off.Content = Loc.T("DontSendAutoReplies"); _off.GroupName = "oof"; _off.IsChecked = true;
        _on.Content = Loc.T("SendAutoReplies"); _on.GroupName = "oof";
        _panel.Children.Add(_off);
        _panel.Children.Add(_on);
        _range.Content = Loc.T("OnlyDuringRange");
        _range.Margin = new Thickness(20, 8, 0, 6);
        _panel.Children.Add(_range);
        var g = new Grid { Margin = new Thickness(40, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition());
        void Row(int r, string label, DatePicker d, TextBox t)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(d);
            t.Margin = new Thickness(8, 0, 0, 0);
            t.VerticalContentAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            Grid.SetRow(l, r); Grid.SetRow(sp, r); Grid.SetColumn(sp, 1);
            g.Children.Add(l); g.Children.Add(sp);
        }
        Row(0, Loc.T("StartTime2"), _startDate, _startTime);
        Row(1, Loc.T("EndTime2"), _endDate, _endTime);
        _panel.Children.Add(g);

        var tabs = new TabControl { Margin = new Thickness(0, 6, 0, 0) };
        var inside = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Child = _inside, Margin = new Thickness(6) };
        tabs.Items.Add(new TabItem { Header = Loc.T("InsideOrg"), Content = inside });
        var outsidePanel = new StackPanel { Margin = new Thickness(6) };
        _outsideEnabled.Content = Loc.T("AutoReplyOutside");
        outsidePanel.Children.Add(_outsideEnabled);
        var aud = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 4, 0, 6) };
        _contactsOnly.Content = Loc.T("MyContactsOnly"); _contactsOnly.GroupName = "aud"; _contactsOnly.Margin = new Thickness(0, 0, 16, 0);
        _anyone.Content = Loc.T("AnyoneOutside"); _anyone.GroupName = "aud";
        aud.Children.Add(_contactsOnly); aud.Children.Add(_anyone);
        outsidePanel.Children.Add(aud);
        outsidePanel.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Child = _outside });
        tabs.Items.Add(new TabItem { Header = Loc.T("OutsideOrg"), Content = outsidePanel });
        _panel.Children.Add(tabs);

        var root = new StackPanel();
        root.Children.Add(_panel);
        var btns = ButtonRow(MakeButton(Loc.T("Ok"), true, false, async () => { if (await SaveAsync()) DialogResult = true; }), MakeButton(Loc.T("Cancel"), false, true));
        btns.Margin = new Thickness(16, 0, 16, 14);
        root.Children.Add(btns);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        OofSettingsInfo? s = null;
        if (!await Ui.RunAsync(async () => s = await _svc.GetOofAsync(), this)) { Close(); return; }
        _on.IsChecked = s!.State != OofState.Disabled;
        _range.IsChecked = s.State == OofState.Scheduled;
        _startDate.SelectedDate = s.Start.Date; _startTime.Text = s.Start.ToString("HH:mm");
        _endDate.SelectedDate = s.End.Date; _endTime.Text = s.End.ToString("HH:mm");
        await _inside.SetHtmlAsync(HtmlUtil.BodyContent(s.InternalReplyHtml));
        await _outside.SetHtmlAsync(HtmlUtil.BodyContent(s.ExternalReplyHtml));
        _outsideEnabled.IsChecked = s.ExternalAudience != OofExternalAudience.None;
        _contactsOnly.IsChecked = s.ExternalAudience == OofExternalAudience.Known;
        _anyone.IsChecked = s.ExternalAudience != OofExternalAudience.Known;
        _panel.IsEnabled = true;
    }

    private async Task<bool> SaveAsync()
    {
        static DateTime Combine(DateTime? d, string t) => (d ?? DateTime.Today).Date + (TimeSpan.TryParse(t, out var ts) ? ts : TimeSpan.Zero);
        var s = new OofSettingsInfo
        {
            State = _on.IsChecked != true ? OofState.Disabled : _range.IsChecked == true ? OofState.Scheduled : OofState.Enabled,
            Start = Combine(_startDate.SelectedDate, _startTime.Text),
            End = Combine(_endDate.SelectedDate, _endTime.Text),
            InternalReplyHtml = $"<html><body>{await _inside.GetHtmlAsync()}</body></html>",
            ExternalReplyHtml = $"<html><body>{await _outside.GetHtmlAsync()}</body></html>",
            ExternalAudience = _outsideEnabled.IsChecked != true ? OofExternalAudience.None : _contactsOnly.IsChecked == true ? OofExternalAudience.Known : OofExternalAudience.All
        };
        if (s.State == OofState.Scheduled && s.End <= s.Start)
        {
            Ui.Error(Loc.Instance.IsGeorgian ? "დასრულების დრო უნდა იყოს დაწყების შემდეგ." : "The end time must be after the start time.", this);
            return false;
        }
        return await Ui.RunAsync(() => _svc.SetOofAsync(s), this);
    }
}
