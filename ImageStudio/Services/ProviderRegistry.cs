using ImageStudio.Configuration;
using System.Net.Http;

namespace ImageStudio.Services;

// Lista dostępnych providerów i ich fabryka. Nowy provider = implementacja IAiProvider + wpis tutaj.
public static class ProviderRegistry
{
    // Nazwa sekretu z sesją ChatGPT (SecretStore)
    public const string ChatGptSecret = "chatgpt";

    public sealed record ProviderInfo(string Id, string DisplayName, string? DefaultTextModel);

    public static IReadOnlyList<ProviderInfo> Available { get; } =
    [
        new(UberLlmProvider.Identifier, "UberLLM", UberLlmProvider.DefaultTextModelId),
        new(OpenRouterProvider.Identifier, "OpenRouter", OpenRouterProvider.DefaultTextModelId),
        new(OpenAiProvider.Identifier, "OpenAI", OpenAiProvider.DefaultTextModelId),
        new(ChatGptProvider.Identifier, "ChatGPT", null),
        new(MistralProvider.Identifier, "Mistral", MistralProvider.DefaultTextModelId),
        new(KiloProvider.Identifier, "Kilo", KiloProvider.DefaultTextModelId),
        new(OpenCodeGoProvider.Identifier, "OpenCode Go", OpenCodeGoProvider.DefaultTextModelId),
    ];

    // secrets (opcjonalne): dostawca z sesją OAuth (ChatGPT) zapisuje w nim odświeżone tokeny
    public static IAiProvider Create(string id, AppSettings settings, ISecretStore? secrets = null) => Create(id, settings, secrets, null);

    public static IAiProvider Create(string id, AppSettings settings, ISecretStore? secrets, Func<HttpMessageHandler>? handlerFactory) => id switch
    {
        UberLlmProvider.Identifier => new UberLlmProvider(settings.BaseUrl, settings.ApiKey, settings.MarketUrl, handlerFactory),
        OpenRouterProvider.Identifier => new OpenRouterProvider(settings.OpenRouterKey, "https://openrouter.ai", handlerFactory),
        OpenAiProvider.Identifier => new OpenAiProvider(settings.OpenAiBaseUrl, settings.OpenAiKey, handlerFactory),
        ChatGptProvider.Identifier => new ChatGptProvider(settings.ChatGptSession, json => secrets?.Save(json, ChatGptSecret),
            null, ChatGptAuth.Resource + "/", handlerFactory?.Invoke(), handlerFactory?.Invoke()),
        MistralProvider.Identifier => new MistralProvider(settings.MistralKey, handlerFactory?.Invoke()),
        KiloProvider.Identifier => new KiloProvider(settings.KiloKey, handlerFactory?.Invoke()),
        OpenCodeGoProvider.Identifier => new OpenCodeGoProvider(settings.OpenCodeGoKey, handlerFactory?.Invoke()),
        _ => throw new InvalidOperationException($"Nieznany provider: {id}"),
    };
}
