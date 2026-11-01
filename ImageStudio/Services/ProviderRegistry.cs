using ImageStudio.Configuration;

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
    public static IAiProvider Create(string id, AppSettings settings, ISecretStore? secrets = null) => id switch
    {
        UberLlmProvider.Identifier => new UberLlmProvider(settings.BaseUrl, settings.ApiKey, settings.MarketUrl),
        OpenRouterProvider.Identifier => new OpenRouterProvider(settings.OpenRouterKey),
        OpenAiProvider.Identifier => new OpenAiProvider(settings.OpenAiBaseUrl, settings.OpenAiKey),
        ChatGptProvider.Identifier => new ChatGptProvider(settings.ChatGptSession, json => secrets?.Save(json, ChatGptSecret)),
        MistralProvider.Identifier => new MistralProvider(settings.MistralKey),
        KiloProvider.Identifier => new KiloProvider(settings.KiloKey),
        OpenCodeGoProvider.Identifier => new OpenCodeGoProvider(settings.OpenCodeGoKey),
        _ => throw new InvalidOperationException($"Nieznany provider: {id}"),
    };
}
