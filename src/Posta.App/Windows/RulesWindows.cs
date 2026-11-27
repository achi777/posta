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
// Rules and Alerts
// =========================================================================================
public sealed class RulesWindow : DialogWindow
{
    private readonly IMailService _svc;
    private readonly ObservableCollection<RuleInfo> _rules = new();
    private readonly List<string> _deleted = new();
    private readonly ListBox _list = new() { Height = 220, ItemContainerStyle = (Style)Application.Current.FindResource("PlainListItem") };
    private readonly TextBlock _desc = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private List<MailFolderInfo> _folders = new();

    public static void ShowFor(IMailService svc, Window? owner) => new RulesWindow(svc) { Owner = owner }.ShowDialog();

    private RulesWindow(IMailService svc)
    {
        _svc = svc;
        Title = $"{Loc.T("RulesTitle")} – {svc.Account.Email}";
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12), Width = 620 };
        root.Children.Add(new TextBlock { Text = Loc.T("EmailRules"), FontSize = 15, Margin = new Thickness(0, 0, 0, 8) });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        Button B(string text, Action a) { var b = MakeButton(text, false, false, a); b.Margin = new Thickness(0, 0, 6, 0); return b; }
        bar.Children.Add(B(Loc.T("NewRule"), NewRule));
        if (!svc.Account.UseNativeMapi) bar.Children.Add(B(Loc.T("ChangeRule"), EditRule));
        if (!svc.Account.UseNativeMapi) bar.Children.Add(B(Loc.T("CopyRule"), CopyRule));
        bar.Children.Add(B(Loc.T("DeleteRule"), DeleteRule));
        bar.Children.Add(B("▲", () => MoveRule(-1)));
        bar.Children.Add(B("▼", () => MoveRule(1)));
        root.Children.Add(bar);
        if (svc.Account.UseNativeMapi) root.Children.Add(new TextBlock
        {
            Text = Loc.T("NativeStandardRulesOnly"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        _list.ItemsSource = _rules;
        _list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <StackPanel Orientation="Horizontal">
    <CheckBox IsChecked="{Binding IsEnabled}" VerticalAlignment="Center" Margin="0,0,8,0"/>
    <TextBlock Text="{Binding Name}" VerticalAlignment="Center"/>
  </StackPanel>
</DataTemplate>
""");
        _list.SelectionChanged += (_, _) => Describe();
        if (!svc.Account.UseNativeMapi) _list.MouseDoubleClick += (_, _) => EditRule();
        root.Children.Add(_list);
        root.Children.Add(new TextBlock { Text = Loc.T("RuleDescription"), Margin = new Thickness(0, 10, 0, 4) });
        root.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), MinHeight = 90, Child = _desc });
        root.Children.Add(ButtonRow(MakeButton(Loc.T("Ok"), true, false, async () => { if (await SaveAsync()) DialogResult = true; }),
            MakeButton(Loc.T("Cancel"), false, true), MakeButton(Loc.T("Apply"), false, false, async () => await SaveAsync())));
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IReadOnlyList<RuleInfo> rules = Array.Empty<RuleInfo>();
        if (!await Ui.RunAsync(async () =>
            {
                rules = await _svc.GetRulesAsync();
                _folders = (await _svc.GetFolderTreeAsync()).ToList();
            }, this)) { Close(); return; }
        _rules.Clear();
        foreach (var r in rules)
        {
            r.MoveToFolderName ??= _folders.FirstOrDefault(f => f.Id == r.MoveToFolderId)?.DisplayName;
            r.CopyToFolderName ??= _folders.FirstOrDefault(f => f.Id == r.CopyToFolderId)?.DisplayName;
            _rules.Add(r);
        }
        if (_rules.Count > 0) _list.SelectedIndex = 0;
    }

    private RuleInfo? Selected => _list.SelectedItem as RuleInfo;

    private void Describe()
    {
        _desc.Text = Selected == null ? "" : RuleEditWindow.Describe(Selected);
    }

    private void NewRule()
    {
        var w = new RuleEditWindow(_svc, new RuleInfo(), _folders) { Owner = this };
        if (w.ShowDialog() == true)
        {
            _rules.Insert(_svc.Account.UseNativeMapi ? _rules.Count : 0, w.Rule);
            _list.SelectedItem = w.Rule;
        }
    }

    private void EditRule()
    {
        if (Selected == null) return;
        if (Selected.HasUnsupportedParts) Ui.Info(Loc.T("RuleUnsupported"), this);
        var w = new RuleEditWindow(_svc, Selected, _folders) { Owner = this };
        if (w.ShowDialog() == true)
        {
            var idx = _rules.IndexOf(Selected);
            _rules[idx] = w.Rule;
            _list.SelectedIndex = idx;
        }
    }

    private void CopyRule()
    {
        if (Selected == null) return;
        var copy = SettingsService.Deserialize<RuleInfo>(SettingsService.Serialize(Selected))!;
        copy.Id = null;
        copy.Name = Loc.F("CopyOf", Selected.Name);
        _rules.Insert(_rules.IndexOf(Selected) + 1, copy);
    }

    private void DeleteRule()
    {
        if (Selected == null) return;
        if (!Ui.Confirm($"{Loc.T("DeleteRule")}: {Selected.Name}?", this)) return;
        if (!string.IsNullOrEmpty(Selected.Id)) _deleted.Add(Selected.Id!);
        _rules.Remove(Selected);
    }

    private void MoveRule(int dir)
    {
        if (Selected == null) return;
        var i = _rules.IndexOf(Selected);
        var j = i + dir;
        if (j < 0 || j >= _rules.Count) return;
        _rules.Move(i, j);
    }

    private async Task<bool> SaveAsync()
    {
        var ok = await Ui.RunAsync(() => _svc.SaveRulesAsync(_rules.ToList(), _deleted.ToList()), this);
        if (ok) { _deleted.Clear(); await LoadAsync(); }
        return ok;
    }
}

// =========================================================================================
// Rule wizard (conditions / actions / name)
// =========================================================================================
public sealed class RuleEditWindow : DialogWindow
{
    private readonly IMailService _svc;
    private readonly List<MailFolderInfo> _folders;
    public RuleInfo Rule { get; }

    private readonly CheckBox _cFrom = new(), _cTo = new(), _cSubject = new(), _cBody = new(), _cSender = new(), _cOnlyMe = new(), _cCc = new(), _cAttach = new(), _cImp = new();
    private readonly TextBox _vFrom = Box(), _vTo = Box(), _vSubject = Box(), _vBody = Box(), _vSender = Box();
    private readonly ComboBox _vImp = new() { Width = 120 };
    private readonly CheckBox _aMove = new(), _aCopy = new(), _aDelete = new(), _aPerm = new(), _aRead = new(), _aFwd = new(), _aRedirect = new(), _aCat = new(), _aImp = new(), _aStop = new();
    private readonly Button _vMove = new(), _vCopy = new();
    private readonly TextBox _vFwd = Box(), _vRedirect = Box();
    private readonly ComboBox _vCat = new() { Width = 200 }, _vAImp = new() { Width = 120 };
    private readonly TextBox _name = new() { Height = 24, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _enabled = new() { IsChecked = true };
    private MailFolderInfo? _moveFolder, _copyFolder;

    private static TextBox Box() => new() { Width = 260, Height = 22, VerticalContentAlignment = VerticalAlignment.Center };

    public RuleEditWindow(IMailService svc, RuleInfo rule, List<MailFolderInfo> folders)
    {
        _svc = svc;
        _folders = folders;
        Rule = SettingsService.Deserialize<RuleInfo>(SettingsService.Serialize(rule))!;
        Rule.Id = rule.Id;
        Title = Loc.T("RuleWizard");
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12), Width = 640 };

        root.Children.Add(Header(Loc.T("Step1Conditions")));
        var native = svc.Account.UseNativeMapi;
        if (!native) root.Children.Add(Line(_cFrom, "CondFrom", _vFrom));
        if (!native) root.Children.Add(Line(_cTo, "CondSentTo", _vTo));
        root.Children.Add(Line(_cSubject, "CondSubject", _vSubject));
        root.Children.Add(Line(_cBody, "CondBody", _vBody));
        root.Children.Add(Line(_cSender, "CondSenderContains", _vSender));
        if (!native) root.Children.Add(Line(_cOnlyMe, "CondSentOnlyToMe", null));
        if (!native) root.Children.Add(Line(_cCc, "CondCcMe", null));
        root.Children.Add(Line(_cAttach, "CondHasAttachment", null));
        foreach (var k in new[] { "High", "NormalPriority", "Low" }) { _vImp.Items.Add(Loc.T(k)); _vAImp.Items.Add(Loc.T(k)); }
        _vImp.SelectedIndex = 0; _vAImp.SelectedIndex = 0;
        root.Children.Add(Line(_cImp, "CondImportance", _vImp));

        root.Children.Add(Header(Loc.T("Step2Actions")));
        _vMove.Style = _vCopy.Style = (Style)FindResource("LinkButton");
        _vMove.Click += (_, _) => PickFolder(true);
        _vCopy.Click += (_, _) => PickFolder(false);
        root.Children.Add(Line(_aMove, "ActMoveTo", _vMove));
        root.Children.Add(Line(_aCopy, "ActCopyTo", _vCopy));
        root.Children.Add(Line(_aDelete, "ActDelete", null));
        if (!native) root.Children.Add(Line(_aPerm, "ActPermanentDelete", null));
        root.Children.Add(Line(_aRead, "ActMarkRead", null));
        if (!native) root.Children.Add(Line(_aFwd, "ActForward", _vFwd));
        if (!native) root.Children.Add(Line(_aRedirect, "ActRedirect", _vRedirect));
        foreach (var c in AppServices.Current.Settings.Categories) _vCat.Items.Add(new ComboBoxItem { Content = Loc.T(c.Name), Tag = c.Name });
        if (_vCat.Items.Count > 0) _vCat.SelectedIndex = 0;
        if (!native) root.Children.Add(Line(_aCat, "ActCategory", _vCat));
        root.Children.Add(Line(_aImp, "ActImportance", _vAImp));
        root.Children.Add(Line(_aStop, "ActStop", null));

        root.Children.Add(Header(Loc.T("Step3Name")));
        root.Children.Add(_name);
        _enabled.Content = Loc.T("RuleEnabled");
        _enabled.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(_enabled);
        root.Children.Add(ButtonRow(MakeButton(Loc.T("Ok"), true, false, Accept), MakeButton(Loc.T("Cancel"), false, true)));
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 80 };
        Load();
    }

    private static TextBlock Header(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6), Foreground = GlyphFactory.Blue };

    private static DockPanel Line(CheckBox cb, string key, FrameworkElement? value)
    {
        cb.Content = Loc.T(key);
        cb.VerticalAlignment = VerticalAlignment.Center;
        var dp = new DockPanel { Margin = new Thickness(6, 2, 0, 2) };
        if (value != null)
        {
            DockPanel.SetDock(value, Dock.Right);
            dp.Children.Add(value);
            if (value is TextBox tb) tb.TextChanged += (_, _) => { if (tb.Text.Length > 0) cb.IsChecked = true; };
        }
        dp.Children.Add(cb);
        return dp;
    }

    private static string Join(IEnumerable<string> l) => string.Join("; ", l);
    private static List<string> Split(string s) => s.Split(';', ',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    private void Load()
    {
        var r = Rule;
        _vFrom.Text = Join(r.FromAddresses); _cFrom.IsChecked = r.FromAddresses.Count > 0;
        _vTo.Text = Join(r.SentToAddresses); _cTo.IsChecked = r.SentToAddresses.Count > 0;
        _vSubject.Text = Join(r.SubjectContains); _cSubject.IsChecked = r.SubjectContains.Count > 0;
        _vBody.Text = Join(r.BodyContains); _cBody.IsChecked = r.BodyContains.Count > 0;
        _vSender.Text = Join(r.SenderContains); _cSender.IsChecked = r.SenderContains.Count > 0;
        _cOnlyMe.IsChecked = r.SentOnlyToMe;
        _cCc.IsChecked = r.MyNameInCc;
        _cAttach.IsChecked = r.HasAttachments;
        _cImp.IsChecked = r.WithImportance.HasValue;
        if (r.WithImportance.HasValue) _vImp.SelectedIndex = r.WithImportance == Importance.High ? 0 : r.WithImportance == Importance.Low ? 2 : 1;
        _moveFolder = _folders.FirstOrDefault(f => f.Id == r.MoveToFolderId);
        _copyFolder = _folders.FirstOrDefault(f => f.Id == r.CopyToFolderId);
        _aMove.IsChecked = r.MoveToFolderId != null;
        _aCopy.IsChecked = r.CopyToFolderId != null;
        _vMove.Content = _moveFolder != null ? FolderVisuals.LocalizedName(_moveFolder) : r.MoveToFolderName ?? Loc.T("ChooseFolder");
        _vCopy.Content = _copyFolder != null ? FolderVisuals.LocalizedName(_copyFolder) : r.CopyToFolderName ?? Loc.T("ChooseFolder");
        _aDelete.IsChecked = r.Delete;
        _aPerm.IsChecked = r.PermanentDelete;
        _aRead.IsChecked = r.MarkAsRead;
        _vFwd.Text = Join(r.ForwardTo); _aFwd.IsChecked = r.ForwardTo.Count > 0;
        _vRedirect.Text = Join(r.RedirectTo); _aRedirect.IsChecked = r.RedirectTo.Count > 0;
        _aCat.IsChecked = r.AssignCategories.Count > 0;
        if (r.AssignCategories.Count > 0) _vCat.SelectedItem = _vCat.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == r.AssignCategories[0]);
        _aImp.IsChecked = r.MarkImportance.HasValue;
        if (r.MarkImportance.HasValue) _vAImp.SelectedIndex = r.MarkImportance == Importance.High ? 0 : r.MarkImportance == Importance.Low ? 2 : 1;
        _aStop.IsChecked = r.StopProcessing;
        _name.Text = r.Name;
        _enabled.IsChecked = r.IsEnabled;
    }

    private void PickFolder(bool move)
    {
        var w = new FolderPickerWindow(_svc, Loc.T("ChooseFolder"), _folders) { Owner = this };
        if (w.ShowDialog() != true || w.SelectedFolder == null) return;
        if (move) { _moveFolder = w.SelectedFolder; _vMove.Content = FolderVisuals.LocalizedName(w.SelectedFolder); _aMove.IsChecked = true; }
        else { _copyFolder = w.SelectedFolder; _vCopy.Content = FolderVisuals.LocalizedName(w.SelectedFolder); _aCopy.IsChecked = true; }
    }

    private static Importance ImpAt(int i) => i == 0 ? Importance.High : i == 2 ? Importance.Low : Importance.Normal;

    private void Accept()
    {
        var r = Rule;
        r.FromAddresses = _cFrom.IsChecked == true ? Split(_vFrom.Text) : new();
        r.SentToAddresses = _cTo.IsChecked == true ? Split(_vTo.Text) : new();
        r.SubjectContains = _cSubject.IsChecked == true ? Split(_vSubject.Text) : new();
        r.BodyContains = _cBody.IsChecked == true ? Split(_vBody.Text) : new();
        r.SenderContains = _cSender.IsChecked == true ? Split(_vSender.Text) : new();
        r.SentOnlyToMe = _cOnlyMe.IsChecked == true;
        r.MyNameInCc = _cCc.IsChecked == true;
        r.HasAttachments = _cAttach.IsChecked == true;
        r.WithImportance = _cImp.IsChecked == true ? ImpAt(_vImp.SelectedIndex) : null;
        r.MoveToFolderId = _aMove.IsChecked == true ? _moveFolder?.Id ?? r.MoveToFolderId : null;
        r.MoveToFolderName = _aMove.IsChecked == true ? _vMove.Content?.ToString() : null;
        r.CopyToFolderId = _aCopy.IsChecked == true ? _copyFolder?.Id ?? r.CopyToFolderId : null;
        r.CopyToFolderName = _aCopy.IsChecked == true ? _vCopy.Content?.ToString() : null;
        r.Delete = _aDelete.IsChecked == true;
        r.PermanentDelete = _aPerm.IsChecked == true;
        r.MarkAsRead = _aRead.IsChecked == true;
        r.ForwardTo = _aFwd.IsChecked == true ? Split(_vFwd.Text) : new();
        r.RedirectTo = _aRedirect.IsChecked == true ? Split(_vRedirect.Text) : new();
        r.AssignCategories = _aCat.IsChecked == true && _vCat.SelectedItem is ComboBoxItem { Tag: string cat } ? new List<string> { cat } : new();
        r.MarkImportance = _aImp.IsChecked == true ? ImpAt(_vAImp.SelectedIndex) : null;
        r.StopProcessing = _aStop.IsChecked == true;
        r.IsEnabled = _enabled.IsChecked == true;
        var hasAction = r.MoveToFolderId != null || r.CopyToFolderId != null || r.Delete || r.PermanentDelete || r.MarkAsRead || r.ForwardTo.Count > 0 ||
                        r.RedirectTo.Count > 0 || r.AssignCategories.Count > 0 || r.MarkImportance.HasValue || (!_svc.Account.UseNativeMapi && r.StopProcessing);
        if (!hasAction)
        {
            Ui.Error(Loc.Instance.IsGeorgian ? "აირჩიეთ მინიმუმ ერთი მოქმედება." : "Select at least one action.", this);
            return;
        }
        if (_svc.Account.UseNativeMapi && r.SubjectContains.Count == 0 && r.BodyContains.Count == 0 && r.SenderContains.Count == 0 && !r.HasAttachments && r.WithImportance == null)
        {
            Ui.Error(Loc.T("NativeRuleConditionRequired"), this);
            return;
        }
        r.Name = string.IsNullOrWhiteSpace(_name.Text) ? Describe(r).Split('\n')[0] : _name.Text.Trim();
        DialogResult = true;
    }

    public static string Describe(RuleInfo r)
    {
        var ka = Loc.Instance.IsGeorgian;
        var lines = new List<string> { ka ? "შეტყობინების მიღების შემდეგ" : "Apply this rule after the message arrives" };
        if (r.FromAddresses.Count > 0) lines.Add((ka ? "გამგზავნი: " : "from ") + Join(r.FromAddresses));
        if (r.SentToAddresses.Count > 0) lines.Add((ka ? "ადრესატი: " : "sent to ") + Join(r.SentToAddresses));
        if (r.SubjectContains.Count > 0) lines.Add((ka ? "თემაში: " : "with ") + Join(r.SubjectContains) + (ka ? "" : " in the subject"));
        if (r.BodyContains.Count > 0) lines.Add((ka ? "ტექსტში: " : "with ") + Join(r.BodyContains) + (ka ? "" : " in the body"));
        if (r.SenderContains.Count > 0) lines.Add(Loc.T("CondSenderContains") + " " + Join(r.SenderContains));
        if (r.WithImportance.HasValue) lines.Add(Loc.T("CondImportance"));
        if (r.SentOnlyToMe) lines.Add(Loc.T("CondSentOnlyToMe"));
        if (r.HasAttachments) lines.Add(Loc.T("CondHasAttachment"));
        if (r.MoveToFolderId != null) lines.Add((ka ? "გადაიტანე: " : "move it to ") + (r.MoveToFolderName ?? "…"));
        if (r.CopyToFolderId != null) lines.Add((ka ? "დააკოპირე: " : "move a copy to ") + (r.CopyToFolderName ?? "…"));
        if (r.Delete) lines.Add(Loc.T("ActDelete"));
        if (r.MarkAsRead) lines.Add(Loc.T("ActMarkRead"));
        if (r.MarkImportance.HasValue) lines.Add(Loc.T("ActImportance"));
        if (r.ForwardTo.Count > 0) lines.Add((ka ? "გადაუგზავნე: " : "forward it to ") + Join(r.ForwardTo));
        if (r.AssignCategories.Count > 0) lines.Add((ka ? "კატეგორია: " : "assign it to the ") + Loc.T(r.AssignCategories[0]));
        if (r.StopProcessing) lines.Add(Loc.T("ActStop"));
        return string.Join("\n", lines);
    }
}
