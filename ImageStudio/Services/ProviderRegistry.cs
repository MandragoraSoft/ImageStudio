using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Lista dostępnych providerów i ich fabryka. Nowy provider = implementacja IAiProvider + wpis tutaj.
public static class ProviderRegistry
{
    public sealed record ProviderInfo(string Id, string DisplayName, string? DefaultTextModel);

    public static IReadOnlyList<ProviderInfo> Available { get; } =
    [
        new(UberLlmProvider.Identifier, "UberLLM", UberLlmProvider.DefaultTextModelId),
        new(OpenRouterProvider.Identifier, "OpenRouter", OpenRouterProvider.DefaultTextModelId),
        new(OpenAiProvider.Identifier, "OpenAI", OpenAiProvider.DefaultTextModelId),
    ];

    public static IAiProvider Create(string id, AppSettings settings) => id switch
    {
        UberLlmProvider.Identifier => new UberLlmProvider(settings.BaseUrl, settings.ApiKey, settings.MarketUrl),
        OpenRouterProvider.Identifier => new OpenRouterProvider(settings.OpenRouterKey),
        OpenAiProvider.Identifier => new OpenAiProvider(settings.OpenAiBaseUrl, settings.OpenAiKey),
        _ => throw new InvalidOperationException($"Nieznany provider: {id}"),
    };
}
