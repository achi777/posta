using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Posta.App.Infrastructure;
using Posta.App.Services;
using Posta.Core.Models;
using Posta.Core.Services;

namespace Posta.App.Views;

/// <summary>Simple selectable list used by the People and Tasks folder panes.</summary>
public abstract class SimpleNavPane : UserControl
{
    protected readonly StackPanel Items = new() { Margin = new Thickness(0, 4, 0, 8) };
    private Border? _selected;

    protected SimpleNavPane()
    {
        Background = (Brush)Application.Current.FindResource("FolderPaneBrush");
        Content = new ScrollViewer { Content = Items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loc.Instance.LanguageChanged += (_, _) => Rebuild();
        AppServices.Current.AccountsChanged += (_, _) => Rebuild();
    }

    protected abstract void Rebuild();

    protected void Header(string text)
    {
        Items.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(12, 8, 6, 4) });
    }

    protected Border Entry(string glyph, string text, Action onSelect, bool select = false)
    {
        var dp = new DockPanel { Margin = new Thickness(22, 0, 6, 0) };
        var icon = Ui.GlyphText(glyph, 13, (Brush)Application.Current.FindResource("TextSecondaryBrush"));
        icon.Margin = new Thickness(0, 0, 8, 0);
        dp.Children.Add(icon);
        dp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Border { Child = dp, Height = 28, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        b.MouseEnter += (_, _) => { if (b != _selected) b.Background = new SolidColorBrush(Color.FromRgb(0xEB, 0xEB, 0xEB)); };
        b.MouseLeave += (_, _) => { if (b != _selected) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (_, _) => { Select(b); onSelect(); };
        Items.Children.Add(b);
        if (select) Select(b);
        return b;
    }

    private void Select(Border b)
    {
        if (_selected != null) _selected.Background = Brushes.Transparent;
        _selected = b;
        b.Background = (Brush)Application.Current.FindResource("SelectionBrush");
    }
}

public sealed class PeopleNavPane : SimpleNavPane
{
    private PeopleView? _view;

    public void Attach(PeopleView view)
    {
        _view = view;
        Rebuild();
    }

    protected override void Rebuild()
    {
        Items.Children.Clear();
        if (_view == null) return;
        Header(Loc.T("MyContacts"));
        var first = true;
        foreach (var svc in AppServices.Current.Accounts)
        {
            var s = svc;
            var name = AppServices.Current.Accounts.Count > 1 ? $"{Loc.T("F_Contacts")} – {svc.Account.Email}" : Loc.T("F_Contacts");
            Entry(G.People, name, () => _ = _view.ShowFolderAsync(new ContactFolderRef { Service = s, FolderId = "", Name = name }), first);
            first = false;
        }
        _ = AddSubFoldersAsync();
    }

    private async Task AddSubFoldersAsync()
    {
        foreach (var svc in AppServices.Current.Accounts.ToList())
        {
            if (svc.State != ConnectionState.Connected) continue;
            try
            {
                var folders = await svc.GetFolderTreeAsync();
                foreach (var f in folders.Where(f => f.IsContactFolder && f.Kind != FolderKind.Contacts))
                {
                    var folder = f;
                    Entry(G.People, f.DisplayName, () => _ = _view!.ShowFolderAsync(new ContactFolderRef { Service = svc, FolderId = folder.Id, Name = folder.DisplayName }));
                }
            }
            catch { }
        }
    }
}

public sealed class TasksNavPane : SimpleNavPane
{
    private TasksView? _view;

    public void Attach(TasksView view)
    {
        _view = view;
        Rebuild();
    }

    protected override void Rebuild()
    {
        Items.Children.Clear();
        if (_view == null) return;
        Header(Loc.Instance.IsGeorgian ? "ჩემი ამოცანები" : "My Tasks");
        Entry(G.Flag, Loc.T("ToDoList"), () => _ = _view.ShowAsync(true), true);
        Entry(G.Tasks, Loc.T("F_Tasks"), () => _ = _view.ShowAsync(false));
    }
}
