using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fluent;
using Microsoft.Win32;
using Posta.App.Controls;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.App.Views;
using Posta.Core.Models;
using Posta.Core.Services;
using Button = Fluent.Button;

namespace Posta.App.Windows;

/// <summary>Base for item forms (contact, group, task) with an Office ribbon built in code.</summary>
public abstract class ItemWindowBase : RibbonWindow
{
    protected readonly Ribbon Ribbon = new() { CanCustomizeRibbon = false, CanCustomizeQuickAccessToolBar = false };
    protected readonly RibbonTabItem MainTab = new();
    protected readonly Grid Body = new();

    protected ItemWindowBase()
    {
        FontFamily = new FontFamily("Segoe UI, Sylfaen");
        FontSize = 12;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        Ribbon.Tabs.Add(MainTab);
        var backstage = new Backstage { Header = Loc.T("File") };
        var tabs = new BackstageTabControl();
        var close = new Button { Header = Loc.T("Close") };
        close.Click += (_, _) => Close();
        tabs.Items.Add(close);
        backstage.Content = tabs;
        Ribbon.Menu = backstage;
        root.Children.Add(Ribbon);
        Grid.SetRow(Body, 1);
        root.Children.Add(Body);
        Content = root;
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    protected RibbonGroupBox Group(string headerKey)
    {
        var g = new RibbonGroupBox { Header = Loc.T(headerKey) };
        MainTab.Groups.Add(g);
        return g;
    }

    protected static Button Large(RibbonGroupBox g, string textKey, string glyph, string color, Action click)
    {
        var b = new Button
        {
            Header = Loc.T(textKey), SizeDefinition = new RibbonControlSizeDefinition("Large"),
            LargeIcon = GlyphFactory.Create(glyph, GlyphFactory.ParseBrush(color, Brushes.Black), 32), Icon = GlyphFactory.Create(glyph, GlyphFactory.ParseBrush(color, Brushes.Black), 16)
        };
        b.Click += (_, _) => click();
        g.Items.Add(b);
        return b;
    }

    protected static Button Middle(RibbonGroupBox g, string textKey, string glyph, string color, Action click)
    {
        var b = new Button { Header = Loc.T(textKey), SizeDefinition = new RibbonControlSizeDefinition("Middle"), Icon = GlyphFactory.Create(glyph, GlyphFactory.ParseBrush(color, Brushes.Black), 16) };
        b.Click += (_, _) => click();
        g.Items.Add(b);
        return b;
    }

    protected static System.Windows.Controls.TextBox Field(double width = double.NaN) => new()
    {
        Height = 24, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(3, 0, 3, 0), Width = width, Margin = new Thickness(0, 2, 0, 2)
    };

    protected static Grid Form(params (string label, FrameworkElement field)[] rows)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = rows[i].label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
            Grid.SetRow(l, i);
            Grid.SetRow(rows[i].field, i);
            Grid.SetColumn(rows[i].field, 1);
            g.Children.Add(l);
            g.Children.Add(rows[i].field);
        }
        return g;
    }

    protected static TextBlock Heading(string text) => new()
    {
        Text = text, Foreground = GlyphFactory.Blue, FontSize = 13, Margin = new Thickness(0, 12, 0, 4)
    };
}

// =========================================================================================
// Contact
// =========================================================================================
public sealed class ContactWindow : ItemWindowBase
{
    private readonly IMailService _svc;
    private readonly string _folderId;
    private readonly ContactInfo _c;
    private readonly System.Windows.Controls.TextBox _first = Field(), _middle = Field(), _last = Field(), _company = Field(), _dept = Field(), _job = Field(), _office = Field();
    private readonly System.Windows.Controls.TextBox _email1 = Field(), _email2 = Field(), _email3 = Field(), _displayAs = Field(), _web = Field(), _im = Field();
    private readonly System.Windows.Controls.TextBox _bphone = Field(), _hphone = Field(), _fax = Field(), _mobile = Field();
    private readonly System.Windows.Controls.TextBox _street = Field(), _city = Field(), _state = Field(), _zip = Field(), _country = Field();
    private readonly System.Windows.Controls.TextBox _manager = Field(), _assistant = Field(), _nick = Field(), _spouse = Field();
    private readonly DatePicker _birthday = new(), _anniversary = new();
    private readonly System.Windows.Controls.ComboBox _addrKind = new() { Width = 140 };
    private readonly System.Windows.Controls.TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 160 };
    private readonly Grid _photo = new() { Width = 96, Height = 96 };
    private readonly ContentControl _cardPreview = new();
    private byte[]? _photoBytes;
    private PostalAddress _business, _home, _other;
    private string _addrShown = "Business";

    public ContactWindow(IMailService svc, string folderId, ContactInfo? contact)
    {
        _svc = svc;
        _folderId = folderId;
        _c = contact ?? new ContactInfo();
        _business = Copy(_c.BusinessAddress); _home = Copy(_c.HomeAddress); _other = Copy(_c.OtherAddress);
        _photoBytes = _c.Photo;
        Width = 980;
        Height = 760;
        MainTab.Header = Loc.T("Contact");
        Title = $"{(string.IsNullOrWhiteSpace(_c.DisplayName) ? Loc.T("Untitled") : _c.DisplayName)} - {Loc.T("Contact")}";

        var actions = Group("GrpActions");
        Large(actions, "SaveClose", G.Save, "#7719AA", async () => await SaveAsync(true));
        Large(actions, "Delete", G.Delete, "#424242", async () => await DeleteAsync());
        Middle(actions, "SaveDraft", G.Save, "#424242", async () => await SaveAsync(false));
        var comm = Group("GrpCommunicate");
        Large(comm, "SendEmail", G.Mail, "#0F6CBD", () => MailActions.ComposeNew(_svc, new[] { new Recipient(DisplayName(), _email1.Text.Trim()) }));
        Large(comm, "ScheduleMeeting", G.Calendar, "#0F6CBD", () =>
        {
            var start = DateTime.Now.Date.AddHours(DateTime.Now.Hour + 1);
            var ev = new CalendarEvent { Start = start, End = start.AddMinutes(30), IsMeeting = true, ReminderSet = true, ReminderMinutes = 15 };
            ev.Attendees.Add(new Attendee { Name = DisplayName(), Email = _email1.Text.Trim() });
            AppointmentWindow.OpenNew(_svc, null, ev, true);
        });
        var tags = Group("GrpTags");
        Middle(tags, "AddPicture", G.Picture, "#8764B8", PickPhoto);
        Middle(tags, "RemovePicture", G.Remove, "#424242", () => { _photoBytes = null; RenderPhoto(); });

        BuildBody();
        Load();
    }

    private static PostalAddress Copy(PostalAddress a) => new() { Street = a.Street, City = a.City, State = a.State, PostalCode = a.PostalCode, Country = a.Country };

    private string DisplayName()
    {
        var n = string.Join(" ", new[] { _first.Text, _middle.Text, _last.Text }.Select(s => s.Trim()).Where(s => s.Length > 0));
        return n.Length > 0 ? n : _company.Text.Trim();
    }

    private void BuildBody()
    {
        Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(16, 10, 16, 16) };
        var top = new DockPanel();
        DockPanel.SetDock(_photo, Dock.Right);
        _photo.Margin = new Thickness(12, 4, 0, 0);
        _photo.Cursor = System.Windows.Input.Cursors.Hand;
        _photo.MouseLeftButtonUp += (_, _) => PickPhoto();
        top.Children.Add(_photo);
        top.Children.Add(Form((Loc.T("FirstName"), _first), (Loc.T("MiddleName"), _middle), (Loc.T("LastName"), _last), (Loc.T("Company"), _company),
            (Loc.T("Department"), _dept), (Loc.T("JobTitle"), _job), (Loc.T("Office"), _office)));
        left.Children.Add(top);
        left.Children.Add(Heading(Loc.Instance.IsGeorgian ? "ინტერნეტი" : "Internet"));
        left.Children.Add(Form((Loc.T("Email"), _email1), (Loc.T("DisplayAs"), _displayAs), (Loc.T("Email2"), _email2), (Loc.T("Email3"), _email3),
            (Loc.T("WebPage"), _web), (Loc.T("IMAddress"), _im)));
        left.Children.Add(Heading(Loc.T("PhoneNumbers")));
        left.Children.Add(Form((Loc.T("BusinessPhone"), _bphone), (Loc.T("HomePhone"), _hphone), (Loc.T("BusinessFax"), _fax), (Loc.T("Mobile"), _mobile)));
        left.Children.Add(Heading(Loc.T("Addresses")));
        foreach (var (k, key) in new[] { ("Business", "BusinessAddress"), ("Home", "HomeAddress"), ("Other", "OtherAddress") })
            _addrKind.Items.Add(new ComboBoxItem { Content = Loc.T(key).TrimEnd('.'), Tag = k });
        _addrKind.SelectedIndex = 0;
        _addrKind.SelectionChanged += (_, _) => { StoreAddress(); _addrShown = (string)((ComboBoxItem)_addrKind.SelectedItem).Tag; ShowAddress(); };
        left.Children.Add(_addrKind);
        left.Children.Add(Form((Loc.T("Street"), _street), (Loc.T("City"), _city), (Loc.T("StateProvince"), _state), (Loc.T("PostalCode"), _zip), (Loc.T("Country"), _country)));
        left.Children.Add(Heading(Loc.T("Details")));
        left.Children.Add(Form((Loc.T("Manager"), _manager), (Loc.T("Assistant"), _assistant), (Loc.T("Nickname"), _nick), (Loc.T("Spouse"), _spouse),
            (Loc.T("Birthday"), _birthday), (Loc.T("Anniversary"), _anniversary)));
        Body.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var right = new DockPanel { Margin = new Thickness(0, 10, 16, 16) };
        var cardBorder = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Padding = new Thickness(0), Child = _cardPreview, Height = 190, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(cardBorder, Dock.Top);
        right.Children.Add(cardBorder);
        var notesHeader = new TextBlock { Text = Loc.T("NotesField"), Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(notesHeader, Dock.Top);
        right.Children.Add(notesHeader);
        right.Children.Add(_notes);
        Grid.SetColumn(right, 1);
        Body.Children.Add(right);

        foreach (var tb in new[] { _first, _middle, _last, _company, _job, _email1, _bphone, _mobile })
            tb.TextChanged += (_, _) => UpdateCard();
    }

    private void Load()
    {
        _first.Text = _c.GivenName; _middle.Text = _c.MiddleName; _last.Text = _c.Surname; _company.Text = _c.Company; _dept.Text = _c.Department;
        _job.Text = _c.JobTitle; _office.Text = _c.Office; _email1.Text = _c.Email1; _email2.Text = _c.Email2; _email3.Text = _c.Email3;
        _displayAs.Text = _c.Email1Name; _web.Text = _c.WebPage; _im.Text = _c.ImAddress; _bphone.Text = _c.BusinessPhone; _hphone.Text = _c.HomePhone;
        _fax.Text = _c.BusinessFax; _mobile.Text = _c.MobilePhone; _manager.Text = _c.Manager; _assistant.Text = _c.Assistant; _nick.Text = _c.Nickname;
        _spouse.Text = _c.SpouseName; _birthday.SelectedDate = _c.Birthday; _anniversary.SelectedDate = _c.Anniversary; _notes.Text = _c.Notes;
        ShowAddress();
        RenderPhoto();
        UpdateCard();
    }

    private PostalAddress CurrentAddress => _addrShown switch { "Home" => _home, "Other" => _other, _ => _business };

    private void ShowAddress()
    {
        var a = CurrentAddress;
        _street.Text = a.Street; _city.Text = a.City; _state.Text = a.State; _zip.Text = a.PostalCode; _country.Text = a.Country;
    }

    private void StoreAddress()
    {
        var a = CurrentAddress;
        a.Street = _street.Text.Trim(); a.City = _city.Text.Trim(); a.State = _state.Text.Trim(); a.PostalCode = _zip.Text.Trim(); a.Country = _country.Text.Trim();
    }

    private void RenderPhoto()
    {
        _photo.Children.Clear();
        _photo.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD)) });
        if (_photoBytes is { Length: > 0 } && new BytesToImageConverter().Convert(_photoBytes, typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture) is ImageSource img)
            _photo.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill } });
        else
            _photo.Children.Add(new TextBlock { Text = G.AddFriend, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 30, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        _photo.ToolTip = Loc.T("AddPicture");
        UpdateCard();
    }

    private void PickPhoto()
    {
        var dlg = new OpenFileDialog { Filter = Loc.T("ImageFilter") };
        if (dlg.ShowDialog(this) != true) return;
        _photoBytes = File.ReadAllBytes(dlg.FileName);
        RenderPhoto();
    }

    private string _autoDisplayAs = "";

    private void UpdateCard()
    {
        // Like Outlook: "Display as" follows name + email until the user edits it.
        if (_displayAs.Text == _autoDisplayAs)
        {
            var name = string.Join(" ", new[] { _first.Text.Trim(), _middle.Text.Trim(), _last.Text.Trim() }.Where(x => x.Length > 0));
            var mail = _email1.Text.Trim();
            _autoDisplayAs = mail.Length == 0 ? "" : name.Length == 0 ? mail : $"{name} ({mail})";
            _displayAs.Text = _autoDisplayAs;
        }
        var c = BuildContact();
        _cardPreview.Content = PeopleView.BuildCard(c, _svc);
        Title = $"{(string.IsNullOrWhiteSpace(c.DisplayName) ? Loc.T("Untitled") : c.DisplayName)} - {Loc.T("Contact")}";
    }

    private ContactInfo BuildContact()
    {
        StoreAddress();
        var c = _c;
        var result = new ContactInfo
        {
            Id = c.Id, AccountId = _svc.Account.Id, FolderId = _folderId,
            GivenName = _first.Text.Trim(), MiddleName = _middle.Text.Trim(), Surname = _last.Text.Trim(), Company = _company.Text.Trim(), Department = _dept.Text.Trim(),
            JobTitle = _job.Text.Trim(), Office = _office.Text.Trim(), Email1 = _email1.Text.Trim(), Email2 = _email2.Text.Trim(), Email3 = _email3.Text.Trim(),
            Email1Name = _displayAs.Text.Trim(), WebPage = _web.Text.Trim(), ImAddress = _im.Text.Trim(), BusinessPhone = _bphone.Text.Trim(), HomePhone = _hphone.Text.Trim(),
            BusinessFax = _fax.Text.Trim(), MobilePhone = _mobile.Text.Trim(), Manager = _manager.Text.Trim(), Assistant = _assistant.Text.Trim(), Nickname = _nick.Text.Trim(),
            SpouseName = _spouse.Text.Trim(), Birthday = _birthday.SelectedDate, Anniversary = _anniversary.SelectedDate, Notes = _notes.Text,
            BusinessAddress = _business, HomeAddress = _home, OtherAddress = _other, Photo = _photoBytes, Categories = c.Categories, BusinessPhone2 = c.BusinessPhone2, Pager = c.Pager
        };
        result.DisplayName = DisplayName();
        var fileAsLastFirst = AppServices.Current.Settings.ContactFileAs == "LastFirst";
        result.FileAs = string.IsNullOrWhiteSpace(result.Surname) ? result.DisplayName : fileAsLastFirst ? $"{result.Surname}, {result.GivenName}".Trim(' ', ',') : result.DisplayName;
        return result;
    }

    private async Task SaveAsync(bool close)
    {
        var c = BuildContact();
        if (string.IsNullOrWhiteSpace(c.DisplayName) && string.IsNullOrWhiteSpace(c.Email1))
        {
            Ui.Error(Loc.T("EnterName"), this);
            return;
        }
        string? id = null;
        if (await Ui.RunAsync(async () => id = await _svc.SaveContactAsync(_folderId, c), this))
        {
            _c.Id = id!;
            AppServices.Current.RaiseDataChanged("contacts");
            if (close) Close();
            else Title = $"{c.DisplayName} - {Loc.T("Contact")}";
        }
    }

    private async Task DeleteAsync()
    {
        if (string.IsNullOrEmpty(_c.Id)) { Close(); return; }
        if (await Ui.RunAsync(() => _svc.DeleteContactAsync(_c.Id), this))
        {
            AppServices.Current.RaiseDataChanged("contacts");
            Close();
        }
    }
}

// =========================================================================================
// Contact group
// =========================================================================================
public sealed class ContactGroupWindow : ItemWindowBase
{
    private readonly IMailService _svc;
    private readonly string _folderId;
    private readonly ContactInfo _c;
    private readonly System.Windows.Controls.TextBox _name = Field(360);
    private readonly ListView _members = new();
    private readonly List<Recipient> _list;

    public ContactGroupWindow(IMailService svc, string folderId, ContactInfo? group)
    {
        _svc = svc;
        _folderId = folderId;
        _c = group ?? new ContactInfo { IsGroup = true };
        _list = _c.Members.ToList();
        Width = 760;
        Height = 600;
        MainTab.Header = Loc.T("ContactGroup");
        Title = $"{(string.IsNullOrWhiteSpace(_c.DisplayName) ? Loc.T("Untitled") : _c.DisplayName)} - {Loc.T("ContactGroup")}";
        var actions = Group("GrpActions");
        Large(actions, "SaveClose", G.Save, "#7719AA", async () => await SaveAsync());
        Large(actions, "Delete", G.Delete, "#424242", async () => await DeleteAsync());
        var members = Group("Members");
        Large(members, "AddMembers", G.AddFriend, "#0F6CBD", AddMembers);
        Large(members, "RemoveMember", G.Remove, "#424242", RemoveMember);
        var comm = Group("GrpCommunicate");
        Large(comm, "SendEmail", G.Mail, "#0F6CBD", () => MailActions.ComposeNew(_svc, _list));

        var sp = new DockPanel { Margin = new Thickness(16, 12, 16, 16) };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        nameRow.Children.Add(new TextBlock { Text = Loc.T("GroupName"), MinWidth = 80, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        nameRow.Children.Add(_name);
        _name.TextChanged += (_, _) => Title = $"{(string.IsNullOrWhiteSpace(_name.Text) ? Loc.T("Untitled") : _name.Text.Trim())} - {Loc.T("ContactGroup")}";
        DockPanel.SetDock(nameRow, Dock.Top);
        sp.Children.Add(nameRow);
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("Name"), Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Recipient.Name)) });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("EmailAddressCol"), Width = 340, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Recipient.Address)) });
        _members.View = gv;
        sp.Children.Add(_members);
        Body.Children.Add(sp);
        _name.Text = _c.DisplayName;
        Refresh();
    }

    private void Refresh()
    {
        _members.ItemsSource = null;
        _members.ItemsSource = _list.OrderBy(m => m.Display).ToList();
    }

    private void AddMembers()
    {
        var w = new AddressBookWindow(_svc, AddressBookMode.SelectMembers) { Owner = this };
        if (w.ShowDialog() != true) return;
        foreach (var r in w.To)
            if (_list.All(m => !string.Equals(m.Address, r.Address, StringComparison.OrdinalIgnoreCase))) _list.Add(r);
        Refresh();
    }

    private void RemoveMember()
    {
        foreach (var r in _members.SelectedItems.Cast<Recipient>().ToList()) _list.Remove(r);
        Refresh();
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { Ui.Error(Loc.T("EnterName"), this); return; }
        _c.DisplayName = _name.Text.Trim();
        _c.FileAs = _c.DisplayName;
        _c.IsGroup = true;
        _c.Members = _list;
        if (await Ui.RunAsync(() => _svc.SaveContactAsync(_folderId, _c), this))
        {
            AppServices.Current.RaiseDataChanged("contacts");
            Close();
        }
    }

    private async Task DeleteAsync()
    {
        if (string.IsNullOrEmpty(_c.Id)) { Close(); return; }
        if (await Ui.RunAsync(() => _svc.DeleteContactAsync(_c.Id), this))
        {
            AppServices.Current.RaiseDataChanged("contacts");
            Close();
        }
    }
}

// =========================================================================================
// Task
// =========================================================================================
public sealed class TaskWindow : ItemWindowBase
{
    private readonly IMailService _svc;
    private readonly TaskInfo _t;
    private readonly System.Windows.Controls.TextBox _subject = Field();
    private readonly DatePicker _start = new() { Width = 160 }, _due = new() { Width = 160 };
    private readonly System.Windows.Controls.ComboBox _status = new() { Width = 200 }, _priority = new() { Width = 120 };
    private readonly System.Windows.Controls.TextBox _percent = Field(60);
    private readonly System.Windows.Controls.CheckBox _reminder = new();
    private readonly DatePicker _remDate = new() { Width = 150 };
    private readonly System.Windows.Controls.TextBox _remTime = Field(70);
    private readonly HtmlEditor _editor = new();

    public TaskWindow(IMailService svc, TaskInfo? task)
    {
        _svc = svc;
        _t = task ?? new TaskInfo { StartDate = DateTime.Today, DueDate = DateTime.Today };
        Width = 900;
        Height = 680;
        MainTab.Header = Loc.T("Task");
        Title = $"{(string.IsNullOrWhiteSpace(_t.Subject) ? Loc.T("Untitled") : _t.Subject)} - {Loc.T("Task")}";
        var actions = Group("GrpActions");
        Large(actions, "SaveClose", G.Save, "#7719AA", async () => await SaveAsync());
        Large(actions, "Delete", G.Delete, "#424242", async () => await DeleteAsync());
        var manage = Group("GrpManageTask");
        Large(manage, "MarkComplete", G.CheckMark, "#107C10", async () => { _status.SelectedIndex = 2; _percent.Text = "100"; await SaveAsync(); });
        var follow = Group("GrpFollowUp");
        Middle(follow, "FlagToday", G.Flag, "#D13438", () => _due.SelectedDate = DateTime.Today);
        Middle(follow, "FlagTomorrow", G.Flag, "#D13438", () => _due.SelectedDate = DateTime.Today.AddDays(1));
        Middle(follow, "FlagNextWeek", G.Flag, "#D13438", () => _due.SelectedDate = DateTime.Today.AddDays(7));

        foreach (var s in Enum.GetValues(typeof(TaskState)).Cast<TaskState>()) _status.Items.Add(new ComboBoxItem { Content = Loc.T("TS_" + s), Tag = s });
        foreach (var (imp, key) in new[] { (Importance.Low, "Low"), (Importance.Normal, "NormalPriority"), (Importance.High, "High") }) _priority.Items.Add(new ComboBoxItem { Content = Loc.T(key), Tag = imp });

        var top = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        top.Children.Add(Form((Loc.T("Subject"), _subject)));
        // Outlook layout: Start date | Status  /  Due date | Priority | % Complete
        var dates = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var w in new[] { 120.0, 0, 100, 0, 120, 0 })
            dates.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : GridLength.Auto });
        dates.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dates.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Pair(string label, FrameworkElement e, int row, int col)
        {
            var tb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetRow(tb, row); Grid.SetColumn(tb, col);
            e.Margin = new Thickness(0, 2, 24, 2);
            e.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(e, row); Grid.SetColumn(e, col + 1);
            dates.Children.Add(tb);
            dates.Children.Add(e);
        }
        Pair(Loc.T("StartDate"), _start, 0, 0);
        Pair(Loc.T("Status"), _status, 0, 2);
        Pair(Loc.T("DueDate"), _due, 1, 0);
        Pair(Loc.T("Priority"), _priority, 1, 2);
        Pair(Loc.T("PercentComplete"), _percent, 1, 4);
        top.Children.Add(dates);
        var rem = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        _reminder.Content = Loc.T("Reminder");
        _reminder.Width = 120;
        _reminder.VerticalAlignment = VerticalAlignment.Center;
        rem.Children.Add(_reminder);
        rem.Children.Add(_remDate);
        _remTime.Margin = new Thickness(8, 0, 0, 0);
        rem.Children.Add(_remTime);
        top.Children.Add(rem);

        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Body.RowDefinitions.Add(new RowDefinition());
        Body.Children.Add(top);
        var eb = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1, 0, 0), Child = _editor };
        Grid.SetRow(eb, 1);
        Body.Children.Add(eb);

        _subject.Text = _t.Subject;
        _start.SelectedDate = _t.StartDate;
        _due.SelectedDate = _t.DueDate;
        _status.SelectedItem = _status.Items.Cast<ComboBoxItem>().First(i => (TaskState)i.Tag == _t.Status);
        _priority.SelectedItem = _priority.Items.Cast<ComboBoxItem>().First(i => (Importance)i.Tag == _t.Importance);
        _percent.Text = _t.PercentComplete.ToString();
        _reminder.IsChecked = _t.ReminderSet;
        var rt = _t.ReminderTime ?? (_t.DueDate ?? DateTime.Today).Date.AddHours(8);
        _remDate.SelectedDate = rt.Date;
        _remTime.Text = rt.ToString("HH:mm");
        _ = _editor.SetHtmlAsync(string.IsNullOrEmpty(_t.BodyHtml) ? "<div><br></div>" : HtmlUtil.BodyContent(HtmlUtil.Sanitize(_t.BodyHtml)));
        Loaded += (_, _) => _subject.Focus();
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_subject.Text)) { Ui.Error(Loc.T("EnterName"), this); return; }
        _t.Subject = _subject.Text.Trim();
        _t.StartDate = _start.SelectedDate;
        _t.DueDate = _due.SelectedDate;
        _t.Status = (TaskState)((ComboBoxItem)_status.SelectedItem).Tag;
        _t.Importance = (Importance)((ComboBoxItem)_priority.SelectedItem).Tag;
        _t.PercentComplete = int.TryParse(_percent.Text, out var p) ? Math.Clamp(p, 0, 100) : 0;
        if (_t.Status == TaskState.Completed) _t.PercentComplete = 100;
        _t.ReminderSet = _reminder.IsChecked == true;
        _t.ReminderTime = (_remDate.SelectedDate ?? DateTime.Today).Date + (TimeSpan.TryParse(_remTime.Text, out var ts) ? ts : TimeSpan.FromHours(8));
        _t.BodyHtml = $"<html><body>{await _editor.GetHtmlAsync()}</body></html>";
        if (await Ui.RunAsync(() => _svc.SaveTaskAsync(_t.FolderId, _t), this))
        {
            AppServices.Current.RaiseDataChanged("tasks");
            Close();
        }
    }

    private async Task DeleteAsync()
    {
        if (string.IsNullOrEmpty(_t.Id)) { Close(); return; }
        if (await Ui.RunAsync(() => _svc.DeleteTaskAsync(_t.Id), this))
        {
            AppServices.Current.RaiseDataChanged("tasks");
            Close();
        }
    }
}
