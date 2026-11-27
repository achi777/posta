using System.Text.Json;
using System.Text.Json.Serialization;
using Posta.Core.Models;

namespace Posta.Core.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Posta");
    public static string LocalFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Posta");
    public static string SignaturesFolder => Path.Combine(DataFolder, "Signatures");

    private string SettingsPath => Path.Combine(DataFolder, "settings.json");
    private string AccountsPath => Path.Combine(DataFolder, "accounts.json");

    public AppSettings Settings { get; private set; } = new();
    public List<AccountConfig> Accounts { get; private set; } = new();

    public event EventHandler? SettingsChanged;

    public SettingsService()
    {
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(LocalFolder);
        Directory.CreateDirectory(SignaturesFolder);
    }

    public void Load()
    {
        Settings = Read<AppSettings>(SettingsPath) ?? new AppSettings();
        Accounts = Read<List<AccountConfig>>(AccountsPath) ?? new List<AccountConfig>();
        if (Settings.Categories.Count == 0)
        {
            Settings.Categories = DefaultCategories();
        }
        LoadSignatures();
    }

    public static List<CategoryDefinition> DefaultCategories() => new()
    {
        new() { Name = "Red Category", Color = "#E7A1A2" },
        new() { Name = "Orange Category", Color = "#F9BA89" },
        new() { Name = "Yellow Category", Color = "#F7DD8F" },
        new() { Name = "Green Category", Color = "#78D168" },
        new() { Name = "Blue Category", Color = "#9FC6EF" },
        new() { Name = "Purple Category", Color = "#B9A6E7" },
    };

    public void Save()
    {
        Write(SettingsPath, Settings);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveAccounts() => Write(AccountsPath, Accounts);

    /// <summary>Signatures are stored as individual .htm files, the same way the classic desktop client does.</summary>
    private void LoadSignatures()
    {
        var list = new List<Signature>();
        foreach (var file in Directory.EnumerateFiles(SignaturesFolder, "*.htm").OrderBy(f => f))
        {
            try { list.Add(new Signature { Name = Path.GetFileNameWithoutExtension(file), Html = File.ReadAllText(file) }); }
            catch { /* ignore broken file */ }
        }
        Settings.Signatures = list;
    }

    public void SaveSignatures(IEnumerable<Signature> signatures)
    {
        var sigs = signatures.ToList();
        var names = new HashSet<string>(sigs.Select(s => SafeFileName(s.Name)), StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(SignaturesFolder, "*.htm"))
        {
            if (!names.Contains(Path.GetFileNameWithoutExtension(file))) File.Delete(file);
        }
        foreach (var s in sigs)
        {
            File.WriteAllText(Path.Combine(SignaturesFolder, SafeFileName(s.Name) + ".htm"), s.Html);
        }
        Settings.Signatures = sigs;
        Save();
    }

    public AccountSignatureDefaults GetSignatureDefaults(string accountId)
    {
        var d = Settings.SignatureDefaults.FirstOrDefault(x => x.AccountId == accountId);
        if (d == null)
        {
            d = new AccountSignatureDefaults { AccountId = accountId };
            Settings.SignatureDefaults.Add(d);
        }
        return d;
    }

    public void AddRecentRecipient(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        Settings.RecentRecipients.RemoveAll(r => string.Equals(r, address, StringComparison.OrdinalIgnoreCase));
        Settings.RecentRecipients.Insert(0, address);
        if (Settings.RecentRecipients.Count > 200) Settings.RecentRecipients.RemoveRange(200, Settings.RecentRecipients.Count - 200);
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            try { File.Copy(path, path + ".bak", true); } catch { }
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tmp, path, true);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);
}
