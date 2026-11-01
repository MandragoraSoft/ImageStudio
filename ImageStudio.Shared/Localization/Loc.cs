using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ImageStudio.Localization;

// Teksty interfejsu w dwóch językach (polski, angielski). Słowniki to płaskie pliki JSON klucz -> tekst, wbudowane w tę bibliotekę
// (Strings.pl.json, Strings.en.json). Polski jest językiem źródłowym: brak klucza w angielskim wraca do polskiego, brak w obu
// pokazuje sam klucz (widać, czego brakuje). Tekst z parametrami to format string.Format: {0}, {1:0.####}.
//   XAML:  {loc:T menu.file}   (xmlns:loc="clr-namespace:ImageStudio.Localization;assembly=ImageStudio.Shared"), odświeża się po zmianie języka
//   Kod:   Loc.T("voice.status.saved", path)
// Bez wywołania Loc.Set aktywny jest polski (testy i projekty bez ustawień dostają przewidywalny język).
public static class Loc
{
    public const string System = "system";
    public const string Polish = "pl";
    public const string English = "en";

    private static readonly Dictionary<string, string> Pl = Load(Polish);
    private static readonly Dictionary<string, string> En = Load(English);
    private static Dictionary<string, string> _current = Pl;

    public static string Language { get; private set; } = Polish;

    // Zmiana języka w trakcie działania: okna z {loc:T} odświeżają się same, kod z Loc.T() musi sam nasłuchiwać
    public static event Action? LanguageChanged;

    // Ustawienie "system" daje polski w systemie polskim, a w każdym innym angielski
    public static string Resolve(string? setting) => setting switch
    {
        Polish => Polish,
        English => English,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Polish ? Polish : English,
    };

    public static void Set(string? setting)
    {
        var language = Resolve(setting);
        if (language == Language) return;

        Language = language;
        _current = language == English ? En : Pl;
        LocalizationSource.Instance.Refresh();
        LanguageChanged?.Invoke();
    }

    public static string T(string key) =>
        _current.TryGetValue(key, out var text) ? text : Pl.TryGetValue(key, out var fallback) ? fallback : key;

    public static string T(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    // Do testów i narzędzi: wszystkie klucze danego języka
    public static IReadOnlyDictionary<string, string> Dictionary(string language) => language == English ? En : Pl;

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"ImageStudio.Localization.Strings.{language}.json")
                           ?? throw new InvalidOperationException($"Brak słownika tekstów: {language}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
