using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageStudio.Services;

// Narzędzia sterujące aplikacją (modele, prompty, rozmiar, generowanie) dla modelu w czacie i dla klientów MCP.
// Nazwy surowe (np. "generate"); w czacie dostają przedrostek ChatPrefix, żeby nie zderzały się z narzędziami MCP.
// Kosztowne narzędzia (generowanie) wymagają zgody użytkownika w aplikacji, o ile nie dał jej już czat.
public sealed class AppControlTools
{
    public const string ChatPrefix = "imagestudio__";

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] Kinds = ["generate", "edit", "video"];
    private static readonly string[] Tabs = ["generate", "edit", "video", "chat"];

    private sealed record Tool(string Name, string Description, string Schema, bool Costly, Func<JsonElement, Task<string>> Run);

    private readonly IAppControl _app;
    private readonly Func<ToolCall, bool> _confirmCostly;
    private readonly Dictionary<string, Tool> _tools;

    public AppControlTools(IAppControl app, Func<ToolCall, bool> confirmCostly)
    {
        _app = app;
        _confirmCostly = confirmCostly;
        _tools = Build().ToDictionary(t => t.Name);
    }

    public IReadOnlyList<ToolDefinition> Definitions =>
        _tools.Values.Select(t => new ToolDefinition(t.Name, t.Description, t.Schema)).ToList();

    public bool IsCostly(string name) => _tools.TryGetValue(name, out var tool) && tool.Costly;

    // alreadyApproved: użytkownik zatwierdził to wywołanie wcześniej (zgoda w czacie), więc nie pytamy drugi raz
    public async Task<string> CallAsync(string name, string argumentsJson, bool alreadyApproved)
    {
        if (!_tools.TryGetValue(name, out var tool)) throw new ArgumentException($"Nieznane narzędzie: {name}");

        if (tool.Costly && !alreadyApproved && !_confirmCostly(new ToolCall("", name, argumentsJson)))
            return "Użytkownik odrzucił uruchomienie (generowanie kosztuje).";

        using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        return await tool.Run(arguments.RootElement.Clone());
    }

    private IEnumerable<Tool> Build()
    {
        yield return new Tool("get_state",
            "Zwraca stan aplikacji ImageStudio: aktywną zakładkę, wybrane modele, prompty, rozmiar, liczbę referencji i status.",
            Schema(), false, _ => Task.FromResult(Serialize(_app.GetState())));

        yield return new Tool("list_models",
            "Lista modeli aktywnego dostawcy dla danego rodzaju (preferowane na początku) z ceną i informacją o wejściu obrazowym.",
            Schema(("kind", KindProp("Rodzaj modeli"), true),
                   ("query", """{"type":"string","description":"Fragment nazwy modelu"}""", false),
                   ("limit", """{"type":"integer","minimum":1,"maximum":100,"description":"Maksymalna liczba wyników (domyślnie 30)"}""", false)),
            false, args => Task.FromResult(Serialize(_app.ListModels(Kind(args), OptString(args, "query"),
                                                                     Math.Clamp(OptInt(args, "limit") ?? 30, 1, 100)))));

        yield return new Tool("select_model", "Wybiera model dla danego rodzaju (id z list_models).",
            Schema(("kind", KindProp("Rodzaj modelu"), true), ("id", """{"type":"string","description":"Id modelu"}""", true)),
            false, args =>
            {
                _app.SelectModel(Kind(args), RequiredString(args, "id"));
                return Task.FromResult("Wybrano model.");
            });

        yield return new Tool("set_prompt",
            "Ustawia prompt (i opcjonalnie część systemową) dla danego rodzaju. Dla wideo system_prompt jest ignorowany.",
            Schema(("kind", KindProp("Rodzaj"), true),
                   ("prompt", """{"type":"string","description":"Treść promptu (koncept)"}""", true),
                   ("system_prompt", """{"type":"string","description":"Część konfiguracyjna: styl, technika"}""", false)),
            false, args =>
            {
                _app.SetPrompt(Kind(args), RequiredString(args, "prompt"), OptString(args, "system_prompt"));
                return Task.FromResult("Ustawiono prompt.");
            });

        yield return new Tool("set_size", "Ustawia rozmiar wyniku generowania i edycji w pikselach.",
            Schema(("width", """{"type":"integer","minimum":64,"maximum":4096}""", true),
                   ("height", """{"type":"integer","minimum":64,"maximum":4096}""", true)),
            false, args =>
            {
                var width = RequiredInt(args, "width");
                var height = RequiredInt(args, "height");
                if (width is < 64 or > 4096 || height is < 64 or > 4096)
                    throw new ArgumentException("Rozmiar musi mieścić się w zakresie 64-4096 px.");
                _app.SetSize(width, height);
                return Task.FromResult($"Ustawiono rozmiar {width}x{height}.");
            });

        yield return new Tool("set_video_options", "Ustawia czas trwania (sekundy) i proporcje wideo; pominięte pola zostają bez zmian.",
            Schema(("duration", """{"type":"integer","minimum":1,"maximum":60}""", false),
                   ("aspect_ratio", """{"type":"string","description":"Np. 16:9, 9:16, 1:1"}""", false)),
            false, args =>
            {
                _app.SetVideoOptions(OptInt(args, "duration"), OptString(args, "aspect_ratio"));
                return Task.FromResult("Ustawiono opcje wideo.");
            });

        yield return new Tool("switch_tab",
            "Przełącza zakładkę aplikacji. Wynik generowania widać w zakładce odpowiedniej dla rodzaju (generate, edit, video).",
            Schema(("tab", $$"""{"type":"string","enum":["generate","edit","video","chat"]}""", true)),
            false, args =>
            {
                var tab = RequiredString(args, "tab");
                if (!Tabs.Contains(tab)) throw new ArgumentException($"Nieznana zakładka: {tab}. Dozwolone: {string.Join(", ", Tabs)}.");
                _app.SwitchTab(tab);
                return Task.FromResult($"Zakładka: {tab}.");
            });

        yield return new Tool("add_reference",
            "Dodaje plik obrazu (pełna ścieżka na tym komputerze) jako referencję do edycji.",
            Schema(("path", """{"type":"string","description":"Ścieżka do pliku png, jpg, jpeg lub webp"}""", true)),
            false, args =>
            {
                var path = RequiredString(args, "path");
                if (!File.Exists(path)) throw new ArgumentException($"Plik nie istnieje: {path}");
                _app.AddReference(path);
                return Task.FromResult("Dodano referencję.");
            });

        yield return new Tool("clear_references", "Usuwa wszystkie referencje edycji.", Schema(), false, _ =>
        {
            _app.ClearReferences();
            return Task.FromResult("Wyczyszczono referencje.");
        });

        yield return new Tool("generate",
            "Uruchamia generowanie (generate), edycję z referencjami (edit) albo wideo (video) z bieżącymi ustawieniami. "
            + "KOSZTUJE pieniądze i wymaga zgody użytkownika. Zwraca ścieżkę zapisanego pliku.",
            Schema(("kind", KindProp("Co uruchomić"), true)),
            true, args => _app.GenerateAsync(Kind(args)));

        yield return new Tool("list_gallery", "Ostatnie wyniki z galerii (najnowsze pierwsze): rodzaj, model, prompt, plik, koszt.",
            Schema(("limit", """{"type":"integer","minimum":1,"maximum":50,"description":"Domyślnie 10"}""", false)),
            false, args => Task.FromResult(Serialize(_app.ListGallery(Math.Clamp(OptInt(args, "limit") ?? 10, 1, 50)))));
    }

    private static string KindProp(string description) =>
        $$"""{"type":"string","enum":["generate","edit","video"],"description":"{{description}}: generate, edit lub video"}""";

    // Schemat obiektu z listą (nazwa, schemat właściwości, wymagana)
    private static string Schema(params (string Name, string Property, bool Required)[] properties)
    {
        var props = string.Join(",", properties.Select(p => $"\"{p.Name}\":{p.Property}"));
        var required = string.Join(",", properties.Where(p => p.Required).Select(p => $"\"{p.Name}\""));
        return $$"""{"type":"object","properties":{{{props}}},"required":[{{required}}]}""";
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static string Kind(JsonElement args)
    {
        var kind = RequiredString(args, "kind");
        if (!Kinds.Contains(kind)) throw new ArgumentException($"Nieznany rodzaj: {kind}. Dozwolone: {string.Join(", ", Kinds)}.");
        return kind;
    }

    private static string RequiredString(JsonElement args, string name) =>
        OptString(args, name) is { Length: > 0 } value ? value : throw new ArgumentException($"Brak parametru \"{name}\".");

    private static string? OptString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? OptInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static int RequiredInt(JsonElement args, string name) =>
        OptInt(args, name) ?? throw new ArgumentException($"Brak parametru \"{name}\" (liczba całkowita).");
}
