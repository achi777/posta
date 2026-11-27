using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.App.Views;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Windows;

// =========================================================================================
// Series / occurrence choice
// =========================================================================================
public sealed class SeriesChoiceWindow : DialogWindow
{
    private readonly RadioButton _first = new() { IsChecked = true, Margin = new Thickness(0, 8, 0, 4) };
    public bool FirstChosen => _first.IsChecked == true;

    public SeriesChoiceWindow(string question, string first, string second)
    {
        Title = Loc.T("AppName");
        var sp = new StackPanel { Margin = new Thickness(20, 16, 20, 14), MinWidth = 320 };
        sp.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap });
        _first.Content = first;
        sp.Children.Add(_first);
        sp.Children.Add(new RadioButton { Content = second });
        sp.Children.Add(ButtonRow(MakeButton(Loc.T("Ok"), true, false, () => DialogResult = true), MakeButton(Loc.T("Cancel"), false, true)));
        Content = sp;
    }
}

// =========================================================================================
// Meeting response / cancellation comment
// =========================================================================================
public sealed class ResponseCommentWindow : DialogWindow
{
    private readonly TextBox _box = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    public string CommentHtml => HtmlUtil.PlainTextToHtml(_box.Text).Replace("font-family:Consolas,'Courier New',monospace;", "");

    public ResponseCommentWindow(string title)
    {
        Title = title;
        var sp = new StackPanel { Margin = new Thickness(18, 14, 18, 14), Width = 460 };
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(_box);
        sp.Children.Add(ButtonRow(MakeButton(Loc.T("Send"), true, false, () => DialogResult = true), MakeButton(Loc.T("Cancel"), false, true)));
        Content = sp;
        Loaded += (_, _) => _box.Focus();
    }
}

// =========================================================================================
// Delay delivery
// =========================================================================================
public sealed class DelayDeliveryWindow : DialogWindow
{
    private readonly CheckBox _enabled = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly DatePicker _date = new() { Width = 150 };
    private readonly TextBox _time = new() { Width = 70, Margin = new Thickness(8, 0, 0, 0), Height = 24, VerticalContentAlignment = VerticalAlignment.Center };
    public DateTime? Value { get; private set; }

    public DelayDeliveryWindow(DateTime? current)
    {
        Title = Loc.T("DelayDelivery");
        var v = current ?? DateTime.Today.AddDays(1).AddHours(8);
        _enabled.Content = Loc.T("DoNotDeliverBefore");
        _enabled.IsChecked = current.HasValue;
        _date.SelectedDate = v.Date;
        _time.Text = v.ToString("HH:mm");
        var sp = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        sp.Children.Add(_enabled);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_date);
        row.Children.Add(_time);
        sp.Children.Add(row);
        sp.Children.Add(ButtonRow(MakeButton(Loc.T("Ok"), true, false, () =>
        {
            if (_enabled.IsChecked == true && _date.SelectedDate.HasValue && TimeSpan.TryParse(_time.Text, out var t)) Value = _date.SelectedDate.Value.Date + t;
            else Value = null;
            DialogResult = true;
        }), MakeButton(Loc.T("Cancel"), false, true)));
        Content = sp;
    }
}

// =========================================================================================
// Custom flag
// =========================================================================================
public sealed class CustomFlagWindow : DialogWindow
{
    private readonly DatePicker _start = new() { Width = 160, SelectedDate = DateTime.Today };
    private readonly DatePicker _due = new() { Width = 160, SelectedDate = DateTime.Today };
    public DateTime? StartDate => _start.SelectedDate;
    public DateTime? DueDate => _due.SelectedDate;

    public CustomFlagWindow()
    {
        Title = Loc.T("CustomFlag");
        var g = new Grid { Margin = new Thickness(18, 14, 18, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition());
        void Row(int r, string label, UIElement e)
        {
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(t, r); Grid.SetRow(e, r); Grid.SetColumn(e, 1);
            g.Children.Add(t); g.Children.Add(e);
        }
        Row(0, Loc.T("StartDate"), _start);
        Row(1, Loc.T("DueDate"), _due);
        var sp = new StackPanel();
        sp.Children.Add(g);
        var btns = ButtonRow(MakeButton(Loc.T("Ok"), true, false, () => DialogResult = true), MakeButton(Loc.T("Cancel"), false, true));
        btns.Margin = new Thickness(18, 10, 18, 14);
        sp.Children.Add(btns);
        Content = sp;
    }
}

// =========================================================================================
// Color categories
// =========================================================================================
public sealed class CategoriesWindow : DialogWindow
{
    private readonly StackPanel _list = new();
    private readonly HashSet<string> _selected;
    public List<string> SelectedCategories => _selected.ToList();

    public CategoriesWindow(IEnumerable<string> current)
    {
        Title = Loc.T("CategorizeTitle");
        _selected = new HashSet<string>(current);
        var root = new DockPanel { Margin = new Thickness(16, 12, 16, 12), Width = 440 };
        var right = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        var add = MakeButton(Loc.T("NewCategory"), false, false, AddCategory); add.Margin = new Thickness(0, 0, 0, 6);
        var rename = MakeButton(Loc.T("Rename"), false, false, RenameSelected); rename.Margin = new Thickness(0, 0, 0, 6);
        var del = MakeButton(Loc.T("Delete"), false, false, DeleteSelected); del.Margin = new Thickness(0, 0, 0, 6);
        right.Children.Add(add); right.Children.Add(rename); right.Children.Add(del);
        DockPanel.SetDock(right, Dock.Right);
        root.Children.Add(right);
        var btns = ButtonRow(MakeButton(Loc.T("Ok"), true, false, () => { AppServices.Current.SettingsService.Save(); DialogResult = true; }), MakeButton(Loc.T("Cancel"), false, true));
        DockPanel.SetDock(btns, Dock.Bottom);
        root.Children.Add(btns);
        root.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Height = 260, Child = new ScrollViewer { Content = _list } });
        Content = root;
        Rebuild();
    }

    private string? _focused;

    private void Rebuild()
    {
        _list.Children.Clear();
        foreach (var c in AppServices.Current.Settings.Categories)
        {
            var name = c.Name;
            var cb = new CheckBox { IsChecked = _selected.Contains(name), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
            cb.Checked += (_, _) => _selected.Add(name);
            cb.Unchecked += (_, _) => _selected.Remove(name);
            var swatch = new Border { Width = 16, Height = 14, Background = GlyphFactory.ParseBrush(c.Color, Brushes.Gray), Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(2), Cursor = Cursors.Hand };
            var cat = c;
            swatch.MouseLeftButtonUp += (_, _) => PickColor(cat);
            var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Background = _focused == name ? (Brush)FindResource("SelectionBrush") : Brushes.Transparent };
            dp.Children.Add(cb);
            dp.Children.Add(swatch);
            dp.Children.Add(new TextBlock { Text = Loc.T(name), VerticalAlignment = VerticalAlignment.Center });
            dp.MouseLeftButtonUp += (_, _) => { _focused = name; Rebuild(); };
            _list.Children.Add(dp);
        }
    }

    private void PickColor(CategoryDefinition c)
    {
        var menu = new ContextMenu();
        foreach (var hex in new[] { "#E7A1A2", "#F9BA89", "#F7DD8F", "#78D168", "#9FC6EF", "#B9A6E7", "#C2C2C2", "#ED8C8E", "#FFD39B", "#86D1D6", "#C6A1E7", "#A2D6A4" })
        {
            var h = hex;
            menu.Items.Add(new MenuItem { Header = new Border { Width = 60, Height = 14, Background = GlyphFactory.ParseBrush(h, Brushes.Gray) }, Command = new RelayAction(() => { c.Color = h; Rebuild(); }) });
        }
        menu.IsOpen = true;
    }

    private void AddCategory()
    {
        var name = Ui.Prompt(Loc.T("NewCategory"), Loc.T("CategoryName"), "", this);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (AppServices.Current.Settings.Categories.Any(x => x.Name == name)) return;
        AppServices.Current.Settings.Categories.Add(new CategoryDefinition { Name = name, Color = "#9FC6EF" });
        _selected.Add(name);
        Rebuild();
    }

    private void RenameSelected()
    {
        var c = AppServices.Current.Settings.Categories.FirstOrDefault(x => x.Name == _focused);
        if (c == null) return;
        var name = Ui.Prompt(Loc.T("Rename"), Loc.T("CategoryName"), Loc.T(c.Name), this);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_selected.Remove(c.Name)) _selected.Add(name);
        c.Name = name;
        _focused = name;
        Rebuild();
    }

    private void DeleteSelected()
    {
        var c = AppServices.Current.Settings.Categories.FirstOrDefault(x => x.Name == _focused);
        if (c == null) return;
        AppServices.Current.Settings.Categories.Remove(c);
        _selected.Remove(c.Name);
        Rebuild();
    }
}

public sealed class RelayAction : ICommand
{
    private readonly Action _a;
    public RelayAction(Action a) => _a = a;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _a();
}

// =========================================================================================
// Folder picker (Move / Copy / rule targets)
// =========================================================================================
public class FolderPickerWindow : DialogWindow
{
    protected readonly TreeView Tree = new() { Height = 320, Width = 380 };
    public MailFolderInfo? SelectedFolder { get; private set; }
    private readonly bool _allowRoot;

    public FolderPickerWindow(IMailService svc, string prompt, List<MailFolderInfo>? folders, bool allowRoot = false)
    {
        Title = Loc.T("ChooseFolder");
        _allowRoot = allowRoot;
        var sp = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        sp.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
        sp.Children.Add(Tree);
        var newBtn = MakeButton(Loc.T("NewFolder") + "...", false, false, async () =>
        {
            var name = Ui.Prompt(Loc.T("NewFolder"), Loc.T("NewFolderPrompt"), "", this);
            if (string.IsNullOrWhiteSpace(name)) return;
            var parent = SelectedFolderFromTree()?.Id ?? (await svc.GetWellKnownFolderAsync(FolderKind.Root))?.Id;
            if (parent == null) return;
            if (await Ui.RunAsync(() => svc.CreateFolderAsync(parent, name, "IPF.Note"), this))
            {
                await LoadAsync(svc, null);
                AppServices.Current.RaiseDataChanged("folders");
            }
        });
        newBtn.Margin = new Thickness(0);
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(newBtn);
        var buttons = ButtonRow(MakeButton(Loc.T("Ok"), true, false, () =>
        {
            SelectedFolder = SelectedFolderFromTree();
            if (SelectedFolder != null || _allowRoot) DialogResult = true;
        }), MakeButton(Loc.T("Cancel"), false, true));
        buttons.Margin = new Thickness(0);
        row.Children.Add(buttons);
        sp.Children.Add(row);
        Content = sp;
        Loaded += async (_, _) => await LoadAsync(svc, folders);
    }

    private MailFolderInfo? SelectedFolderFromTree() => (Tree.SelectedItem as TreeViewItem)?.Tag as MailFolderInfo;

    private async Task LoadAsync(IMailService svc, List<MailFolderInfo>? folders)
    {
        if (folders == null || folders.Count == 0)
        {
            try { folders = (await svc.GetFolderTreeAsync()).ToList(); } catch { folders = new List<MailFolderInfo>(); }
        }
        Tree.Items.Clear();
        var root = new TreeViewItem { Header = svc.Account.Email, IsExpanded = true, FontWeight = FontWeights.SemiBold };
        Tree.Items.Add(root);
        var byParent = folders.Where(f => f.MailboxEmail == null && (f.IsMailFolder || f.Kind == FolderKind.Root)).ToLookup(f => f.ParentId ?? "");
        void Add(ItemsControl parent, string id)
        {
            foreach (var f in byParent[id].OrderBy(f => f.Kind == FolderKind.Generic ? 1 : 0).ThenBy(f => FolderVisuals.LocalizedName(f)))
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(Ui.GlyphText(FolderVisuals.Glyph(f.Kind, f.FolderClass), 13, Brushes.DimGray));
                sp.Children.Add(new TextBlock { Text = FolderVisuals.LocalizedName(f), Margin = new Thickness(6, 0, 0, 0) });
                var item = new TreeViewItem { Header = sp, Tag = f, FontWeight = FontWeights.Normal, IsExpanded = f.Kind == FolderKind.Inbox };
                parent.Items.Add(item);
                Add(item, f.Id);
            }
        }
        Add(root, "");
    }
}

// =========================================================================================
// New folder
// =========================================================================================
public sealed class NewFolderWindow : FolderPickerWindow
{
    private readonly TextBox _name = new() { Style = (Style)Application.Current.FindResource("FieldBox") };
    private readonly ComboBox _type = new() { Height = 24 };
    public string FolderName => _name.Text.Trim();
    public string FolderClass => (_type.SelectedItem as ComboBoxItem)?.Tag as string ?? "IPF.Note";
    public MailFolderInfo? ParentFolder => SelectedFolder;

    public NewFolderWindow(IMailService svc, List<MailFolderInfo> folders, MailFolderInfo? parent)
        : base(svc, Loc.T("SelectWherePlace"), folders, allowRoot: true)
    {
        Title = Loc.T("NewFolder");
        foreach (var (key, cls) in new[] { ("MailAndPostItems", "IPF.Note"), ("CalendarItems", "IPF.Appointment"), ("ContactItems", "IPF.Contact"), ("TaskItems", "IPF.Task") })
            _type.Items.Add(new ComboBoxItem { Content = Loc.T(key), Tag = cls });
        _type.SelectedIndex = 0;
        if (Content is StackPanel sp)
        {
            sp.Children.Insert(0, new TextBlock { Text = Loc.T("NewFolderPrompt"), Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Insert(1, _name);
            sp.Children.Insert(2, new TextBlock { Text = Loc.T("FolderContains"), Margin = new Thickness(0, 10, 0, 4) });
            sp.Children.Insert(3, _type);
            sp.Children.Insert(4, new Border { Height = 10 });
        }
        Loaded += (_, _) => _name.Focus();
    }
}

// =========================================================================================
// Open shared calendar (name)
// =========================================================================================
public sealed class OpenCalendarWindow : DialogWindow
{
    private readonly TextBox _name = new() { Style = (Style)Application.Current.FindResource("FieldBox"), Width = 300 };
    private readonly IMailService _svc;
    public string? SelectedEmail { get; private set; }

    public OpenCalendarWindow(IMailService svc)
    {
        _svc = svc;
        Title = Loc.T("OpenSharedCalendarTitle");
        _name.TextChanged += (_, _) => SelectedEmail = null;
        var sp = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var nameBtn = MakeButton(Loc.T("NamePrompt").TrimEnd(':') + "...", false, false, () =>
        {
            var pick = AddressBookWindow.PickOne(_svc, this, _name.Text);
            if (pick != null) { _name.Text = pick.Name; SelectedEmail = pick.Email; }
        });
        nameBtn.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(nameBtn);
        row.Children.Add(_name);
        sp.Children.Add(row);
        sp.Children.Add(ButtonRow(MakeButton(Loc.T("Ok"), true, false, async () => await ResolveAndClose()), MakeButton(Loc.T("Cancel"), false, true)));
        Content = sp;
        Loaded += (_, _) => _name.Focus();
    }

    private async Task ResolveAndClose()
    {
        var text = _name.Text.Trim();
        if (text.Length == 0) return;
        if (SelectedEmail != null) { DialogResult = true; return; }
        if (Ui.IsValidEmail(text)) { SelectedEmail = text; DialogResult = true; return; }
        IReadOnlyList<ResolvedName> found = Array.Empty<ResolvedName>();
        await Ui.RunAsync(async () => found = await _svc.ResolveNamesAsync(text, true), this);
        var pick = found.Count == 1 ? found[0] : found.Count > 1 ? AddressBookWindow.Choose(found, this, text) : null;
        if (pick == null) { Ui.Error(Loc.F("UnresolvedRecipient", text), this); return; }
        SelectedEmail = pick.Email;
        DialogResult = true;
    }
}

// =========================================================================================
// Address book (GAL search) – browse / select recipients / pick one
// =========================================================================================
public enum AddressBookMode { Browse, SelectRecipients, SelectAttendees, SelectUsers, SelectMembers, PickOne }

public sealed class AddressBookWindow : DialogWindow
{
    private readonly IMailService _svc;
    private readonly AddressBookMode _mode;
    private readonly TextBox _search = new() { Height = 24, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListView _results = new() { Height = 300, SelectionMode = SelectionMode.Extended };
    private readonly ObservableCollection<ResolvedName> _items = new();
    private readonly TextBox _to = new(), _cc = new(), _bcc = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public List<Recipient> To { get; private set; } = new();
    public List<Recipient> Cc { get; private set; } = new();
    public List<Recipient> Bcc { get; private set; } = new();
    public ResolvedName? Picked { get; private set; }

    public AddressBookWindow(IMailService svc, AddressBookMode mode, IEnumerable<ResolvedName>? fixedList = null)
    {
        _svc = svc;
        _mode = mode;
        Title = Loc.F("SelectNames", Loc.T("GlobalAddressList"));
        ResizeMode = ResizeMode.CanResize;
        SizeToContent = SizeToContent.Manual;
        Width = 760;
        Height = mode is AddressBookMode.SelectRecipients or AddressBookMode.SelectAttendees ? 600 : mode is AddressBookMode.SelectUsers or AddressBookMode.SelectMembers ? 540 : 500;
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var go = MakeButton(Loc.T("Go"), false, false, async () => await SearchAsync());
        go.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(go, Dock.Right);
        top.Children.Add(go);
        top.Children.Add(new TextBlock { Text = Loc.T("Search") + ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        top.Children.Add(_search);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var hint = new TextBlock { Text = Loc.T("TypeToSearchDirectory"), Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(hint, Dock.Top);
        root.Children.Add(hint);

        // buttons
        var ok = MakeButton(Loc.T("Ok"), true, false, Accept);
        var buttons = ButtonRow(ok, MakeButton(Loc.T("Cancel"), false, true));
        if (mode is AddressBookMode.Browse or AddressBookMode.SelectRecipients)
        {
            var members = MakeButton(Loc.T("Members"), false, false, async () => await ShowGroupMembersAsync());
            members.IsEnabled = false;
            _results.SelectionChanged += (_, _) => members.IsEnabled =
                _results.SelectedItems.Count == 1 && _results.SelectedItem is ResolvedName { Kind: RecipientKind.DistributionList };
            buttons.Children.Insert(0, members);
        }
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        if (mode is AddressBookMode.SelectRecipients or AddressBookMode.SelectAttendees or AddressBookMode.SelectUsers or AddressBookMode.SelectMembers)
        {
            var fields = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            fields.ColumnDefinitions.Add(new ColumnDefinition());
            var rows = mode is AddressBookMode.SelectUsers or AddressBookMode.SelectMembers ? 1 : 3;
            var labels = mode == AddressBookMode.SelectUsers
                ? new[] { Loc.T("Add").TrimEnd('.') }
                : mode == AddressBookMode.SelectMembers
                ? new[] { Loc.T("Members") }
                : mode == AddressBookMode.SelectAttendees
                ? new[] { Loc.T("Required"), Loc.T("Optional"), Loc.T("Resources") }
                : new[] { Loc.T("To"), Loc.T("Cc"), Loc.T("Bcc") };
            var boxes = new[] { _to, _cc, _bcc };
            for (int i = 0; i < rows; i++)
            {
                fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var idx = i;
                var b = MakeButton(labels[i] + " ->", false, false, () => AddSelectedTo(boxes[idx]));
                b.Margin = new Thickness(0, 0, 8, 6);
                boxes[i].Margin = new Thickness(0, 0, 0, 6);
                boxes[i].Height = 24;
                boxes[i].VerticalContentAlignment = VerticalAlignment.Center;
                Grid.SetRow(b, i); Grid.SetRow(boxes[i], i); Grid.SetColumn(boxes[i], 1);
                fields.Children.Add(b); fields.Children.Add(boxes[i]);
            }
            DockPanel.SetDock(fields, Dock.Bottom);
            root.Children.Add(fields);
        }

        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("DisplayName"), DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ResolvedName.Name)), Width = 200 });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("Title2"), DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ResolvedName.Title)), Width = 130 });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("BusinessPhoneCol"), DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ResolvedName.Phone)), Width = 120 });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("Department"), DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ResolvedName.Department)), Width = 110 });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("EmailAddressCol"), DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ResolvedName.Email)), Width = 200 });
        _results.View = gv;
        _results.ItemsSource = _items;
        _results.MouseDoubleClick += (_, _) =>
        {
            if (_mode is AddressBookMode.SelectRecipients or AddressBookMode.SelectAttendees or AddressBookMode.SelectUsers or AddressBookMode.SelectMembers) AddSelectedTo(_to);
            else if (_mode == AddressBookMode.Browse && _results.SelectedItem is ResolvedName r) new ContactCardWindow(_svc, r) { Owner = this }.Show();
            else Accept();
        };
        root.Children.Add(_results);
        Content = root;

        if (fixedList != null) foreach (var r in fixedList) _items.Add(r);
        _search.TextChanged += (_, _) => { _timer.Stop(); if (_search.Text.Trim().Length >= 2) _timer.Start(); };
        _search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } };
        _timer.Tick += async (_, _) => { _timer.Stop(); await SearchAsync(); };
        Loaded += async (_, _) =>
        {
            _search.Focus();
            // Like Outlook: show the address list right away instead of an empty window.
            if (fixedList == null && _items.Count == 0 && _search.Text.Length == 0) await SearchAsync(silent: true);
        };
    }

    public void Preload(IEnumerable<Recipient> to, IEnumerable<Recipient> cc, IEnumerable<Recipient> bcc)
    {
        _to.Text = string.Join("; ", to.Select(r => r.FullDisplay));
        _cc.Text = string.Join("; ", cc.Select(r => r.FullDisplay));
        _bcc.Text = string.Join("; ", bcc.Select(r => r.FullDisplay));
    }

    public void SetQuery(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return;
        _search.Text = q;
        _ = SearchAsync();
    }

    private async Task SearchAsync(bool silent = false)
    {
        var q = _search.Text.Trim();
        try
        {
            var found = await _svc.ResolveNamesAsync(q, _mode is not (AddressBookMode.SelectRecipients or AddressBookMode.SelectMembers));
            _items.Clear();
            foreach (var f in found.OrderBy(f => f.Name)) _items.Add(f);
            if (_items.Count > 0) _results.SelectedIndex = 0;
        }
        catch (Exception ex) { if (!silent) Ui.Error(ex.Message, this); }
    }

    private void AddSelectedTo(TextBox box)
    {
        foreach (var r in _results.SelectedItems.Cast<ResolvedName>())
        {
            var s = r.ToString();
            if (box.Text.Contains(r.Email, StringComparison.OrdinalIgnoreCase)) continue;
            box.Text = string.IsNullOrWhiteSpace(box.Text) ? s : box.Text.TrimEnd().TrimEnd(';') + "; " + s;
        }
    }

    private async Task ShowGroupMembersAsync()
    {
        if (_results.SelectedItem is not ResolvedName { Kind: RecipientKind.DistributionList } group) return;
        IReadOnlyList<Recipient> members = [];
        if (!await Ui.RunAsync(async () => members = await _svc.ExpandGroupAsync(group.Email), this)) return;
        var window = new GroupMembersWindow(group.Name, members) { Owner = this };
        window.ShowDialog();
    }

    private static List<Recipient> ParseList(string text) =>
        text.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).Select(Controls.RecipientBox.Parse).ToList();

    private void Accept()
    {
        switch (_mode)
        {
            case AddressBookMode.SelectRecipients:
            case AddressBookMode.SelectAttendees:
            case AddressBookMode.SelectUsers:
            case AddressBookMode.SelectMembers:
                To = ParseList(_to.Text); Cc = ParseList(_cc.Text); Bcc = ParseList(_bcc.Text);
                DialogResult = true;
                break;
            case AddressBookMode.PickOne:
                Picked = _results.SelectedItem as ResolvedName;
                if (Picked != null) DialogResult = true;
                break;
            default:
                if (_results.SelectedItem is ResolvedName r) new ContactCardWindow(_svc, r) { Owner = this }.Show();
                else DialogResult = true;
                break;
        }
    }

    public static ResolvedName? PickOne(IMailService svc, Window? owner, string? query)
    {
        var w = new AddressBookWindow(svc, AddressBookMode.PickOne) { Owner = owner };
        w.SetQuery(query);
        return w.ShowDialog() == true ? w.Picked : null;
    }

    public static ResolvedName? Choose(IReadOnlyList<ResolvedName> options, Window owner, string query)
    {
        var svc = AppServices.Current.DefaultAccount!;
        var w = new AddressBookWindow(svc, AddressBookMode.PickOne, options) { Owner = owner, Title = Loc.T("CheckNames") + ": " + query };
        return w.ShowDialog() == true ? w.Picked : null;
    }
}

internal sealed class GroupMembersWindow : DialogWindow
{
    public GroupMembersWindow(string groupName, IReadOnlyList<Recipient> members)
    {
        Title = $"{Loc.T("Members")} — {groupName}";
        var root = new DockPanel { Margin = new Thickness(14), Width = 500 };
        var close = ButtonRow(MakeButton(Loc.T("Ok"), true, true, Close));
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(new ListBox
        {
            Height = 290, ItemsSource = members.OrderBy(m => m.Display), DisplayMemberPath = nameof(Recipient.FullDisplay)
        });
        Content = root;
    }
}

// =========================================================================================
// Contact card for a directory entry
// =========================================================================================
public sealed class ContactCardWindow : Window
{
    public ContactCardWindow(IMailService svc, ResolvedName r)
    {
        Title = r.Name;
        Width = 520;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI, Sylfaen");
        var c = new ContactInfo
        {
            DisplayName = r.Name, Email1 = r.Email, JobTitle = r.Title ?? "", Department = r.Department ?? "", Company = r.Company ?? "",
            Office = r.Office ?? "", BusinessPhone = r.Phone ?? "", MobilePhone = r.Mobile ?? ""
        };
        Content = new ScrollViewer { Content = PeopleView.BuildCard(c, svc), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += async (_, _) =>
        {
            var photo = await svc.GetUserPhotoAsync(r.Email);
            if (photo != null) { c.Photo = photo; Content = new ScrollViewer { Content = PeopleView.BuildCard(c, svc) }; }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
