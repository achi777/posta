using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Posta.App.Infrastructure;
using Posta.Core.Models;
using Posta.Core.Services;
using Posta.Exchange;
using Posta.Outlook;
using Posta.Mapi;

namespace Posta.App.Services;

/// <summary>Application-wide services: settings, accounts, cache, WebView2 environment and background sync.</summary>
public sealed class AppServices
{
    public static AppServices Current { get; private set; } = null!;

    public SettingsService SettingsService { get; } = new();
    public AppSettings Settings => SettingsService.Settings;
    public MessageCache Cache { get; private set; } = null!;
    public ObservableCollection<IMailService> Accounts { get; } = new();
    public bool DemoMode { get; private set; }

    public event EventHandler? AccountsChanged;
    public event EventHandler<IReadOnlyList<NewMailNotification>>? NewMailArrived;
    public event EventHandler? RemindersChanged;
    public event EventHandler? SyncTick;
    /// <summary>Raised when something changed that requires the current views to refresh (folder, item ids).</summary>
    public event EventHandler<string>? DataChanged;

    public ObservableCollection<ReminderInfo> ActiveReminders { get; } = new();

    private readonly DispatcherTimer _pollTimer = new();
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, DateTime> _lastMailCheck = new();
    private readonly Dictionary<string, DateTime> _snoozed = new();
    private Task<CoreWebView2Environment>? _webEnvTask;
    private bool _polling;

    public static void Initialize(bool demo)
    {
        Current = new AppServices { DemoMode = demo };
        Current.Init();
    }

    private void Init()
    {
        SettingsService.Load();
        Cache = new MessageCache(Path.Combine(SettingsService.LocalFolder, DemoMode ? "cache-demo.db" : "cache.db"));
        Loc.Instance.SetLanguage(Settings.Language);

        if (DemoMode)
        {
            var demo = new AccountConfig { Id = "demo", DisplayName = "ანა გელაშვილი", Email = "ana.gelashvili@contoso.example", IsDemo = true };
            demo.SharedCalendars.Add(new SharedCalendarRef { OwnerEmail = "nino.beridze@contoso.example", OwnerName = "ნინო ბერიძე", DisplayName = "ნინო ბერიძე", Color = "#C0E4A8" });
            Accounts.Add(new DemoMailService(demo));
        }
        else
        {
            foreach (var a in SettingsService.Accounts.OrderBy(a => a.SortOrder))
                Accounts.Add(CreateService(a));
        }

        _pollTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, Settings.SendReceiveIntervalMinutes));
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _reminderTimer.Tick += async (_, _) => await CheckRemindersAsync();
    }

    public static IMailService CreateService(AccountConfig a) =>
        a.IsDemo ? new DemoMailService(a) :
        a.UseNativeMapi ? new NativeMapiMailService(a) :
        a.UseOutlookInterop ? new OutlookMailService(a) :
        new EwsMailService(a);

    public IMailService? DefaultAccount => Accounts.FirstOrDefault();
    public IMailService? GetAccount(string? id) => Accounts.FirstOrDefault(a => a.Account.Id == id) ?? DefaultAccount;

    public string? CategoryColor(string? name) =>
        Settings.Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Color;

    public void RaiseDataChanged(string what) => DataChanged?.Invoke(this, what);

    // ------------------------------------------------------------------ WebView2

    public Task<CoreWebView2Environment> GetWebViewEnvironmentAsync()
    {
        return _webEnvTask ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(SettingsService.LocalFolder, "WebView2"),
            new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection"));
    }

    // ------------------------------------------------------------------ Accounts

    /// <summary>Connects all accounts; prompts for passwords where none are stored.</summary>
    public async Task ConnectAllAsync(Window owner)
    {
        foreach (var svc in Accounts.ToList())
            await ConnectAccountAsync(svc, owner, interactive: true);
        StartBackground();
    }

    public async Task<bool> ConnectAccountAsync(IMailService svc, Window? owner, bool interactive)
    {
        if (Settings.WorkOffline) return false;
        bool noPassword = svc.Account.UseWindowsCredentials || svc.Account.IsDemo || svc.Account.UseOutlookInterop;
        string? password = noPassword ? null : CredentialStore.Load(svc.Account.Id);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!noPassword && string.IsNullOrEmpty(password))
            {
                if (!interactive || owner == null) return false;
                var dlg = new Windows.PasswordWindow(svc.Account) { Owner = owner };
                if (dlg.ShowDialog() != true) return false;
                password = dlg.Password;
                if (dlg.Remember) CredentialStore.Save(svc.Account.Id, svc.Account.EffectiveUser, password);
            }
            try
            {
                await svc.ConnectAsync(password);
                SettingsService.SaveAccounts();
                return true;
            }
            catch (MailServiceException) when (svc.State == ConnectionState.NeedsPassword)
            {
                password = null;
                CredentialStore.Delete(svc.Account.Id);
            }
            catch (MailServiceException)
            {
                return false;
            }
        }
        return false;
    }

    public async Task<IMailService> AddAccountAsync(AccountConfig config, string? password, bool remember)
    {
        var svc = CreateService(config);
        await svc.ConnectAsync(password);
        if (!config.UseWindowsCredentials && password != null && remember)
            CredentialStore.Save(config.Id, config.EffectiveUser, password);
        config.SortOrder = SettingsService.Accounts.Count;
        SettingsService.Accounts.Add(config);
        SettingsService.SaveAccounts();
        Accounts.Add(svc);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        StartBackground();
        return svc;
    }

    public void RemoveAccount(IMailService svc)
    {
        Accounts.Remove(svc);
        SettingsService.Accounts.RemoveAll(a => a.Id == svc.Account.Id);
        SettingsService.SaveAccounts();
        CredentialStore.Delete(svc.Account.Id);
        Cache.ClearAccount(svc.Account.Id);
        _ = CloseServiceAsync(svc);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task CloseServiceAsync(IMailService service)
    {
        try
        {
            if (service is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (service is IDisposable disposable) disposable.Dispose();
        }
        catch { /* Removing an account must still work if its transport is already broken. */ }
    }

    public void NotifyAccountsChanged()
    {
        SettingsService.SaveAccounts();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ Background sync

    public void StartBackground()
    {
        foreach (var a in Accounts)
            if (!_lastMailCheck.ContainsKey(a.Account.Id)) _lastMailCheck[a.Account.Id] = DateTime.UtcNow;
        _pollTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, Settings.SendReceiveIntervalMinutes));
        _pollTimer.Start();
        _reminderTimer.Start();
        _ = CheckRemindersAsync();
    }

    public async Task PollAsync()
    {
        if (_polling || Settings.WorkOffline) return;
        _polling = true;
        try
        {
            var all = new List<NewMailNotification>();
            foreach (var svc in Accounts.ToList())
            {
                if (svc.State != ConnectionState.Connected)
                {
                    if (svc.State is ConnectionState.Offline or ConnectionState.Error) await ConnectAccountAsync(svc, null, false);
                    if (svc.State != ConnectionState.Connected) continue;
                }
                var since = _lastMailCheck.TryGetValue(svc.Account.Id, out var t) ? t : DateTime.UtcNow.AddMinutes(-2);
                var now = DateTime.UtcNow;
                try
                {
                    var list = await svc.GetNewMailSinceAsync(since);
                    _lastMailCheck[svc.Account.Id] = now;
                    all.AddRange(list);
                }
                catch { }
            }
            SyncTick?.Invoke(this, EventArgs.Empty);
            if (all.Count > 0)
            {
                if (Settings.PlaySound) { try { SystemSounds.Asterisk.Play(); } catch { } }
                NewMailArrived?.Invoke(this, all);
            }
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task CheckRemindersAsync()
    {
        if (!Settings.ShowReminders) return;
        var due = new List<ReminderInfo>();
        foreach (var svc in Accounts.ToList())
        {
            if (svc.State != ConnectionState.Connected) continue;
            try
            {
                var list = await svc.GetUpcomingRemindersAsync(DateTime.Now);
                due.AddRange(list.Where(r => !Settings.DismissedReminders.Contains(r.Key) &&
                                             !(_snoozed.TryGetValue(r.Key, out var until) && until > DateTime.Now)));
            }
            catch { }
        }
        var changed = false;
        foreach (var r in due)
        {
            if (ActiveReminders.All(x => x.Key != r.Key)) { ActiveReminders.Add(r); changed = true; }
        }
        foreach (var r in ActiveReminders.ToList())
        {
            if (due.All(x => x.Key != r.Key)) { ActiveReminders.Remove(r); changed = true; }
        }
        if (changed)
        {
            if (Settings.PlayReminderSound && ActiveReminders.Count > 0) { try { SystemSounds.Exclamation.Play(); } catch { } }
            RemindersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task DismissReminderAsync(ReminderInfo r)
    {
        Settings.DismissedReminders.Add(r.Key);
        if (Settings.DismissedReminders.Count > 2000) Settings.DismissedReminders.Remove(Settings.DismissedReminders.First());
        SettingsService.Save();
        ActiveReminders.Remove(r);
        var svc = GetAccount(r.AccountId);
        if (svc != null) { try { await svc.DismissReminderAsync(r.ItemId); } catch { } }
        RemindersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SnoozeReminder(ReminderInfo r, TimeSpan duration)
    {
        _snoozed[r.Key] = DateTime.Now + duration;
        ActiveReminders.Remove(r);
        RemindersChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshRemindersAsync() => CheckRemindersAsync();
}
