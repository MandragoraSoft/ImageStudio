using ImageStudio.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Services;
using Microsoft.Extensions.Logging;

namespace ImageStudio.ViewModels;

// Wiersz serwera MCP w panelu: nazwa, opis (polecenie albo adres), zaznaczenie i stan połączenia
public sealed partial class McpServerItem : ObservableObject
{
    private readonly Action<McpServerItem> _toggled;

    public McpServerItem(string name, string description, bool enabled, Action<McpServerItem> toggled)
    {
        Name = name;
        Description = description;
        _isEnabled = enabled;
        _toggled = toggled;
    }

    public string Name { get; }
    public string Description { get; }

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _status = "";

    // Zmiana z kodu (np. po nieudanym połączeniu) nie wywołuje ponownego łączenia
    private bool _silent;

    public void SetEnabledSilently(bool value)
    {
        _silent = true;
        IsEnabled = value;
        _silent = false;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_silent) _toggled(this);
    }
}

// Narzędzia czatu z serwerów MCP: lista serwerów z mcp.json, łączenie zaznaczonych i wykonywanie wywołań modelu.
// Zaznaczone serwery, główny przełącznik i tryb zgody są zapamiętywane w state.json (przez MainViewModel).
public sealed partial class ToolsViewModel : ObservableObject, IToolHost
{
    private static readonly ILogger Log = AppLog.For<ToolsViewModel>();

    private readonly McpToolHost _host;
    private readonly string? _configPath;
    private readonly HashSet<string> _enabledNames;
    private McpConfig _config = new();
    private bool _initialized;

    private readonly AppControlTools? _appTools;
    private readonly BuiltinTools? _builtin;
    private readonly IMemoryStore? _memory;

    public ToolsViewModel(McpToolHost host, bool useTools, bool requireApproval, IEnumerable<string> enabledServers,
                          string? configPath = null, AppControlTools? appTools = null, bool appControlEnabled = false,
                          AppServerViewModel? server = null, BuiltinTools? builtin = null, bool builtinEnabled = true,
                          IMemoryStore? memory = null)
    {
        _builtin = builtin;
        _builtinEnabled = builtinEnabled;
        _memory = memory;
        _host = host;
        _configPath = configPath;
        _useTools = useTools;
        _requireApproval = requireApproval;
        _enabledNames = new HashSet<string>(enabledServers);
        _appTools = appTools;
        _appControlEnabled = appControlEnabled;
        Server = server;
    }

    // Lokalny serwer MCP z narzędziami sterującymi aplikacją (dla zewnętrznych klientów); null = niedostępny
    public AppServerViewModel? Server { get; }

    // Model w czacie może sterować aplikacją (imagestudio__*): modele, prompty, rozmiar, generowanie, galeria
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tools), nameof(Summary))]
    private bool _appControlEnabled;

    // Wbudowane narzędzia (builtin__*): data i czas, pamięć między sesjami, pliki w folderze roboczym
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tools), nameof(Summary))]
    private bool _builtinEnabled;

    public bool HasBuiltin => _builtin is not null;

    public ObservableCollection<McpServerItem> Servers { get; } = [];

    // Główny przełącznik: wyłączony = model nie dostaje żadnych narzędzi, choć serwery pozostają połączone
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tools), nameof(Summary))]
    private bool _useTools;

    [ObservableProperty]
    private bool _requireApproval;

    [ObservableProperty]
    private string _info = "";

    public IReadOnlyList<ToolDefinition> Tools => !UseTools
        ? []
        : [.. _host.Tools, .. AppToolDefinitions(), .. BuiltinToolDefinitions()];

    // Zapamiętane informacje trafiają do instrukcji systemowej, gdy narzędzia pamięci są dostępne dla modelu
    public string? SystemNote => UseTools && BuiltinEnabled ? _builtin?.SystemNote : null;

    public string Summary => UseTools ? Loc.T("tools.summary.on", Tools.Count) : Loc.T("tools.summary.off");

    private IEnumerable<ToolDefinition> AppToolDefinitions() =>
        AppControlEnabled && _appTools is not null
            ? _appTools.Definitions.Select(d => d with { Name = AppControlTools.ChatPrefix + d.Name })
            : [];

    private IEnumerable<ToolDefinition> BuiltinToolDefinitions() =>
        BuiltinEnabled && _builtin is not null
            ? _builtin.Definitions.Select(d => d with { Name = BuiltinTools.ChatPrefix + d.Name })
            : [];

    // Nazwy zaznaczonych serwerów, także tych, których chwilowo nie ma w mcp.json (żeby ich nie zgubić przy zapisie stanu)
    public IReadOnlyCollection<string> EnabledServers => _enabledNames;

    // Narzędzia aplikacji (przedrostek imagestudio__) obsługuje AppControlTools, reszta idzie do serwerów MCP.
    // Gdy czat wymaga zgody, użytkownik już zatwierdził to wywołanie, więc kosztowne nie pytają drugi raz.
    public Task<string> CallAsync(ToolCall call, CancellationToken ct = default)
    {
        if (call.Name.StartsWith(BuiltinTools.ChatPrefix, StringComparison.Ordinal) && _builtin is not null)
            return BuiltinEnabled
                ? _builtin.CallAsync(call.Name[BuiltinTools.ChatPrefix.Length..], call.ArgumentsJson, alreadyApproved: RequireApproval)
                : Task.FromResult("Wbudowane narzędzia są wyłączone.");

        if (!call.Name.StartsWith(AppControlTools.ChatPrefix, StringComparison.Ordinal) || _appTools is null)
            return _host.CallAsync(call, ct);

        return AppControlEnabled
            ? _appTools.CallAsync(call.Name[AppControlTools.ChatPrefix.Length..], call.ArgumentsJson, alreadyApproved: RequireApproval)
            : Task.FromResult("Sterowanie aplikacją jest wyłączone.");
    }

    // Wczytuje listę serwerów i łączy zaznaczone; wywoływane raz po starcie aplikacji
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        LoadServers();
        if (Server is not null) await Server.InitializeAsync();
        await Task.WhenAll(Servers.Where(s => s.IsEnabled).Select(ConnectAsync));
    }

    private void LoadServers()
    {
        _config = McpConfig.Load(_configPath);
        Info = _config.Error ?? "";
        Servers.Clear();
        foreach (var (name, server) in _config.McpServers.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
            Servers.Add(new McpServerItem(name, server.Description, _enabledNames.Contains(name), OnServerToggled));
    }

    private async void OnServerToggled(McpServerItem item)
    {
        if (item.IsEnabled)
        {
            _enabledNames.Add(item.Name);
            await ConnectAsync(item);
        }
        else
        {
            _enabledNames.Remove(item.Name);
            await _host.DisconnectAsync(item.Name);
            item.Status = "";
            OnPropertyChanged(nameof(Summary));
        }
    }

    private async Task ConnectAsync(McpServerItem item)
    {
        item.Status = Loc.T("tools.connecting");
        try
        {
            var count = await _host.ConnectAsync(item.Name, _config.McpServers[item.Name]);
            item.Status = Loc.T("tools.connected", count);
        }
        catch (Exception ex)
        {
            item.Status = Loc.T("msg.error", ex.Message);
            Log.LogError("Serwer MCP {Server}: {Message}", item.Name, ex.Message);
            _enabledNames.Remove(item.Name);
            item.SetEnabledSilently(false);
        }

        OnPropertyChanged(nameof(Summary));
    }

    // Ponowne wczytanie mcp.json (po edycji) z połączeniem zaznaczonych serwerów od nowa
    [RelayCommand]
    private async Task ReloadAsync()
    {
        await _host.DisposeAsync();
        LoadServers();
        OnPropertyChanged(nameof(Summary));
        await Task.WhenAll(Servers.Where(s => s.IsEnabled).Select(ConnectAsync));
    }

    // Podgląd tego, co model zapamiętał (memory.json) i folderu, w którym czyta i zapisuje pliki
    [RelayCommand]
    private void OpenMemory() => OpenPath(_memory?.FilePath, createFile: true);

    [RelayCommand]
    private void OpenWorkspace()
    {
        if (_builtin is null) return;

        Directory.CreateDirectory(_builtin.WorkspaceRoot);
        OpenPath(_builtin.WorkspaceRoot, createFile: false);
    }

    private void OpenPath(string? path, bool createFile)
    {
        if (path is null) return;

        try
        {
            if (createFile && !File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "[]");
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Info = Loc.T("tools.openFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenConfig()
    {
        try
        {
            var path = _configPath ?? McpConfig.DefaultPath;
            McpConfig.EnsureFile(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Info = Loc.T("tools.openFailed", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        if (Server is not null) await Server.DisposeAsync();
    }
}
