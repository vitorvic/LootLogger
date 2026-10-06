using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace LootLogger.App.Localization;

/// <summary>Holds the interface texts and switches language while the program runs.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static readonly IReadOnlyList<string> Languages = ["pt-BR", "en"];

    private Dictionary<string, string> _current = Strings.Portuguese;

    public string Language { get; private set; } = "pt-BR";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => _current.TryGetValue(key, out var text) ? text
        : Strings.Portuguese.TryGetValue(key, out var fallback) ? fallback
        : key;

    public void SetLanguage(string language)
    {
        Language = language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "pt-BR";
        _current = Language == "en" ? Strings.English : Strings.Portuguese;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    public string Format(string key, params object[] args) => string.Format(this[key], args);
}

/// <summary>XAML shortcut: Text="{l:T Dashboard}" stays in sync with the chosen language.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
