using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ImageStudio.Configuration;

// Ustawienia: appsettings.json (domyślne, w katalogu aplikacji) + settings.json użytkownika (nadpisuje)
// Klucz API: zmienna UBERLLM_API_KEY (ma pierwszeństwo) albo SecretStore.
public sealed record AppSettings(string BaseUrl, string MarketUrl, int MaxSide, string ApiKey, string Theme)
{
    private static string UserSettingsPath => Path.Combine(AppPaths.UserDataDir, "settings.json");

    public static AppSettings Load()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile(UserSettingsPath, optional: true)
            .Build();

        var apiKey = Environment.GetEnvironmentVariable("UBERLLM_API_KEY") ?? SecretStore.Load() ?? "";

        return new AppSettings(
            BaseUrl: config["Uberllm:BaseUrl"]!,
            MarketUrl: config["Uberllm:MarketUrl"]!,
            MaxSide: int.Parse(config["Uberllm:MaxSide"]!),
            ApiKey: apiKey,
            Theme: config["Ui:Theme"] ?? "system");
    }

    // Adresy, limit wymiarów i motyw trafiają do settings.json użytkownika, klucz do sekretów
    public static void Save(string baseUrl, string marketUrl, int maxSide, string apiKey, string theme)
    {
        Directory.CreateDirectory(AppPaths.UserDataDir);
        var json = JsonSerializer.Serialize(
            new { Uberllm = new { BaseUrl = baseUrl, MarketUrl = marketUrl, MaxSide = maxSide }, Ui = new { Theme = theme } },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(UserSettingsPath, json);
        SecretStore.Save(apiKey);
    }
}
