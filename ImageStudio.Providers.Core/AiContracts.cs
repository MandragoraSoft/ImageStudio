namespace ImageStudio.Services;

// Kontrakty wspólne dla wszystkich dostawców AI (IAiProvider)
// Atrybuty modelu z /v1/models, używane do filtrowania w UI
public sealed record ImageModelInfo(string Id, string? OwnedBy, bool Nsfw, IReadOnlyList<string> InputModalities,
                                    string? Description = null, IReadOnlyList<string>? OutputModalities = null);

public sealed record ImageModels(IReadOnlyList<ImageModelInfo> Generation, IReadOnlyList<ImageModelInfo> Edit);

// Model czatu z listą parametrów, które deklaruje (supported_parameters)
// AcceptsImage: czy katalog deklaruje wejście obrazowe (null = katalog nic o tym nie mówi)
public sealed record ChatModelInfo(string Id, IReadOnlyList<string> SupportedParameters, bool? AcceptsImage = null);

// Narzędzie dla modelu: nazwa, opis i schemat parametrów (JSON Schema jako tekst)
public sealed record ToolDefinition(string Name, string Description, string ParametersJson);

// Prośba modelu o wywołanie narzędzia; argumenty to tekst JSON
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

// Odpowiedź modelu: tekst i/lub prośby o narzędzia
public sealed record ChatReply(string Content, IReadOnlyList<ToolCall> ToolCalls);

// Wiadomość czatu (role: system, user, assistant, tool). Wiadomość asystenta może nieść ToolCalls,
// a wynik narzędzia (rola tool) odwołuje się do nich przez ToolCallId. IsUser, RoleLabel i DisplayText służą widokowi.
public sealed record ChatMessage(string Role, string Content, IReadOnlyList<ToolCall>? ToolCalls = null,
                                 string? ToolCallId = null, string? ToolName = null, IReadOnlyList<ChatImage>? Images = null)
{
    public bool IsUser => Role == "user";
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
    decimal? Requests24h)
{
    // OpenRouter podaje cenę tokenów obrazu (za 1M tok), nie cenę za obraz
    public const string TokenUnit = "1M tok";

    // Cena jednego wyniku do szacunku kosztu; null, gdy cena jest za tokeny
    public decimal? PerResult => Unit == TokenUnit ? null : PriceNow;
}
