using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace Posta.App.Infrastructure;

/// <summary>
/// Runtime localization (Georgian default, English). Strings are bound via {ui:Loc Key} so the
/// UI language can be switched without restarting.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private readonly Dictionary<string, (string ka, string en)> _strings = new(StringComparer.Ordinal);
    private bool _georgian = true;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    private Loc()
    {
        Strings.RegisterMain(_strings);
        Strings.RegisterMail(_strings);
        Strings.RegisterCalendar(_strings);
        Strings.RegisterPeople(_strings);
        Strings.RegisterDialogs(_strings);
        Strings.RegisterOptions(_strings);
    }

    public bool IsGeorgian => _georgian;
    public string LanguageCode => _georgian ? "ka-GE" : "en-US";
    public CultureInfo Culture { get; private set; } = new("ka-GE");

    public string this[string key]
    {
        get
        {
            if (_strings.TryGetValue(key, out var v)) return _georgian ? v.ka : v.en;
            return key;
        }
    }

    public void SetLanguage(string code)
    {
        _georgian = !code.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        try { Culture = new CultureInfo(_georgian ? "ka-GE" : "en-US"); } catch { Culture = CultureInfo.InvariantCulture; }
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGeorgian)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string T(string key) => Instance[key];
    public static string F(string key, params object?[] args) => string.Format(Instance.Culture, Instance[key], args);
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) { Key = key; }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public string? Suffix { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        if (Suffix != null) binding.StringFormat = "{0}" + Suffix;
        return binding.ProvideValue(serviceProvider);
    }
}
