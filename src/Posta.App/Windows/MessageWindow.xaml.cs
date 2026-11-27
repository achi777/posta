using System.Windows;
using System.Windows.Controls;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Windows;

public partial class MessageWindow
{
    private readonly IMailService _svc;
    private MailMessageDetail _msg;

    public string MessageId => _msg.Id;

    public MessageWindow(IMailService svc, MailMessageDetail msg)
    {
        InitializeComponent();
        _svc = svc;
        _msg = msg;
        Title = $"{(string.IsNullOrWhiteSpace(msg.Subject) ? Loc.T("NoSubject") : msg.Subject)} - {Loc.T("Message")}";
        Pane.Show(msg, svc);
        Pane.ItemChanged += (_, _) => AppServices.Current.RaiseDataChanged("items:" + _msg.FolderId);
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) Close();
            if (e.Key == System.Windows.Input.Key.Delete) Delete_Click(this, e);
        };
    }

    private void Changed() => AppServices.Current.RaiseDataChanged("items:" + _msg.FolderId);

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (await Ui.RunAsync(() => _svc.DeleteItemsAsync(new[] { _msg.Id }, false), this)) { Changed(); Close(); }
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        var archive = (await _svc.GetFolderTreeAsync()).FirstOrDefault(f => f.Kind == FolderKind.Archive);
        if (archive == null) return;
        if (await Ui.RunAsync(() => _svc.MoveItemsAsync(new[] { _msg.Id }, archive.Id), this)) { Changed(); Close(); }
    }

    private async void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if (await Ui.RunAsync(() => _svc.IgnoreConversationAsync(_msg.Id), this)) { Changed(); Close(); }
    }

    private async void Junk_Click(object sender, RoutedEventArgs e)
    {
        var junk = (sender as FrameworkElement)?.Tag as string == "true";
        if (await Ui.RunAsync(() => _svc.MarkAsJunkAsync(new[] { _msg.Id }, junk), this)) { Changed(); Close(); }
    }

    private async void Reply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && Enum.TryParse<ComposeMode>(t, out var mode))
            await MailActions.ReplyAsync(_svc, _msg, mode);
    }

    private void Meeting_Click(object sender, RoutedEventArgs e) => MailActions.ReplyWithMeeting(_svc, _msg);
    private async void ForwardAttach_Click(object sender, RoutedEventArgs e) => await MailActions.ForwardAsAttachmentAsync(_svc, _msg);

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        var w = new FolderPickerWindow(_svc, Loc.T("MoveItemsTo"), null) { Owner = this };
        if (w.ShowDialog() != true || w.SelectedFolder == null) return;
        if (await Ui.RunAsync(() => _svc.MoveItemsAsync(new[] { _msg.Id }, w.SelectedFolder.Id), this)) { Changed(); Close(); }
    }

    private async void Rule_Click(object sender, RoutedEventArgs e)
    {
        var rule = new RuleInfo { Name = _msg.Subject, FromAddresses = { _msg.From.Address } };
        var folders = (await _svc.GetFolderTreeAsync()).ToList();
        var w = new RuleEditWindow(_svc, rule, folders) { Owner = this };
        if (w.ShowDialog() != true) return;
        await Ui.RunAsync(async () =>
        {
            var rules = (await _svc.GetRulesAsync()).ToList();
            rules.Insert(0, w.Rule);
            await _svc.SaveRulesAsync(rules, Array.Empty<string>());
        }, this);
    }

    private async void Unread_Click(object sender, RoutedEventArgs e)
    {
        if (await Ui.RunAsync(() => _svc.SetReadAsync(new[] { _msg.Id }, false), this)) Changed();
    }

    private void Categorize_Opened(object? sender, EventArgs e)
    {
        CategorizeBtn.Items.Clear();
        foreach (var c in AppServices.Current.Settings.Categories)
        {
            var name = c.Name;
            var mi = new Fluent.MenuItem { Header = Loc.T(name), IsCheckable = true, IsChecked = _msg.Categories.Contains(name) };
            mi.Click += async (_, _) =>
            {
                if (_msg.Categories.Contains(name)) _msg.Categories.Remove(name); else _msg.Categories.Add(name);
                if (await Ui.RunAsync(() => _svc.SetCategoriesAsync(_msg.Id, _msg.Categories), this)) { Pane.Show(_msg, _svc); Changed(); }
            };
            CategorizeBtn.Items.Add(mi);
        }
    }

    private async void Flag_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        var state = tag switch { "complete" => FlagState.Complete, "clear" => FlagState.NotFlagged, _ => FlagState.Flagged };
        if (await Ui.RunAsync(() => _svc.SetFlagAsync(new[] { _msg.Id }, state, DateTime.Today, DateTime.Today), this))
        {
            _msg.Flag = state;
            Pane.Show(_msg, _svc);
            Changed();
        }
    }

    private void Zoom_Click(object sender, RoutedEventArgs e) => Pane.Zoom = Pane.Zoom >= 1.75 ? 1 : Pane.Zoom + 0.25;
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await MailActions.SaveMessageAsAsync(_svc, _msg, this);
    private async void SaveAttachments_Click(object sender, RoutedEventArgs e) => await MailActions.SaveAllAttachmentsAsync(_svc, _msg, this);
    private void Print_Click(object sender, RoutedEventArgs e) => Pane.Print();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
