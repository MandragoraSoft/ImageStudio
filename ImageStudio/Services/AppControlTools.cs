using System.IO;
using System.Text.Json;
using static ImageStudio.Services.McpToolArgs;

namespace ImageStudio.Services;

// Narzędzia sterujące aplikacją (modele, prompty, rozmiar, generowanie) dla modelu w czacie i dla klientów MCP.
// Nazwy surowe (np. "generate"); w czacie dostają przedrostek ChatPrefix, żeby nie zderzały się z narzędziami MCP.
// Kosztowne narzędzia (generowanie) wymagają zgody użytkownika w aplikacji, o ile nie dał jej już czat.
public sealed class AppControlTools : IMcpToolSet
{
    public const string ChatPrefix = "imagestudio__";

    private const string KindEnum = """["generate","edit","video"]""";
    private static readonly string[] Kinds = ["generate", "edit", "video"];
    private static readonly string[] Tabs = ["generate", "edit", "video", "chat", "voice"];

    private sealed record Tool(string Name, string Description, string Schema, bool Costly, Func<JsonElement, Task<string>> Run, string[]? Needs = null);

    private readonly IAppControl _app;
    private readonly Func<ToolCall, bool> _confirmCostly;
    private readonly Dictionary<string, Tool> _tools;

    private readonly McpLimits _limits;

    public AppControlTools(IAppControl app, Func<ToolCall, bool> confirmCostly, McpLimits? limits = null)
    {
        _app = app;
        _limits = limits ?? new McpLimits();
        _confirmCostly = confirmCostly;
        _tools = Build().ToDictionary(t => t.Name);
    }

    // Narzędzia widoczne dla modelu: te, których rodzaj ma modele u aktywnego dostawcy (np. OpenCode Go nie ma żadnych graficznych);
    // enum "kind" zawęża się do dostępnych rodzajów. Liczone przy każdym wywołaniu, bo dostawca i katalog modeli się zmieniają.
    public IReadOnlyList<ToolDefinition> Definitions
    {
        get
        {
            var kinds = _app.AvailableKinds();
            var enumText = $"[{string.Join(",", kinds.Select(k => $"\"{k}\""))}]";
            return _tools.Values.Where(t => IsAvailable(t, kinds))
                .Select(t => new ToolDefinition(t.Name, t.Description, t.Schema.Replace(KindEnum, enumText))).ToList();
        }
    }

    // Wszystkie narzędzia niezależnie od dostawcy (lista w ustawieniach czatu)
    public IReadOnlyList<ToolDefinition> AllDefinitions =>
        _tools.Values.Select(t => new ToolDefinition(t.Name, t.Description, t.Schema)).ToList();

    private static bool IsAvailable(Tool tool, IReadOnlyList<string> kinds) => tool.Needs is null || tool.Needs.Any(kinds.Contains);

    public bool IsCostly(string name) => _tools.TryGetValue(name, out var tool) && tool.Costly;

    // alreadyApproved: użytkownik zatwierdził to wywołanie wcześniej (zgoda w czacie), więc nie pytamy drugi raz
    public async Task<string> CallAsync(string name, string argumentsJson, bool alreadyApproved)
    {
        if (!_tools.TryGetValue(name, out var tool)) throw new ArgumentException($"Nieznane narzędzie: {name}");
        if (!IsAvailable(tool, _app.AvailableKinds()))
            throw new ArgumentException($"Narzędzie {name} jest niedostępne: aktywny dostawca nie ma modeli tego rodzaju.");

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
                   ("limit", """{"type":"integer","minimum":1,"description":"Maksymalna liczba wyników (domyślnie i maksimum z Ustawień, zakładka MCP)"}""", false)),
            false, args =>
            {
                var limit = _limits.ListSize(OptInt(args, "limit"));
                return Task.FromResult(Paged(_app.ListModels(Kind(args), OptString(args, "query"), limit + 1), limit, _limits.MaxListLimit));
            }, Needs: Kinds);

        yield return new Tool("select_model", "Wybiera model dla danego rodzaju (id z list_models).",
            Schema(("kind", KindProp("Rodzaj modelu"), true), ("id", """{"type":"string","description":"Id modelu"}""", true)),
            false, args =>
            {
                _app.SelectModel(Kind(args), RequiredString(args, "id"));
                return Task.FromResult("Wybrano model.");
            }, Needs: Kinds);

        yield return new Tool("set_prompt",
            "Ustawia prompt (i opcjonalnie część systemową) dla danego rodzaju. Dla wideo system_prompt jest ignorowany.",
            Schema(("kind", KindProp("Rodzaj"), true),
                   ("prompt", """{"type":"string","description":"Treść promptu (koncept)"}""", true),
                   ("system_prompt", """{"type":"string","description":"Część konfiguracyjna: styl, technika"}""", false)),
            false, args =>
            {
                _app.SetPrompt(Kind(args), RequiredString(args, "prompt"), OptString(args, "system_prompt"));
                return Task.FromResult("Ustawiono prompt.");
            }, Needs: Kinds);

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
            }, Needs: ["generate", "edit"]);

        yield return new Tool("set_video_options", "Ustawia czas trwania (sekundy) i proporcje wideo; pominięte pola zostają bez zmian.",
            Schema(("duration", """{"type":"integer","minimum":1,"maximum":60}""", false),
                   ("aspect_ratio", """{"type":"string","description":"Np. 16:9, 9:16, 1:1"}""", false)),
            false, args =>
            {
                _app.SetVideoOptions(OptInt(args, "duration"), OptString(args, "aspect_ratio"));
                return Task.FromResult("Ustawiono opcje wideo.");
            }, Needs: ["video"]);

        yield return new Tool("switch_tab",
            "Przełącza zakładkę aplikacji. Wynik generowania widać w zakładce odpowiedniej dla rodzaju (generate, edit, video).",
            Schema(("tab", $$"""{"type":"string","enum":["generate","edit","video","chat","voice"]}""", true)),
            false, args =>
            {
                var tab = RequiredString(args, "tab");
                if (!Tabs.Contains(tab)) throw new ArgumentException($"Nieznana zakładka: {tab}. Dozwolone: {string.Join(", ", Tabs)}.");
                _app.SwitchTab(tab);
                return Task.FromResult($"Zakładka: {tab}.");
            });

        yield return new Tool("add_reference",
            "Dodaje obraz jako referencję do edycji: plik (pełna ścieżka na tym komputerze) albo adres http(s) do pobrania.",
            Schema(("path", """{"type":"string","description":"Ścieżka do pliku png, jpg, jpeg lub webp"}""", false),
                   ("url", """{"type":"string","description":"Adres http(s) obrazu"}""", false)),
            false, async args =>
            {
                if (OptString(args, "url") is { Length: > 0 } url)
                {
                    await _app.AddReferenceFromUrlAsync(url);
                    return "Dodano referencję z adresu.";
                }

                var path = RequiredString(args, "path");
                if (!File.Exists(path)) throw new ArgumentException($"Plik nie istnieje: {path}");
                _app.AddReference(path);
                return "Dodano referencję.";
            }, Needs: ["generate", "edit"]);

        yield return new Tool("clear_references", "Usuwa wszystkie referencje edycji.", Schema(), false, _ =>
        {
            _app.ClearReferences();
            return Task.FromResult("Wyczyszczono referencje.");
        }, Needs: ["generate", "edit"]);

        yield return new Tool("generate",
            "Uruchamia generowanie (generate), edycję z referencjami (edit) albo wideo (video) z bieżącymi ustawieniami. "
            + "KOSZTUJE pieniądze i wymaga zgody użytkownika. Zwraca ścieżkę zapisanego pliku.",
            Schema(("kind", KindProp("Co uruchomić"), true)),
            true, args => _app.GenerateAsync(Kind(args)), Needs: Kinds);

        yield return new Tool("remove_reference", "Usuwa jedną referencję edycji (indeks od 0, kolejność jak w get_state).",
            Schema(("index", """{"type":"integer","minimum":0}""", true)),
            false, args =>
            {
                _app.RemoveReference(RequiredInt(args, "index"));
                return Task.FromResult("Usunięto referencję.");
            }, Needs: ["generate", "edit"]);

        yield return new Tool("add_video_attachment",
            "Dodaje plik obrazu (pełna ścieżka) jako załącznik promptu wideo (obraz wejściowy).",
            Schema(("path", """{"type":"string","description":"Ścieżka do pliku png, jpg, jpeg lub webp"}""", true)),
            false, args =>
            {
                var path = RequiredString(args, "path");
                if (!File.Exists(path)) throw new ArgumentException($"Plik nie istnieje: {path}");
                _app.AddVideoAttachment(path);
                return Task.FromResult("Dodano załącznik wideo.");
            }, Needs: ["video"]);

        yield return new Tool("clear_video_attachments", "Usuwa wszystkie załączniki promptu wideo.", Schema(), false, _ =>
        {
            _app.ClearVideoAttachments();
            return Task.FromResult("Wyczyszczono załączniki wideo.");
        }, Needs: ["video"]);

        yield return new Tool("set_aspect",
            "Ustawia proporcje obrazu (zachowując pole): 1:1, 4:3, 3:2, 16:9, 9:16 albo \"source\" (jak pierwsza referencja).",
            Schema(("ratio", """{"type":"string","description":"Np. 16:9 albo source"}""", true)),
            false, args =>
            {
                var ratio = RequiredString(args, "ratio");
                _app.SetAspect(ratio);
                return Task.FromResult($"Proporcje: {ratio}.");
            }, Needs: ["generate", "edit"]);

        yield return new Tool("reload_models", "Ponownie pobiera listy modeli i ceny aktywnego dostawcy.", Schema(), false, async _ =>
        {
            await _app.ReloadModelsAsync();
            return "Przeładowano modele.";
        });
    }

    private static string KindProp(string description) =>
        $$"""{"type":"string","enum":["generate","edit","video"],"description":"{{description}}: generate, edit lub video"}""";

    private string Kind(JsonElement args)
    {
        var kind = RequiredString(args, "kind");
        if (!Kinds.Contains(kind)) throw new ArgumentException($"Nieznany rodzaj: {kind}. Dozwolone: {string.Join(", ", Kinds)}.");
        var available = _app.AvailableKinds();
        if (!available.Contains(kind))
            throw new ArgumentException($"Aktywny dostawca nie ma modeli rodzaju {kind}. Dostępne: {(available.Count == 0 ? "brak" : string.Join(", ", available))}.");
        return kind;
    }
}
