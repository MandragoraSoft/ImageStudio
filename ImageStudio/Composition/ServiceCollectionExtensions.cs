using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.Tabs.Chat;
using ImageStudio.Tabs.Edit;
using ImageStudio.Tabs.Generate;
using ImageStudio.Tabs.Video;
using ImageStudio.Tabs.Voice;
using ImageStudio.ViewModels;
using ImageStudio.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ImageStudio.Composition;

// Composition root: jedyne miejsce, które zna konkretne klasy. Rejestracja jest podzielona na część wspólną dla aplikacji
// i testów (AddImageStudioCore) oraz infrastrukturę produkcyjną (AddImageStudioInfrastructure: pliki, okna systemowe,
// prawdziwy dostawca). Testy dodają zamiast niej magazyny w pamięci i atrapy.
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddImageStudioCore(this IServiceCollection services)
    {
        services.AddSingleton<IActivityTracker, ActivityTracker>();
        services.AddSingleton<ImageDownloadService>();
        services.AddSingleton<McpToolHost>();
        services.AddSingleton<LegacyPromptImporter>();
        services.AddSingleton<IProviderFactory, ProviderFactory>();
        services.TryAddSingleton(new McpOptions());

        services.AddSingleton<MainViewModel>();

        // Zakładki lewego panelu: każda jest wstrzykiwana jako IMainTab; kolejność daje Order, widok wybiera MainWindow
        services.AddSingleton<IMainTab>(sp => new GenerateTab(sp.GetRequiredService<MainViewModel>()));
        services.AddSingleton<IMainTab>(sp => new EditTab(sp.GetRequiredService<MainViewModel>()));
        services.AddSingleton<IMainTab>(sp => new VideoTab(sp.GetRequiredService<MainViewModel>()));
        services.AddSingleton<IMainTab>(sp => new ChatTab(sp.GetRequiredService<MainViewModel>().Chat));
        services.AddSingleton<IMainTab>(sp => new VoiceTab(sp.GetRequiredService<MainViewModel>().Voice));

        services.AddTransient<MainWindow>();
        return services;
    }

    public static IServiceCollection AddImageStudioInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IUserStateStore, FileUserStateStore>();
        services.AddSingleton<IGalleryStore, FileGalleryStore>();
        services.AddSingleton<IChatSessionStore, FileChatSessionStore>();
        services.AddSingleton<IVoiceGalleryStore, FileVoiceGalleryStore>();
        services.AddSingleton<IMemoryStore, FileMemoryStore>();
        services.AddSingleton<IPresetStore, FilePresetStore>();
        services.AddSingleton<ISecretStore, FileSecretStore>();
        services.AddSingleton<ISettingsStore, FileSettingsStore>();
        services.AddSingleton<IWindowStateStore, FileWindowStateStore>();
        services.AddSingleton(_ => ModelPreferences.Load());

        // Ustawienia i początkowy dostawca; zmiana dostawcy w Ustawieniach idzie przez IProviderFactory w MainViewModel
        services.AddSingleton(sp => sp.GetRequiredService<ISettingsStore>().Load());
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            return sp.GetRequiredService<IProviderFactory>().Create(settings.ProviderId, settings);
        });

        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<IThemeService, WpfThemeService>();
        services.AddSingleton<IAudioPlayer, NAudioPlayer>();
        services.AddSingleton<IAudioRecorder, NAudioRecorder>();
        return services;
    }
}
