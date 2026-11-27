using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.App.Windows;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Controls;

/// <summary>
/// Recipient field (To/Cc/Bcc): resolved recipients as chips, free typing with auto-complete from
/// recent recipients, contacts and the Global Address List, and "Check Names" resolution.
/// </summary>
public sealed class RecipientBox : Border
{
    private sealed class Chip
    {
        public Recipient Recipient { get; set; } = new();
        public bool Resolved { get; set; }
        public Border Element { get; set; } = null!;
    }

    private readonly WrapPanel _panel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _input = new() { BorderThickness = new Thickness(0), MinWidth = 60, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Padding = new Thickness(0, 2, 0, 2) };
    private readonly Popup _popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly ListBox _suggestions = new() { MaxHeight = 280, MinWidth = 340, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC6, 0xC4)) };
    private readonly List<Chip> _chips = new();
    private readonly DispatcherTimer _lookupTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? _lookupCts;

    public IMailService? Service { get; set; }
    public event EventHandler? Changed;

    public RecipientBox()
    {
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC6, 0xC4));
        BorderThickness = new Thickness(0, 0, 0, 1);
        MinHeight = 26;
        Padding = new Thickness(4, 1, 4, 1);
        Background = Brushes.White;
        Cursor = Cursors.IBeam;
        _panel.Children.Add(_input);
        Child = _panel;
        _popup.Child = new Border { Background = Brushes.White, Child = _suggestions, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.25 } };
        _popup.PlacementTarget = this;
        _suggestions.ItemContainerStyle = (Style)Application.Current.FindResource("PlainListItem");
        _suggestions.MouseLeftButtonUp += (_, _) => AcceptSuggestion();

        MouseLeftButtonDown += (_, _) => _input.Focus();
        _input.PreviewKeyDown += Input_PreviewKeyDown;
        _input.TextChanged += (_, _) =>
        {
            if (_input.Text.EndsWith(';') || _input.Text.EndsWith(','))
            {
                CommitText(_input.Text.TrimEnd(';', ','));
                return;
            }
            _lookupTimer.Stop();
            if (_input.Text.Trim().Length >= 1) _lookupTimer.Start();
            else _popup.IsOpen = false;
        };
        _input.LostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is DependencyObject d && (IsDescendant(_popup.Child, d))) return;
            if (!string.IsNullOrWhiteSpace(_input.Text)) CommitText(_input.Text);
        };
        _lookupTimer.Tick += async (_, _) => { _lookupTimer.Stop(); await LookupAsync(); };
        AllowDrop = true;
    }

    private static bool IsDescendant(DependencyObject? root, DependencyObject d)
    {
        while (d != null)
        {
            if (d == root) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    public new void Focus() => _input.Focus();

    public List<Recipient> Recipients => _chips.Select(c => c.Recipient).ToList();
    public bool HasUnresolved => _chips.Any(c => !c.Resolved) || !string.IsNullOrWhiteSpace(_input.Text);
    public bool IsEmpty => _chips.Count == 0 && string.IsNullOrWhiteSpace(_input.Text);

    public void SetRecipients(IEnumerable<Recipient> recipients)
    {
        foreach (var c in _chips) _panel.Children.Remove(c.Element);
        _chips.Clear();
        foreach (var r in recipients) AddChip(r, Ui.IsValidEmail(r.Address));
    }

    public void Add(IEnumerable<Recipient> recipients)
    {
        foreach (var r in recipients)
            if (_chips.All(c => !string.Equals(c.Recipient.Address, r.Address, StringComparison.OrdinalIgnoreCase)))
                AddChip(r, true);
    }

    private void AddChip(Recipient r, bool resolved)
    {
        var chip = new Chip { Recipient = r, Resolved = resolved };
        var text = new TextBlock
        {
            Text = r.Display,
            Foreground = resolved ? Brushes.Black : Brushes.DarkRed,
            TextDecorations = resolved ? TextDecorations.Underline : null,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!resolved)
        {
            var wavy = new TextDecoration { Location = TextDecorationLocation.Underline, Pen = new Pen(Brushes.Red, 1) { DashStyle = DashStyles.Dot } };
            text.TextDecorations = new TextDecorationCollection { wavy };
        }
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(text);
        sp.Children.Add(new TextBlock { Text = ";", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        var border = new Border
        {
            Child = sp, Padding = new Thickness(2, 1, 0, 1), Margin = new Thickness(0, 1, 2, 1), CornerRadius = new CornerRadius(2),
            ToolTip = r.FullDisplay, Cursor = Cursors.Arrow, Focusable = true, Background = Brushes.Transparent
        };
        chip.Element = border;
        border.MouseEnter += (_, _) => border.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF1, 0xFB));
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2) Edit(chip);
            else border.Focus();
        };
        border.KeyDown += (_, e) => { if (e.Key is Key.Delete or Key.Back) { Remove(chip); _input.Focus(); } };
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Menu(Loc.T("Copy"), () => { try { Clipboard.SetText(r.FullDisplay); } catch { } }, G.Copy));
        menu.Items.Add(Ui.Menu(Loc.T("Edit"), () => Edit(chip), G.Edit));
        menu.Items.Add(Ui.Menu(Loc.T("OpenContactCard"), () => { if (Service != null) new ContactCardWindow(Service, new ResolvedName { Name = r.Name, Email = r.Address }) { Owner = Window.GetWindow(this) }.Show(); }, G.Contact));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Menu(Loc.T("Remove"), () => Remove(chip), G.Delete));
        border.ContextMenu = menu;
        _panel.Children.Insert(_panel.Children.IndexOf(_input), border);
        _chips.Add(chip);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Edit(Chip chip)
    {
        Remove(chip);
        _input.Text = chip.Recipient.Address;
        _input.Focus();
        _input.CaretIndex = _input.Text.Length;
    }

    private void Remove(Chip chip)
    {
        _panel.Children.Remove(chip.Element);
        _chips.Remove(chip);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_popup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down: _suggestions.SelectedIndex = Math.Min(_suggestions.Items.Count - 1, _suggestions.SelectedIndex + 1); e.Handled = true; return;
                case Key.Up: _suggestions.SelectedIndex = Math.Max(0, _suggestions.SelectedIndex - 1); e.Handled = true; return;
                case Key.Enter:
                case Key.Tab when _suggestions.SelectedItem != null:
                    if (AcceptSuggestion()) { e.Handled = e.Key == Key.Enter; return; }
                    break;
                case Key.Escape: _popup.IsOpen = false; e.Handled = true; return;
            }
        }
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_input.Text))
        {
            CommitText(_input.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && _input.Text.Length == 0 && _chips.Count > 0)
        {
            Remove(_chips[^1]);
            e.Handled = true;
        }
    }

    private bool AcceptSuggestion()
    {
        if ((_suggestions.SelectedItem as ListBoxItem)?.Tag is not ResolvedName n) return false;
        _input.Text = "";
        _popup.IsOpen = false;
        AddChip(n.ToRecipient(), true);
        AppServices.Current.SettingsService.AddRecentRecipient(n.ToString());
        _input.Focus();
        return true;
    }

    private void CommitText(string text)
    {
        _input.Text = "";
        _popup.IsOpen = false;
        foreach (var part in text.Split(';', ',').Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var r = Parse(part);
            AddChip(r, Ui.IsValidEmail(r.Address));
        }
    }

    public static Recipient Parse(string s)
    {
        s = s.Trim();
        var lt = s.LastIndexOf('<');
        var gt = s.LastIndexOf('>');
        if (lt >= 0 && gt > lt)
            return new Recipient(s[..lt].Trim().Trim('"'), s[(lt + 1)..gt].Trim());
        return new Recipient("", s);
    }

    private async Task LookupAsync()
    {
        var q = _input.Text.Trim();
        if (q.Length == 0) { _popup.IsOpen = false; return; }
        _lookupCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lookupCts = cts;

        var results = new List<ResolvedName>();
        // Recent recipients (auto-complete list)
        if (AppServices.Current.Settings.AutoCompleteRecipients)
        {
            foreach (var r in AppServices.Current.Settings.RecentRecipients.Where(r => r.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(6))
            {
                var p = Parse(r);
                results.Add(new ResolvedName { Name = p.Name, Email = p.Address });
            }
        }
        Show(results);
        if (Service == null || q.Length < 2) return;
        try
        {
            var found = await Service.ResolveNamesAsync(q, false, cts.Token);
            if (cts.IsCancellationRequested || _input.Text.Trim() != q) return;
            foreach (var f in found)
            {
                var cached = results.FindIndex(r => string.Equals(r.Email, f.Email, StringComparison.OrdinalIgnoreCase));
                if (cached < 0) results.Add(f);
                else if (!string.IsNullOrWhiteSpace(f.Name)) results[cached] = f;
            }
            Show(results);
        }
        catch { }
    }

    private void Show(List<ResolvedName> results)
    {
        _suggestions.Items.Clear();
        foreach (var r in results.Take(12))
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(r.Name) ? r.Email : r.Name, FontWeight = FontWeights.SemiBold });
            var sub = string.Join("  ·  ", new[] { r.Email, r.Title, r.Department }.Where(s => !string.IsNullOrWhiteSpace(s)));
            sp.Children.Add(new TextBlock { Text = sub, Foreground = Brushes.DimGray, FontSize = 11 });
            _suggestions.Items.Add(new ListBoxItem { Content = sp, Tag = r });
        }
        if (_suggestions.Items.Count > 0) _suggestions.SelectedIndex = 0;
        _popup.IsOpen = _suggestions.Items.Count > 0 && _input.IsKeyboardFocused;
    }

    /// <summary>Resolves all unresolved entries against the directory. Returns false if something remains unresolved.</summary>
    public async Task<bool> CheckNamesAsync(Window owner)
    {
        if (!string.IsNullOrWhiteSpace(_input.Text)) CommitText(_input.Text);
        foreach (var chip in _chips.Where(c => !c.Resolved).ToList())
        {
            if (Service == null) return false;
            IReadOnlyList<ResolvedName> found;
            try { found = await Service.ResolveNamesAsync(string.IsNullOrWhiteSpace(chip.Recipient.Address) ? chip.Recipient.Name : chip.Recipient.Address); }
            catch { found = Array.Empty<ResolvedName>(); }
            ResolvedName? pick = found.Count == 1 ? found[0] : found.Count > 1 ? AddressBookWindow.Choose(found, owner, chip.Recipient.Address) : null;
            if (pick == null)
            {
                Ui.Error(Loc.F("UnresolvedRecipient", chip.Recipient.Display), owner);
                return false;
            }
            var idx = _panel.Children.IndexOf(chip.Element);
            Remove(chip);
            AddChip(pick.ToRecipient(), true);
            var last = _chips[^1];
            _panel.Children.Remove(last.Element);
            _panel.Children.Insert(Math.Min(idx, _panel.Children.Count - 1), last.Element);
        }
        // Refresh native directory names on exact address matches, including names cached by older builds.
        foreach (var chip in _chips.Where(c => c.Resolved && (Service?.Account.UseNativeMapi == true || string.IsNullOrWhiteSpace(c.Recipient.Name)
                     || string.Equals(c.Recipient.Name, c.Recipient.Address, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            if (Service == null) break;
            IReadOnlyList<ResolvedName> found;
            try { found = await Service.ResolveNamesAsync(chip.Recipient.Address); }
            catch { continue; }
            var match = found.FirstOrDefault(f => string.Equals(f.Email, chip.Recipient.Address, StringComparison.OrdinalIgnoreCase));
            if (match == null || string.IsNullOrWhiteSpace(match.Name) || match.Name == chip.Recipient.Name) continue;
            var idx = _panel.Children.IndexOf(chip.Element);
            Remove(chip);
            AddChip(match.ToRecipient(), true);
            var last = _chips[^1];
            _panel.Children.Remove(last.Element);
            _panel.Children.Insert(Math.Min(idx, _panel.Children.Count - 1), last.Element);
        }
        foreach (var c in _chips) AppServices.Current.SettingsService.AddRecentRecipient(c.Recipient.FullDisplay);
        return true;
    }
}
