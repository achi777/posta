using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Windows;

// =========================================================================================
// Folder / calendar permissions (Folder Properties → Permissions)
// =========================================================================================
public sealed class PermissionsWindow : DialogWindow
{
    private readonly IMailService _svc;
    private readonly string _folderId;
    private readonly bool _isCalendar;
    private readonly ObservableCollection<PermissionEntry> _entries = new();
    private readonly ListView _list = new() { Height = 170 };
    private readonly ComboBox _level = new() { Width = 300 };
    private readonly RadioButton _readNone = new(), _readTime = new(), _readDetails = new(), _readFull = new();
    private readonly CheckBox _create = new(), _createSub = new(), _editOwn = new(), _editAll = new(), _owner = new(), _contact = new(), _visible = new();
    private readonly RadioButton _delNone = new(), _delOwn = new(), _delAll = new();
    private bool _updating;

    private static readonly PermissionLevel[] Levels =
    {
        PermissionLevel.Owner, PermissionLevel.PublishingEditor, PermissionLevel.Editor, PermissionLevel.PublishingAuthor, PermissionLevel.Author,
        PermissionLevel.NoneditingAuthor, PermissionLevel.Reviewer, PermissionLevel.Contributor, PermissionLevel.FreeBusyTimeOnly,
        PermissionLevel.FreeBusyTimeAndSubjectAndLocation, PermissionLevel.None, PermissionLevel.Custom
    };

    public static void ShowFor(IMailService svc, string folderId, string folderName, Window? owner, bool isCalendar = false)
    {
        new PermissionsWindow(svc, folderId, folderName, isCalendar) { Owner = owner }.ShowDialog();
    }

    private PermissionsWindow(IMailService svc, string folderId, string folderName, bool isCalendar)
    {
        _svc = svc;
        _folderId = folderId;
        _isCalendar = isCalendar;
        Title = $"{folderName} – {Loc.T("Permissions")}";
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12), Width = 640 };

        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("Name"), Width = 340, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(PermissionEntry.Display)) { Converter = new StandardUserConverter() } });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("PermissionLevel").TrimEnd(':'), Width = 260, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(PermissionEntry.Level)) { Converter = new LocalizeConverter(), ConverterParameter = "PL_" } });
        _list.View = gv;
        _list.ItemsSource = _entries;
        _list.SelectionChanged += (_, _) => ShowSelected();
        root.Children.Add(_list);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 10) };
        var add = MakeButton(Loc.T("Add"), false, false, AddUser); add.Margin = new Thickness(0, 0, 6, 0);
        var rem = MakeButton(Loc.T("Remove"), false, false, RemoveUser); rem.Margin = new Thickness(0, 0, 6, 0);
        btns.Children.Add(add); btns.Children.Add(rem);
        root.Children.Add(btns);

        var lvl = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        lvl.Children.Add(new TextBlock { Text = Loc.T("PermissionLevel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var l in Levels.Where(l => isCalendar || l is not (PermissionLevel.FreeBusyTimeOnly or PermissionLevel.FreeBusyTimeAndSubjectAndLocation)))
            _level.Items.Add(new ComboBoxItem { Content = Loc.T("PL_" + l), Tag = l });
        _level.SelectionChanged += (_, _) =>
        {
            if (_updating || Selected == null || _level.SelectedItem is not ComboBoxItem { Tag: PermissionLevel pl }) return;
            if (pl != PermissionLevel.Custom) Selected.ApplyLevel(pl);
            else Selected.Level = PermissionLevel.Custom;
            ShowSelected();
            _list.Items.Refresh();
        };
        lvl.Children.Add(_level);
        root.Children.Add(lvl);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        GroupBox Box(string header, int r, int c, params UIElement[] items)
        {
            var sp = new StackPanel { Margin = new Thickness(6) };
            foreach (var i in items) { if (i is FrameworkElement fe) fe.Margin = new Thickness(0, 2, 0, 2); sp.Children.Add(i); }
            var g = new GroupBox { Header = header, Content = sp, Margin = new Thickness(0, 0, c == 0 ? 6 : 0, 6) };
            Grid.SetRow(g, r); Grid.SetColumn(g, c);
            grid.Children.Add(g);
            return g;
        }
        _readNone.Content = Loc.T("ReadNone"); _readTime.Content = Loc.T("ReadFreeBusyTime"); _readDetails.Content = Loc.T("ReadFreeBusyDetails"); _readFull.Content = Loc.T("ReadFull");
        foreach (var rb in new[] { _readNone, _readTime, _readDetails, _readFull }) { rb.GroupName = "read"; rb.Checked += (_, _) => Custom(); }
        _create.Content = Loc.T("CreateItems"); _createSub.Content = Loc.T("CreateSubfolders"); _editOwn.Content = Loc.T("EditOwn"); _editAll.Content = Loc.T("EditAll");
        _owner.Content = Loc.T("FolderOwner"); _contact.Content = Loc.T("FolderContact"); _visible.Content = Loc.T("FolderVisible");
        foreach (var cb in new[] { _create, _createSub, _editOwn, _editAll, _owner, _contact, _visible }) { cb.Checked += (_, _) => Custom(); cb.Unchecked += (_, _) => Custom(); }
        _delNone.Content = Loc.T("ReadNone"); _delOwn.Content = Loc.T("DeleteOwn"); _delAll.Content = Loc.T("DeleteAllPerm");
        foreach (var rb in new[] { _delNone, _delOwn, _delAll }) { rb.GroupName = "del"; rb.Checked += (_, _) => Custom(); }
        if (!isCalendar) { _readTime.Visibility = Visibility.Collapsed; _readDetails.Visibility = Visibility.Collapsed; }
        Box(Loc.T("Read"), 0, 0, _readNone, _readTime, _readDetails, _readFull);
        Box(Loc.T("Write"), 0, 1, _create, _createSub, _editOwn, _editAll);
        Box(Loc.T("DeleteItemsPerm"), 1, 0, _delNone, _delOwn, _delAll);
        Box(Loc.T("Other"), 1, 1, _owner, _contact, _visible);
        root.Children.Add(grid);

        root.Children.Add(ButtonRow(MakeButton(Loc.T("Ok"), true, false, async () => { if (await SaveAsync()) DialogResult = true; }),
            MakeButton(Loc.T("Cancel"), false, true),
            MakeButton(Loc.T("Apply"), false, false, async () => await SaveAsync())));
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
    }

    private sealed class StandardUserConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
            value is string s && s is "Default" or "Anonymous" ? Loc.T(s) : value;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    private PermissionEntry? Selected => _list.SelectedItem as PermissionEntry;

    private async Task LoadAsync()
    {
        IReadOnlyList<PermissionEntry> list = Array.Empty<PermissionEntry>();
        if (!await Ui.RunAsync(async () => list = await _svc.GetFolderPermissionsAsync(_folderId), this)) { Close(); return; }
        _entries.Clear();
        foreach (var e in list.OrderBy(e => e.StandardUser == "Default" ? 0 : e.StandardUser == "Anonymous" ? 1 : 2).ThenBy(e => e.Display)) _entries.Add(e);
        if (_entries.Count > 0) _list.SelectedIndex = 0;
    }

    private void ShowSelected()
    {
        var e = Selected;
        _updating = true;
        var enabled = e != null;
        _level.IsEnabled = enabled;
        if (e != null)
        {
            _level.SelectedItem = _level.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (PermissionLevel)i.Tag == e.Level);
            _readNone.IsChecked = e.ReadItems == ReadAccess.None;
            _readTime.IsChecked = e.ReadItems == ReadAccess.TimeOnly;
            _readDetails.IsChecked = e.ReadItems == ReadAccess.TimeAndSubjectAndLocation;
            _readFull.IsChecked = e.ReadItems == ReadAccess.FullDetails;
            _create.IsChecked = e.CanCreateItems;
            _createSub.IsChecked = e.CanCreateSubFolders;
            _editOwn.IsChecked = e.EditItems != ItemAccessScope.None;
            _editAll.IsChecked = e.EditItems == ItemAccessScope.All;
            _delNone.IsChecked = e.DeleteItems == ItemAccessScope.None;
            _delOwn.IsChecked = e.DeleteItems == ItemAccessScope.Owned;
            _delAll.IsChecked = e.DeleteItems == ItemAccessScope.All;
            _owner.IsChecked = e.IsFolderOwner;
            _contact.IsChecked = e.IsFolderContact;
            _visible.IsChecked = e.IsFolderVisible;
        }
        _updating = false;
    }

    private void Custom()
    {
        if (_updating || Selected == null) return;
        var e = Selected;
        e.ReadItems = _readFull.IsChecked == true ? ReadAccess.FullDetails : _readDetails.IsChecked == true ? ReadAccess.TimeAndSubjectAndLocation : _readTime.IsChecked == true ? ReadAccess.TimeOnly : ReadAccess.None;
        e.CanCreateItems = _create.IsChecked == true;
        e.CanCreateSubFolders = _createSub.IsChecked == true;
        e.EditItems = _editAll.IsChecked == true ? ItemAccessScope.All : _editOwn.IsChecked == true ? ItemAccessScope.Owned : ItemAccessScope.None;
        e.DeleteItems = _delAll.IsChecked == true ? ItemAccessScope.All : _delOwn.IsChecked == true ? ItemAccessScope.Owned : ItemAccessScope.None;
        e.IsFolderOwner = _owner.IsChecked == true;
        e.IsFolderContact = _contact.IsChecked == true;
        e.IsFolderVisible = _visible.IsChecked == true;
        // Map back to a named level if it matches one exactly
        var match = Levels.Where(l => l != PermissionLevel.Custom).FirstOrDefault(l =>
        {
            var probe = new PermissionEntry();
            probe.ApplyLevel(l);
            return probe.ReadItems == e.ReadItems && probe.CanCreateItems == e.CanCreateItems && probe.CanCreateSubFolders == e.CanCreateSubFolders &&
                   probe.EditItems == e.EditItems && probe.DeleteItems == e.DeleteItems && probe.IsFolderOwner == e.IsFolderOwner &&
                   probe.IsFolderContact == e.IsFolderContact && probe.IsFolderVisible == e.IsFolderVisible;
        }, PermissionLevel.Custom);
        e.Level = match;
        _updating = true;
        _level.SelectedItem = _level.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (PermissionLevel)i.Tag == match);
        _updating = false;
        _list.Items.Refresh();
    }

    private void AddUser()
    {
        var w = new AddressBookWindow(_svc, AddressBookMode.SelectUsers) { Owner = this, Title = Loc.T("Add").TrimEnd('.') };
        if (w.ShowDialog() != true) return;
        foreach (var r in w.To.Concat(w.Cc).Concat(w.Bcc))
        {
            if (_entries.Any(e => string.Equals(e.Email, r.Address, StringComparison.OrdinalIgnoreCase))) continue;
            var e = new PermissionEntry { Email = r.Address, Name = r.Display };
            e.ApplyLevel(_isCalendar ? PermissionLevel.Reviewer : PermissionLevel.Reviewer);
            _entries.Add(e);
            _list.SelectedItem = e;
        }
    }

    private void RemoveUser()
    {
        if (Selected is { IsStandard: false } e) _entries.Remove(e);
    }

    private async Task<bool> SaveAsync() => await Ui.RunAsync(() => _svc.SetFolderPermissionsAsync(_folderId, _entries.ToList()), this);
}

// =========================================================================================
// Share calendar (sharing invitation)
// =========================================================================================
public sealed class ShareCalendarWindow : DialogWindow
{
    private readonly IMailService _svc;
    private readonly CalendarSource _source;
    private readonly Controls.RecipientBox _to = new();
    private readonly ComboBox _details = new() { Width = 260 };
    private readonly CheckBox _requestBack = new();
    private readonly TextBox _note = new() { AcceptsReturn = true, Height = 110, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    public ShareCalendarWindow(IMailService svc, CalendarSource source)
    {
        _svc = svc;
        _source = source;
        Title = Loc.T("ShareCalendarTitle");
        _to.Service = svc;
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12), Width = 520 };
        root.Children.Add(new TextBlock { Text = Loc.T("ShareCalendarIntro"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = Brushes.DimGray });
        var toRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var toBtn = MakeButton(Loc.T("To"), false, false, () =>
        {
            var w = new AddressBookWindow(_svc, AddressBookMode.SelectRecipients) { Owner = this };
            if (w.ShowDialog() == true) _to.Add(w.To.Concat(w.Cc));
        });
        toBtn.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(toBtn, Dock.Left);
        toRow.Children.Add(toBtn);
        toRow.Children.Add(_to);
        root.Children.Add(toRow);
        root.Children.Add(new TextBlock { Text = Loc.T("Subject") + ": " + Loc.F("ShareCalendarMailSubject", source.DisplayName), Margin = new Thickness(0, 0, 0, 10) });

        var det = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        det.Children.Add(new TextBlock { Text = Loc.T("AccessLevel") + ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var (key, lvl) in new[] { ("AvailabilityOnly", PermissionLevel.FreeBusyTimeOnly), ("LimitedDetails", PermissionLevel.FreeBusyTimeAndSubjectAndLocation),
                     ("FullDetails", PermissionLevel.Reviewer), ("CanEdit", PermissionLevel.Editor), ("Delegate", PermissionLevel.Owner) })
            _details.Items.Add(new ComboBoxItem { Content = Loc.T(key), Tag = lvl });
        _details.SelectedIndex = 2;
        det.Children.Add(_details);
        root.Children.Add(det);
        _requestBack.Content = Loc.T("AlsoRequestPermission");
        root.Children.Add(_requestBack);
        root.Children.Add(new TextBlock { Text = Loc.T("ContactNotes") + ":", Margin = new Thickness(0, 10, 0, 4) });
        root.Children.Add(_note);
        root.Children.Add(ButtonRow(MakeButton(Loc.T("Send"), true, false, async () => await ShareAsync()), MakeButton(Loc.T("Cancel"), false, true)));
        Content = root;
        Loaded += (_, _) => _to.Focus();
    }

    private async Task ShareAsync()
    {
        if (!await _to.CheckNamesAsync(this)) return;
        var recipients = _to.Recipients;
        if (recipients.Count == 0) { Ui.Error(Loc.T("NoRecipients"), this); return; }
        var level = (PermissionLevel)((ComboBoxItem)_details.SelectedItem).Tag;
        var ok = await Ui.RunAsync(async () =>
        {
            var folderId = _source.FolderId;
            if (string.IsNullOrEmpty(folderId)) folderId = (await _svc.GetWellKnownFolderAsync(FolderKind.Calendar))?.Id ?? "";
            var perms = (await _svc.GetFolderPermissionsAsync(folderId)).ToList();
            foreach (var r in recipients)
            {
                var existing = perms.FirstOrDefault(p => string.Equals(p.Email, r.Address, StringComparison.OrdinalIgnoreCase));
                if (existing == null) { existing = new PermissionEntry { Email = r.Address, Name = r.Display }; perms.Add(existing); }
                existing.ApplyLevel(level);
            }
            await _svc.SetFolderPermissionsAsync(folderId, perms);

            var me = _svc.UserDisplayName ?? _svc.Account.DisplayName;
            var body = $"<div style=\"font-family:Calibri,'Segoe UI',Sylfaen,sans-serif;font-size:11pt\">" +
                       (string.IsNullOrWhiteSpace(_note.Text) ? "" : HtmlUtil.Encode(_note.Text).Replace("\n", "<br>") + "<br><br>") +
                       $"<div style=\"border:1px solid #C8C6C4;padding:12px;background:#F8F8F8\"><b style=\"font-size:13pt\">\U0001F4C5 {HtmlUtil.Encode(_source.DisplayName)}</b><br>" +
                       Loc.F("ShareCalendarMailBody", HtmlUtil.Encode(me), HtmlUtil.Encode(((ComboBoxItem)_details.SelectedItem).Content?.ToString() ?? "")) +
                       $"<br><br>{HtmlUtil.Encode(_svc.Account.Email)}</div></div>";
            if (_requestBack.IsChecked == true)
                body += $"<p>{(Loc.Instance.IsGeorgian ? "ასევე გთხოვთ, გამიზიაროთ თქვენი კალენდარი." : "I would also like to view your calendar.")}</p>";
            await _svc.SendAsync(new OutgoingMessage
            {
                AccountId = _svc.Account.Id, To = recipients, Subject = Loc.F("ShareCalendarMailSubject", _source.DisplayName), BodyHtml = body
            });
        }, this);
        if (ok)
        {
            Ui.Info(Loc.F("SharedSuccess", string.Join(", ", recipients.Select(r => r.Display))), this);
            DialogResult = true;
        }
    }
}
