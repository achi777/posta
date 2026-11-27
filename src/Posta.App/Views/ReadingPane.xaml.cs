using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Views;

public partial class ReadingPane : UserControl
{
    private MailMessageDetail? _msg;
    private IMailService? _svc;
    private bool _allowPictures;

    /// <summary>Raised after the message state changed (e.g. meeting response sent) so the list can refresh.</summary>
    public event EventHandler<string>? ItemChanged;

    public ReadingPane()
    {
        InitializeComponent();
        Body.MailtoRequested += (_, uri) => MailActions.ComposeFromMailto(_svc, uri);
        Body.ExternalContentDetected += (_, blocked) => Dispatcher.Invoke(() => ShowPicturesBar(blocked));
        Loc.Instance.LanguageChanged += (_, _) => { if (_msg != null && _svc != null) Show(_msg, _svc); };
    }

    public MailMessageDetail? Message => _msg;
    public IMailService? Service => _svc;

    public double Zoom
    {
        get => Body.Zoom;
        set => Body.Zoom = value;
    }

    public void Clear()
    {
        _msg = null;
        ContentRoot.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    public void ShowLoading()
    {
        EmptyState.Visibility = Visibility.Collapsed;
    }

    public void Show(MailMessageDetail m, IMailService svc)
    {
        var same = _msg?.Id == m.Id;
        _msg = m;
        _svc = svc;
        if (!same) _allowPictures = !AppServices.Current.Settings.BlockExternalPictures;
        EmptyState.Visibility = Visibility.Collapsed;
        ContentRoot.Visibility = Visibility.Visible;

        SubjectText.Text = string.IsNullOrWhiteSpace(m.Subject) ? Loc.T("NoSubject") : m.Subject;
        CategoryChips.ItemsSource = m.Categories;
        FromName.Text = m.From.Display;
        FromAddress.Text = string.IsNullOrEmpty(m.From.Address) || m.From.Address == m.From.Display ? "" : $"<{m.From.Address}>";
        if (m.Sender != null)
        {
            OnBehalfText.Text = Loc.F("OnBehalfOf", m.Sender.Display, m.From.Display);
            OnBehalfText.Visibility = Visibility.Visible;
        }
        else OnBehalfText.Visibility = Visibility.Collapsed;

        ToLine.Text = m.To.Count > 0 ? Loc.T("To") + ":  " + HtmlUtil.RecipientsLine(m.To) : "";
        ToLine.ToolTip = HtmlUtil.RecipientsLineFull(m.To);
        CcLine.Text = m.Cc.Count > 0 ? Loc.T("Cc") + ":  " + HtmlUtil.RecipientsLine(m.Cc) : "";
        CcLine.ToolTip = HtmlUtil.RecipientsLineFull(m.Cc);
        CcLine.Visibility = m.Cc.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var date = m.Received > DateTime.MinValue ? m.Received : m.Sent;
        DateText.Text = date > DateTime.MinValue ? date.ToString("ddd dd.MM.yyyy HH:mm", Loc.Instance.Culture) : "";
        ActionButtons.Visibility = m.IsDraft ? Visibility.Collapsed : Visibility.Visible;

        AvatarText.Text = m.From.Initials;
        AvatarCircle.Fill = InitialsBrushConverter.For(m.From.Address);
        AvatarPhoto.Visibility = Visibility.Collapsed;
        _ = LoadPhotoAsync(m.From.Address);

        BuildInfoBars(m);
        BuildAttachments(m);
        Body.ShowMessage(m.BodyHtml, _allowPictures);
    }

    private async Task LoadPhotoAsync(string email)
    {
        if (_svc == null || string.IsNullOrEmpty(email) || !AppServices.Current.Settings.ShowSenderPhotos) return;
        var bytes = await _svc.GetUserPhotoAsync(email);
        if (bytes == null || _msg?.From.Address != email) return;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            AvatarPhoto.Fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            AvatarPhoto.Visibility = Visibility.Visible;
        }
        catch { }
    }

    // ------------------------------------------------------------------ Info bars

    private Border InfoBar(string glyph, string text, Brush? background = null, UIElement? extra = null, Action? onClick = null)
    {
        var dp = new DockPanel { LastChildFill = true };
        var icon = Ui.GlyphText(glyph, 13, (Brush)FindResource("TextSecondaryBrush"));
        icon.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(icon, Dock.Left);
        dp.Children.Add(icon);
        if (extra != null)
        {
            DockPanel.SetDock(extra, Dock.Right);
            dp.Children.Add(extra);
        }
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextBrush") };
        dp.Children.Add(tb);
        var b = new Border
        {
            Background = background ?? (Brush)FindResource("InfoBarBrush"),
            Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 3), Child = dp, CornerRadius = new CornerRadius(2)
        };
        if (onClick != null)
        {
            b.Cursor = Cursors.Hand;
            b.MouseLeftButtonUp += (_, _) => onClick();
        }
        return b;
    }

    private Border? _picturesBar;

    private void ShowPicturesBar(bool show)
    {
        if (_picturesBar != null) { InfoBars.Children.Remove(_picturesBar); _picturesBar = null; }
        if (!show) return;
        _picturesBar = InfoBar(G.Info, Loc.T("PicturesBlocked"), null, null, () =>
        {
            _allowPictures = true;
            if (_msg != null) Body.ShowMessage(_msg.BodyHtml, true);
        });
        InfoBars.Children.Add(_picturesBar);
    }

    private void BuildInfoBars(MailMessageDetail m)
    {
        InfoBars.Children.Clear();
        _picturesBar = null;
        if (m.Importance == Importance.High)
            InfoBars.Children.Add(InfoBar(G.Important, Loc.T("HighImportanceInfo")));
        if (m.Flag == FlagState.Flagged)
            InfoBars.Children.Add(InfoBar(G.Flag, Loc.F("FlaggedInfo", DateTime.Today.ToString("d", Loc.Instance.Culture))));
        if (m.ReadReceiptRequested)
            InfoBars.Children.Add(InfoBar(G.Info, Loc.T("ReadReceiptRequested")));

        if (m.IsMeetingItem && m.MeetingStart.HasValue)
        {
            var when = $"{m.MeetingStart.Value.ToString("dddd, d MMMM yyyy HH:mm", Loc.Instance.Culture)} – {m.MeetingEnd?.ToString("HH:mm", Loc.Instance.Culture)}";
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            void Row(int r, string label, string value)
            {
                var l = new TextBlock { Text = label, Foreground = (Brush)FindResource("TextSecondaryBrush"), Margin = new Thickness(0, 1, 16, 1) };
                var v = new TextBlock { Text = value, Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
                grid.Children.Add(l); grid.Children.Add(v);
            }
            Row(0, Loc.T("When"), when);
            Row(1, Loc.T("Where"), m.MeetingLocation ?? "");
            panel.Children.Add(grid);

            if (m.IsMeetingRequest && m.CanRespondToMeeting != false)
            {
                var status = m.MyResponse switch
                {
                    MeetingResponse.Accept => Loc.T("YouAccepted"),
                    MeetingResponse.Tentative => Loc.T("YouTentative"),
                    MeetingResponse.Decline => Loc.T("YouDeclined"),
                    _ => Loc.T("NotResponded")
                };
                var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                buttons.Children.Add(RespondButton(G.Accept, "Accept", "#107C10", MeetingResponse.Accept));
                buttons.Children.Add(RespondButton(G.Tentative, "Tentative", "#5C2E91", MeetingResponse.Tentative));
                buttons.Children.Add(RespondButton(G.Decline, "Decline", "#C4314B", MeetingResponse.Decline));
                var cal = new Button { Style = (Style)FindResource("FlatButton"), Content = Loc.T("Calendar..."), Margin = new Thickness(4, 0, 0, 0) };
                cal.Click += (_, _) => MailActions.ShowCalendarAt(m.MeetingStart.Value);
                buttons.Children.Add(cal);
                panel.Children.Add(buttons);
                InfoBars.Children.Add(InfoBar(G.Calendar, status));
            }
            else if (m.CanRespondToMeeting != false && m.ItemClass.StartsWith("IPM.Schedule.Meeting.Canceled", StringComparison.OrdinalIgnoreCase))
            {
                var remove = new Button { Style = (Style)FindResource("FlatButton"), Content = Loc.T("RemoveFromCalendar") };
                remove.Click += async (_, _) =>
                {
                    if (_svc == null || _msg == null) return;
                    if (await Ui.RunAsync(() => _svc.RespondToMeetingAsync(_msg.Id, MeetingResponse.Decline, false, null)))
                        ItemChanged?.Invoke(this, _msg.Id);
                };
                InfoBars.Children.Add(InfoBar(G.Cancel, Loc.T("MeetingCancelledInfo"), (Brush)FindResource("WarningBarBrush"), remove));
            }
            InfoBars.Children.Add(panel);
        }
    }

    private Button RespondButton(string glyph, string key, string color, MeetingResponse response)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(Ui.GlyphText(glyph, 13, GlyphFactory.ParseBrush(color, Brushes.Black)));
        sp.Children.Add(new TextBlock { Text = Loc.T(key), Margin = new Thickness(6, 0, 4, 0) });
        sp.Children.Add(Ui.GlyphText(G.ChevronDown, 9));
        var b = new Button { Style = (Style)FindResource("FlatButton"), Content = sp, BorderBrush = (Brush)FindResource("BorderStrongBrush"), Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4) };
        b.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            menu.Items.Add(Ui.Menu(Loc.T("EditResponse"), () => Respond(response, MeetingResponseMode.Edit)));
            menu.Items.Add(Ui.Menu(Loc.T("SendResponseNow"), () => Respond(response, MeetingResponseMode.SendNow)));
            menu.Items.Add(Ui.Menu(Loc.T("DontSendResponse"), () => Respond(response, MeetingResponseMode.DontSend)));
            menu.PlacementTarget = b;
            menu.IsOpen = true;
        };
        return b;
    }

    private async void Respond(MeetingResponse response, MeetingResponseMode mode)
    {
        if (_svc == null || _msg == null) return;
        if (await MailActions.RespondToMeetingAsync(_svc, _msg.Id, _msg.Subject, response, mode, Window.GetWindow(this)))
            ItemChanged?.Invoke(this, _msg.Id);
    }

    // ------------------------------------------------------------------ Attachments

    private void BuildAttachments(MailMessageDetail m)
    {
        AttachmentList.Items.Clear();
        var files = m.Attachments.Where(a => !a.IsInline).ToList();
        foreach (var a in files) AttachmentList.Items.Add(AttachmentChip(a));
        if (files.Count > 1)
        {
            var all = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("SaveAllAttachments"), Margin = new Thickness(8, 10, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            all.Click += async (_, _) => { if (_svc != null && _msg != null) await MailActions.SaveAllAttachmentsAsync(_svc, _msg, Window.GetWindow(this)); };
            AttachmentList.Items.Add(all);
        }
        AttachmentList.Visibility = files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public static (string glyph, Brush brush) FileIcon(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".doc" or ".docx" or ".rtf" => ("", GlyphFactory.ParseBrush("#185ABD", Brushes.Blue)),
            ".xls" or ".xlsx" or ".csv" => ("", GlyphFactory.ParseBrush("#107C41", Brushes.Green)),
            ".ppt" or ".pptx" => ("", GlyphFactory.ParseBrush("#C43E1C", Brushes.OrangeRed)),
            ".pdf" => ("", GlyphFactory.ParseBrush("#D13438", Brushes.Red)),
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => ("", GlyphFactory.ParseBrush("#8764B8", Brushes.Purple)),
            ".zip" or ".rar" or ".7z" => ("", GlyphFactory.ParseBrush("#C19C00", Brushes.Goldenrod)),
            ".eml" or ".msg" => ("", GlyphFactory.ParseBrush("#0F6CBD", Brushes.Blue)),
            _ => ("", GlyphFactory.ParseBrush("#605E5C", Brushes.Gray))
        };
    }

    private FrameworkElement AttachmentChip(AttachmentInfo a)
    {
        var (glyph, brush) = FileIcon(a.Name);
        var grid = new Grid { Width = 210 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Ui.GlyphText(glyph, 22, brush);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        grid.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
        texts.Children.Add(new TextBlock { Text = a.Name, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = a.Name });
        texts.Children.Add(new TextBlock { Text = a.SizeDisplay, FontSize = 11, Foreground = (Brush)FindResource("TextSecondaryBrush") });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        var drop = new Button { Style = (Style)FindResource("FlatButton"), Content = Ui.GlyphText(G.ChevronDown, 9), Padding = new Thickness(4, 2, 4, 2) };
        Grid.SetColumn(drop, 2);
        grid.Children.Add(drop);

        var chip = new Border
        {
            BorderBrush = (Brush)FindResource("BorderStrongBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2, 4, 2, 4), Margin = new Thickness(4, 4, 4, 0), Background = Brushes.White, Child = grid, Cursor = Cursors.Hand
        };
        chip.MouseEnter += (_, _) => chip.Background = (Brush)FindResource("HoverBrush");
        chip.MouseLeave += (_, _) => chip.Background = Brushes.White;

        ContextMenu Menu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(Ui.Menu(Loc.T("Open"), () => OpenAttachment(a), G.Open));
            menu.Items.Add(Ui.Menu(Loc.T("QuickPrint"), () => PrintAttachment(a), G.Print));
            menu.Items.Add(Ui.Menu(Loc.T("SaveAsDots"), () => SaveAttachment(a), G.Save));
            menu.Items.Add(Ui.Menu(Loc.T("SaveAllAttachments"), async () => { if (_svc != null && _msg != null) await MailActions.SaveAllAttachmentsAsync(_svc, _msg, Window.GetWindow(this)); }, G.SaveAs));
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.Menu(Loc.T("Copy"), () => CopyAttachment(a), G.Copy));
            return menu;
        }
        chip.ContextMenu = Menu();
        drop.Click += (_, _) => { var m = Menu(); m.PlacementTarget = drop; m.IsOpen = true; };
        chip.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) OpenAttachment(a); };
        return chip;
    }

    private async void OpenAttachment(AttachmentInfo a)
    {
        if (_svc != null && _msg != null) await MailActions.OpenAttachmentAsync(_svc, _msg.Id, a);
    }

    private async void PrintAttachment(AttachmentInfo a)
    {
        if (_svc != null && _msg != null) await MailActions.OpenAttachmentAsync(_svc, _msg.Id, a, "print");
    }

    private async void SaveAttachment(AttachmentInfo a)
    {
        if (_svc != null && _msg != null) await MailActions.SaveAttachmentAsync(_svc, _msg.Id, a, Window.GetWindow(this));
    }

    private async void CopyAttachment(AttachmentInfo a)
    {
        if (_svc == null || _msg == null) return;
        var path = await MailActions.DownloadAttachmentToTempAsync(_svc, _msg.Id, a);
        if (path != null) Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
    }

    // ------------------------------------------------------------------ Actions

    private void Reply_Click(object sender, RoutedEventArgs e) { if (_svc != null && _msg != null) _ = MailActions.ReplyAsync(_svc, _msg, ComposeMode.Reply); }
    private void ReplyAll_Click(object sender, RoutedEventArgs e) { if (_svc != null && _msg != null) _ = MailActions.ReplyAsync(_svc, _msg, ComposeMode.ReplyAll); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (_svc != null && _msg != null) _ = MailActions.ReplyAsync(_svc, _msg, ComposeMode.Forward); }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (_svc == null || _msg == null) return;
        var svc = _svc;
        var msg = _msg;
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Menu(Loc.T("ForwardAsAttachment"), () => _ = MailActions.ForwardAsAttachmentAsync(svc, msg), G.Attach));
        menu.Items.Add(Ui.Menu(Loc.T("ReplyWithMeeting"), () => MailActions.ReplyWithMeeting(svc, msg), G.Calendar));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Menu(Loc.T("Print"), () => Body.Print(), G.Print));
        menu.Items.Add(Ui.Menu(Loc.T("SaveAsDots"), () => _ = MailActions.SaveMessageAsAsync(svc, msg, Window.GetWindow(this)), G.SaveAs));
        menu.Items.Add(Ui.Menu(Loc.T("OpenInNewWindow"), () => MailActions.OpenMessageWindow(svc, msg), G.Open));
        menu.PlacementTarget = MoreButton;
        menu.IsOpen = true;
    }

    public void Print() => Body.Print();
}

public enum MeetingResponseMode { Edit, SendNow, DontSend }
