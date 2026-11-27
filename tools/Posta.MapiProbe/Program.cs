using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Posta.Core.Models;
using Posta.Mapi;

namespace Posta.MapiProbe;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => new Application().Run(new ProbeWindow(args));
}

internal sealed class ProbeWindow : Window
{
    private readonly TextBox _email = new();
    private readonly TextBox _server = new();
    private readonly TextBox _user = new();
    private readonly PasswordBox _password = new();
    private readonly Button _connect = new() { Content = "კავშირის შემოწმება", Padding = new Thickness(16, 6, 16, 6) };
    private readonly CheckBox _full = new() { Content = "სრული ტესტი: წაკითხვა + დროებითი საქაღალდის შექმნა/წაშლა", Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _output = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _endpoint = new();
    private readonly TextBox _dn = new();

    public ProbeWindow(string[] args)
    {
        string Option(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        _email.Text = Option("--email"); _server.Text = Option("--server"); _user.Text = Option("--user");
        Title = "Posta — დამოუკიდებელი MAPI/HTTP კავშირის შემოწმება";
        Width = 850; Height = 770; MinWidth = 640; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        var root = new DockPanel { Margin = new Thickness(24) };
        var form = new StackPanel(); DockPanel.SetDock(form, Dock.Top); root.Children.Add(form);
        form.Children.Add(new TextBlock { Text = "Exchange-თან პირდაპირი კავშირი", FontSize = 24, Margin = new Thickness(0, 0, 0, 8) });
        form.Children.Add(new TextBlock { Text = "Outlook არ გამოიყენება. ტესტი კითხულობს მხოლოდ საქაღალდეებსა და წერილების სიას; არაფერს აგზავნის ან ცვლის.", TextWrapping = TextWrapping.Wrap });
        Add(form, "ელფოსტა", _email); Add(form, "Autodiscover მისამართი (HTTPS)", _server);
        Add(form, "მომხმარებელი (DOMAIN\\user ან UPN)", _user); Add(form, "მიმდინარე პაროლი (არ შეინახება)", _password);
        var advanced = new StackPanel(); Add(advanced, "MAPI MailStore URL (Autodiscover-ის ნაცვლად)", _endpoint);
        Add(advanced, "LegacyExchangeDN", _dn);
        form.Children.Add(new Expander { Header = "ხელით მითითება", Content = advanced, Margin = new Thickness(0, 10, 0, 0) });
        form.Children.Add(_full);
        _connect.Margin = new Thickness(0, 12, 0, 12); _connect.HorizontalAlignment = HorizontalAlignment.Left;
        form.Children.Add(_connect); _connect.Click += async (_, _) => await RunAsync();
        root.Children.Add(_output); Content = root;
        Loaded += (_, _) => _password.Focus();
    }

    private static void Add(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
        control.Height = 30; control.Padding = new Thickness(5, 2, 5, 2); panel.Children.Add(control);
    }

    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(_password.Password)) { _output.Text = "შეიყვანეთ მიმდინარე პაროლი ამ ფანჯარაში."; return; }
        _connect.IsEnabled = false; _output.Clear();
        var stage = "input";
        var report = new Dictionary<string, object?> { ["timeUtc"] = DateTime.UtcNow, ["provider"] = "native-mapi-http", ["usesOutlook"] = false };
        try
        {
            var user = _user.Text.Trim(); var domain = "";
            if (user.Contains('\\')) { var parts = user.Split('\\', 2); domain = parts[0]; user = parts[1]; }
            var credentials = new NetworkCredential(user, _password.Password, domain);
            MapiEndpoint endpoint;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            if (!string.IsNullOrWhiteSpace(_endpoint.Text) && !string.IsNullOrWhiteSpace(_dn.Text))
                endpoint = new(new Uri(_endpoint.Text.Trim()), _dn.Text.Trim());
            else
            {
                stage = "autodiscover"; Log("Autodiscover — MAPI მისამართის მიღება…");
                var discovery = new Uri(_server.Text.Trim());
                endpoint = await MapiHttpSession.DiscoverAsync(discovery, _email.Text.Trim(), credentials, timeout.Token);
                // Credentials may not move to a different host without the user selecting it.
                if (!string.Equals(endpoint.MailStore.Host, discovery.Host, StringComparison.OrdinalIgnoreCase))
                    throw new MapiException("Autodiscover სხვა ჰოსტს აბრუნებს. ხელით მითითების ველებში გადაამოწმეთ მისამართი და LegacyDN.");
                _endpoint.Text = endpoint.MailStore.ToString(); _dn.Text = endpoint.LegacyDn;
            }
            stage = "connect"; Log("MAPI/HTTP Connect…");
            await using var session = new MapiHttpSession(endpoint.MailStore, credentials);
            var connection = await session.ConnectAsync(endpoint.LegacyDn, timeout.Token);
            report["server"] = connection.ServerVersion; Log("Connect წარმატებულია.");
            stage = "logon"; var logon = await session.LogonAsync(endpoint.LegacyDn, timeout.Token);
            Log("Mailbox Logon წარმატებულია.");
            stage = "folders";
            var folders = await session.ReadTableAsync(logon, logon.FolderIds[3], true, MapiHttpSession.HierarchyColumns, 5000, timeout.Token);
            report["foldersRead"] = folders.Rows.Count; Log($"საქაღალდეები: {folders.Rows.Count}");
            stage = "inbox";
            var messages = await session.ReadTableAsync(logon, logon.FolderIds[4], false, MapiHttpSession.ContentsColumns, 20, timeout.Token);
            report["inboxTotal"] = messages.Total; report["inboxRowsRead"] = messages.Rows.Count;
            Log($"Inbox: {messages.Total}; წაკითხულია {messages.Rows.Count} ჩანაწერი.");
            foreach (var row in messages.Rows) Log("• " + (row.GetValueOrDefault(0x0037001fu) as string ?? "(უსათაურო)"));
            if (_full.IsChecked == true)
            {
                stage = "full"; Log("— სრული ტესტი (NativeMapiMailService) —");
                await FullVerifyAsync(endpoint, user, domain, _password.Password, report, timeout.Token);
            }
            report["success"] = true; Log("დამოუკიდებელი MAPI/HTTP კავშირი დადასტურდა. ეს ჯერ არ ადასტურებს დანარჩენი ფუნქციების მუშაობას.");
        }
        catch (Exception ex)
        {
            report["success"] = false; report["stage"] = stage;
            // Store only exception types and numeric status, never raw bodies, headers or credentials.
            report["errorType"] = ex.GetType().Name;
            Log($"ეტაპი: {stage}\n{ex.Message}");
        }
        finally
        {
            _password.Clear(); _connect.IsEnabled = true;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Posta");
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "mapi-probe.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Log("ტესტის შედეგი: %LocalAppData%\\Posta\\mapi-probe.json (პაროლისა და წერილების ტექსტის გარეშე).");
            }
            catch (IOException) { Log("შედეგის ფაილში ჩაწერა ვერ მოხერხდა."); }
        }
    }

    private async Task FullVerifyAsync(MapiEndpoint endpoint, string user, string domain, string password, Dictionary<string, object?> report, CancellationToken ct)
    {
        var account = new AccountConfig
        {
            Id = "probe", Email = _email.Text.Trim(), UseNativeMapi = true,
            ServerUrl = string.IsNullOrWhiteSpace(_server.Text) ? endpoint.MailStore.GetLeftPart(UriPartial.Authority) : _server.Text.Trim(),
            UserName = domain.Length > 0 ? domain + "\\" + user : user
        };
        await using var svc = new NativeMapiMailService(account);
        await svc.ConnectAsync(password, ct);
        var folders = await svc.GetFolderTreeAsync(ct: ct);
        report["svcFolders"] = folders.Count; Log($"საქაღალდეები (service): {folders.Count}");
        var inbox = folders.First(f => f.Kind == FolderKind.Inbox);
        var page = await svc.GetItemsAsync(new MailQuery { FolderId = inbox.Id, PageSize = 3 }, ct);
        report["svcInboxTotal"] = page.TotalCount;
        if (page.Items.Count > 0)
        {
            var msg = await svc.GetMessageAsync(page.Items[0].Id, ct);
            report["bodyChars"] = msg.BodyHtml.Length; report["recipients"] = msg.To.Count + msg.Cc.Count + msg.Bcc.Count; report["attachments"] = msg.Attachments.Count;
            Log($"წერილის body: {msg.BodyHtml.Length} სიმბ., მიმღები {msg.To.Count + msg.Cc.Count + msg.Bcc.Count}, დანართი {msg.Attachments.Count}");
        }
        var cals = await svc.GetOwnCalendarsAsync(ct);
        report["calendars"] = cals.Count;
        if (cals.Count > 0)
        {
            var source = cals.FirstOrDefault(c => c.IsDefault) ?? cals[0];
            var evs = await svc.GetEventsAsync(source, DateTime.Today.AddDays(-30), DateTime.Today.AddDays(30), ct);
            report["events"] = evs.Count; report["recurringOccurrences"] = evs.Count(e => e.IsOccurrence);
            Log($"კალენდარი: {cals.Count}, მოვლენა ±30დ: {evs.Count} (მათ შორის განმეორებადიდან {evs.Count(e => e.IsOccurrence)})");
        }
        var contacts = await svc.GetContactsAsync("", null, ct); report["contacts"] = contacts.Count;
        var tasks = await svc.GetTasksAsync("", true, ct); report["tasks"] = tasks.Count;
        Log($"კონტაქტები: {contacts.Count}, ამოცანები: {tasks.Count}");

        // GAL / ResolveNames (NSPI) — experimental; isolate failures so the rest of the test still reports.
        try
        {
            var browse = await svc.ResolveNamesAsync("", false, ct); report["galBrowse"] = browse.Count;
            var resolved = await svc.ResolveNamesAsync("odishelidze", false, ct); report["galResolve"] = resolved.Count;
            report["galFirstHasEmail"] = resolved.FirstOrDefault() is { Email.Length: > 0 };
            Log($"GAL: browse {browse.Count}, resolve 'odishelidze' {resolved.Count}");
        }
        catch (Exception ex) { report["galError"] = ex.Message; Log("GAL შეცდომა: " + ex.Message); }

        // Reversible folder round-trip: a uniquely named, empty throwaway folder under Inbox, hard-deleted at the end.
        var name = "Posta-ტესტი-" + Guid.NewGuid().ToString("N")[..6];
        var created = await svc.CreateFolderAsync(inbox.Id, name, "IPF.Note", ct);
        await svc.RenameFolderAsync(created, name + "-ok", ct);
        var found = (await svc.GetFolderTreeAsync(ct: ct)).Any(f => f.Id == created && f.DisplayName == name + "-ok");
        await svc.DeleteFolderAsync(created, true, ct);
        var gone = (await svc.GetFolderTreeAsync(ct: ct)).All(f => f.Id != created);
        report["folderRoundtrip"] = found && gone;
        Log($"საქაღალდის შექმნა/გადარქმევა/წაშლა: {(found && gone ? "წარმატება" : "ვერ დადასტურდა")}");
    }

    private void Log(string line) { _output.AppendText(line + Environment.NewLine); _output.ScrollToEnd(); }
}
