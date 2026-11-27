using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Posta.App.Controls;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.App.Views;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Windows;

public partial class ComposeWindow
{
    private IMailService _svc;
    private readonly OutgoingMessage _msg;
    private readonly List<AttachmentInfo> _attachments = new();
    private readonly DispatcherTimer _autoSave = new();
    private bool _dirty;
    private bool _sent;
    private bool _closingConfirmed;
    private Importance _importance = Importance.Normal;
    private Sensitivity _sensitivity = Sensitivity.Normal;
    private DateTime? _deferred;
    private FlagState _flag;
    private double _zoom = 1;
    private bool _plainText;

    private static readonly string[] Fonts = { "Aptos", "Calibri", "Arial", "Segoe UI", "Sylfaen", "Times New Roman", "Georgia", "Verdana", "Tahoma", "Courier New", "Consolas", "Cambria", "Trebuchet MS", "BPG Arial", "BPG Nino Mtavruli" };
    private static readonly double[] Sizes = { 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72 };

    public ComposeWindow(IMailService svc, OutgoingMessage msg)
    {
        InitializeComponent();
        _svc = svc;
        _msg = msg;
        var s = AppServices.Current.Settings;

        ToBox.Service = CcBox.Service = BccBox.Service = svc;
        ToBox.SetRecipients(msg.To);
        CcBox.SetRecipients(msg.Cc);
        BccBox.SetRecipients(msg.Bcc);
        if (msg.Bcc.Count > 0) ToggleBcc(true);
        SubjectBox.Text = msg.Subject;
        _importance = msg.Importance;
        _sensitivity = msg.Sensitivity;
        _flag = msg.Flag;
        ReadReceiptBox.IsChecked = msg.RequestReadReceipt;
        DeliveryReceiptBox.IsChecked = msg.RequestDeliveryReceipt;
        NoSaveSentBtn.IsChecked = !s.SaveSentItems;
        _attachments.AddRange(msg.Attachments);
        RefreshAttachments();
        UpdateImportanceButtons();

        Editor.DefaultFont = s.ComposeFontFamily;
        Editor.DefaultSizePt = s.ComposeFontSizePt;
        Editor.DefaultColor = s.ComposeFontColor;
        _plainText = s.ComposeFormat == ComposeFormat.PlainText && msg.Mode == ComposeMode.New;
        if (_plainText) { PlainBtn.IsChecked = true; HtmlBtn.IsChecked = false; Editor.PlainText = true; }
        _ = Editor.SetHtmlAsync(_plainText ? HtmlUtil.PlainTextToHtml(HtmlUtil.ToPlainText(msg.BodyHtml)) : msg.BodyHtml);
        Editor.StateChanged += (_, st) => Dispatcher.Invoke(() => UpdateFormatState(st));
        Editor.ContentChanged += (_, _) => _dirty = true;
        Editor.KeyCommand += (_, key) => Dispatcher.Invoke(async () =>
        {
            if (key == "send") await SendAsync();
            else if (key == "save") await SaveDraftAsync(false);
            else if (key == "link") Link_Click(this, new RoutedEventArgs());
        });

        foreach (var f in Fonts) FontBox.Items.Add(f);
        foreach (var z in Sizes) SizeBox.Items.Add(z.ToString(System.Globalization.CultureInfo.InvariantCulture));
        FontBox.Text = s.ComposeFontFamily.Split(',')[0].Trim();
        SizeBox.Text = s.ComposeFontSizePt.ToString();

        ToBox.Changed += (_, _) => _dirty = true;
        CcBox.Changed += (_, _) => _dirty = true;
        BccBox.Changed += (_, _) => _dirty = true;

        SetupFrom();
        UpdateTitle();
        if (msg.Mode is ComposeMode.Reply or ComposeMode.ReplyAll or ComposeMode.Forward)
        {
            InfoBar.Visibility = Visibility.Visible;
            InfoText.Text = msg.Mode == ComposeMode.Forward
                ? (Loc.Instance.IsGeorgian ? "გადაგზავნა" : "Forwarding message")
                : (Loc.Instance.IsGeorgian ? "პასუხი" : "Replying to message");
        }

        AllowDrop = true;
        Drop += Body_Drop;
        DragOver += Body_DragOver;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Loaded += async (_, _) =>
        {
            await Editor.Ready;
            if (msg.Mode == ComposeMode.New && ToBox.IsEmpty) ToBox.Focus();
            else { Editor.Focus(); await Editor.FocusStart(); }
            _dirty = false;
        };
        _autoSave.Interval = TimeSpan.FromMinutes(Math.Max(1, s.AutoSaveDraftMinutes));
        _autoSave.Tick += async (_, _) => { if (_dirty && !_sent) await SaveDraftAsync(true); };
        if (s.AutoSaveDraftMinutes > 0) _autoSave.Start();
    }

    private void UpdateTitle()
    {
        var subject = string.IsNullOrWhiteSpace(SubjectBox.Text) ? Loc.T("Untitled") : SubjectBox.Text;
        Title = $"{subject} - {Loc.T("Message")}";
    }

    private void SubjectBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _dirty = true;
        UpdateTitle();
    }

    // =====================================================================================
    // From (multiple accounts / shared mailboxes)
    // =====================================================================================

    private string? _fromAddress;

    private void SetupFrom()
    {
        var multiple = AppServices.Current.Accounts.Count > 1 || _svc.Account.AdditionalMailboxes.Count > 0;
        ShowFromBtn.IsChecked = multiple;
        ToggleFrom(multiple);
        FromText.Text = _svc.Account.Email;
    }

    private void ToggleFrom(bool show)
    {
        FromButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FromText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowFrom_Click(object sender, RoutedEventArgs e) => ToggleFrom(ShowFromBtn.IsChecked == true);

    private void From_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var acc in AppServices.Current.Accounts)
        {
            var a = acc;
            menu.Items.Add(Ui.Menu(a.Account.Email, () =>
            {
                _svc = a;
                _fromAddress = null;
                FromText.Text = a.Account.Email;
                ToBox.Service = CcBox.Service = BccBox.Service = a;
            }, G.Mail));
            foreach (var mb in a.Account.AdditionalMailboxes)
            {
                var m = mb;
                menu.Items.Add(Ui.Menu(m, () => { _svc = a; _fromAddress = m; FromText.Text = m; }, G.People));
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Menu(Loc.T("OtherFolder").Replace(Loc.Instance.IsGeorgian ? "საქაღალდე" : "Folder", Loc.Instance.IsGeorgian ? "მისამართი" : "E-mail Address"), () =>
        {
            var addr = Ui.Prompt(Loc.T("FromBtn"), Loc.T("EmailAddress"), "", this);
            if (!string.IsNullOrWhiteSpace(addr)) { _fromAddress = addr; FromText.Text = addr; }
        }));
        menu.PlacementTarget = FromButton;
        menu.IsOpen = true;
    }

    // =====================================================================================
    // Formatting
    // =====================================================================================

    private bool _updatingState;

    private void UpdateFormatState(EditorState st)
    {
        _updatingState = true;
        BoldBtn.IsChecked = st.Bold;
        ItalicBtn.IsChecked = st.Italic;
        UnderlineBtn.IsChecked = st.Underline;
        BulletsBtn.IsChecked = st.Bullets;
        NumberingBtn.IsChecked = st.Numbering;
        AlignLeftBtn.IsChecked = st.Align == "left";
        AlignCenterBtn.IsChecked = st.Align == "center";
        AlignRightBtn.IsChecked = st.Align == "right";
        if (!FontBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(st.FontName)) FontBox.Text = st.FontName;
        if (!SizeBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(st.FontSize)) SizeBox.Text = st.FontSize;
        _updatingState = false;
    }

    private async void Exec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string cmd }) return;
        if (cmd == "paste")
        {
            Editor.Focus();
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null) { await Editor.InsertHtml($"<img src=\"{ImageToDataUri(img)}\">"); return; }
            }
            if (Clipboard.ContainsText()) { await Editor.InsertHtml(Clipboard.ContainsText(TextDataFormat.Html) ? HtmlUtil.BodyContent(ExtractHtmlFragment(Clipboard.GetText(TextDataFormat.Html))) : HtmlUtil.Encode(Clipboard.GetText()).Replace("\n", "<br>")); }
            return;
        }
        await Editor.Command(cmd);
    }

    private static string ExtractHtmlFragment(string cf)
    {
        var s = cf.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        var e = cf.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        return s >= 0 && e > s ? cf[(s + 20)..e] : cf;
    }

    private static string ImageToDataUri(System.Windows.Media.Imaging.BitmapSource img)
    {
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    private async void FontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingState || FontBox.SelectedItem is not string f) return;
        await Editor.Command("fontName", f);
    }

    private async void SizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingState || SizeBox.SelectedItem is not string s) return;
        if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pt)) await Editor.SetFontSize(pt);
    }

    private async void GrowFont_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(SizeBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var cur)) cur = 11;
        var up = (sender as FrameworkElement)?.Tag as string == "up";
        var next = up ? Sizes.FirstOrDefault(s => s > cur, 72) : Sizes.LastOrDefault(s => s < cur, 8);
        SizeBox.Text = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Editor.SetFontSize(next);
    }

    private static readonly string[] Palette = { "#000000", "#C00000", "#FF0000", "#FFC000", "#FFFF00", "#92D050", "#00B050", "#00B0F0", "#0070C0", "#002060", "#7030A0", "#7F7F7F", "#FFFFFF" };

    private void ColorMenu(UIElement target, Action<string> pick)
    {
        var menu = new ContextMenu();
        var wrap = new WrapPanel { Width = 7 * 26 };
        foreach (var c in Palette)
        {
            var b = new Button { Width = 22, Height = 22, Margin = new Thickness(2), Background = GlyphFactory.ParseBrush(c, Brushes.Black), BorderBrush = Brushes.Gray, Cursor = Cursors.Hand };
            var col = c;
            b.Click += (_, _) => { menu.IsOpen = false; pick(col); };
            wrap.Children.Add(b);
        }
        menu.Items.Add(new MenuItem { Header = wrap, StaysOpenOnClick = true });
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private void FontColor_Click(object sender, RoutedEventArgs e) => ColorMenu((UIElement)sender, c => _ = Editor.Command("foreColor", c));
    private void Highlight_Click(object sender, RoutedEventArgs e) => ColorMenu((UIElement)sender, c => _ = Editor.Command("hiliteColor", c == "#000000" ? "transparent" : c));

    private async void Style_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }) await Editor.FormatBlock(tag);
    }

    private async void Format_Click(object sender, RoutedEventArgs e)
    {
        var plain = (sender as FrameworkElement)?.Tag as string == "plain";
        if (plain == _plainText) { HtmlBtn.IsChecked = !plain; PlainBtn.IsChecked = plain; return; }
        _plainText = plain;
        HtmlBtn.IsChecked = !plain;
        PlainBtn.IsChecked = plain;
        if (plain)
        {
            var text = await Editor.GetTextAsync();
            await Editor.SetHtmlAsync(HtmlUtil.PlainTextToHtml(text));
        }
    }

    // =====================================================================================
    // Insert
    // =====================================================================================

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        var url = Ui.Prompt(Loc.T("Link"), Loc.T("InsertLinkPrompt"), "https://", this);
        if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
        await Editor.Command("createLink", url);
    }

    private async void Table_Click(object sender, RoutedEventArgs e)
    {
        var size = Ui.Prompt(Loc.T("Table"), Loc.T("TableSizePrompt"), "3x2", this);
        if (size == null) return;
        var parts = size.ToLowerInvariant().Split('x', '×', '*');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var cols) || !int.TryParse(parts[1], out var rows)) return;
        cols = Math.Clamp(cols, 1, 20); rows = Math.Clamp(rows, 1, 50);
        var sb = new System.Text.StringBuilder("<table style=\"border-collapse:collapse;border:1px solid #a6a6a6\">");
        for (int r = 0; r < rows; r++)
        {
            sb.Append("<tr>");
            for (int c = 0; c < cols; c++) sb.Append("<td style=\"border:1px solid #a6a6a6;padding:3px 6px;min-width:60px\">&nbsp;</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table><div><br></div>");
        await Editor.InsertHtml(sb.ToString());
    }

    private async void Picture_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.T("ImageFilter"), Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames)
        {
            var bytes = await File.ReadAllBytesAsync(f);
            var ext = Path.GetExtension(f).TrimStart('.').ToLowerInvariant();
            var mime = ext switch { "jpg" or "jpeg" => "image/jpeg", "gif" => "image/gif", "bmp" => "image/bmp", _ => "image/png" };
            await Editor.InsertHtml($"<img src=\"data:{mime};base64,{Convert.ToBase64String(bytes)}\">");
        }
    }

    private async void Hr_Click(object sender, RoutedEventArgs e) => await Editor.InsertHtml("<hr style=\"border:none;border-top:1px solid #a6a6a6\"><div><br></div>");
    private async void DateTime_Click(object sender, RoutedEventArgs e) => await Editor.InsertHtml(HtmlUtil.Encode(DateTime.Now.ToString("f", Loc.Instance.Culture)));

    private async void Emoji_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Fluent.MenuItem mi || mi.Header is not string s) return;
        var menu = new ContextMenu();
        foreach (var sym in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var x = sym;
            menu.Items.Add(Ui.Menu(x, () => _ = Editor.InsertHtml(x)));
        }
        menu.IsOpen = true;
        await Task.CompletedTask;
    }

    private void SignatureDropDown_Opened(object? sender, EventArgs e)
    {
        if (sender is not Fluent.DropDownButton dd) return;
        dd.Items.Clear();
        foreach (var sig in AppServices.Current.Settings.Signatures)
        {
            var s = sig;
            var mi = new Fluent.MenuItem { Header = s.Name };
            mi.Click += async (_, _) => await Editor.SetSignature(HtmlUtil.BodyContent(s.Html));
            dd.Items.Add(mi);
        }
        if (dd.Items.Count > 0) dd.Items.Add(new Separator());
        var manage = new Fluent.MenuItem { Header = Loc.T("Signatures"), Icon = GlyphFactory.Create(G.Edit, GlyphFactory.DefaultBrush, 16) };
        manage.Click += (_, _) => new SignaturesWindow { Owner = this }.ShowDialog();
        dd.Items.Add(manage);
    }

    // =====================================================================================
    // Attachments
    // =====================================================================================

    private void AttachFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.T("AllFilesFilter"), Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) AddFile(f);
    }

    private void AddFile(string path)
    {
        if (!File.Exists(path)) return;
        var info = new FileInfo(path);
        _attachments.Add(new AttachmentInfo { Name = info.Name, Size = info.Length, LocalPath = path });
        _dirty = true;
        RefreshAttachments();
    }

    private void Body_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Body_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files) AddFile(f);
        e.Handled = true;
    }

    private void RefreshAttachments()
    {
        AttachmentsList.Items.Clear();
        foreach (var a in _attachments)
        {
            var (glyph, brush) = ReadingPane.FileIcon(a.Name);
            var dp = new DockPanel();
            var icon = Ui.GlyphText(glyph, 18, brush);
            icon.Margin = new Thickness(4, 0, 6, 0);
            dp.Children.Add(icon);
            var remove = new Button { Style = (Style)FindResource("FlatButton"), Content = Ui.GlyphText(G.Cancel, 9), Padding = new Thickness(4, 2, 4, 2), ToolTip = Loc.T("RemoveAttachment") };
            DockPanel.SetDock(remove, Dock.Right);
            dp.Children.Add(remove);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = a.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 });
            texts.Children.Add(new TextBlock { Text = a.SizeDisplay, FontSize = 11, Foreground = Brushes.Gray });
            dp.Children.Add(texts);
            var chip = new Border { BorderBrush = (Brush)FindResource("BorderStrongBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(2, 3, 2, 3), Margin = new Thickness(0, 0, 6, 4), Child = dp, Width = 220, ToolTip = a.Name };
            var att = a;
            remove.Click += (_, _) =>
            {
                _attachments.Remove(att);
                if (!string.IsNullOrEmpty(att.Id)) _msg.RemovedAttachmentIds.Add(att.Id);
                _dirty = true;
                RefreshAttachments();
            };
            chip.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.ClickCount != 2) return;
                if (att.LocalPath != null) Ui.OpenUrl(att.LocalPath);
                else if (!string.IsNullOrEmpty(_msg.SourceItemId ?? _msg.DraftId) && !string.IsNullOrEmpty(att.Id))
                    _ = MailActions.OpenAttachmentAsync(_svc, (_msg.DraftId ?? _msg.SourceItemId)!, att);
            };
            AttachmentsList.Items.Add(chip);
        }
        AttachmentsList.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================================
    // Recipients
    // =====================================================================================

    private void PickRecipients_Click(object sender, RoutedEventArgs e)
    {
        var w = new AddressBookWindow(_svc, AddressBookMode.SelectRecipients) { Owner = this };
        w.Preload(ToBox.Recipients, CcBox.Recipients, BccBox.Recipients);
        if (w.ShowDialog() != true) return;
        ToBox.SetRecipients(w.To);
        CcBox.SetRecipients(w.Cc);
        BccBox.SetRecipients(w.Bcc);
        if (w.Bcc.Count > 0) ToggleBcc(true);
    }

    private void AddressBook_Click(object sender, RoutedEventArgs e) => PickRecipients_Click(sender, e);

    private async void CheckNames_Click(object sender, RoutedEventArgs e) => await CheckNamesAsync();

    private async Task<bool> CheckNamesAsync() =>
        await ToBox.CheckNamesAsync(this) && await CcBox.CheckNamesAsync(this) && await BccBox.CheckNamesAsync(this);

    private void ShowBcc_Click(object sender, RoutedEventArgs e) => ToggleBcc(ShowBccBtn.IsChecked == true);

    private void ToggleBcc(bool show)
    {
        ShowBccBtn.IsChecked = show;
        BccButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BccBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================================
    // Options
    // =====================================================================================

    private void Importance_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        var target = tag == "High" ? Importance.High : Importance.Low;
        _importance = _importance == target ? Importance.Normal : target;
        UpdateImportanceButtons();
        _dirty = true;
    }

    private void UpdateImportanceButtons()
    {
        HighImpBtn.IsChecked = _importance == Importance.High;
        LowImpBtn.IsChecked = _importance == Importance.Low;
    }

    private void Sensitivity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && Enum.TryParse<Sensitivity>(t, out var s)) _sensitivity = s;
        _dirty = true;
    }

    private void FlagCompose_Click(object sender, RoutedEventArgs e)
    {
        _flag = (sender as FrameworkElement)?.Tag as string == "flag" ? FlagState.Flagged : FlagState.NotFlagged;
        _dirty = true;
    }

    private void DelayDelivery_Click(object sender, RoutedEventArgs e)
    {
        var w = new DelayDeliveryWindow(_deferred) { Owner = this };
        if (w.ShowDialog() == true)
        {
            _deferred = w.Value;
            InfoBar.Visibility = _deferred.HasValue ? Visibility.Visible : Visibility.Collapsed;
            InfoText.Text = _deferred.HasValue ? $"{Loc.T("DoNotDeliverBefore")} {_deferred.Value.ToString("f", Loc.Instance.Culture)}" : "";
        }
    }

    private void Spelling_Click(object sender, RoutedEventArgs e) =>
        Ui.Info(Loc.Instance.IsGeorgian ? "მართლწერა მოწმდება აკრეფისას (წითლად ხაზგასმული სიტყვები). დააწკაპუნეთ მარჯვენა ღილაკით შესწორებისთვის." : "Spelling is checked as you type (red underlines). Right-click a word for suggestions.", this);

    private async void Zoom_Click(object sender, RoutedEventArgs e)
    {
        _zoom = _zoom >= 1.5 ? 1 : _zoom + 0.25;
        await Editor.SetZoom(_zoom);
    }

    private void Print_Click(object sender, RoutedEventArgs e) => Editor.Print();

    // =====================================================================================
    // Send / save / close
    // =====================================================================================

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await SendAsync(); }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await SaveDraftAsync(false); }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await CheckNamesAsync(); }
        else if (e.Key == Key.B && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { e.Handled = true; PickRecipients_Click(this, e); }
        else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    private async Task<OutgoingMessage> BuildMessageAsync()
    {
        var html = await Editor.GetHtmlAsync();
        _msg.AccountId = _svc.Account.Id;
        _msg.FromAddress = _fromAddress;
        _msg.To = ToBox.Recipients;
        _msg.Cc = CcBox.Recipients;
        _msg.Bcc = BccBox.Recipients;
        _msg.Subject = SubjectBox.Text;
        var style = MailActions.DefaultBodyStyle();
        _msg.BodyHtml = $"<html><head><meta charset=\"utf-8\"></head><body><div style=\"{style}\">{html}</div></body></html>";
        _msg.Importance = _importance;
        _msg.Sensitivity = _sensitivity;
        _msg.RequestReadReceipt = ReadReceiptBox.IsChecked == true;
        _msg.RequestDeliveryReceipt = DeliveryReceiptBox.IsChecked == true;
        _msg.DeferredDelivery = _deferred;
        _msg.SaveSentCopy = NoSaveSentBtn.IsChecked != true;
        _msg.Flag = _flag;
        _msg.Attachments = _attachments.ToList();
        return _msg;
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (_sent) return;
        if (!await CheckNamesAsync()) return;
        if (ToBox.IsEmpty && CcBox.IsEmpty && BccBox.IsEmpty)
        {
            Ui.Error(Loc.T("NoRecipients"), this);
            return;
        }
        if (string.IsNullOrWhiteSpace(SubjectBox.Text) && !Ui.Confirm(Loc.T("NoSubjectPrompt"), this)) return;
        if (AppServices.Current.Settings.WarnMissingAttachment && _attachments.Count == 0 &&
            await Editor.MatchesAsync(Loc.T("AttachmentKeywords")) && !Ui.Confirm(Loc.T("MissingAttachmentPrompt"), this, MessageBoxImage.Warning)) return;

        var msg = await BuildMessageAsync();
        SendButton.IsEnabled = false;
        InfoBar.Visibility = Visibility.Visible;
        InfoText.Text = Loc.T("Sending");
        if (await Ui.RunAsync(() => _svc.SendAsync(msg), this))
        {
            _sent = true;
            foreach (var r in msg.To.Concat(msg.Cc).Concat(msg.Bcc)) AppServices.Current.SettingsService.AddRecentRecipient(r.FullDisplay);
            AppServices.Current.SettingsService.Save();
            AppServices.Current.RaiseDataChanged("folders");
            _closingConfirmed = true;
            Close();
        }
        else
        {
            SendButton.IsEnabled = !msg.SubmissionOutcomeUnknown;
            InfoText.Text = "";
            InfoBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveDraftAsync(false);

    private async Task<bool> SaveDraftAsync(bool silent)
    {
        if (_msg.SubmissionOutcomeUnknown) return false;
        var msg = await BuildMessageAsync();
        var ok = await Ui.RunAsync(async () => await _svc.SaveDraftAsync(msg), this, !silent);
        if (ok)
        {
            _dirty = false;
            Editor.IsDirty = false;
            _attachments.RemoveAll(a => a.LocalPath != null || a.Content != null);
            _attachments.Clear();
            _attachments.AddRange(msg.Attachments);
            InfoBar.Visibility = Visibility.Visible;
            InfoText.Text = Loc.F("DraftSaved", DateTime.Now.ToString("t", Loc.Instance.Culture));
            AppServices.Current.RaiseDataChanged("folders");
        }
        return ok;
    }

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_msg.DraftId))
            await Ui.RunAsync(() => _svc.DeleteItemsAsync(new[] { _msg.DraftId! }, false), this);
        _closingConfirmed = true;
        Close();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingConfirmed || _sent) { _autoSave.Stop(); return; }
        var dirty = _dirty || Editor.IsDirty;
        if (!dirty) { _autoSave.Stop(); return; }
        e.Cancel = true;
        var r = Ui.YesNoCancel(Loc.T("SaveChangesPrompt"), this);
        if (r == MessageBoxResult.Cancel) return;
        if (r == MessageBoxResult.Yes && !await SaveDraftAsync(false)) return;
        _closingConfirmed = true;
        _autoSave.Stop();
        // Close() cannot be called from inside Closing; defer it.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
