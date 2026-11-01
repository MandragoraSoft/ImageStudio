using ImageStudio.Composition;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

// Magazyny w pamięci: testy nie dotykają plików użytkownika i nie muszą się wzajemnie wykluczać
internal sealed class MemoryUserStateStore : IUserStateStore
{
    public UserState State { get; private set; } = new();
    public UserState Load() => State;
    public void Save(UserState state) => State = state;
}

internal sealed class MemoryGalleryStore : IGalleryStore
{
    public List<GalleryEntry> Entries { get; } = [];
    public IReadOnlyList<GalleryEntry> Load() => Entries.ToList();

    public void Save(IEnumerable<GalleryEntry> entries)
    {
        var snapshot = entries.ToList();
        Entries.Clear();
        Entries.AddRange(snapshot);
    }
}

internal sealed class MemoryPresetStore : IPresetStore
{
    public List<Preset> Presets { get; } = [];
    public IReadOnlyList<Preset> LoadAll() => Presets.ToList();

    public Preset Create(Preset preset)
    {
        preset.FilePath = $"memory:{Guid.NewGuid():N}";
        Presets.Add(preset);
        return preset;
    }

    public void Save(Preset preset)
    {
        if (!Presets.Contains(preset)) Presets.Add(preset);
    }

    public void Delete(Preset preset) => Presets.Remove(preset);
}

internal sealed class MemorySecretStore : ISecretStore
{
    public Dictionary<string, string> Secrets { get; } = [];
    public string? Load(string name = "uberllm") => Secrets.GetValueOrDefault(name);

    public void Save(string value, string name = "uberllm")
    {
        if (string.IsNullOrWhiteSpace(value)) Secrets.Remove(name);
        else Secrets[name] = value;
    }
}

internal sealed class MemorySettingsStore(AppSettings initial) : ISettingsStore
{
    public AppSettings Settings { get; private set; } = initial;
    public int SaveCalls { get; private set; }
    public AppSettings Load() => Settings;

    public void Save(string baseUrl, string marketUrl, int maxSide, string apiKey, string theme, string providerId,
                     string openRouterKey, string openAiBaseUrl, string openAiKey)
    {
        SaveCalls++;
        Settings = new AppSettings(baseUrl, marketUrl, maxSide, apiKey, theme, providerId, openRouterKey, openAiBaseUrl, openAiKey);
    }
}

internal sealed class MemoryWindowStateStore : IWindowStateStore
{
    private readonly Dictionary<string, WindowRecord> _windows = [];
    private readonly Dictionary<string, double> _columns = [];
    public WindowRecord? GetWindow(string key) => _windows.GetValueOrDefault(key);
    public void SetWindow(string key, WindowRecord record) => _windows[key] = record;
    public double? GetColumn(string key) => _columns.TryGetValue(key, out var width) ? width : null;
    public void SetColumn(string key, double width) => _columns[key] = width;
}

internal sealed class FakeThemeService : IThemeService
{
    public List<string> Applied { get; } = [];
    public void Apply(string theme) => Applied.Add(theme);
}

// Fabryka zwraca tę samą atrapę: zmiana dostawcy w teście nie sięga do prawdziwych klientów HTTP
internal sealed class FakeProviderFactory(FakeProvider provider) : IProviderFactory
{
    public List<string> Created { get; } = [];

    public IAiProvider Create(string providerId, AppSettings settings)
    {
        Created.Add(providerId);
        return provider;
    }
}

// Aplikacja złożona z tego samego kontenera co produkcyjna (AddImageStudioCore), ale z atrapami i magazynami w pamięci
internal sealed class TestApp : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-testapp-" + Guid.NewGuid().ToString("N"));

    private TestApp(Action<IServiceCollection>? configure, FakeProvider? provider, FakeDialogs? dialogs)
    {
        Directory.CreateDirectory(_dir);
        var settings = new AppSettings("https://api.test", "https://market.test", 1536, "", "system");

        var services = new ServiceCollection().AddImageStudioCore();
        services.AddSingleton(provider ?? new FakeProvider());
        services.AddSingleton<IAiProvider>(sp => sp.GetRequiredService<FakeProvider>());
        services.AddSingleton(dialogs ?? new FakeDialogs());
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<FakeDialogs>());
        services.AddSingleton<IThemeService, FakeThemeService>();
        services.AddSingleton<IProviderFactory>(sp => new FakeProviderFactory(sp.GetRequiredService<FakeProvider>()));
        services.AddSingleton<IUserStateStore, MemoryUserStateStore>();
        services.AddSingleton<IGalleryStore, MemoryGalleryStore>();
        services.AddSingleton<IPresetStore, MemoryPresetStore>();
        services.AddSingleton<ISecretStore, MemorySecretStore>();
        services.AddSingleton<IWindowStateStore, MemoryWindowStateStore>();
        services.AddSingleton(settings);
        services.AddSingleton<ISettingsStore>(new MemorySettingsStore(settings));
        services.AddSingleton(new ModelPreferences { PathOverride = Path.Combine(_dir, "models.json") });
        services.AddSingleton(new McpOptions(Path.Combine(_dir, "brak-mcp.json")));
        configure?.Invoke(services);

        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public ServiceProvider Services { get; }

    public static TestApp Create(Action<IServiceCollection>? configure = null, FakeProvider? provider = null, FakeDialogs? dialogs = null) =>
        new(configure, provider, dialogs);

    // Skrót: sam MainViewModel z (opcjonalnie własnymi) atrapami
    public static MainViewModel CreateVm(FakeProvider? provider = null, FakeDialogs? dialogs = null) => Create(null, provider, dialogs).Vm;

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
    public MainViewModel Vm => Get<MainViewModel>();
    public FakeProvider Provider => Get<FakeProvider>();
    public FakeDialogs Dialogs => Get<FakeDialogs>();
    public MemoryPresetStore Presets => (MemoryPresetStore)Get<IPresetStore>();
    public MemoryUserStateStore State => (MemoryUserStateStore)Get<IUserStateStore>();
    public MemorySecretStore Secrets => (MemorySecretStore)Get<ISecretStore>();
    public MemorySettingsStore Settings => (MemorySettingsStore)Get<ISettingsStore>();
    public IReadOnlyList<IMainTab> Tabs => Services.GetServices<IMainTab>().OrderBy(t => t.Order).ToList();

    public PresetsViewModel NewPresetsViewModel() =>
        new(Dialogs, Get<IPresetStore>(), Get<LegacyPromptImporter>());

    public void Dispose()
    {
        // McpToolHost implementuje tylko IAsyncDisposable, więc synchroniczne Dispose kontenera by go odrzuciło
        Services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
