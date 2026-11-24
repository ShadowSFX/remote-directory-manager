using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using RDMan.Core.Localization;

namespace RDMan.Infrastructure;

/// <summary>Live-switchable localization source. XAML binds to <c>{l:Tr Key}</c>; changing the language updates every binding.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private string _language = "es";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string Language
    {
        get => _language;
        private set
        {
            if (_language == value) return;
            _language = value;
            var culture = new CultureInfo(value == "es" ? "es-ES" : "en-US");
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The user's Windows display language, captured before we override CurrentUICulture.</summary>
    private static readonly CultureInfo UserUiCulture = CultureInfo.CurrentUICulture;
    private static readonly CultureInfo UserRegionalCulture = CultureInfo.CurrentCulture;

    public void Apply(string preference) => Language = Strings.ResolveLanguage(preference, UserUiCulture, UserRegionalCulture);

    public string this[string key] => Strings.Get(key, _language);

    public static string T(string key) => Strings.Get(key, Instance._language);

    public static string F(string key, params object?[] args) => Strings.Format(key, Instance._language, args);
}

/// <summary><c>Text="{l:Tr Common.Save}"</c></summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
