using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Lista dostępnych providerów i ich fabryka. Nowy provider = implementacja IAiProvider + wpis tutaj.
public static class ProviderRegistry
{
    public sealed record ProviderInfo(string Id, string DisplayName);

    public static IReadOnlyList<ProviderInfo> Available { get; } =
    [
        new(UberLlmProvider.Identifier, "UberLLM"),
        new(OpenRouterProvider.Identifier, "OpenRouter"),
        new(OpenAiProvider.Identifier, "OpenAI"),
    ];

    public static IAiProvider Create(string id, AppSettings settings) => id switch
    {
        UberLlmProvider.Identifier => new UberLlmProvider(settings.BaseUrl, settings.ApiKey, settings.MarketUrl),
        OpenRouterProvider.Identifier => new OpenRouterProvider(settings.OpenRouterKey),
        OpenAiProvider.Identifier => new OpenAiProvider(settings.OpenAiBaseUrl, settings.OpenAiKey),
        _ => throw new InvalidOperationException($"Nieznany provider: {id}"),
    };
}
