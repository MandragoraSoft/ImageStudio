using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ImageStudio.Configuration;

// Ustawienia: appsettings.json (domyślne, w katalogu aplikacji) + settings.json użytkownika (nadpisuje)
// Klucz API: zmienna UBERLLM_API_KEY (ma pierwszeństwo) albo SecretStore.
public sealed record AppSettings(string BaseUrl, string MarketUrl, int MaxSide, string ApiKey, string Theme,
                                 string ProviderId = "uberllm", string OpenRouterKey = "",
                                 string OpenAiBaseUrl = "https://api.openai.com", string OpenAiKey = "")
{
    private static string UserSettingsPath => Path.Combine(AppPaths.UserDataDir, "settings.json");

    public static AppSettings Load()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile(UserSettingsPath, optional: true)
            .Build();

        return new AppSettings(
            BaseUrl: config["Uberllm:BaseUrl"]!,
            MarketUrl: config["Uberllm:MarketUrl"]!,
            MaxSide: int.Parse(config["Uberllm:MaxSide"]!),
            ApiKey: Secret("UBERLLM_API_KEY", "uberllm"),
            Theme: config["Ui:Theme"] ?? "system",
            ProviderId: config["Ai:Provider"] ?? "uberllm",
            OpenRouterKey: Secret("OPENROUTER_API_KEY", "openrouter"),
            OpenAiBaseUrl: config["OpenAi:BaseUrl"] ?? "https://api.openai.com",
            OpenAiKey: Secret("OPENAI_API_KEY", "openai"));
    }

    // Gradacja sekretu: niepusta zmienna środowiskowa > zaszyfrowany SecretStore (DPAPI) > pusty
    public static string Secret(string envVar, string name) =>
        Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } env ? env : SecretStore.Load(name) ?? "";

    // Adresy, limit wymiarów, motyw i provider trafiają do settings.json użytkownika, klucz do sekretów
    public static void Save(string baseUrl, string marketUrl, int maxSide, string apiKey, string theme,
                            string providerId = "uberllm", string openRouterKey = "",
                            string openAiBaseUrl = "https://api.openai.com", string openAiKey = "")
    {
        Directory.CreateDirectory(AppPaths.UserDataDir);
        var json = JsonSerializer.Serialize(
            new
            {
                Uberllm = new { BaseUrl = baseUrl, MarketUrl = marketUrl, MaxSide = maxSide },
                Ui = new { Theme = theme },
                Ai = new { Provider = providerId },
                OpenAi = new { BaseUrl = openAiBaseUrl },
            },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(UserSettingsPath, json);
        SecretStore.Save(apiKey);
        SecretStore.Save(openRouterKey, "openrouter");
        SecretStore.Save(openAiKey, "openai");
    }
}
