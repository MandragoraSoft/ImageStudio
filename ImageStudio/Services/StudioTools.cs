using System.Text.Json;
using ImageStudio.Configuration;
using static ImageStudio.Services.McpToolArgs;

namespace ImageStudio.Services;

// Narzędzia MCP nad StudioApi: jawne parametry, wynik jako plik i wpis w bibliotece, bez zakładek i podglądu.
// Nazwy surowe; w czacie mają ten sam przedrostek co AppControlTools. Kosztowne i nieodwracalne wymagają zgody użytkownika.
public sealed class StudioTools : IMcpToolSet
{
    private sealed record Tool(string Name, string Description, string Schema, bool NeedsApproval, Func<JsonElement, Task<string>> Run,
                               string[]? Needs = null);

    // Wynik operacji na pliku, czytelny dla modelu
    private sealed record Created(string Id, string Kind, string Model, string Path, string Cost, string? Text = null,
                                 IReadOnlyList<string>? Warnings = null);

    private readonly IStudioApi _studio;
    private readonly Func<ToolCall, bool> _confirm;
    private readonly McpLimits _limits;
    private readonly Dictionary<string, Tool> _tools;

    public StudioTools(IStudioApi studio, Func<ToolCall, bool> confirm, McpLimits? limits = null)
    {
        _studio = studio;
        _limits = limits ?? new McpLimits();
        _confirm = confirm;
        _tools = Build().ToDictionary(t => t.Name);
    }

    public IReadOnlyList<ToolDefinition> Definitions
    {
        get
        {
            var kinds = _studio.AvailableKinds();
            return _tools.Values.Where(t => t.Needs is null || t.Needs.Any(kinds.Contains)).Select(ToDefinition).ToList();
        }
    }

    public IReadOnlyList<ToolDefinition> AllDefinitions => _tools.Values.Select(ToDefinition).ToList();

    public bool IsCostly(string name) => _tools.TryGetValue(name, out var tool) && tool.NeedsApproval;

    public async Task<string> CallAsync(string name, string argumentsJson, bool alreadyApproved)
    {
        if (!_tools.TryGetValue(name, out var tool)) throw new ArgumentException($"Nieznane narzędzie: {name}");
        if (tool.Needs is not null && !tool.Needs.Any(_studio.AvailableKinds().Contains))
            throw new ArgumentException($"Narzędzie {name} jest niedostępne: aktywny dostawca nie ma modeli tego rodzaju.");

        if (tool.NeedsApproval && !alreadyApproved && !_confirm(new ToolCall("", name, argumentsJson)))
            return "Użytkownik odrzucił wykonanie (operacja kosztuje albo jest nieodwracalna).";

        using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        return await tool.Run(arguments.RootElement.Clone());
    }

    private static ToolDefinition ToDefinition(Tool t) => new(t.Name, t.Description, t.Schema);

    private static Created Describe(StudioResult r) => Describe(r.Entry) with { Warnings = r.Warnings };

    private static Created Describe(GalleryEntry e) =>
        new(e.Id, e.Kind, e.Model, e.FilePath, e.CostLabel, e.Kind == "generate" || e.Kind == "edit" || e.Kind == "video" ? null : e.Prompt);

    // Rozmiar wyniku w pikselach; pominięte pola to 1024
    private static (int Width, int Height) Dimensions(int? width, int? height)
    {
        var (w, h) = (width ?? 1024, height ?? 1024);
        if (w is < StudioDefaults.MinPixels or > StudioDefaults.MaxPixels || h is < StudioDefaults.MinPixels or > StudioDefaults.MaxPixels)
            throw new ArgumentException($"Rozmiar musi mieścić się w zakresie {StudioDefaults.MinPixels}-{StudioDefaults.MaxPixels} px.");
        return (w, h);
    }

    // Parametry obrazu wspólne dla generowania i zestawu; zestaw (preset) uzupełnia tylko pominięte
    private static ImageOptions Options(JsonElement args, Preset? preset) => new(
        OptInt(args, "seed") ?? (int.TryParse(preset?.SeedText, out var seed) ? seed : null),
        OptString(args, "negative_prompt") ?? preset?.NegativePrompt,
        OptString(args, "quality") is { } quality ? (quality == "auto" ? null : quality) : preset?.Quality is "auto" ? null : preset?.Quality);

    private Preset? PresetOf(JsonElement args, string kind) =>
        OptString(args, "preset") is { Length: > 0 } name
            ? _studio.FindPreset(name) is { } p && p.Kind == kind ? p : throw new ArgumentException($"Zestaw \"{name}\" dotyczy innego rodzaju niż {kind}.")
            : null;

    private IEnumerable<Tool> Build()
    {
        // Pola wspólne dla generate_image i edit_image
        (string, string, bool)[] common =
        [
            ("system_prompt", """{"type":"string","description":"Część konfiguracyjna: styl, technika (dołączana przed promptem)"}""", false),
            ("model", """{"type":"string","description":"Id modelu (list_models); pominięty = domyślny wybrany w aplikacji"}""", false),
            ("preset", """{"type":"string","description":"Nazwa zestawu (list_presets): uzupełnia pominięte pola"}""", false),
        ];

        yield return new Tool("get_defaults",
            "Zwraca aktywnego dostawcę i modele wybrane domyślnie dla każdego rodzaju (generate, edit, video, speech, transcription, chat): "
            + "tych użyje narzędzie wywołane bez parametru model. To te same ustawienia, które zapisuje okno aplikacji.",
            Schema(), false, _ => Task.FromResult(Serialize(_studio.GetDefaults())));

        yield return new Tool("set_default_model",
            "Ustawia domyślny model rodzaju (zapisywany w ustawieniach aplikacji; okno pokaże go po odświeżeniu lub przy starcie).",
            Schema(("kind", """{"type":"string","enum":["generate","edit","video","speech","transcription","chat"]}""", true),
                   ("model", """{"type":"string","description":"Id modelu z list_models albo list_audio_models"}""", true)),
            false, args =>
            {
                _studio.SetDefaultModel(RequiredString(args, "kind"), RequiredString(args, "model"));
                return Task.FromResult("Zapisano domyślny model.");
            });

        yield return new Tool("list_models",
            "Lista modeli aktywnego dostawcy dla rodzaju generate, edit albo video (preferowane na początku) z ceną i informacją o wejściu obrazowym.",
            Schema(("kind", """{"type":"string","enum":["generate","edit","video"],"description":"Rodzaj modeli"}""", true),
                   ("query", """{"type":"string","description":"Fragment nazwy modelu"}""", false),
                   ("limit", """{"type":"integer","minimum":1,"description":"Domyślnie i maksimum z Ustawień (zakładka MCP)"}""", false)),
            false, async args =>
            {
                var limit = _limits.ListSize(OptInt(args, "limit"));
                return Paged(await _studio.ModelsAsync(RequiredString(args, "kind"), OptString(args, "query"), limit + 1), limit, _limits.MaxListLimit);
            }, Needs: ["generate", "edit", "video"]);

        yield return new Tool("list_audio_models",
            "Lista modeli mowy (speech) albo rozpoznawania mowy (transcription) aktywnego dostawcy z ceną.",
            Schema(("kind", """{"type":"string","enum":["speech","transcription"]}""", true),
                   ("query", """{"type":"string","description":"Fragment nazwy modelu"}""", false),
                   ("limit", """{"type":"integer","minimum":1,"description":"Domyślnie i maksimum z Ustawień (zakładka MCP)"}""", false)),
            false, async args =>
            {
                var kind = RequiredString(args, "kind");
                if (kind is not ("speech" or "transcription")) throw new ArgumentException("Rodzaj: speech albo transcription.");
                var limit = _limits.ListSize(OptInt(args, "limit"));
                return Paged(await _studio.AudioModelsAsync(kind, OptString(args, "query"), limit + 1), limit, _limits.MaxListLimit);
            }, Needs: ["speech", "transcription"]);

        yield return new Tool("list_voices", "Lista głosów modelu mowy (pusta = model ma tylko głos domyślny).",
            Schema(("model", """{"type":"string","description":"Id modelu mowy"}""", true)),
            false, async args => Serialize(await _studio.VoicesAsync(RequiredString(args, "model"))), Needs: ["speech"]);

        yield return new Tool("speak",
            "Czyta tekst na głos i zapisuje plik dźwiękowy (nie odtwarza go). KOSZTUJE pieniądze i wymaga zgody. Zwraca ścieżkę pliku.",
            Schema(("text", """{"type":"string","description":"Tekst do odczytania"}""", true),
                   ("model", """{"type":"string","description":"Id modelu mowy (list_audio_models); pominięty = domyślny"}""", false),
                   ("voice", """{"type":"string","description":"Głos (list_voices)"}""", false),
                   ("speed", """{"type":"number","minimum":0.25,"maximum":4,"description":"Tempo, 1 = normalne"}""", false),
                   ("format", """{"type":"string","enum":["mp3","wav"]}""", false),
                   ("language", $$"""{"type":"string","description":"Kod języka, na który najpierw przetłumaczyć tekst: {{string.Join(", ", StudioDefaults.SpeechLanguages.Keys)}}"}""", false)),
            true, async args => Serialize(Describe(await _studio.SpeakAsync(new SpeechRequest(
                RequiredString(args, "text"), OptString(args, "model"), OptString(args, "voice"), OptDouble(args, "speed"),
                OptString(args, "format") ?? "mp3", OptString(args, "language"))))),
            Needs: ["speech"]);

        yield return new Tool("transcribe",
            "Zamienia plik dźwiękowy (mp3, wav, ogg, flac, m4a, webm; do 40 MB) na tekst. KOSZTUJE pieniądze i wymaga zgody. Zwraca tekst.",
            Schema(("path", """{"type":"string","description":"Pełna ścieżka pliku dźwiękowego"}""", true),
                   ("model", """{"type":"string","description":"Id modelu rozpoznawania (list_audio_models); pominięty = domyślny"}""", false),
                   ("language", """{"type":"string","description":"Kod języka nagrania, np. pl"}""", false),
                   ("hint", """{"type":"string","description":"Podpowiedź: nazwy własne i słowa, których się spodziewamy"}""", false)),
            true, async args => Serialize(Describe(await _studio.TranscribeAsync(new TranscriptionRequest(
                RequiredString(args, "path"), OptString(args, "model"), OptString(args, "language"), OptString(args, "hint"))))),
            Needs: ["transcription"]);

        yield return new Tool("generate_image",
            "Generuje obraz z podanych parametrów (bez zmiany stanu okna) i zapisuje go w galerii. KOSZTUJE pieniądze i wymaga zgody. Zwraca ścieżkę pliku.",
            Schema([("prompt", """{"type":"string","description":"Koncept: co ma powstać (pominięty = z zestawu)"}""", false),
                   ("width", """{"type":"integer","minimum":64,"maximum":4096}""", false),
                   ("height", """{"type":"integer","minimum":64,"maximum":4096}""", false),
                   ("seed", """{"type":"integer"}""", false),
                   ("negative_prompt", """{"type":"string","description":"Tylko natywny parametr modelu, który go potwierdza w schemacie; inaczej ostrzeżenie w wyniku i nic nie jest wysyłane"}""", false),
                   ("quality", """{"type":"string","enum":["auto","low","medium","high"]}""", false),
                   .. common]),
            true, async args =>
            {
                var preset = PresetOf(args, "generate");
                var prompt = OptString(args, "prompt") ?? preset?.Prompt ?? throw new ArgumentException("Brak parametru \"prompt\".");
                var (width, height) = Dimensions(OptInt(args, "width") ?? preset?.Width, OptInt(args, "height") ?? preset?.Height);
                return Serialize(Describe(await _studio.GenerateImageAsync(new ImageRequest(
                    prompt, OptString(args, "system_prompt") ?? preset?.SystemPrompt, OptString(args, "model") ?? preset?.Model,
                    width, height, Options(args, preset)))));
            }, Needs: ["generate"]);

        yield return new Tool("edit_image",
            "Edytuje obrazy referencyjne promptem (bez zmiany stanu okna) i zapisuje wynik w galerii. KOSZTUJE pieniądze i wymaga zgody. Zwraca ścieżkę pliku.",
            Schema([("prompt", """{"type":"string","description":"Co zmienić (pominięty = z zestawu)"}""", false),
                   ("references", """{"type":"array","items":{"type":"string"},"description":"Pełne ścieżki plików albo adresy http(s); pierwszy jest podstawowy"}""", true),
                   ("width", """{"type":"integer","minimum":64,"maximum":4096,"description":"Z height: dopasowuje wynik do tych proporcji"}""", false),
                   ("height", """{"type":"integer","minimum":64,"maximum":4096}""", false),
                   ("negative_prompt", """{"type":"string","description":"Tylko natywny parametr modelu, który go potwierdza w schemacie; inaczej ostrzeżenie w wyniku i nic nie jest wysyłane"}""", false),
                   .. common]),
            true, async args =>
            {
                var preset = PresetOf(args, "edit");
                var prompt = OptString(args, "prompt") ?? preset?.Prompt ?? throw new ArgumentException("Brak parametru \"prompt\".");
                var fit = OptInt(args, "width") is not null || OptInt(args, "height") is not null
                    ? Dimensions(OptInt(args, "width"), OptInt(args, "height"))
                    : ((int, int)?)null;
                return Serialize(Describe(await _studio.EditImageAsync(new EditRequest(
                    prompt, OptString(args, "system_prompt") ?? preset?.SystemPrompt, OptStrings(args, "references"),
                    OptString(args, "model") ?? preset?.Model, fit?.Item1, fit?.Item2,
                    NegativePrompt: OptString(args, "negative_prompt") ?? preset?.NegativePrompt))));
            }, Needs: ["edit"]);

        yield return new Tool("generate_video",
            "Tworzy wideo z promptu i opcjonalnych obrazów wejściowych (bez zmiany stanu okna). KOSZTUJE pieniądze i wymaga zgody. Zwraca ścieżkę pliku.",
            Schema(("prompt", """{"type":"string"}""", true),
                   ("model", """{"type":"string","description":"Id modelu wideo (list_models); pominięty = domyślny"}""", false),
                   ("duration", """{"type":"integer","minimum":1,"maximum":60,"description":"Sekundy, domyślnie 5"}""", false),
                   ("aspect_ratio", """{"type":"string","enum":["16:9","9:16","1:1","4:3"]}""", false),
                   ("attachments", """{"type":"array","items":{"type":"string"},"description":"Obrazy wejściowe: ścieżki plików albo adresy http(s)"}""", false),
                   ("system_prompt", """{"type":"string","description":"Dołączany przed promptem jako część tekstu (API wideo nie ma roli system)"}""", false),
                   ("negative_prompt", """{"type":"string","description":"Tylko natywny parametr modelu, który go potwierdza w schemacie; inaczej ostrzeżenie w wyniku i nic nie jest wysyłane"}""", false)),
            true, async args => Serialize(Describe(await _studio.GenerateVideoAsync(new VideoGenerationRequest(
                RequiredString(args, "prompt"), OptString(args, "model"), OptInt(args, "duration") ?? 5,
                OptString(args, "aspect_ratio") ?? "16:9", OptStrings(args, "attachments"),
                NegativePrompt: OptString(args, "negative_prompt"), SystemPrompt: OptString(args, "system_prompt"))))),
            Needs: ["video"]);

        yield return new Tool("rebuild_prompt",
            "Przebudowuje prompt modelem tekstowym i zwraca nowe pola (system_prompt, prompt) do użycia w kolejnym wywołaniu; nic nie zmienia w aplikacji.",
            Schema(("prompt", """{"type":"string","description":"Koncept do ulepszenia"}""", true),
                   ("system_prompt", """{"type":"string","description":"Obecna część konfiguracyjna"}""", false),
                   ("instruction", """{"type":"string","description":"Własne polecenie przebudowy (domyślne z aplikacji)"}""", false),
                   ("model", """{"type":"string","description":"Model tekstowy; pominięty = domyślny"}""", false)),
            false, async args =>
            {
                var parts = await _studio.RebuildPromptAsync(new PromptRebuildRequest(
                    RequiredString(args, "prompt"), OptString(args, "system_prompt"), OptString(args, "instruction"), OptString(args, "model")));
                return Serialize(new { system_prompt = parts.System, prompt = parts.Concept, changes = parts.Changes });
            }, Needs: ["chat"]);

        yield return new Tool("describe_images",
            "Opisuje obrazy (pliki lub adresy http(s)) modelem tekstowym, który przyjmuje obrazy; nic nie zmienia w aplikacji. Zwraca opis.",
            Schema(("images", """{"type":"array","items":{"type":"string"},"description":"Pełne ścieżki plików albo adresy http(s)"}""", true),
                   ("model", """{"type":"string","description":"Model tekstowy z wejściem obrazowym"}""", true),
                   ("max_side", """{"type":"integer","minimum":256,"maximum":4096,"description":"Dłuższy bok po zmniejszeniu, domyślnie 1536"}""", false)),
            false, args => _studio.DescribeImagesAsync(new DescribeRequest(
                OptStrings(args, "images"), RequiredString(args, "model"), OptInt(args, "max_side") ?? 1536)),
            Needs: ["chat"]);

        yield return new Tool("list_presets", "Zestawy (presety) zapisane w aplikacji: nazwa, rodzaj, prompt, model i rozmiar.",
            Schema(("kind", """{"type":"string","enum":["generate","edit"]}""", false)),
            false, args => Task.FromResult(Serialize(_studio.Presets(OptString(args, "kind")).Select(p => new
            {
                p.Name, p.Description, p.Kind, p.Prompt, system_prompt = p.SystemPrompt, p.Model, size = $"{p.Width}x{p.Height}",
            }))));

        yield return new Tool("save_preset", "Zapisuje zestaw (nadpisuje o tej samej nazwie i rodzaju); użyj go potem w generate_image / edit_image (preset).",
            Schema(("name", """{"type":"string"}""", true),
                   ("kind", """{"type":"string","enum":["generate","edit"]}""", true),
                   ("prompt", """{"type":"string"}""", true),
                   ("system_prompt", """{"type":"string"}""", false),
                   ("model", """{"type":"string"}""", false),
                   ("width", """{"type":"integer","minimum":64,"maximum":4096}""", false),
                   ("height", """{"type":"integer","minimum":64,"maximum":4096}""", false),
                   ("seed", """{"type":"integer"}""", false),
                   ("negative_prompt", """{"type":"string","description":"Tylko natywny parametr modelu, który go potwierdza w schemacie; inaczej ostrzeżenie w wyniku i nic nie jest wysyłane"}""", false),
                   ("quality", """{"type":"string","enum":["auto","low","medium","high"]}""", false)),
            false, args =>
            {
                var (width, height) = Dimensions(OptInt(args, "width"), OptInt(args, "height"));
                var saved = _studio.SavePreset(new SavePresetRequest(
                    RequiredString(args, "name"), RequiredString(args, "kind"), RequiredString(args, "prompt"),
                    OptString(args, "system_prompt"), OptString(args, "model"), width, height, Options(args, null)));
                return Task.FromResult($"Zapisano zestaw \"{saved.Name}\".");
            });

        yield return new Tool("list_gallery",
            "Wyniki z galerii obrazów, wideo i głosu (najnowsze pierwsze): id, rodzaj, model, prompt (dla transkrypcji tekst), plik, koszt.",
            Schema(("kind", """{"type":"string","enum":["generate","edit","video","speech","transcript","media","voice"],"description":"Pominięty = wszystko; media = obrazy i wideo, voice = mowa i transkrypcje"}""", false),
                   ("limit", """{"type":"integer","minimum":1,"description":"Domyślnie i maksimum z Ustawień (zakładka MCP)"}""", false)),
            false, args =>
            {
                var limit = _limits.ListSize(OptInt(args, "limit"));
                var items = _studio.List(OptString(args, "kind"), limit + 1)
                    .Select(e => new GalleryItem(e.Id, e.Kind, e.Model, e.Prompt, e.FilePath, e.CostLabel, e.CreatedUtc)).ToList();
                return Task.FromResult(Paged(items, limit, _limits.MaxListLimit));
            });

        yield return new Tool("repeat_gallery_entry",
            "Powtarza operację z wpisu galerii (obraz, edycja, wideo, mowa, rozpoznawanie) z tym samym promptem, wejściami i parametrami; "
            + "opcjonalnie innym modelem. KOSZTUJE pieniądze i wymaga zgody. Zwraca nowy wpis (id, ścieżka pliku).",
            Schema(("id", """{"type":"string","description":"Id z list_gallery"}""", true),
                   ("model", """{"type":"string","description":"Inny model tego samego rodzaju (pominięty = ten sam co we wpisie)"}""", false)),
            true, async args => Serialize(Describe(await _studio.RepeatAsync(new RepeatRequest(RequiredString(args, "id"), OptString(args, "model"))))));

        yield return new Tool("remove_gallery_entry", "Usuwa wpis z galerii; plik zostaje na dysku.",
            Schema(("id", """{"type":"string","description":"Id z list_gallery"}""", true)),
            false, args =>
            {
                _studio.RemoveEntry(RequiredString(args, "id"));
                return Task.FromResult("Usunięto wpis.");
            });

        yield return new Tool("delete_gallery_entry", "Usuwa wpis razem z plikiem wyniku. NIEODWRACALNE, wymaga zgody. Transkrypcji (cudzy plik) nie dotyczy.",
            Schema(("id", """{"type":"string","description":"Id z list_gallery"}""", true)),
            true, async args =>
            {
                await _studio.DeleteEntryAsync(RequiredString(args, "id"));
                return "Usunięto wpis i plik.";
            });

        yield return new Tool("clear_gallery", "Czyści galerię (media albo voice); pliki zostają na dysku. Wymaga zgody.",
            Schema(("scope", """{"type":"string","enum":["media","voice"]}""", true)),
            true, args =>
            {
                _studio.Clear(RequiredString(args, "scope"));
                return Task.FromResult("Wyczyszczono galerię.");
            });

        yield return new Tool("export_gallery_entry", "Kopiuje plik wyniku z galerii pod podaną ścieżkę (zastępuje „Zapisz jako”). Wymaga zgody.",
            Schema(("id", """{"type":"string","description":"Id z list_gallery"}""", true),
                   ("target", """{"type":"string","description":"Pełna ścieżka docelowa pliku"}""", true)),
            true, async args => $"Zapisano: {await _studio.ExportAsync(RequiredString(args, "id"), RequiredString(args, "target"))}");
    }
}
