using ImageStudio.Localization;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Services;
using Microsoft.Extensions.Logging;

namespace ImageStudio.ViewModels;

// Sterowanie lokalnym serwerem MCP aplikacji: włączenie, port, status i konfiguracja do wklejenia w kliencie MCP
public sealed partial class AppServerViewModel : ObservableObject, IAsyncDisposable
{
    public const int DefaultPort = 39517;

    private static readonly ILogger Log = AppLog.For<AppServerViewModel>();

    private readonly McpAppServer _server;
    private readonly IDialogService _dialogs;
    private readonly Func<string> _token;
    private bool _silent;
    private int _disposed;
    private readonly object _pendingChangesLock = new();
    private readonly HashSet<Task> _pendingChanges = [];

    // token: dostarcza (i przy pierwszym użyciu tworzy) sekret serwera; w aplikacji to SecretStore
    public AppServerViewModel(McpAppServer server, IDialogService dialogs, bool enabled, int port, Func<string>? token = null,
                              ISecretStore? secrets = null)
    {
        _secrets = secrets ?? new FileSecretStore();
        _server = server;
        _dialogs = dialogs;
        _token = token ?? StoredToken;
        _enabled = enabled;
        _port = port is > 0 and <= 65535 ? port : DefaultPort;
    }

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Endpoint))]
    private int _port;

    [ObservableProperty]
    private string _status = "";

    public string Endpoint => McpAppServer.EndpointFor(Port);

    // Po starcie aplikacji uruchamia serwer, jeśli był włączony
    public Task InitializeAsync() => TrackChangeAsync(() => Enabled ? StartAsync() : Task.CompletedTask);

    partial void OnEnabledChanged(bool value)
    {
        if (_silent) return;
        _ = TrackChangeAsync(() => value ? StartAsync() : StopAsync());
    }

    partial void OnPortChanged(int value)
    {
        if (Enabled && !_silent) _ = TrackChangeAsync(StartAsync);
    }

    private async Task StartAsync()
    {
        try
        {
            Status = Loc.T("mcpServer.starting");
            await _server.StartAsync(Port, _token());
            Status = Loc.T("mcpServer.running", Endpoint);
        }
        catch (Exception ex)
        {
            Status = Loc.T("msg.error", ex.Message);
            Log.LogError("Serwer MCP aplikacji: {Message}", ex.Message);
            _silent = true;
            Enabled = false;
            _silent = false;
        }
    }

    private async Task StopAsync()
    {
        await _server.StopAsync();
        Status = Loc.T("mcpServer.stopped");
    }

    // Gotowy fragment konfiguracji klienta MCP (format Claude Desktop) z adresem i tokenem
    public string ClientConfig => JsonSerializer.Serialize(new
    {
        mcpServers = new
        {
            imagestudio = new
            {
                url = Endpoint,
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + _token() },
            },
        },
    }, new JsonSerializerOptions { WriteIndented = true });

    [RelayCommand]
    private void CopyConfig()
    {
        _dialogs.CopyText(ClientConfig);
        Status = Loc.T("mcpServer.copied");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Task[] pending;
        lock (_pendingChangesLock) pending = _pendingChanges.ToArray();
        await Task.WhenAll(pending);
        await _server.DisposeAsync();
    }

    private async Task TrackChangeAsync(Func<Task> change)
    {
        Task pending;
        lock (_pendingChangesLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            pending = change();
            _pendingChanges.Add(pending);
        }

        try { await pending; }
        finally
        {
            lock (_pendingChangesLock) _pendingChanges.Remove(pending);
        }
    }

    private readonly ISecretStore _secrets;

    // Token przechowywany szyfrowanie (DPAPI) obok kluczy API; tworzony losowo przy pierwszym użyciu
    private string StoredToken()
    {
        if (_secrets.Load("mcp-server") is { Length: > 0 } existing) return existing;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _secrets.Save(token, "mcp-server");
        return token;
    }
}
