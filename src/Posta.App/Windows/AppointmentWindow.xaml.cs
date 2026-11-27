using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Posta.App.Controls;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.App.Views;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Windows;

public partial class AppointmentWindow
{
    private readonly IMailService _svc;
    private readonly CalendarSource? _source;
    private readonly CalendarEvent _ev;
    private readonly EditScope _scope;
    private readonly bool _readOnly;
    private readonly bool _isNew;
    private bool _isMeeting;
    private bool _attendeeView;
    private bool _saved;
    private bool _closingConfirmed;
    private bool _persisting;
    private Importance _importance;
    private RecurrenceInfo? _recurrence;
    private readonly List<AttachmentInfo> _attachments = new();
    private static readonly int[] ReminderChoices = { -1, 0, 5, 10, 15, 30, 60, 120, 180, 240, 480, 720, 1080, 1440, 2880, 4320, 10080, 20160 };

    private AppointmentWindow(IMailService svc, CalendarSource? source, CalendarEvent ev, bool meeting, EditScope scope, bool readOnly)
    {
        InitializeComponent();
        _svc = svc;
        _source = source;
        _ev = ev;
        _scope = scope;
        _isNew = string.IsNullOrEmpty(ev.Id);
        _readOnly = readOnly || ev.CanEditItem == false || (_isNew && source != null && !source.CanCreate);
        _isMeeting = meeting || ev.Attendees.Any(a => a.Type != AttendeeType.Organizer);
        _attendeeView = !_isNew && ev.IsMeeting && !ev.IsOrganizer;
        _importance = ev.Importance;
        _recurrence = ev.Recurrence?.Clone();
        if (_scope == EditScope.Single && ev.IsOccurrence) _recurrence = null;
        _attachments.AddRange(ev.Attachments);

        RequiredBox.Service = OptionalBox.Service = svc;
        RequiredBox.SetRecipients(ev.Attendees.Where(a => a.Type == AttendeeType.Required).Select(a => a.ToRecipient()));
        OptionalBox.SetRecipients(ev.Attendees.Where(a => a.Type is AttendeeType.Optional or AttendeeType.Resource).Select(a => a.ToRecipient()));
        TitleBox.Text = ev.Subject;
        LocationBox.Text = ev.Location;
        FillTimes(StartTime);
        FillTimes(EndTime);
        AllDayBox.IsChecked = ev.IsAllDay;
        SetDateTime(ev.Start, ev.IsAllDay ? ev.End.AddDays(-1) : ev.End);
        PrivateBtn.IsChecked = ev.IsPrivate;
        RequestResponsesItem.IsChecked = ev.ResponseRequested;
        AllowProposeItem.IsChecked = ev.AllowNewTimeProposal;

        foreach (var (sa, key) in new[] { (ShowAs.Free, "Free"), (ShowAs.WorkingElsewhere, "WorkingElsewhere"), (ShowAs.Tentative, "Tentative"), (ShowAs.Busy, "Busy"), (ShowAs.OutOfOffice, "OutOfOffice") })
            ShowAsBox.Items.Add(new ComboBoxItem { Content = Loc.T(key), Tag = sa });
        ShowAsBox.SelectedItem = ShowAsBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (ShowAs)i.Tag == ev.ShowAs) ?? ShowAsBox.Items[3];
        foreach (var m in ReminderChoices)
            ReminderBox.Items.Add(new ComboBoxItem { Content = ReminderText(m), Tag = m });
        var rem = ev.ReminderSet ? ev.ReminderMinutes : -1;
        ReminderBox.SelectedItem = ReminderBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == rem) ?? AddReminderChoice(rem);

        Editor.DefaultFont = AppServices.Current.Settings.ComposeFontFamily;
        Editor.DefaultSizePt = AppServices.Current.Settings.ComposeFontSizePt;
        _ = Editor.SetHtmlAsync(string.IsNullOrEmpty(ev.BodyHtml) ? "<div><br></div>" : HtmlUtil.BodyContent(HtmlUtil.Sanitize(ev.BodyHtml)));

        Scheduler.Service = svc;
        Scheduler.TimeChanged += (_, range) => SetDateTime(range.start, range.end);

        UpdateMode();
        UpdateImportance();
        UpdateRecurrenceText();
        RefreshAttachments();
        UpdateTitle();

        Closing += OnClosing;
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    public static void OpenNew(IMailService svc, CalendarSource? source, CalendarEvent ev, bool meeting, bool openRecurrence = false)
    {
        var w = new AppointmentWindow(svc, source, ev, meeting, EditScope.Single, false);
        w.Show();
        if (openRecurrence) w.Dispatcher.BeginInvoke(() => w.Recurrence_Click(w, new RoutedEventArgs()));
    }

    public static async Task OpenExistingAsync(IMailService svc, CalendarSource? source, string itemId, EditScope scope, bool readOnly)
    {
        CalendarEvent? ev = null;
        if (!await Ui.RunAsync(async () => ev = await svc.GetEventAsync(itemId))) return;
        if (scope == EditScope.Series && ev!.IsOccurrence)
        {
            if (!string.IsNullOrEmpty(ev.SeriesMasterId))
            {
                var masterId = ev.SeriesMasterId;
                if (!await Ui.RunAsync(async () => ev = await svc.GetEventAsync(masterId))) return;
            }
            else
            {
                var duration = ev.Duration;
                ev.Start = ev.Recurrence != null ? ev.Recurrence.StartDate.Date + ev.Start.TimeOfDay : ev.Start;
                ev.End = ev.Start + duration;
            }
        }
        new AppointmentWindow(svc, source, ev!, ev!.IsMeeting, scope, readOnly).Show();
    }

    private static string ReminderText(int m) => m switch
    {
        -1 => Loc.T("None"),
        0 => Loc.T("AtStart"),
        < 60 => Loc.F("ReminderMinutes", m),
        < 1440 => Loc.F("ReminderHours", m / 60.0),
        < 10080 => Loc.F("ReminderDays", m / 1440.0),
        _ => Loc.F("ReminderWeeks", m / 10080.0)
    };

    private ComboBoxItem AddReminderChoice(int m)
    {
        var item = new ComboBoxItem { Content = ReminderText(m), Tag = m };
        ReminderBox.Items.Add(item);
        return item;
    }

    private static void FillTimes(ComboBox box)
    {
        var c = Loc.Instance.Culture;
        for (int i = 0; i < 48; i++) box.Items.Add(DateTime.Today.AddMinutes(i * 30).ToString("t", c));
    }

    private void SetDateTime(DateTime start, DateTime end)
    {
        var c = Loc.Instance.Culture;
        StartDate.SelectedDate = start.Date;
        StartTime.Text = start.ToString("t", c);
        EndDate.SelectedDate = end.Date;
        EndTime.Text = end.ToString("t", c);
    }

    private static DateTime Combine(DateTime? date, string time)
    {
        var d = date ?? DateTime.Today;
        if (DateTime.TryParse(time, Loc.Instance.Culture, DateTimeStyles.NoCurrentDateDefault, out var t) ||
            DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out t))
            return d.Date + t.TimeOfDay;
        return d.Date;
    }

    private (DateTime start, DateTime end) ReadTimes()
    {
        var allDay = AllDayBox.IsChecked == true;
        var start = allDay ? (StartDate.SelectedDate ?? DateTime.Today).Date : Combine(StartDate.SelectedDate, StartTime.Text);
        var end = allDay ? (EndDate.SelectedDate ?? start).Date.AddDays(1) : Combine(EndDate.SelectedDate, EndTime.Text);
        return (start, end);
    }

    private TimeSpan _duration;

    private void StartDate_Changed(object? sender, SelectionChangedEventArgs e) => KeepDuration();
    private void StartTime_Changed(object sender, RoutedEventArgs e) => KeepDuration();
    private void StartTime_SelectionChanged(object sender, SelectionChangedEventArgs e) => Dispatcher.BeginInvoke(KeepDuration);

    private void KeepDuration()
    {
        if (!IsLoaded) { _duration = _ev.End - _ev.Start; return; }
        var start = Combine(StartDate.SelectedDate, StartTime.Text);
        if (_duration <= TimeSpan.Zero) _duration = TimeSpan.FromMinutes(30);
        var end = start + _duration;
        var c = Loc.Instance.Culture;
        EndDate.SelectedDate = AllDayBox.IsChecked == true ? start.Date + TimeSpan.FromDays(Math.Max(0, _duration.Days - 1)) : end.Date;
        if (AllDayBox.IsChecked != true) EndTime.Text = end.ToString("t", c);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _duration = _ev.End - _ev.Start;
        EndTime.LostFocus += (_, _) => { var (s, en) = ReadTimes(); if (en > s) _duration = en - s; };
        EndDate.SelectedDateChanged += (_, _) => { var (s, en) = ReadTimes(); if (en > s) _duration = en - s; };
        if (_isNew) TitleBox.Focus();
    }

    private void AllDay_Changed(object sender, RoutedEventArgs e)
    {
        var allDay = AllDayBox.IsChecked == true;
        StartTime.Visibility = EndTime.Visibility = allDay ? Visibility.Collapsed : Visibility.Visible;
        if (IsLoaded)
        {
            ShowAsBox.SelectedIndex = allDay ? 0 : 3;
            if (allDay) { EndDate.SelectedDate = StartDate.SelectedDate; }
        }
    }

    private void UpdateMode()
    {
        var meetingText = Loc.T(_isMeeting ? "Meeting" : "Appointment");
        MainTab.Header = _isMeeting ? (Loc.Instance.IsGeorgian ? "შეხვედრის მოწვევა" : "Meeting") : Loc.T("Appointment");
        RequiredBtn.Visibility = RequiredBox.Visibility = OptionalBtn.Visibility = OptionalBox.Visibility = _isMeeting ? Visibility.Visible : Visibility.Collapsed;
        SendButton.Visibility = _isMeeting && !_attendeeView ? Visibility.Visible : Visibility.Collapsed;
        SendText.Text = _isNew ? Loc.T("Send") : Loc.T("SendUpdate");
        SaveCloseBtn.Visibility = _isMeeting && !_attendeeView ? Visibility.Collapsed : Visibility.Visible;
        InviteBtn.Visibility = _isMeeting ? Visibility.Collapsed : Visibility.Visible;
        CancelInviteBtn.Visibility = _isMeeting && _isNew ? Visibility.Visible : Visibility.Collapsed;
        ResponseOptionsBtn.Visibility = _isMeeting && !_attendeeView ? Visibility.Visible : Visibility.Collapsed;
        RespondGroup.Visibility = _attendeeView ? Visibility.Visible : Visibility.Collapsed;
        DeleteBtn.Header = _isMeeting && !_isNew && !_attendeeView ? Loc.T("CancelMeeting") : Loc.T("Delete");
        DeleteBtn.IsEnabled = _ev.CanDeleteItem != false;
        var editable = !_readOnly && !_attendeeView;
        TitleBox.IsReadOnly = LocationBox.IsReadOnly = !editable;
        StartDate.IsEnabled = EndDate.IsEnabled = StartTime.IsEnabled = EndTime.IsEnabled = AllDayBox.IsEnabled = editable;
        RequiredBox.IsEnabled = OptionalBox.IsEnabled = editable;
        RecurrenceBtn.IsEnabled = RecurrenceLink.IsEnabled = editable && !(_ev.IsOccurrence && _scope == EditScope.Single);
        if (_readOnly)
        {
            SaveCloseBtn.IsEnabled = DeleteBtn.IsEnabled = false;
            InfoBar.Visibility = Visibility.Visible;
            InfoText.Text = Loc.T("ReadOnlyCalendar");
        }
        else if (_attendeeView)
        {
            InfoBar.Visibility = Visibility.Visible;
            var status = _ev.MyResponse switch
            {
                MeetingResponse.Accept => Loc.T("YouAccepted"),
                MeetingResponse.Tentative => Loc.T("YouTentative"),
                MeetingResponse.Decline => Loc.T("YouDeclined"),
                _ => Loc.T("NotResponded")
            };
            InfoText.Text = $"{status}  {Loc.T("Organizer")}: {_ev.Organizer?.Display}";
        }
        else if (!_isNew && _isMeeting && _ev.Attendees.Count > 0)
        {
            InfoBar.Visibility = Visibility.Visible;
            var acc = _ev.Attendees.Count(a => a.Response == MeetingResponse.Accept);
            var ten = _ev.Attendees.Count(a => a.Response == MeetingResponse.Tentative);
            var dec = _ev.Attendees.Count(a => a.Response == MeetingResponse.Decline);
            InfoText.Text = Loc.Instance.IsGeorgian
                ? $"პასუხები: {acc} დაეთანხმა, {ten} სავარაუდოდ, {dec} უარი."
                : $"Responses: {acc} accepted, {ten} tentatively accepted, {dec} declined.";
        }
        if (_source != null && _source.IsShared && !_readOnly)
        {
            InfoBar.Visibility = Visibility.Visible;
            InfoText.Text = (InfoText.Text.Length > 0 ? InfoText.Text + "   " : "") + Loc.F("CalendarOf", _source.OwnerName);
        }
    }

    private void UpdateTitle()
    {
        var subject = string.IsNullOrWhiteSpace(TitleBox.Text) ? Loc.T("Untitled") : TitleBox.Text;
        Title = $"{subject} - {Loc.T(_isMeeting ? "Meeting" : "Appointment")}";
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTitle();

    private void UpdateImportance()
    {
        HighImpBtn.IsChecked = _importance == Importance.High;
        LowImpBtn.IsChecked = _importance == Importance.Low;
    }

    private void Importance_Click(object sender, RoutedEventArgs e)
    {
        var target = (sender as FrameworkElement)?.Tag as string == "High" ? Importance.High : Importance.Low;
        _importance = _importance == target ? Importance.Normal : target;
        UpdateImportance();
    }

    private void UpdateRecurrenceText()
    {
        if (_recurrence == null || _recurrence.Type == RecurrenceType.None)
        {
            RecurrenceText.Visibility = Visibility.Collapsed;
            return;
        }
        RecurrenceText.Visibility = Visibility.Visible;
        RecurrenceText.Text = "↻ " + RecurrenceWindow.Describe(_recurrence, ReadTimes().start, ReadTimes().end);
    }

    private void Recurrence_Click(object sender, RoutedEventArgs e)
    {
        var (start, end) = ReadTimes();
        var w = new RecurrenceWindow(_recurrence, start, end, AllDayBox.IsChecked == true) { Owner = this };
        if (w.ShowDialog() != true) return;
        _recurrence = w.Result;
        if (w.Result != null) SetDateTime(w.StartTime, w.EndTime);
        UpdateRecurrenceText();
    }

    private void Invite_Click(object sender, RoutedEventArgs e)
    {
        _isMeeting = true;
        UpdateMode();
        UpdateTitle();
        RequiredBox.Focus();
    }

    private void CancelInvite_Click(object sender, RoutedEventArgs e)
    {
        _isMeeting = false;
        RequiredBox.SetRecipients(Array.Empty<Recipient>());
        OptionalBox.SetRecipients(Array.Empty<Recipient>());
        UpdateMode();
        UpdateTitle();
    }

    private void AddressBook_Click(object sender, RoutedEventArgs e)
    {
        var w = new AddressBookWindow(_svc, AddressBookMode.SelectAttendees) { Owner = this };
        w.Preload(RequiredBox.Recipients, OptionalBox.Recipients, Array.Empty<Recipient>());
        if (w.ShowDialog() != true) return;
        RequiredBox.SetRecipients(w.To);
        OptionalBox.SetRecipients(w.Cc.Concat(w.Bcc));
        if (!_isMeeting && (w.To.Count + w.Cc.Count) > 0) { _isMeeting = true; UpdateMode(); }
    }

    private void ShowAppt_Click(object sender, RoutedEventArgs e)
    {
        ApptPage.Visibility = Visibility.Visible;
        Scheduler.Visibility = Visibility.Collapsed;
        ShowApptBtn.IsChecked = true;
        ShowSchedBtn.IsChecked = false;
    }

    private void ShowSched_Click(object sender, RoutedEventArgs e)
    {
        ApptPage.Visibility = Visibility.Collapsed;
        Scheduler.Visibility = Visibility.Visible;
        ShowApptBtn.IsChecked = false;
        ShowSchedBtn.IsChecked = true;
        var (start, end) = ReadTimes();
        var attendees = new List<Recipient> { new(_svc.Account.DisplayName, _source?.OwnerEmail ?? _svc.Account.Email) };
        attendees.AddRange(RequiredBox.Recipients);
        attendees.AddRange(OptionalBox.Recipients);
        _ = Scheduler.LoadAsync(attendees, start, end);
    }

    private void Categorize_Opened(object? sender, EventArgs e)
    {
        CategorizeBtn.Items.Clear();
        foreach (var c in AppServices.Current.Settings.Categories)
        {
            var name = c.Name;
            var mi = new Fluent.MenuItem { Header = Loc.T(name), IsCheckable = true, IsChecked = _ev.Categories.Contains(name) };
            mi.Click += (_, _) => { if (_ev.Categories.Contains(name)) _ev.Categories.Remove(name); else _ev.Categories.Add(name); };
            CategorizeBtn.Items.Add(mi);
        }
    }

    private async void Exec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string cmd }) await Editor.Command(cmd);
    }

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        var url = Ui.Prompt(Loc.T("Link"), Loc.T("InsertLinkPrompt"), "https://", this);
        if (!string.IsNullOrWhiteSpace(url)) await Editor.Command("createLink", url);
    }

    private async void Table_Click(object sender, RoutedEventArgs e) =>
        await Editor.InsertHtml("<table style=\"border-collapse:collapse\"><tr><td style=\"border:1px solid #a6a6a6;min-width:80px\">&nbsp;</td><td style=\"border:1px solid #a6a6a6;min-width:80px\">&nbsp;</td></tr><tr><td style=\"border:1px solid #a6a6a6\">&nbsp;</td><td style=\"border:1px solid #a6a6a6\">&nbsp;</td></tr></table>");

    private void AttachFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = Loc.T("AllFilesFilter") };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames)
            _attachments.Add(new AttachmentInfo { Name = Path.GetFileName(f), Size = new FileInfo(f).Length, LocalPath = f });
        RefreshAttachments();
    }

    private void RefreshAttachments()
    {
        AttachmentsList.Items.Clear();
        foreach (var a in _attachments)
        {
            var (glyph, brush) = ReadingPane.FileIcon(a.Name);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Ui.GlyphText(glyph, 16, brush));
            sp.Children.Add(new TextBlock { Text = $"{a.Name} ({a.SizeDisplay})", Margin = new Thickness(6, 0, 0, 0) });
            var chip = new Border { BorderBrush = (Brush)FindResource("BorderStrongBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 4, 6, 0), Child = sp, Cursor = System.Windows.Input.Cursors.Hand };
            var att = a;
            chip.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.ClickCount == 2 && !string.IsNullOrEmpty(att.Id)) _ = MailActions.OpenAttachmentAsync(_svc, _ev.Id, att);
                else if (ev.ClickCount == 2 && att.LocalPath != null) Ui.OpenUrl(att.LocalPath);
            };
            AttachmentsList.Items.Add(chip);
        }
    }

    // =====================================================================================
    // Save / send / delete
    // =====================================================================================

    private async Task<CalendarEvent?> BuildAsync()
    {
        var (start, end) = ReadTimes();
        if (end < start || (end == start && AllDayBox.IsChecked != true))
        {
            Ui.Error(Loc.Instance.IsGeorgian ? "დასრულების დრო უნდა იყოს დაწყების შემდეგ." : "The end time you entered occurs before the start time.", this);
            return null;
        }
        if (_isMeeting && !await RequiredBox.CheckNamesAsync(this)) return null;
        if (_isMeeting && !await OptionalBox.CheckNamesAsync(this)) return null;
        var ev = _ev.CloneShallow();
        ev.Subject = TitleBox.Text.Trim();
        ev.Location = LocationBox.Text.Trim();
        ev.Start = start;
        ev.End = end;
        ev.IsAllDay = AllDayBox.IsChecked == true;
        ev.ShowAs = (ShowAs)((ComboBoxItem)ShowAsBox.SelectedItem).Tag;
        var rem = (int)((ComboBoxItem)ReminderBox.SelectedItem).Tag;
        ev.ReminderSet = rem >= 0;
        ev.ReminderMinutes = Math.Max(0, rem);
        ev.Sensitivity = PrivateBtn.IsChecked == true ? Sensitivity.Private : Sensitivity.Normal;
        ev.Importance = _importance;
        ev.ResponseRequested = RequestResponsesItem.IsChecked;
        ev.AllowNewTimeProposal = AllowProposeItem.IsChecked;
        ev.Recurrence = _recurrence ?? (_ev.IsRecurring && !_ev.IsOccurrence ? new RecurrenceInfo { Type = RecurrenceType.None } : null);
        var html = await Editor.GetHtmlAsync();
        ev.BodyHtml = $"<html><body><div style=\"{MailActions.DefaultBodyStyle()}\">{html}</div></body></html>";
        ev.Attendees = new List<Attendee>();
        if (_isMeeting)
        {
            ev.Attendees.AddRange(RequiredBox.Recipients.Select(r => new Attendee { Name = r.Name, Email = r.Address, Type = AttendeeType.Required }));
            ev.Attendees.AddRange(OptionalBox.Recipients.Select(r => new Attendee { Name = r.Name, Email = r.Address, Type = AttendeeType.Optional }));
        }
        ev.Attachments = _attachments.ToList();
        return ev;
    }

    private CalendarSource TargetSource => _source ?? new CalendarSource { AccountId = _svc.Account.Id, Key = "default", Access = CalendarAccess.Owner };

    private async void SaveClose_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly || _attendeeView) { _closingConfirmed = true; Close(); return; }
        await PersistAsync(false, true);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        await PersistAsync(true, false);
    }

    private async Task PersistAsync(bool send, bool offerUpdate)
    {
        if (_persisting || _readOnly || _attendeeView) return;
        _persisting = true;
        try
        {
            var ev = await BuildAsync();
            if (ev == null) return;
            if (send && ev.Attendees.Count == 0)
            {
                Ui.Error(Loc.T("NoRecipients"), this);
                return;
            }
            if (offerUpdate && _isMeeting && !_isNew && ev.Attendees.Count > 0)
                send = Ui.Confirm(Loc.Instance.IsGeorgian ? "გავუგზავნო განახლება მონაწილეებს?" : "Send an update to the attendees?", this);
            if (!await Ui.RunAsync(() => _svc.SaveEventAsync(TargetSource, ev, send, _scope), this))
            {
                if (ev.MeetingSubmissionNeedsReview)
                {
                    _ev.Id = ev.Id;
                    _ev.MeetingSubmissionNeedsReview = true;
                    AppServices.Current.RaiseDataChanged("calendar");
                }
                return;
            }
            if (send)
                foreach (var a in ev.Attendees) AppServices.Current.SettingsService.AddRecentRecipient(new Recipient(a.Name, a.Email).FullDisplay);
            Done();
        }
        finally
        {
            _persisting = false;
        }
    }

    private void Done()
    {
        _saved = true;
        _closingConfirmed = true;
        AppServices.Current.RaiseDataChanged("calendar");
        _ = AppServices.Current.RefreshRemindersAsync();
        Close();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_isNew) { _closingConfirmed = true; Close(); return; }
        var notify = false;
        string? message = null;
        if (_isMeeting && !_attendeeView)
        {
            var w = new ResponseCommentWindow(Loc.T("CancelMeeting") + ": " + _ev.Subject) { Owner = this };
            if (w.ShowDialog() != true) return;
            notify = true;
            message = w.CommentHtml;
        }
        else if (_attendeeView)
        {
            var r = Ui.YesNoCancel(Loc.T("DeleteMeetingResponse"), this);
            if (r == MessageBoxResult.Cancel) return;
            notify = r == MessageBoxResult.Yes;
        }
        if (await Ui.RunAsync(() => _svc.DeleteEventAsync(_ev.Id, notify, message, _scope), this)) Done();
    }

    private async void Respond_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string t } || !Enum.TryParse<MeetingResponse>(t, out var r)) return;
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Menu(Loc.T("EditResponse"), async () => { if (await MailActions.RespondToMeetingAsync(_svc, _ev.Id, _ev.Subject, r, MeetingResponseMode.Edit, this)) Done(); }));
        menu.Items.Add(Ui.Menu(Loc.T("SendResponseNow"), async () => { if (await MailActions.RespondToMeetingAsync(_svc, _ev.Id, _ev.Subject, r, MeetingResponseMode.SendNow, this)) Done(); }));
        menu.Items.Add(Ui.Menu(Loc.T("DontSendResponse"), async () => { if (await MailActions.RespondToMeetingAsync(_svc, _ev.Id, _ev.Subject, r, MeetingResponseMode.DontSend, this)) Done(); }));
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        await Task.CompletedTask;
    }

    private void ReplyOrganizer_Click(object sender, RoutedEventArgs e)
    {
        if (_ev.Organizer == null) return;
        MailActions.ComposeNew(_svc, new[] { _ev.Organizer }, HtmlUtil.SubjectPrefix(_ev.Subject, "RE:"));
    }

    private async void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_isNew) return;
        var c = Loc.Instance.Culture;
        var body = $"<div><br></div><div style=\"border-top:solid #E1E1E1 1pt;padding-top:3pt\"><b>{Loc.T("Subject")}:</b> {HtmlUtil.Encode(_ev.Subject)}<br><b>{Loc.T("When")}:</b> {_ev.Start.ToString("f", c)} – {_ev.End.ToString("t", c)}<br><b>{Loc.T("Where")}:</b> {HtmlUtil.Encode(_ev.Location)}</div><div><br></div>{HtmlUtil.BodyContent(_ev.BodyHtml)}";
        MailActions.ComposeNew(_svc, null, HtmlUtil.SubjectPrefix(_ev.Subject, "FW:"), body);
        await Task.CompletedTask;
    }

    private void Print_Click(object sender, RoutedEventArgs e) => Editor.Print();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingConfirmed || _saved || _readOnly || _attendeeView) return;
        if (_persisting) { e.Cancel = true; return; }
        var changed = Editor.IsDirty || TitleBox.Text != _ev.Subject || LocationBox.Text != _ev.Location || ReadTimes().start != _ev.Start;
        if (!changed) return;
        e.Cancel = true;
        var r = Ui.YesNoCancel(Loc.T("SaveChangesPrompt"), this);
        if (r == MessageBoxResult.Cancel) return;
        if (r == MessageBoxResult.No) { _closingConfirmed = true; _ = Dispatcher.BeginInvoke(new Action(Close)); return; }
        await PersistAsync(false, false);
    }
}
