namespace ImageStudio.Configuration;

// Magazyny danych użytkownika za interfejsami (wstrzykiwane przez DI). Implementacje File* opakowują istniejące
// klasy plikowe w %LOCALAPPDATA%\ImageStudio; testy podstawiają magazyny w pamięci.

public interface IUserStateStore
{
    UserState Load();
    void Save(UserState state);
}

public interface IGalleryStore
{
    IReadOnlyList<GalleryEntry> Load();
    void Save(IEnumerable<GalleryEntry> entries);
}

public interface IPresetStore
{
    IReadOnlyList<Preset> LoadAll();
    Preset Create(Preset preset);
    void Save(Preset preset);
    void Delete(Preset preset);
}

public interface ISecretStore
{
    string? Load(string name = "uberllm");
    void Save(string value, string name = "uberllm");
}

public interface ISettingsStore
{
    // Ustawienia z domyślnych, pliku użytkownika i sekretów (zmienna środowiskowa ma pierwszeństwo nad sekretem)
    AppSettings Load();

    void Save(string baseUrl, string marketUrl, int maxSide, string apiKey, string theme, string providerId,
              string openRouterKey, string openAiBaseUrl, string openAiKey, bool useStreaming = true);
}

// Zapisane sesje czatu (sesja incognito nigdy tu nie trafia)
public interface IChatSessionStore
{
    IReadOnlyList<ChatSessionInfo> List();
    ChatSessionData? Load(string id);
    void Save(ChatSessionData session);
    void Delete(string id);
}

public interface IWindowStateStore
{
    WindowRecord? GetWindow(string key);
    void SetWindow(string key, WindowRecord record);
    double? GetColumn(string key);
    void SetColumn(string key, double width);
}

public sealed class FileUserStateStore : IUserStateStore
{
    public UserState Load() => UserState.Load();
    public void Save(UserState state) => state.Save();
}

public sealed class FileGalleryStore : IGalleryStore
{
    public IReadOnlyList<GalleryEntry> Load() => GalleryStore.Load();
    public void Save(IEnumerable<GalleryEntry> entries) => GalleryStore.Save(entries);
}

public sealed class FilePresetStore : IPresetStore
{
    public IReadOnlyList<Preset> LoadAll() => PresetStore.LoadAll();
    public Preset Create(Preset preset) => PresetStore.Create(preset);
    public void Save(Preset preset) => PresetStore.Save(preset);
    public void Delete(Preset preset) => PresetStore.Delete(preset);
}

public sealed class FileSecretStore : ISecretStore
{
    public string? Load(string name = "uberllm") => SecretStore.Load(name);
    public void Save(string value, string name = "uberllm") => SecretStore.Save(value, name);
}

public sealed class FileSettingsStore : ISettingsStore
{
    public AppSettings Load() => AppSettings.Load();

    public void Save(string baseUrl, string marketUrl, int maxSide, string apiKey, string theme, string providerId,
                     string openRouterKey, string openAiBaseUrl, string openAiKey, bool useStreaming = true) =>
        AppSettings.Save(baseUrl, marketUrl, maxSide, apiKey, theme, providerId, openRouterKey, openAiBaseUrl, openAiKey, useStreaming);
}

public sealed class FileWindowStateStore : IWindowStateStore
{
    public WindowRecord? GetWindow(string key) => WindowStateStore.GetWindow(key);
    public void SetWindow(string key, WindowRecord record) => WindowStateStore.SetWindow(key, record);
    public double? GetColumn(string key) => WindowStateStore.GetColumn(key);
    public void SetColumn(string key, double width) => WindowStateStore.SetColumn(key, width);
}

// Ścieżka konfiguracji serwerów MCP; null = domyślna (testy podają nieistniejący plik)
public sealed record McpOptions(string? ConfigPath = null);
