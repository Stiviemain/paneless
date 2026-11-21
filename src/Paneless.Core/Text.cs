using System.Globalization;

namespace Paneless.Core;

/// <summary>User-facing texts from the core library go through here, so the app can translate them
/// (the app sets <see cref="Translate"/>; without it, texts stay English).</summary>
public static class Text
{
    public static Func<string, string> Translate { get; set; } = s => s;

    public static string T(string english) => Translate(english);

    public static string F(string english, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, Translate(english), args); }
        catch (FormatException) { return string.Format(CultureInfo.InvariantCulture, english, args); }
    }
}
