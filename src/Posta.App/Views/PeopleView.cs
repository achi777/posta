using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.App.Windows;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Views;

public sealed class ContactFolderRef
{
    public IMailService Service { get; init; } = null!;
    public string FolderId { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>People module: People (list + card), Business Card, Phone and List views.</summary>
public sealed class PeopleView : UserControl
{
    private readonly ObservableCollection<ContactInfo> _contacts = new();
    private readonly Grid _root = new();
    private readonly TextBox _search = new() { Style = (Style)Application.Current.FindResource("SearchBox"), Margin = new Thickness(10, 8, 10, 6) };
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), ItemContainerStyle = (Style)Application.Current.FindResource("PlainListItem"), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xED, 0xEB, 0xE9)), BorderThickness = new Thickness(0), Background = Brushes.White,
        RowHeaderWidth = 0, SelectionMode = DataGridSelectionMode.Extended, CanUserAddRows = false
    };
    private readonly ListBox _cards = new() { BorderThickness = new Thickness(0), Background = Brushes.White };
    private readonly ScrollViewer _cardPane = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.White };
    private readonly ProgressBar _loading = new() { Height = 2, IsIndeterminate = true, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(0) };
    private ContactFolderRef? _folder;
    private bool _loaded;

    public event EventHandler? StatusChanged;
    public int ItemCount => _contacts.Count;
    public ContactFolderRef? Folder => _folder;

    private static AppSettings S => AppServices.Current.Settings;

    public PeopleView()
    {
        Background = Brushes.White;
        _search.Tag = Loc.T("SearchContacts");
        _search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await ReloadAsync(); else if (e.Key == Key.Escape) { _search.Text = ""; await ReloadAsync(); } };
        _list.ItemsSource = _contacts;
        _list.ItemTemplate = PersonTemplate();
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_contacts);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ContactInfo.IndexLetter)));
        _list.GroupStyle.Add(new GroupStyle { HeaderTemplate = LetterHeaderTemplate() });
        _list.SelectionChanged += (_, _) => ShowCard(_list.SelectedItem as ContactInfo);
        _list.MouseDoubleClick += (_, e) => { if (Ui.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) != null) _ = OpenSelectedAsync(); };
        _list.ContextMenuOpening += (_, e) => { _list.ContextMenu = ItemMenu(); };
        _list.ContextMenu = new ContextMenu();

        _grid.ItemsSource = _contacts;
        _grid.MouseDoubleClick += (_, e) => { if (Ui.FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) != null) _ = OpenSelectedAsync(); };
        _grid.ContextMenuOpening += (_, _) => { _grid.ContextMenu = ItemMenu(); };
        _grid.ContextMenu = new ContextMenu();

        _cards.ItemsSource = _contacts;
        _cards.ItemTemplate = BusinessCardTemplate();
        ScrollViewer.SetHorizontalScrollBarVisibility(_cards, ScrollBarVisibility.Disabled);
        _cards.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        _cards.MouseDoubleClick += (_, e) => { if (Ui.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) != null) _ = OpenSelectedAsync(); };

        Content = _root;
        Loc.Instance.LanguageChanged += (_, _) => { _search.Tag = Loc.T("SearchContacts"); BuildLayout(); };
        AppServices.Current.DataChanged += async (_, what) => { if (what == "contacts" && _loaded) await ReloadAsync(); };
        BuildLayout();
    }

    public void ApplyLayout() => BuildLayout();

    private void BuildLayout()
    {
        _root.Children.Clear();
        _root.ColumnDefinitions.Clear();
        _root.RowDefinitions.Clear();
        DetachAll();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());
        _root.Children.Add(_search);

        switch (S.PeopleView)
        {
            case PeopleViewMode.List:
            case PeopleViewMode.Phone:
                BuildGridColumns(S.PeopleView == PeopleViewMode.Phone);
                Grid.SetRow(_grid, 1);
                _root.Children.Add(_grid);
                break;
            case PeopleViewMode.BusinessCard:
            case PeopleViewMode.Card:
                Grid.SetRow(_cards, 1);
                _root.Children.Add(_cards);
                break;
            default:
                _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
                _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _root.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetRow(_list, 1);
                _root.Children.Add(_list);
                var split = new GridSplitter { Style = (Style)Application.Current.FindResource("GridSplitterV") };
                Grid.SetColumn(split, 1);
                Grid.SetRowSpan(split, 2);
                _root.Children.Add(split);
                Grid.SetColumn(_cardPane, 2);
                Grid.SetRowSpan(_cardPane, 2);
                _root.Children.Add(_cardPane);
                if (_list.SelectedItem == null && _contacts.Count > 0) _list.SelectedIndex = 0;
                ShowCard(_list.SelectedItem as ContactInfo);
                break;
        }
        Grid.SetRow(_loading, 1);
        _root.Children.Add(_loading);
    }

    private void DetachAll()
    {
        foreach (var c in new FrameworkElement[] { _search, _list, _grid, _cards, _cardPane, _loading })
            if (c.Parent is Panel p) p.Children.Remove(c);
    }

    private void BuildGridColumns(bool phone)
    {
        _grid.Columns.Clear();
        void Col(string header, string path, double width) =>
            _grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width) });
        Col(Loc.T("DisplayName"), nameof(ContactInfo.DisplayName), 200);
        Col(Loc.T("Company"), nameof(ContactInfo.Company), 160);
        Col(Loc.T("FileAs"), nameof(ContactInfo.FileAs), 170);
        if (phone)
        {
            Col(Loc.T("BusinessPhoneCol"), nameof(ContactInfo.BusinessPhone), 150);
            Col(Loc.T("Mobile").TrimEnd('.'), nameof(ContactInfo.MobilePhone), 150);
            Col(Loc.T("HomePhone"), nameof(ContactInfo.HomePhone), 140);
        }
        else
        {
            Col(Loc.T("JobTitle"), nameof(ContactInfo.JobTitle), 150);
            Col(Loc.T("Department"), nameof(ContactInfo.Department), 140);
            Col(Loc.T("BusinessPhoneCol"), nameof(ContactInfo.BusinessPhone), 140);
        }
        Col(Loc.T("EmailAddressCol"), nameof(ContactInfo.PrimaryEmail), 0);
    }

    private static DataTemplate PersonTemplate()
    {
        var xaml = """
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Grid Margin="4,2">
    <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
    <Grid Width="36" Height="36" Margin="0,0,10,0">
      <Ellipse Fill="{Binding PrimaryEmail, Converter={StaticResource InitialsBrush}}"/>
      <TextBlock Text="{Binding Initials}" Foreground="White" FontWeight="SemiBold" HorizontalAlignment="Center" VerticalAlignment="Center"/>
      <Ellipse>
        <Ellipse.Fill><ImageBrush ImageSource="{Binding Photo, Converter={StaticResource BytesToImage}}" Stretch="UniformToFill"/></Ellipse.Fill>
      </Ellipse>
    </Grid>
    <StackPanel Grid.Column="1" VerticalAlignment="Center">
      <TextBlock Text="{Binding DisplayName}" FontSize="13.5" TextTrimming="CharacterEllipsis"/>
      <TextBlock Text="{Binding PrimaryEmail}" Foreground="#605E5C" FontSize="11.5" TextTrimming="CharacterEllipsis"/>
    </StackPanel>
  </Grid>
</DataTemplate>
""";
        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static DataTemplate LetterHeaderTemplate()
    {
        var xaml = """
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <TextBlock Text="{Binding Name}" FontSize="15" Foreground="#0F6CBD" FontWeight="SemiBold" Margin="10,10,0,2"/>
</DataTemplate>
""";
        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static DataTemplate BusinessCardTemplate()
    {
        var xaml = """
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Border Width="270" Height="150" Margin="6" BorderBrush="#C8C6C4" BorderThickness="1" Background="White" CornerRadius="2">
    <Grid>
      <Grid.ColumnDefinitions><ColumnDefinition Width="80"/><ColumnDefinition/></Grid.ColumnDefinitions>
      <Border Background="#EFF6FC">
        <Grid Width="54" Height="54" VerticalAlignment="Top" Margin="0,14,0,0">
          <Ellipse Fill="{Binding PrimaryEmail, Converter={StaticResource InitialsBrush}}"/>
          <TextBlock Text="{Binding Initials}" Foreground="White" FontSize="18" FontWeight="SemiBold" HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Grid>
      </Border>
      <StackPanel Grid.Column="1" Margin="10,10,8,8">
        <TextBlock Text="{Binding DisplayName}" FontSize="14" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
        <TextBlock Text="{Binding Company}" Foreground="#605E5C" TextTrimming="CharacterEllipsis"/>
        <TextBlock Text="{Binding JobTitle}" Foreground="#605E5C" TextTrimming="CharacterEllipsis"/>
        <TextBlock Text="{Binding BusinessPhone}" Margin="0,8,0,0" TextTrimming="CharacterEllipsis"/>
        <TextBlock Text="{Binding MobilePhone}" TextTrimming="CharacterEllipsis"/>
        <TextBlock Text="{Binding PrimaryEmail}" Foreground="#0F6CBD" TextTrimming="CharacterEllipsis"/>
      </StackPanel>
    </Grid>
  </Border>
</DataTemplate>
""";
        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    // =====================================================================================
    // Contact card (People view right pane)
    // =====================================================================================

    private async void ShowCard(ContactInfo? summary)
    {
        if (summary == null || _folder == null)
        {
            _cardPane.Content = new TextBlock { Text = Loc.T("SelectItemToRead"), Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
            return;
        }
        ContactInfo c = summary;
        try { c = await _folder.Service.GetContactAsync(summary.Id); } catch { }
        if (_list.SelectedItem != summary) return;
        _cardPane.Content = BuildCard(c, _folder.Service);
    }

    public static FrameworkElement BuildCard(ContactInfo c, IMailService svc)
    {
        var root = new StackPanel { Margin = new Thickness(30, 24, 30, 24), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        var header = new DockPanel();
        var avatar = new Grid { Width = 84, Height = 84, Margin = new Thickness(0, 0, 18, 0) };
        avatar.Children.Add(new System.Windows.Shapes.Ellipse { Fill = InitialsBrushConverter.For(c.PrimaryEmail) });
        avatar.Children.Add(new TextBlock { Text = c.Initials, Foreground = Brushes.White, FontSize = 28, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (c.Photo is { Length: > 0 })
        {
            var img = new BytesToImageConverter().Convert(c.Photo, typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture) as ImageSource;
            if (img != null) avatar.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill } });
        }
        DockPanel.SetDock(avatar, Dock.Left);
        header.Children.Add(avatar);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = c.DisplayName, FontSize = 24, FontWeight = FontWeights.Light, TextWrapping = TextWrapping.Wrap });
        var sub = string.Join(", ", new[] { c.JobTitle, c.Department, c.Company }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (sub.Length > 0) names.Children.Add(new TextBlock { Text = sub, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button Action(string glyph, string text, Action click)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Ui.GlyphText(glyph, 14, GlyphFactory.Blue));
            sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0) });
            var b = new Button { Style = (Style)Application.Current.FindResource("FlatButton"), Content = sp, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 4, 8, 4) };
            b.Click += (_, _) => click();
            return b;
        }
        if (!string.IsNullOrWhiteSpace(c.PrimaryEmail) || c.IsGroup)
        {
            actions.Children.Add(Action(G.Mail, Loc.T("SendEmail"), () => MailActions.ComposeNew(svc, c.IsGroup ? c.Members : new[] { new Recipient(c.DisplayName, c.PrimaryEmail) })));
            actions.Children.Add(Action(G.Calendar, Loc.T("ScheduleMeeting"), () =>
            {
                var start = DateTime.Now.Date.AddHours(DateTime.Now.Hour + 1);
                var ev = new CalendarEvent { Start = start, End = start.AddMinutes(30), IsMeeting = true, ReminderSet = true, ReminderMinutes = 15 };
                foreach (var r in c.IsGroup ? c.Members : new List<Recipient> { new(c.DisplayName, c.PrimaryEmail) })
                    ev.Attendees.Add(new Attendee { Name = r.Name, Email = r.Address });
                AppointmentWindow.OpenNew(svc, null, ev, true);
            }));
        }
        if (!string.IsNullOrWhiteSpace(c.MobilePhone) || !string.IsNullOrWhiteSpace(c.BusinessPhone))
            actions.Children.Add(Action(G.Phone, Loc.T("Call"), () => Ui.OpenUrl("tel:" + (string.IsNullOrWhiteSpace(c.MobilePhone) ? c.BusinessPhone : c.MobilePhone).Replace(" ", ""))));
        names.Children.Add(actions);
        header.Children.Add(names);
        root.Children.Add(header);
        root.Children.Add(new Rectangle2());

        void Section(string title, IEnumerable<(string label, string value, bool link)> rows)
        {
            var list = rows.Where(r => !string.IsNullOrWhiteSpace(r.value)).ToList();
            if (list.Count == 0) return;
            root.Children.Add(new TextBlock { Text = title, FontSize = 15, Foreground = GlyphFactory.Blue, Margin = new Thickness(0, 18, 0, 6) });
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            int r = 0;
            foreach (var (label, value, link) in list)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = new TextBlock { Text = label, Foreground = Brushes.DimGray, Margin = new Thickness(0, 3, 10, 3) };
                FrameworkElement v;
                if (link)
                {
                    var btn = new Button { Style = (Style)Application.Current.FindResource("LinkButton"), Content = value, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3) };
                    var val = value;
                    btn.Click += (_, _) => MailActions.ComposeNew(svc, new[] { new Recipient(c.DisplayName, val) });
                    v = btn;
                }
                else v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
                Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
                g.Children.Add(l); g.Children.Add(v);
                r++;
            }
            root.Children.Add(g);
        }

        if (c.IsGroup)
        {
            Section(Loc.T("Members"), c.Members.Select(m => (m.Name, m.Address, true)));
        }
        else
        {
            Section(Loc.Instance.IsGeorgian ? "საკონტაქტო ინფორმაცია" : "Contact information", new[]
            {
                (Loc.T("Email").TrimEnd('.'), c.Email1, true), (Loc.T("Email2").TrimEnd('.'), c.Email2, true), (Loc.T("Email3").TrimEnd('.'), c.Email3, true),
                (Loc.T("BusinessPhoneCol"), c.BusinessPhone, false), (Loc.T("Mobile").TrimEnd('.'), c.MobilePhone, false), (Loc.T("HomePhone").TrimEnd('.'), c.HomePhone, false),
                (Loc.T("BusinessFax").TrimEnd('.'), c.BusinessFax, false), (Loc.T("IMAddress"), c.ImAddress, false), (Loc.T("WebPage"), c.WebPage, false)
            });
            Section(Loc.T("Work"), new[]
            {
                (Loc.T("Company"), c.Company, false), (Loc.T("JobTitle"), c.JobTitle, false), (Loc.T("Department"), c.Department, false), (Loc.T("Office"), c.Office, false),
                (Loc.T("Manager"), c.Manager, false), (Loc.T("Assistant"), c.Assistant, false), (Loc.T("BusinessAddress").TrimEnd('.'), c.BusinessAddress.ToString(), false)
            });
            Section(Loc.T("Details"), new[]
            {
                (Loc.T("HomeAddress").TrimEnd('.'), c.HomeAddress.ToString(), false), (Loc.T("Birthday"), c.Birthday?.ToString("D", Loc.Instance.Culture) ?? "", false),
                (Loc.T("Anniversary"), c.Anniversary?.ToString("D", Loc.Instance.Culture) ?? "", false), (Loc.T("Spouse"), c.SpouseName, false), (Loc.T("Nickname"), c.Nickname, false)
            });
            if (!string.IsNullOrWhiteSpace(c.Notes))
            {
                root.Children.Add(new TextBlock { Text = Loc.T("ContactNotes"), FontSize = 15, Foreground = GlyphFactory.Blue, Margin = new Thickness(0, 18, 0, 6) });
                root.Children.Add(new TextBlock { Text = c.Notes, TextWrapping = TextWrapping.Wrap });
            }
        }
        return root;
    }

    private sealed class Rectangle2 : Border
    {
        public Rectangle2() { Height = 1; Background = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD)); Margin = new Thickness(0, 18, 0, 0); }
    }

    // =====================================================================================
    // Data
    // =====================================================================================

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        if (_folder == null)
        {
            var svc = AppServices.Current.DefaultAccount;
            if (svc == null) return;
            _folder = new ContactFolderRef { Service = svc, FolderId = "", Name = Loc.T("F_Contacts") };
        }
        await ReloadAsync();
    }

    public async Task ShowFolderAsync(ContactFolderRef f)
    {
        _folder = f;
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        if (_folder == null) { await EnsureLoadedAsync(); return; }
        _loading.Visibility = Visibility.Visible;
        try
        {
            var list = await _folder.Service.GetContactsAsync(_folder.FolderId, string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text.Trim());
            var selected = (_list.SelectedItem as ContactInfo)?.Id;
            _contacts.Clear();
            foreach (var c in list.OrderBy(c => c.SortKey, StringComparer.CurrentCultureIgnoreCase)) _contacts.Add(c);
            _loaded = true;
            if (selected != null) _list.SelectedItem = _contacts.FirstOrDefault(c => c.Id == selected);
            if (_list.SelectedItem == null && _contacts.Count > 0 && S.PeopleView == PeopleViewMode.People) _list.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _cardPane.Content = new TextBlock { Text = ex.Message, Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap };
        }
        finally
        {
            _loading.Visibility = Visibility.Collapsed;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetViewMode(PeopleViewMode m)
    {
        S.PeopleView = m;
        AppServices.Current.SettingsService.Save();
        BuildLayout();
    }

    private List<ContactInfo> Selected => S.PeopleView switch
    {
        PeopleViewMode.List or PeopleViewMode.Phone => _grid.SelectedItems.Cast<ContactInfo>().ToList(),
        PeopleViewMode.BusinessCard or PeopleViewMode.Card => _cards.SelectedItems.Cast<ContactInfo>().ToList(),
        _ => _list.SelectedItems.Cast<ContactInfo>().ToList()
    };

    private ContextMenu ItemMenu()
    {
        var m = new ContextMenu();
        var sel = Selected;
        m.Items.Add(Ui.Menu(Loc.T("Open"), () => _ = OpenSelectedAsync(), G.Open, sel.Count == 1));
        m.Items.Add(Ui.Menu(Loc.T("SendEmail"), EmailSelected, G.Mail, sel.Count > 0));
        m.Items.Add(Ui.Menu(Loc.T("ScheduleMeeting"), MeetingWithSelected, G.Calendar, sel.Count > 0));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Menu(Loc.T("Delete"), () => _ = DeleteSelectedAsync(), G.Delete, sel.Count > 0));
        return m;
    }

    public void NewContact(bool group)
    {
        var svc = _folder?.Service ?? AppServices.Current.DefaultAccount;
        if (svc == null) return;
        if (group) new ContactGroupWindow(svc, _folder?.FolderId ?? "", null).Show();
        else new ContactWindow(svc, _folder?.FolderId ?? "", null).Show();
    }

    public async Task OpenSelectedAsync()
    {
        var c = Selected.FirstOrDefault();
        if (c == null || _folder == null) return;
        ContactInfo? full = null;
        if (!await Ui.RunAsync(async () => full = await _folder.Service.GetContactAsync(c.Id))) return;
        if (full!.IsGroup) new ContactGroupWindow(_folder.Service, _folder.FolderId, full).Show();
        else new ContactWindow(_folder.Service, _folder.FolderId, full).Show();
    }

    public async Task DeleteSelectedAsync()
    {
        var sel = Selected;
        if (sel.Count == 0 || _folder == null) return;
        foreach (var c in sel)
        {
            if (await Ui.RunAsync(() => _folder.Service.DeleteContactAsync(c.Id))) _contacts.Remove(c);
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void EmailSelected()
    {
        var sel = Selected;
        if (sel.Count == 0) return;
        var to = sel.SelectMany(c => c.IsGroup ? c.Members : new List<Recipient> { new(c.DisplayName, c.PrimaryEmail) }).Where(r => !string.IsNullOrWhiteSpace(r.Address));
        MailActions.ComposeNew(_folder?.Service, to);
    }

    public void MeetingWithSelected()
    {
        var sel = Selected;
        var svc = _folder?.Service ?? AppServices.Current.DefaultAccount;
        if (sel.Count == 0 || svc == null) return;
        var start = DateTime.Now.Date.AddHours(DateTime.Now.Hour + 1);
        var ev = new CalendarEvent { Start = start, End = start.AddMinutes(S.DefaultMeetingMinutes), IsMeeting = true, ReminderSet = S.DefaultRemindersOn, ReminderMinutes = S.DefaultReminderMinutes };
        foreach (var r in sel.SelectMany(c => c.IsGroup ? c.Members : new List<Recipient> { new(c.DisplayName, c.PrimaryEmail) }))
            ev.Attendees.Add(new Attendee { Name = r.Name, Email = r.Address });
        AppointmentWindow.OpenNew(svc, null, ev, true);
    }

    public void Print()
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;
        var doc = new FlowDocument { PagePadding = new Thickness(50), FontFamily = new FontFamily("Segoe UI, Sylfaen"), FontSize = 11, ColumnWidth = double.PositiveInfinity };
        foreach (var c in _contacts)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            p.Inlines.Add(new Bold(new Run(c.DisplayName)));
            foreach (var line in new[] { c.Company, c.JobTitle, c.PrimaryEmail, c.BusinessPhone, c.MobilePhone }.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                p.Inlines.Add(new LineBreak());
                p.Inlines.Add(new Run(line));
            }
            doc.Blocks.Add(p);
        }
        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, Loc.T("F_Contacts"));
    }
}
