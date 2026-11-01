namespace ImageStudio.Services;

// Źródło narzędzi dla czatu (dziś serwery MCP): definicje wysyłane do modelu i wykonanie wywołań, o które model poprosi
public interface IToolHost
{
    // Narzędzia dostępne w tej chwili; pusta lista = czat działa bez narzędzi
    IReadOnlyList<ToolDefinition> Tools { get; }

    // Tekst dopisywany do instrukcji systemowej (np. zapamiętane informacje); null = nic do dopisania
    string? SystemNote => null;

    // Czy każde wywołanie wymaga zgody użytkownika
    bool RequireApproval { get; }

    // Wynik jako tekst dla modelu; błędy narzędzia wracają jako tekst albo wyjątek (czat zamieni go na tekst)
    Task<string> CallAsync(ToolCall call, CancellationToken ct = default);
}
