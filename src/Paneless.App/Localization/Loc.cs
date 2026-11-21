using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia.Markup.Xaml;

namespace Paneless.App.Localization;

/// <summary>Translations. The English text is the key; de.json / fr.json (embedded) map it to German and French.
/// Missing entries simply stay English. The language is chosen once at start-up (Settings › Language).</summary>
public static class Loc
{
    static Dictionary<string, string> _map = new();

    /// <summary>"en", "de" or "fr" (what is actually used).</summary>
    public static string Language { get; private set; } = "en";

    public static readonly (string Code, string Name)[] Languages = [("", "Automatic (Windows language)"), ("en", "English"), ("de", "Deutsch"), ("fr", "Français")];

    /// <summary>Picks the language: the setting, or Windows' display language when it's "" (automatic).</summary>
    public static void Init(string? setting)
    {
        var code = string.IsNullOrEmpty(setting) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : setting;
        Language = code is "de" or "fr" ? code : "en";
        _map = Language == "en" ? new() : Load(Language);
        if (Language != "en")
        {
            var culture = CultureInfo.GetCultureInfo(Language);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
    }

    static Dictionary<string, string> Load(string code)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Paneless.App.Localization.{code}.json");
            if (s == null) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        catch { return new(); }
    }

    /// <summary>The text in the chosen language.</summary>
    public static string T(string english) => _map.TryGetValue(english, out var t) && t.Length > 0 ? t : english;

    /// <summary>A translated text with {0}, {1}… filled in.</summary>
    public static string F(string english, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(english), args); }
        catch (FormatException) { return string.Format(CultureInfo.InvariantCulture, english, args); }
    }

    /// <summary>"1 device" / "3 devices", translated. {0} is the number; <paramref name="more"/> fills {1}, {2}…</summary>
    public static string Count(int n, string one, string many, params object?[] more) => F(n == 1 ? one : many, [n, .. more]);
}

/// <summary>XAML: Text="{l:T 'Mirror options'}".</summary>
public sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
