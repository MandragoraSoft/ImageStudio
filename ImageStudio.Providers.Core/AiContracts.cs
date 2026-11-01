namespace ImageStudio.Services;

// Kontrakty wspólne dla wszystkich dostawców AI (IAiProvider)
// Atrybuty modelu z /v1/models, używane do filtrowania w UI
public sealed record ImageModelInfo(string Id, string? OwnedBy, bool Nsfw, IReadOnlyList<string> InputModalities,
                                    string? Description = null, IReadOnlyList<string>? OutputModalities = null);

public sealed record ImageModels(IReadOnlyList<ImageModelInfo> Generation, IReadOnlyList<ImageModelInfo> Edit);

// Model czatu z listą parametrów, które deklaruje (supported_parameters)
// AcceptsImage: czy katalog deklaruje wejście obrazowe (null = katalog nic o tym nie mówi)
// OwnedBy / InputModalities / Description zasilają listę modeli czatu (ikony możliwości, filtry); null = katalog nic nie mówi
public sealed record ChatModelInfo(string Id, IReadOnlyList<string> SupportedParameters, bool? AcceptsImage = null,
                                   string? OwnedBy = null, IReadOnlyList<string>? InputModalities = null, string? Description = null);

// Narzędzie dla modelu: nazwa, opis i schemat parametrów (JSON Schema jako tekst)
public sealed record ToolDefinition(string Name, string Description, string ParametersJson);

// Prośba modelu o wywołanie narzędzia; argumenty to tekst JSON
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

// Odpowiedź modelu: tekst i/lub prośby o narzędzia
// Reasoning: widoczne rozumowanie modelu, jeśli API je zwraca (reasoning_content / reasoning), inaczej null
public sealed record ChatReply(string Content, IReadOnlyList<ToolCall> ToolCalls, string? Reasoning = null, TokenUsage? Usage = null);

// Zużycie tokenów w jednym wywołaniu czatu; null = API nie podało danej wartości.
// Cached to część tokenów wejścia obsłużona z pamięci podręcznej dostawcy
public sealed record TokenUsage(int? Input, int? Output, int? Cached);

// Rozumowanie w żądaniu czatu. Parameter to parametr, który model deklaruje w supported_parameters
// ("reasoning_effort", "reasoning" albo "include_reasoning"); Enabled włącza lub wyłącza, Effort ustawia poziom
public sealed record ChatOptions(string Parameter, bool Enabled, string? Effort = null);

public static class ReasoningSupport
{
    public static readonly IReadOnlyList<string> Levels = ["low", "medium", "high"];
    public const string DefaultEffort = "medium";

    // Który parametr rozumowania obsługuje model (null = model nie daje sterować rozumowaniem)
    public static string? ParameterFor(IEnumerable<string> supportedParameters)
    {
        var supported = supportedParameters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new[] { "reasoning_effort", "reasoning", "include_reasoning" }.FirstOrDefault(supported.Contains);
    }
}

// Wiadomość czatu (role: system, user, assistant, tool). Wiadomość asystenta może nieść ToolCalls,
// a wynik narzędzia (rola tool) odwołuje się do nich przez ToolCallId. IsUser, RoleLabel i DisplayText służą widokowi.
// Przy odpowiedzi strumieniowej wiadomość powstaje pusta i jest uzupełniana kawałkami (ChatDelta)
public sealed class ChatMessage : System.ComponentModel.INotifyPropertyChanged
{
    private string _content;
    private IReadOnlyList<ToolCall>? _toolCalls;
    private string? _reasoning;
    private TokenUsage? _usage;

    public ChatMessage(string Role, string Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null,
                       string? ToolName = null, IReadOnlyList<ChatImage>? Images = null, string? Reasoning = null,
                       TokenUsage? Usage = null)
    {
        this.Role = Role;
        _content = Content;
        _toolCalls = ToolCalls;
        this.ToolCallId = ToolCallId;
        this.ToolName = ToolName;
        this.Images = Images;
        _reasoning = Reasoning;
        _usage = Usage;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public string Role { get; }
    public string? ToolCallId { get; }
    public string? ToolName { get; }
    public IReadOnlyList<ChatImage>? Images { get; }

    // Treść, rozumowanie, wywołania narzędzi i zużycie są ustawialne: odpowiedź strumieniowa dopisuje je na bieżąco,
    // a widok (wiązanie) odświeża się sam
    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            Raise(nameof(Content), nameof(DisplayText));
        }
    }

    public IReadOnlyList<ToolCall>? ToolCalls
    {
        get => _toolCalls;
        set
        {
            _toolCalls = value;
            Raise(nameof(ToolCalls), nameof(HasToolCalls), nameof(RoleLabel), nameof(DisplayText));
        }
    }

    public string? Reasoning
    {
        get => _reasoning;
        set
        {
            if (_reasoning == value) return;
            _reasoning = value;
            Raise(nameof(Reasoning), nameof(HasReasoning));
        }
    }

    public TokenUsage? Usage
    {
        get => _usage;
        set
        {
            _usage = value;
            Raise(nameof(Usage), nameof(HasUsage), nameof(UsageText));
        }
    }

    // Tokeny wejścia, wyjścia i z cache pod odpowiedzią modelu; "—" = API nie podało wartości
    public bool HasUsage => Usage is not null;

    public string UsageText
    {
        get
        {
            static string Format(int? value) => value is { } v ? v.ToString("N0") : "—";
            return Usage is { } u ? $"Tokeny: wejście {Format(u.Input)} · wyjście {Format(u.Output)} · cache {Format(u.Cached)}" : "";
        }
    }

    // Rozumowanie jest tylko do podglądu: nie wraca do API w historii rozmowy
    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);
    public bool IsUser => Role == "user";

    // Odpowiedzi modelu są pokazywane jako Markdown (kod, listy, pogrubienia); tekst użytkownika i narzędzi zostaje surowy
    public bool IsAssistant => Role == "assistant";
    public bool HasImages => Images is { Count: > 0 };
    public bool HasToolCalls => ToolCalls is { Count: > 0 };

    public string RoleLabel => Role switch
    {
        "user" => "Ty",
        "assistant" => HasToolCalls ? "Model (wywołuje narzędzie)" : "Model",
        "tool" => $"Narzędzie {ToolName}".TrimEnd(),
        _ => "System",
    };

    // Tekst wiadomości, a przy prośbach o narzędzia także lista wywołań
    public string DisplayText => HasToolCalls
        ? string.Join("\n", (string.IsNullOrWhiteSpace(Content) ? [] : new[] { Content })
            .Concat(ToolCalls!.Select(c => $"→ {c.Name}({c.ArgumentsJson})")))
        : Content;
}

// Wynik generowania/edycji: bajty obrazu i koszt z API (null, gdy API go nie zwróciło)
public sealed record ImageResult(byte[] Bytes, decimal? CostUsd);

// Opcjonalne parametry generowania; null = nie wysyłamy (API odrzuca nieznane klucze)
public sealed record ImageOptions(int? Seed, string? NegativePrompt, string? Quality);

// Prompt rozdzielony: System = konfiguracja (styl, technika), Concept = treść wskazana przez użytkownika
// Changes: jednozdaniowe podsumowanie, co model zmienił (opcjonalne)
public sealed record PromptParts(string System, string Concept, string? Changes = null);

// Jeden krok przebudowy promptu przez model: wejście, wynik i polecenie; lista takich kroków to kontekst kolejnych przebudów
public sealed record PromptRevision(string InSystem, string InConcept, string OutSystem, string OutConcept,
                                    string Instruction, string? Changes = null);

// Cena z rynku: PriceNow za jednostkę (Unit), reszta to dane pomocnicze do wyświetlenia
public sealed record ModelPricing(
    decimal PriceNow,
    decimal? ListUsd,
    decimal? DiscountPct,
    string? Unit,
    decimal? MarketOffers,
    decimal? Requests24h,
    decimal? OutputPrice = null,
    decimal? CachePrice = null)
{
    // Modele tekstowe: PriceNow to cena wejścia za 1M tokenów (Unit = TokenUnit), OutputPrice cena wyjścia,
    // CachePrice cena odczytu z cache; null = dostawca nie podaje
    // OpenRouter podaje cenę tokenów obrazu (za 1M tok), nie cenę za obraz
    public const string TokenUnit = "1M tok";

    // Cena jednego wyniku do szacunku kosztu; null, gdy cena jest za tokeny
    public decimal? PerResult => Unit == TokenUnit ? null : PriceNow;
}

// Katalog dostawcy pobrany jednym wywołaniem ReloadAsync. Część katalogu, której nie udało się pobrać, jest null
// (aplikacja zostawia wtedy poprzednią listę), a przyczyna trafia do Warnings. Pricing bywa pusty (modele bez cen)
// Speech / Transcription: modele dźwięku (mowa z tekstu, tekst z nagrania); null = dostawca ich nie obsługuje albo pobranie się nie udało
public sealed record ProviderCatalog(ImageModels? Image, IReadOnlyList<ImageModelInfo>? Video, IReadOnlyList<ChatModelInfo>? Chat,
                                     IReadOnlyDictionary<string, ModelPricing> Pricing, IReadOnlyList<string> Warnings,
                                     IReadOnlyList<AudioModelInfo>? Speech = null, IReadOnlyList<AudioModelInfo>? Transcription = null);

// Model dźwięku (mowa albo transkrypcja). Cena jest w opisie modelu (nie na rynku): jednostka to "1K characters" albo "minute"
public sealed record AudioModelInfo(string Id, string? OwnedBy, ModelPricing? Pricing = null, string? Description = null);

// Wynik syntezy mowy: gotowy plik (Extension bez kropki, np. "mp3") i koszt z odpowiedzi (null = dostawca go nie podaje)
public sealed record AudioResult(byte[] Bytes, string Extension, decimal? CostUsd);

// Wynik rozpoznawania mowy: tekst i koszt (null = nieznany)
public sealed record TranscriptResult(string Text, decimal? CostUsd);

// Kawałek odpowiedzi strumieniowej: dopisywany tekst i/lub rozumowanie (null = brak w tym kawałku)
public sealed record ChatDelta(string? Content, string? Reasoning);
