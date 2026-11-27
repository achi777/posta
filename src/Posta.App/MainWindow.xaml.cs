using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.App.ViewModels;
using Posta.App.Views;
using Posta.App.Windows;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App;

public enum AppModule { Mail, Calendar, People, Tasks }

public partial class MainWindow
{
    private AppModule _module = AppModule.Mail;
    private bool _initialized;
    private RemindersWindow? _reminders;

    private static AppServices App => AppServices.Current;
    private AppSettings S => App.Settings;

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowPlacement();

        Folders.FolderSelected += async (_, node) => await OnFolderSelectedAsync(node);
        Folders.ItemsDropped += async (_, _) => await Mail.RefreshAsync(true);
        Folders.CleanUpRequested += async (_, node) => { if (Mail.Node?.Folder?.Id == node.Folder?.Id) await Mail.CleanUpAsync(true); };
        Mail.AllFoldersProvider = svc => Folders.FoldersOf(svc);
        Mail.StatusChanged += (_, _) => UpdateStatus();
        Mail.StatusMessage += (_, msg) => StatusSync.Text = msg;
        CalendarModule.StatusChanged += (_, _) => UpdateStatus();
        PeopleModule.StatusChanged += (_, _) => UpdateStatus();
        TasksModule.StatusChanged += (_, _) => UpdateStatus();
        CalendarNav.Attach(CalendarModule);
        PeopleNav.Attach(PeopleModule);
        TasksNav.Attach(TasksModule);

        MailActions.CalendarNavigationRequested += (_, date) =>
        {
            NavCalendar.IsChecked = true;
            CalendarModule.NavigateTo(date);
        };

        App.AccountsChanged += async (_, _) => await OnAccountsChangedAsync();
        App.NewMailArrived += (_, list) => OnNewMail(list);
        App.RemindersChanged += (_, _) => OnRemindersChanged();
        App.DataChanged += async (_, what) =>
        {
            if (what.StartsWith("items:") && Mail.Folder?.Id == what[6..]) await Mail.RefreshAsync(true);
        };
        foreach (var svc in App.Accounts) svc.StateChanged += (_, _) => Dispatcher.Invoke(UpdateStatus);

        Loc.Instance.LanguageChanged += (_, _) => { UpdateTitle(); UpdateStatus(); UpdateLanguageButtons(); UpdatePaneTitle(); TellMeBox.Tag = Loc.T("TellMe"); };
        TellMeBox.Tag = Loc.T("TellMe");

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Closing += (_, _) => SaveWindowPlacement();
        Closed += (_, _) => Application.Current.Shutdown();
    }

    // =====================================================================================
    // Startup
    // =====================================================================================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLanguageButtons();
        ApplyFolderPaneMode();
        ApplyToDoBar();
        ShowAvatarsCheck.IsChecked = S.ShowSenderPhotos;
        WorkOfflineButton.IsChecked = S.WorkOffline;
        ZoomSlider.Value = S.ReadingZoom;
        SetModule(AppModule.Mail);

        if (App.Accounts.Count == 0)
        {
            WelcomePanel.Visibility = Visibility.Visible;
            UpdateStatus();
            return;
        }
        await StartAsync();
    }

    private async Task StartAsync()
    {
        WelcomePanel.Visibility = Visibility.Collapsed;
        StatusConnection.Text = Loc.T("Connecting");
        await Folders.LoadAsync(useCacheFirst: true);
        if (Folders.SelectedNode == null) Folders.SelectInbox();
        await App.ConnectAllAsync(this);
        await Folders.LoadAsync();
        if (Folders.SelectedNode == null) Folders.SelectInbox();
        RefreshBackstage();
        _initialized = true;
        UpdateStatus();
        _ = CalendarModule.InitializeAsync();
        _ = CheckOofBannerAsync();
    }

    private async Task OnAccountsChangedAsync()
    {
        foreach (var svc in App.Accounts) { svc.StateChanged -= Svc_StateChanged; svc.StateChanged += Svc_StateChanged; }
        if (App.Accounts.Count == 0)
        {
            WelcomePanel.Visibility = Visibility.Visible;
            return;
        }
        WelcomePanel.Visibility = Visibility.Collapsed;
        await Folders.LoadAsync();
        if (Folders.SelectedNode == null) Folders.SelectInbox();
        RefreshBackstage();
        await CalendarModule.InitializeAsync();
        if (!_initialized) { _initialized = true; App.StartBackground(); }
    }

    private void UpdatePaneTitle() => LeftPaneTitle.Text = _module switch
    {
        AppModule.Calendar => Loc.T("Calendar"),
        AppModule.People => Loc.T("MyContacts"),
        AppModule.Tasks => Loc.T("Tasks"),
        _ => Loc.T("Folders")
    };

    private void Svc_StateChanged(object? sender, EventArgs e) => Dispatcher.Invoke(UpdateStatus);

    private async Task CheckOofBannerAsync()
    {
        var svc = App.DefaultAccount;
        if (svc == null || svc.State != ConnectionState.Connected) return;
        try
        {
            var oof = await svc.GetOofAsync();
            OofBar.Visibility = oof.State != OofState.Disabled ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    private async void OofTurnOff_Click(object sender, RoutedEventArgs e)
    {
        var svc = App.DefaultAccount;
        if (svc == null) return;
        if (await Ui.RunAsync(async () =>
            {
                var oof = await svc.GetOofAsync();
                oof.State = OofState.Disabled;
                await svc.SetOofAsync(oof);
            }, this))
            OofBar.Visibility = Visibility.Collapsed;
    }

    private void RestoreWindowPlacement()
    {
        if (!double.IsNaN(S.WindowLeft) && !double.IsNaN(S.WindowTop) &&
            S.WindowLeft > SystemParameters.VirtualScreenLeft - 50 && S.WindowTop > SystemParameters.VirtualScreenTop - 50)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = S.WindowLeft;
            Top = S.WindowTop;
        }
        Width = Math.Max(MinWidth, S.WindowWidth);
        Height = Math.Max(MinHeight, S.WindowHeight);
        if (S.WindowMaximized) WindowState = WindowState.Maximized;
        LeftPaneColumn.Width = new GridLength(Math.Clamp(S.FolderPaneWidth, 160, 500));
    }

    private void SaveWindowPlacement()
    {
        S.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            S.WindowLeft = Left; S.WindowTop = Top; S.WindowWidth = Width; S.WindowHeight = Height;
        }
        App.SettingsService.Save();
    }

    // =====================================================================================
    // Modules
    // =====================================================================================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<AppModule>(tag, out var m)) SetModule(m);
    }

    public void SetModule(AppModule m)
    {
        _module = m;
        Mail.Visibility = m == AppModule.Mail ? Visibility.Visible : Visibility.Collapsed;
        CalendarModule.Visibility = m == AppModule.Calendar ? Visibility.Visible : Visibility.Collapsed;
        PeopleModule.Visibility = m == AppModule.People ? Visibility.Visible : Visibility.Collapsed;
        TasksModule.Visibility = m == AppModule.Tasks ? Visibility.Visible : Visibility.Collapsed;
        Folders.Visibility = m == AppModule.Mail ? Visibility.Visible : Visibility.Collapsed;
        CalendarNav.Visibility = m == AppModule.Calendar ? Visibility.Visible : Visibility.Collapsed;
        PeopleNav.Visibility = m == AppModule.People ? Visibility.Visible : Visibility.Collapsed;
        TasksNav.Visibility = m == AppModule.Tasks ? Visibility.Visible : Visibility.Collapsed;

        var mail = m == AppModule.Mail ? Visibility.Visible : Visibility.Collapsed;
        var cal = m == AppModule.Calendar ? Visibility.Visible : Visibility.Collapsed;
        var ppl = m == AppModule.People ? Visibility.Visible : Visibility.Collapsed;
        var tsk = m == AppModule.Tasks ? Visibility.Visible : Visibility.Collapsed;
        MailNewGroup.Visibility = MailDeleteGroup.Visibility = MailRespondGroup.Visibility = MailQuickStepsGroup.Visibility =
            MailMoveGroup.Visibility = MailTagsGroup.Visibility = mail;
        FilterEmailButton.Visibility = mail;
        ViewMessagesGroup.Visibility = ViewArrangeGroup.Visibility = mail;
        CalNewGroup.Visibility = CalGoToGroup.Visibility = CalArrangeGroup.Visibility = CalManageGroup.Visibility = CalShareGroup.Visibility = cal;
        PeopleNewGroup.Visibility = PeopleDeleteGroup.Visibility = PeopleCommunicateGroup.Visibility = PeopleViewGroup.Visibility = ppl;
        TasksNewGroup.Visibility = TasksDeleteGroup.Visibility = TasksManageGroup.Visibility = TasksFollowUpGroup.Visibility = tsk;

        UpdatePaneTitle();
        switch (m)
        {
            case AppModule.Calendar:
                UpdateCalendarViewButtons();
                _ = CalendarModule.EnsureLoadedAsync();
                break;
            case AppModule.People:
                _ = PeopleModule.EnsureLoadedAsync();
                UpdatePeopleViewButtons();
                break;
            case AppModule.Tasks:
                _ = TasksModule.EnsureLoadedAsync();
                break;
        }
        var radio = m switch { AppModule.Calendar => NavCalendar, AppModule.People => NavPeople, AppModule.Tasks => NavTasks, _ => NavMail };
        if (radio.IsChecked != true) radio.IsChecked = true;
        if (Ribbon.SelectedTabIndex != 0 && Ribbon.SelectedTabItem != HomeTab) { }
        UpdateTitle();
        UpdateStatus();
    }

    private async Task OnFolderSelectedAsync(FolderNode node)
    {
        if (_module != AppModule.Mail) SetModule(AppModule.Mail);
        MinimizedTitle.Text = node.Title;
        await Mail.ShowFolderAsync(node);
        UpdateTitle();
        UpdateStatus();
    }

    private void UpdateTitle()
    {
        var app = Loc.T("AppName");
        Title = _module switch
        {
            AppModule.Mail when Mail.Node != null => $"{Mail.Node.Title} - {Mail.Service?.Account.Email} - {app}",
            AppModule.Calendar => $"{Loc.T("Calendar")} - {App.DefaultAccount?.Account.Email} - {app}",
            AppModule.People => $"{Loc.T("F_Contacts")} - {App.DefaultAccount?.Account.Email} - {app}",
            AppModule.Tasks => $"{Loc.T("F_Tasks")} - {App.DefaultAccount?.Account.Email} - {app}",
            _ => app
        };
    }

    private void UpdateStatus()
    {
        switch (_module)
        {
            case AppModule.Mail:
                // Like Outlook: while searching/filtering show the result count and "Filter applied".
                var narrowed = !string.IsNullOrWhiteSpace(Mail.SearchText) || Mail.Filter != MailFilter.All;
                StatusItems.Text = narrowed
                    ? Loc.F("Items", Mail.TotalCount) + "    " + Loc.T(string.IsNullOrWhiteSpace(Mail.SearchText) ? "FilterApplied" : "SearchResults")
                    : Loc.F("Items", Mail.Folder?.TotalCount ?? Mail.TotalCount);
                var unread = Mail.Folder?.UnreadCount ?? 0;
                StatusUnread.Text = unread > 0 ? Loc.F("UnreadCount", unread) : "";
                break;
            case AppModule.Calendar:
                StatusItems.Text = Loc.F("Items", CalendarModule.ItemCount);
                StatusUnread.Text = "";
                break;
            case AppModule.People:
                StatusItems.Text = Loc.F("Items", PeopleModule.ItemCount);
                StatusUnread.Text = "";
                break;
            case AppModule.Tasks:
                StatusItems.Text = Loc.F("Items", TasksModule.ItemCount);
                StatusUnread.Text = "";
                break;
        }

        var accounts = App.Accounts.ToList();
        if (S.WorkOffline) StatusConnection.Text = Loc.T("WorkingOffline");
        else if (accounts.Count == 0) StatusConnection.Text = "";
        else if (accounts.Any(a => a.State == ConnectionState.Connecting)) StatusConnection.Text = Loc.T("Connecting");
        else if (accounts.All(a => a.State == ConnectionState.Connected)) StatusConnection.Text = Loc.T("ConnectedTo");
        else if (accounts.Any(a => a.State == ConnectionState.NeedsPassword)) StatusConnection.Text = Loc.T("NeedPassword");
        else if (accounts.Any(a => a.State is ConnectionState.Error or ConnectionState.Offline))
        {
            var err = accounts.FirstOrDefault(a => a.LastError != null)?.LastError;
            StatusConnection.Text = Loc.T("Disconnected");
            StatusConnection.ToolTip = err;
        }
        if (!StatusSync.Text.StartsWith("⚠"))
            StatusSync.Text = Mail.IsLoading ? Loc.T("UpdatingFolder") : (accounts.Count > 0 && accounts.All(a => a.State == ConnectionState.Connected) ? Loc.T("AllFoldersUpToDate") : "");
    }

    // =====================================================================================
    // Keyboard shortcuts
    // =====================================================================================

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Backstage.IsOpen) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var inText = Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (ctrl && !shift)
        {
            switch (key)
            {
                case Key.D1: SetModule(AppModule.Mail); e.Handled = true; return;
                case Key.D2: SetModule(AppModule.Calendar); e.Handled = true; return;
                case Key.D3: SetModule(AppModule.People); e.Handled = true; return;
                case Key.D4: SetModule(AppModule.Tasks); e.Handled = true; return;
                case Key.N: NewItemForModule(); e.Handled = true; return;
                case Key.E: if (_module == AppModule.Mail) { Mail.FocusSearch(); e.Handled = true; } return;
            }
            if (!inText && _module == AppModule.Mail)
            {
                switch (key)
                {
                    case Key.R: await Mail.ReplyAsync(ComposeMode.Reply); e.Handled = true; return;
                    case Key.F: await Mail.ReplyAsync(ComposeMode.Forward); e.Handled = true; return;
                    case Key.Q: await Mail.MarkReadAsync(true); e.Handled = true; return;
                    case Key.U: await Mail.MarkReadAsync(false); e.Handled = true; return;
                    case Key.P: Mail.Print(); e.Handled = true; return;
                    case Key.A: Mail.SelectAll(); e.Handled = true; return;
                }
            }
        }
        if (ctrl && shift)
        {
            switch (key)
            {
                case Key.R when _module == AppModule.Mail && !inText: await Mail.ReplyAsync(ComposeMode.ReplyAll); e.Handled = true; return;
                case Key.M: MailActions.ComposeNew(CurrentService()); e.Handled = true; return;
                case Key.A: CalendarModule.NewAppointment(false, false); e.Handled = true; return;
                case Key.Q: CalendarModule.NewAppointment(true, false); e.Handled = true; return;
                case Key.C: PeopleModule.NewContact(false); e.Handled = true; return;
                case Key.L: PeopleModule.NewContact(true); e.Handled = true; return;
                case Key.K: TasksModule.NewTask(); e.Handled = true; return;
                case Key.B: AddressBook_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.V when _module == AppModule.Mail: await Mail.MoveToOtherAsync(false); e.Handled = true; return;
            }
        }
        if (key == Key.F9) { SendReceive_Click(this, new RoutedEventArgs()); e.Handled = true; }
        if (key == Key.F3 || (key == Key.F && ctrl && shift)) { if (_module == AppModule.Mail) Mail.FocusSearch(); e.Handled = true; }
        if (key == Key.Delete && !inText)
        {
            switch (_module)
            {
                case AppModule.Calendar: await CalendarModule.DeleteSelectedAsync(); e.Handled = true; break;
                case AppModule.People: await PeopleModule.DeleteSelectedAsync(); e.Handled = true; break;
                case AppModule.Tasks: await TasksModule.DeleteSelectedAsync(); e.Handled = true; break;
            }
        }
    }

    private IMailService? CurrentService() =>
        _module == AppModule.Mail ? Mail.Service ?? App.DefaultAccount : App.DefaultAccount;

    private void NewItemForModule()
    {
        switch (_module)
        {
            case AppModule.Calendar: CalendarModule.NewAppointment(false, false); break;
            case AppModule.People: PeopleModule.NewContact(false); break;
            case AppModule.Tasks: TasksModule.NewTask(); break;
            default: MailActions.ComposeNew(CurrentService()); break;
        }
    }

    // =====================================================================================
    // New items
    // =====================================================================================

    private void NewEmail_Click(object sender, RoutedEventArgs e) => MailActions.ComposeNew(CurrentService());
    private void NewAppointment_Click(object sender, RoutedEventArgs e) => CalendarModule.NewAppointment(false, false);
    private void NewMeeting_Click(object sender, RoutedEventArgs e) => CalendarModule.NewAppointment(true, false);
    private void NewAllDay_Click(object sender, RoutedEventArgs e) => CalendarModule.NewAppointment(false, true);
    private void NewContact_Click(object sender, RoutedEventArgs e) => PeopleModule.NewContact(false);
    private void NewContactGroup_Click(object sender, RoutedEventArgs e) => PeopleModule.NewContact(true);
    private void NewTask_Click(object sender, RoutedEventArgs e) => TasksModule.NewTask();

    // =====================================================================================
    // Mail commands
    // =====================================================================================

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        switch (_module)
        {
            case AppModule.Calendar: await CalendarModule.DeleteSelectedAsync(); break;
            case AppModule.People: await PeopleModule.DeleteSelectedAsync(); break;
            case AppModule.Tasks: await TasksModule.DeleteSelectedAsync(); break;
            default: await Mail.DeleteAsync(false); break;
        }
    }

    private async void Archive_Click(object sender, RoutedEventArgs e) => await Mail.ArchiveAsync();
    private async void Ignore_Click(object sender, RoutedEventArgs e) => await Mail.IgnoreAsync();
    private async void CleanUpConversation_Click(object sender, RoutedEventArgs e) => await Mail.CleanUpAsync(false);
    private async void CleanUpFolder_Click(object sender, RoutedEventArgs e) => await Mail.CleanUpAsync(true);
    private async void BlockSender_Click(object sender, RoutedEventArgs e) => await Mail.JunkAsync(true);
    private async void NotJunk_Click(object sender, RoutedEventArgs e) => await Mail.JunkAsync(false);
    private async void Reply_Click(object sender, RoutedEventArgs e) => await Mail.ReplyAsync(ComposeMode.Reply);
    private async void ReplyAll_Click(object sender, RoutedEventArgs e) => await Mail.ReplyAsync(ComposeMode.ReplyAll);
    private async void Forward_Click(object sender, RoutedEventArgs e) => await Mail.ReplyAsync(ComposeMode.Forward);
    private async void ReplyWithMeeting_Click(object sender, RoutedEventArgs e) => await Mail.ReplyWithMeetingAsync();
    private async void ForwardAsAttachment_Click(object sender, RoutedEventArgs e) => await Mail.ForwardAsAttachmentAsync();
    private async void ToggleRead_Click(object sender, RoutedEventArgs e) => await Mail.ToggleReadAsync();

    private async void ReplyDelete_Click(object sender, RoutedEventArgs e)
    {
        var item = Mail.Current;
        if (item == null) return;
        await Mail.ReplyAsync(ComposeMode.Reply);
        await Mail.DeleteItemsAsync(new[] { item }, false);
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        await Mail.FlagSelectedAsync(MailView.FlagKind.Complete);
        await Mail.MarkReadAsync(true);
    }

    private void MoveDropDown_Opened(object? sender, EventArgs e)
    {
        MoveDropDown.Items.Clear();
        if (Mail.Service == null) return;
        foreach (var f in Folders.FoldersOf(Mail.Service).Where(f => f.IsMailFolder && f.Kind is FolderKind.Inbox or FolderKind.Archive or FolderKind.Generic && f.Id != Mail.Folder?.Id).Take(10))
        {
            var target = f;
            var mi = new Fluent.MenuItem { Header = FolderVisuals.LocalizedName(f), Icon = GlyphFactory.Create(G.Folder, GlyphFactory.DefaultBrush, 16) };
            mi.Click += async (_, _) => await Mail.MoveToAsync(target.Id);
            MoveDropDown.Items.Add(mi);
        }
        MoveDropDown.Items.Add(new Separator());
        var other = new Fluent.MenuItem { Header = Loc.T("OtherFolder"), Icon = GlyphFactory.Create(G.Move, GlyphFactory.DefaultBrush, 16) };
        other.Click += async (_, _) => await Mail.MoveToOtherAsync(false);
        MoveDropDown.Items.Add(other);
        var copy = new Fluent.MenuItem { Header = Loc.T("CopyToFolder"), Icon = GlyphFactory.Create(G.Copy, GlyphFactory.DefaultBrush, 16) };
        copy.Click += async (_, _) => await Mail.MoveToOtherAsync(true);
        MoveDropDown.Items.Add(copy);
    }

    private void CategorizeDropDown_Opened(object? sender, EventArgs e)
    {
        CategorizeDropDown.Items.Clear();
        foreach (var item in Mail.BuildCategoryItems(Mail.Selected)) CategorizeDropDown.Items.Add(item);
    }

    private void FollowUpDropDown_Opened(object? sender, EventArgs e)
    {
        FollowUpDropDown.Items.Clear();
        foreach (var item in Mail.BuildFollowUpItems(Mail.Selected)) FollowUpDropDown.Items.Add(item);
    }

    private void CreateRule_Click(object sender, RoutedEventArgs e)
    {
        var svc = CurrentService();
        if (svc == null) return;
        var rule = new RuleInfo { Name = Mail.Current?.Subject ?? "" };
        if (Mail.Current != null) rule.FromAddresses.Add(Mail.Current.From.Address);
        var w = new RuleEditWindow(svc, rule, Folders.FoldersOf(svc).ToList()) { Owner = this };
        if (w.ShowDialog() != true) return;
        _ = Ui.RunAsync(async () =>
        {
            var rules = (await svc.GetRulesAsync()).ToList();
            rules.Insert(0, w.Rule);
            await svc.SaveRulesAsync(rules, Array.Empty<string>());
        }, this);
    }

    private void Rules_Click(object sender, RoutedEventArgs e)
    {
        Backstage.IsOpen = false;
        var svc = SelectedBackstageAccount() ?? CurrentService();
        if (svc != null) RulesWindow.ShowFor(svc, this);
    }

    private async void FilterEmail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<MailFilter>(tag, out var f))
        {
            SetModule(AppModule.Mail);
            await Mail.SetFilterAsync(f);
        }
    }

    private async void SearchPeople_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = SearchPeopleBox.Text.Trim();
        if (text.Length == 0) return;
        var svc = CurrentService();
        if (svc == null) return;
        IReadOnlyList<ResolvedName>? res = null;
        if (!await Ui.RunAsync(async () => res = await svc.ResolveNamesAsync(text), this)) return;
        if (res!.Count == 0) { Ui.Info(Loc.F("UnresolvedRecipient", text), this); return; }
        var pick = res.Count == 1 ? res[0] : AddressBookWindow.PickOne(svc, this, text);
        if (pick != null) new ContactCardWindow(svc, pick) { Owner = this }.Show();
    }

    private void AddressBook_Click(object sender, RoutedEventArgs e)
    {
        var svc = CurrentService();
        if (svc == null) return;
        var w = new AddressBookWindow(svc, AddressBookMode.Browse) { Owner = this };
        w.ShowDialog();
    }

    // =====================================================================================
    // Send / Receive
    // =====================================================================================

    private async void SendReceive_Click(object sender, RoutedEventArgs e)
    {
        StatusSync.Text = Loc.T("UpdatingFolder");
        foreach (var svc in App.Accounts.Where(a => a.State != ConnectionState.Connected).ToList())
            await App.ConnectAccountAsync(svc, this, true);
        await App.PollAsync();
        await Folders.LoadAsync();
        await RefreshCurrentModuleAsync();
        UpdateStatus();
    }

    private async void UpdateFolder_Click(object sender, RoutedEventArgs e) => await RefreshCurrentModuleAsync();

    private async void PrepareOfflineFolder_Click(object sender, RoutedEventArgs e)
    {
        if (S.WorkOffline || _module != AppModule.Mail || Mail.Service is not { State: ConnectionState.Connected } service ||
            Mail.Folder is not { MailboxEmail: null, IsMailFolder: true, Kind: not FolderKind.Root } folder)
        {
            Ui.Info(Loc.T("OfflinePrepareNeedsConnection"), this);
            return;
        }
        if (sender is not System.Windows.Controls.Control button) return;
        button.IsEnabled = false;
        StatusSync.Text = Loc.T("OfflinePreparingFolder");
        try
        {
            (int Saved, int Skipped) result = default;
            if (await Ui.RunAsync(async () => result = await OpenedMessageStore.PrepareFolderAsync(service, folder), this))
                Ui.Info(Loc.F("OfflinePrepareComplete", result.Saved, result.Skipped), this);
        }
        finally
        {
            button.IsEnabled = true;
            UpdateStatus();
        }
    }

    private async Task RefreshCurrentModuleAsync()
    {
        switch (_module)
        {
            case AppModule.Calendar: await CalendarModule.ReloadAsync(); break;
            case AppModule.People: await PeopleModule.ReloadAsync(); break;
            case AppModule.Tasks: await TasksModule.ReloadAsync(); break;
            default: await Mail.RefreshAsync(); break;
        }
    }

    private async void WorkOffline_Click(object sender, RoutedEventArgs e)
    {
        S.WorkOffline = WorkOfflineButton.IsChecked == true;
        App.SettingsService.Save();
        if (S.WorkOffline)
        {
            foreach (var service in App.Accounts.OfType<Posta.Mapi.NativeMapiMailService>().ToList())
                await service.GoOfflineAsync();
        }
        else
        {
            await App.ConnectAllAsync(this);
            await Mail.RefreshAsync();
        }
        UpdateStatus();
    }

    // =====================================================================================
    // Folder tab
    // =====================================================================================

    private async void NewFolder_Click(object sender, RoutedEventArgs e) => await Folders.NewFolderAsync(Folders.SelectedNode);
    private async void RenameFolder_Click(object sender, RoutedEventArgs e) => await Folders.RenameAsync(null);
    private async void CopyFolder_Click(object sender, RoutedEventArgs e) => await Folders.MoveFolderAsync(null, true);
    private async void MoveFolder_Click(object sender, RoutedEventArgs e) => await Folders.MoveFolderAsync(null, false);
    private async void DeleteFolder_Click(object sender, RoutedEventArgs e) => await Folders.DeleteFolderAsync(null);
    private async void MarkAllRead_Click(object sender, RoutedEventArgs e) => await Folders.MarkAllReadAsync(null);
    private async void DeleteAll_Click(object sender, RoutedEventArgs e) => await Folders.EmptyAsync(null);
    private void ToggleFavorite_Click(object sender, RoutedEventArgs e) => Folders.ToggleFavorite(null);

    private void FolderPermissions_Click(object sender, RoutedEventArgs e)
    {
        if (_module == AppModule.Calendar) { CalendarPermissions_Click(sender, e); return; }
        var n = Folders.SelectedNode;
        if (n?.Folder == null || n.Service == null) return;
        PermissionsWindow.ShowFor(n.Service, n.Folder.Id, n.Title, this);
    }

    // =====================================================================================
    // View tab
    // =====================================================================================

    private async void ResetView_Click(object sender, RoutedEventArgs e)
    {
        S.ReadingPane = ReadingPanePosition.Right;
        S.MessageListWidth = 420;
        S.ShowSenderPhotos = true;
        S.MessagePreviewLines = 1;
        S.FolderPane = FolderPaneMode.Normal;
        App.SettingsService.Save();
        ApplyFolderPaneMode();
        Mail.ApplyLayout();
        await Mail.SetSortAsync(SortField.Date, true);
    }

    private void ShowAvatars_Click(object sender, RoutedEventArgs e)
    {
        S.ShowSenderPhotos = ShowAvatarsCheck.IsChecked == true;
        App.SettingsService.Save();
        Mail.ApplyLayout();
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t }) S.MessagePreviewLines = int.Parse(t);
        App.SettingsService.Save();
        Mail.ApplyLayout();
    }

    private async void ArrangeBy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && Enum.TryParse<SortField>(t, out var f)) await Mail.SetSortAsync(f);
    }

    private async void ReverseSort_Click(object sender, RoutedEventArgs e) => await Mail.ReverseSortAsync();

    private void FolderPaneMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && Enum.TryParse<FolderPaneMode>(t, out var m))
        {
            S.FolderPane = m;
            App.SettingsService.Save();
            ApplyFolderPaneMode();
        }
    }

    private void MinimizeFolderPane_Click(object sender, RoutedEventArgs e)
    {
        S.FolderPane = FolderPaneMode.Minimized;
        ApplyFolderPaneMode();
    }

    private void RestoreFolderPane_Click(object sender, MouseButtonEventArgs e)
    {
        S.FolderPane = FolderPaneMode.Normal;
        ApplyFolderPaneMode();
    }

    private void ApplyFolderPaneMode()
    {
        switch (S.FolderPane)
        {
            case FolderPaneMode.Minimized:
                LeftPane.Visibility = Visibility.Collapsed;
                MinimizedPane.Visibility = Visibility.Visible;
                LeftPaneColumn.Width = new GridLength(32);
                LeftSplitter.Visibility = Visibility.Collapsed;
                break;
            case FolderPaneMode.Off:
                LeftPane.Visibility = Visibility.Collapsed;
                MinimizedPane.Visibility = Visibility.Collapsed;
                LeftPaneColumn.Width = new GridLength(0);
                LeftSplitter.Visibility = Visibility.Collapsed;
                break;
            default:
                LeftPane.Visibility = Visibility.Visible;
                MinimizedPane.Visibility = Visibility.Collapsed;
                LeftPaneColumn.Width = new GridLength(Math.Clamp(S.FolderPaneWidth, 160, 500));
                LeftSplitter.Visibility = Visibility.Visible;
                break;
        }
    }

    private void LeftSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        S.FolderPaneWidth = LeftPaneColumn.ActualWidth;
        App.SettingsService.Save();
    }

    private void ReadingPaneMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && Enum.TryParse<ReadingPanePosition>(t, out var p))
        {
            S.ReadingPane = p;
            App.SettingsService.Save();
            Mail.ApplyLayout();
            PeopleModule.ApplyLayout();
            TasksModule.ApplyLayout();
        }
    }

    private void ToDoBar_Click(object sender, RoutedEventArgs e)
    {
        S.ShowToDoBar = ToDoBarToggle.IsChecked == true;
        App.SettingsService.Save();
        ApplyToDoBar();
    }

    private void ApplyToDoBar()
    {
        ToDoBarToggle.IsChecked = S.ShowToDoBar;
        ToDoBarHost.Visibility = S.ShowToDoBar ? Visibility.Visible : Visibility.Collapsed;
        ToDoColumn.Width = S.ShowToDoBar ? new GridLength(260) : new GridLength(0);
        if (S.ShowToDoBar) _ = ToDoBar.LoadAsync();
    }

    private void NormalView_Click(object sender, RoutedEventArgs e)
    {
        S.FolderPane = FolderPaneMode.Normal;
        S.ReadingPane = ReadingPanePosition.Right;
        ApplyFolderPaneMode();
        Mail.ApplyLayout();
    }

    private void ReadingView_Click(object sender, RoutedEventArgs e)
    {
        S.FolderPane = FolderPaneMode.Minimized;
        S.ReadingPane = ReadingPanePosition.Right;
        ApplyFolderPaneMode();
        Mail.ApplyLayout();
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomText == null) return;
        var z = Math.Round(ZoomSlider.Value, 2);
        ZoomText.Text = $"{z * 100:0}%";
        if (!IsLoaded) return;
        S.ReadingZoom = z;
        Mail.ReadingPane.Zoom = z;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = Math.Min(2, ZoomSlider.Value + 0.1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = Math.Max(0.5, ZoomSlider.Value - 0.1);

    private void CloseAllItems_Click(object sender, RoutedEventArgs e)
    {
        foreach (var w in Application.Current.Windows.OfType<Window>().Where(w => w != this && w is not RemindersWindow).ToList()) w.Close();
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string code }) return;
        S.Language = code;
        App.SettingsService.Save();
        Loc.Instance.SetLanguage(code);
        UpdateLanguageButtons();
    }

    private void UpdateLanguageButtons()
    {
        LangKaButton.IsChecked = Loc.Instance.IsGeorgian;
        LangEnButton.IsChecked = !Loc.Instance.IsGeorgian;
    }

    // =====================================================================================
    // Calendar commands
    // =====================================================================================

    private void CalToday_Click(object sender, RoutedEventArgs e) => CalendarModule.GoToday();
    private void CalNext7_Click(object sender, RoutedEventArgs e) => CalendarModule.ShowNextDays(7);

    private void CalView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && Enum.TryParse<CalendarViewMode>(t, out var m))
        {
            CalendarModule.SetViewMode(m);
            UpdateCalendarViewButtons();
        }
    }

    private void UpdateCalendarViewButtons()
    {
        var m = CalendarModule.ViewMode;
        CalDayBtn.IsChecked = m == CalendarViewMode.Day;
        CalWorkWeekBtn.IsChecked = m == CalendarViewMode.WorkWeek;
        CalWeekBtn.IsChecked = m == CalendarViewMode.Week;
        CalMonthBtn.IsChecked = m == CalendarViewMode.Month;
        CalScheduleBtn.IsChecked = m == CalendarViewMode.Schedule;
    }

    private async void OpenSharedCalendar_Click(object sender, RoutedEventArgs e)
    {
        Backstage.IsOpen = false;
        SetModule(AppModule.Calendar);
        await CalendarNav.OpenSharedCalendarAsync(false);
    }

    private async void OpenSharedCalendarFromAddressBook_Click(object sender, RoutedEventArgs e)
    {
        SetModule(AppModule.Calendar);
        await CalendarNav.OpenSharedCalendarAsync(true);
    }

    private async void NewCalendar_Click(object sender, RoutedEventArgs e) => await CalendarNav.CreateCalendarAsync();

    private void CalendarGroups_Click(object sender, RoutedEventArgs e) => CalendarNav.ShowGroupsMenu(sender as UIElement);

    private void EmailCalendar_Click(object sender, RoutedEventArgs e) => CalendarModule.EmailCalendar();

    private void ShareCalendar_Click(object sender, RoutedEventArgs e) => CalendarNav.ShareSelectedCalendar();

    private void CalendarPermissions_Click(object sender, RoutedEventArgs e) => CalendarNav.ShowPermissionsForSelected();

    private async void OpenOtherUsersFolder_Click(object sender, RoutedEventArgs e)
    {
        Backstage.IsOpen = false;
        var svc = App.DefaultAccount;
        if (svc == null) return;
        var pick = AddressBookWindow.PickOne(svc, this, null);
        if (pick == null || string.IsNullOrWhiteSpace(pick.Email)) return;
        if (pick.Email.Equals(svc.Account.Email, StringComparison.OrdinalIgnoreCase)) return;
        if (svc.Account.AdditionalMailboxes.Contains(pick.Email, StringComparer.OrdinalIgnoreCase)) return;
        if (svc.Account.UseNativeMapi && !await Ui.RunAsync(() => svc.GetFolderTreeAsync(pick.Email), this)) return;
        svc.Account.AdditionalMailboxes.Add(pick.Email);
        App.NotifyAccountsChanged();
        await Folders.LoadAsync();
    }

    // =====================================================================================
    // People & tasks commands
    // =====================================================================================

    private void ContactEmail_Click(object sender, RoutedEventArgs e) => PeopleModule.EmailSelected();
    private void ContactMeeting_Click(object sender, RoutedEventArgs e) => PeopleModule.MeetingWithSelected();

    private void PeopleView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && Enum.TryParse<PeopleViewMode>(t, out var m))
        {
            PeopleModule.SetViewMode(m);
            UpdatePeopleViewButtons();
        }
    }

    private void UpdatePeopleViewButtons()
    {
        var m = S.PeopleView;
        PeopleViewPeople.IsChecked = m == PeopleViewMode.People;
        PeopleViewCard.IsChecked = m is PeopleViewMode.BusinessCard or PeopleViewMode.Card;
        PeopleViewPhone.IsChecked = m == PeopleViewMode.Phone;
        PeopleViewList.IsChecked = m == PeopleViewMode.List;
    }

    private async void TaskComplete_Click(object sender, RoutedEventArgs e) => await TasksModule.MarkCompleteAsync();

    private async void TaskDue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t }) await TasksModule.SetDueAsync(int.Parse(t));
    }

    // =====================================================================================
    // Backstage
    // =====================================================================================

    private void RefreshBackstage()
    {
        BackstageAccountBox.Items.Clear();
        foreach (var a in App.Accounts)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Ui.GlyphText(G.Mail, 18, GlyphFactory.Blue));
            var t = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            t.Children.Add(new TextBlock { Text = a.Account.Email, FontSize = 13 });
            t.Children.Add(new TextBlock { Text = Loc.T("Exchange"), FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray });
            sp.Children.Add(t);
            BackstageAccountBox.Items.Add(new ComboBoxItem { Content = sp, Tag = a });
        }
        if (BackstageAccountBox.Items.Count > 0) BackstageAccountBox.SelectedIndex = 0;
        var def = App.DefaultAccount;
        AboutUser.Text = def?.UserDisplayName ?? def?.Account.DisplayName ?? "";
        AboutEmail.Text = def?.Account.Email ?? "";
        AboutVersion.Text = Loc.F("Version", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0");
        AboutServer.Text = def?.ServerVersion != null ? Loc.F("ServerVersion", def.ServerVersion) : "";
    }

    private IMailService? SelectedBackstageAccount() => (BackstageAccountBox.SelectedItem as ComboBoxItem)?.Tag as IMailService;

    private void BackstageAccountBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var svc = SelectedBackstageAccount();
        BackstageServerInfo.Text = svc == null ? "" :
            $"{svc.Account.ServerUrl}" + (svc.ServerVersion != null ? "  ·  " + Loc.F("ServerVersion", svc.ServerVersion) : "");
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        Backstage.IsOpen = false;
        var w = new AddAccountWindow { Owner = this };
        if (w.ShowDialog() == true)
        {
            if (WelcomePanel.Visibility == Visibility.Visible)
            {
                WelcomePanel.Visibility = Visibility.Collapsed;
            }
            await OnAccountsChangedAsync();
        }
    }

    private void TryDemo_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        Process.Start(new ProcessStartInfo(exe, "--demo") { UseShellExecute = false });
        Close();
    }

    private void AccountSettings_Click(object sender, RoutedEventArgs e)
    {
        Backstage.IsOpen = false;
        new AccountSettingsWindow { Owner = this }.ShowDialog();
        RefreshBackstage();
    }

    private void AutoReplies_Click(object sender, RoutedEventArgs e)
    {
        Backstage.IsOpen = false;
        var svc = SelectedBackstageAccount() ?? App.DefaultAccount;
        if (svc == null) return;
        var w = new AutoRepliesWindow(svc) { Owner = this };
        w.ShowDialog();
        _ = CheckOofBannerAsync();
    }

    private void CleanupTools_Click(object sender, RoutedEventArgs e)
    {
        var svc = SelectedBackstageAccount() ?? App.DefaultAccount;
        if (svc == null) return;
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Menu(Loc.T("EmptyDeletedItems"), async () =>
        {
            var node = Folders.AllNodes().FirstOrDefault(n => n.Service == svc && n.Folder?.Kind == FolderKind.DeletedItems && n.MailboxEmail == null);
            if (node != null) await Folders.EmptyAsync(node);
        }, G.Delete));
        menu.PlacementTarget = CleanupToolsButton;
        menu.IsOpen = true;
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        Backstage.IsOpen = false;
        await Mail.SaveAsAsync();
    }

    private async void SaveAttachments_Click(object sender, RoutedEventArgs e)
    {
        Backstage.IsOpen = false;
        var m = Mail.ReadingPane.Message;
        var svc = Mail.ReadingPane.Service;
        if (m != null && svc != null) await MailActions.SaveAllAttachmentsAsync(svc, m, this);
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        Backstage.IsOpen = false;
        switch (_module)
        {
            case AppModule.Calendar: CalendarModule.Print(); break;
            case AppModule.People: PeopleModule.Print(); break;
            default: Mail.Print(); break;
        }
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        Backstage.IsOpen = false;
        var w = new OptionsWindow { Owner = this };
        if (w.ShowDialog() == true)
        {
            Mail.ApplyLayout();
            ApplyFolderPaneMode();
            _ = CalendarModule.ReloadAsync();
            UpdateLanguageButtons();
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        Backstage.IsOpen = true;
        RefreshBackstage();
        BackstageTabs.SelectedItem = AboutTab;
    }

    private void TellMeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var q = TellMeBox.Text.Trim().ToLowerInvariant();
        if (q.Length == 0) return;
        bool Has(params string[] words) => words.Any(w => q.Contains(w));
        var raw = TellMeBox.Text.Trim();
        TellMeBox.Text = "";
        if (Has("signature", "ხელმოწ")) new SignaturesWindow { Owner = this }.ShowDialog();
        else if (Has("option", "პარამეტ", "setting")) Options_Click(sender, e);
        else if (Has("rule", "წეს")) Rules_Click(sender, e);
        else if (Has("auto", "oof", "out of office", "ავტომატ", "შვებულ")) AutoReplies_Click(sender, e);
        else if (Has("permission", "ნებართვ")) { SetModule(AppModule.Calendar); CalendarPermissions_Click(sender, e); }
        else if (Has("share", "გაზიარ")) { SetModule(AppModule.Calendar); CalendarNav.ShareSelectedCalendar(); }
        else if (Has("calendar", "კალენდ")) SetModule(AppModule.Calendar);
        else if (Has("contact", "კონტაქ", "people", "ხალხ")) SetModule(AppModule.People);
        else if (Has("task", "ამოცან")) SetModule(AppModule.Tasks);
        else if (Has("account", "ანგარიშ")) AccountSettings_Click(sender, e);
        else if (Has("new mail", "email", "ელფოსტ", "წერილ")) MailActions.ComposeNew(CurrentService());
        else if (Has("meeting", "შეხვედრ")) CalendarModule.NewAppointment(true, false);
        else if (Has("language", "ენა", "english", "ქართ")) Language_Click(Loc.Instance.IsGeorgian ? LangEnButton : LangKaButton, e);
        else
        {
            SetModule(AppModule.Mail);
            _ = Mail.SearchForAsync(raw);
        }
        TellMeBox.Text = "";
    }

    // =====================================================================================
    // Notifications & reminders
    // =====================================================================================

    private void OnNewMail(IReadOnlyList<NewMailNotification> list)
    {
        _ = Folders.LoadAsync();
        if (Mail.Folder?.Kind == FolderKind.Inbox) _ = Mail.RefreshAsync(true);
        if (S.ShowDesktopAlert)
        {
            foreach (var n in list.Take(3)) DesktopAlertWindow.Show(n, this);
        }
        if (S.ShowEnvelopeInTaskbar)
        {
            TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
            TaskbarItemInfo.Overlay = (System.Windows.Media.ImageSource)GlyphFactory.Create(G.MailFill, GlyphFactory.Blue, 16);
        }
        if (_module == AppModule.Tasks || S.ShowToDoBar) _ = ToDoBar.LoadAsync();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (TaskbarItemInfo != null) TaskbarItemInfo.Overlay = null;
    }

    private void OnRemindersChanged()
    {
        var count = App.ActiveReminders.Count;
        StatusReminders.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusRemindersText.Text = Loc.F("Reminders", count);
        if (count > 0 && S.ShowReminders) ShowReminders();
        else _reminders?.Refresh();
    }

    private void ShowReminders()
    {
        if (_reminders == null || !_reminders.IsLoaded)
        {
            _reminders = new RemindersWindow { Owner = this };
            _reminders.Closed += (_, _) => _reminders = null;
            _reminders.Show();
        }
        else
        {
            _reminders.Refresh();
            _reminders.Activate();
        }
    }

    private void RemindersWindow_Click(object sender, RoutedEventArgs e) => ShowReminders();
}
