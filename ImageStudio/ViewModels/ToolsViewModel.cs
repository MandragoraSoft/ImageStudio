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

// Pojedyncze narzędzie na liście grup: włączane i wyłączane osobno. Name to nazwa widoczna dla modelu (z przedrostkiem grupy)
public sealed partial class ToolItem : ObservableObject
{
    private readonly Action<ToolItem> _toggled;
    private bool _silent;

    public ToolItem(string name, string title, string description, int tokens, bool enabled, Action<ToolItem> toggled)
    {
        Name = name;
        Title = title;
        Description = description;
        Tokens = tokens;
        _isEnabled = enabled;
        _toggled = toggled;
    }

    public string Name { get; }
    public string Title { get; }
    public string Description { get; }

    // Szacunek tokenów opisu i schematu: tyle narzędzie kosztuje w każdym żądaniu, gdy jest włączone
    public int Tokens { get; }
    public string TokensText => "~" + TokenEstimator.Short(Tokens);

    [ObservableProperty]
    private bool _isEnabled;

    // Ukrycie przez wyszukiwanie w panelu; nie wpływa na to, czy narzędzie jest wysyłane do modelu
    [ObservableProperty]
    private bool _isVisible = true;

    // Zmiana zbiorcza z grupy nie zgłasza każdego narzędzia osobno
    public void SetSilently(bool value)
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

// Grupa narzędzi (zestaw wbudowany, sterowanie aplikacją albo serwer MCP): rozwijana, z polem zaznaczającym wszystkie narzędzia naraz.
// IsChecked: true = wszystkie włączone, false = żadne, null = część
public sealed partial class ToolGroupItem : ObservableObject
{
    private readonly Action<ToolGroupItem> _changed;

    public ToolGroupItem(string title, string description, IReadOnlyList<ToolItem> tools, bool expanded, Action<ToolGroupItem> changed)
    {
        Title = title;
        Description = description;
        Tools = tools;
        _isExpanded = expanded;
        _changed = changed;
        foreach (var tool in tools) tool.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolItem.IsEnabled)) RaiseState();
        };
    }

    // Grupa ukryta, gdy wyszukiwanie nie pasuje do żadnego jej narzędzia (ustawia ToolsViewModel)
    [ObservableProperty]
    private bool _isVisible = true;

    // Tokeny włączonych narzędzi (tyle grupa dokłada do każdego żądania) i wszystkich (do sortowania)
    public int Tokens => Tools.Where(t => t.IsEnabled).Sum(t => t.Tokens);
    public int TotalTokens => Tools.Sum(t => t.Tokens);
    public string TokensText => "~" + TokenEstimator.Short(Tokens);

    public string Title { get; }
    public string Description { get; }
    public IReadOnlyList<ToolItem> Tools { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public bool? IsChecked
    {
        get
        {
            var enabled = Tools.Count(t => t.IsEnabled);
            return enabled == Tools.Count ? true : enabled == 0 ? false : null;
        }
        set
        {
            // Kliknięcie pola z częściowym zaznaczeniem wyłącza wszystko, kolejne włącza wszystko
            foreach (var tool in Tools) tool.SetSilently(value == true);
            RaiseState();
            _changed(this);
        }
    }

    // Licznik włączonych narzędzi w nagłówku, np. "2/4"
    public string Counter => $"{Tools.Count(t => t.IsEnabled)}/{Tools.Count}";

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(Counter));
        OnPropertyChanged(nameof(Tokens));
        OnPropertyChanged(nameof(TokensText));
    }
}

// Narzędzia czatu: serwery MCP z mcp.json (łączenie zaznaczonych), wbudowane grupy (data i czas, pamięć, pliki) i sterowanie
// aplikacją. Lista grup pozwala włączać i wyłączać każde narzędzie albo całą grupę; wyłączone nie trafiają do modelu.
// Zaznaczone serwery, główny przełącznik, tryb zgody i wyłączone narzędzia są zapamiętywane w state.json (przez MainViewModel).
public sealed partial class ToolsViewModel : ObservableObject, IToolHost
{
    private static readonly ILogger Log = AppLog.For<ToolsViewModel>();

    private readonly McpToolHost _host;
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    private readonly object _pendingChangesLock = new();
    private readonly HashSet<Task> _pendingChanges = [];
    private readonly string? _configPath;
    private readonly HashSet<string> _enabledNames;
    private McpConfig _config = new();
    private bool _initialized;

    private readonly IMcpToolSet? _appTools;
    private readonly McpLimits _limits;
    private readonly BuiltinTools? _builtin;
    private readonly IMemoryStore? _memory;
    private readonly ISkillStore? _skills;

    // Nazwy (widoczne dla modelu) narzędzi wyłączonych przez użytkownika; nowe narzędzia są domyślnie włączone
    private readonly HashSet<string> _disabled;

    // Profile: nazwa -> nazwy wyłączonych narzędzi w chwili zapisu (zapis w state.json przez MainViewModel)
    private readonly Dictionary<string, List<string>> _profiles;

    // disabledTools null = pierwsze uruchomienie po wprowadzeniu listy: stan wynika z dawnych przełączników grup
    // (sterowanie aplikacją było domyślnie wyłączone, wbudowane domyślnie włączone)
    public ToolsViewModel(McpToolHost host, bool useTools, bool requireApproval, IEnumerable<string> enabledServers,
                          string? configPath = null, IMcpToolSet? appTools = null, bool appControlEnabled = false,
                          AppServerViewModel? server = null, BuiltinTools? builtin = null, bool builtinEnabled = true,
                          IMemoryStore? memory = null, IEnumerable<string>? disabledTools = null, McpLimits? limits = null,
                          ISkillStore? skills = null, IReadOnlyDictionary<string, List<string>>? profiles = null)
    {
        _limits = limits ?? new McpLimits();
        _limits.Changed += () =>
        {
            OnPropertyChanged(nameof(LimitWarning));
        };
        Permissions.Changed += () => OnPropertyChanged(nameof(PermissionsSummary));
        _builtin = builtin;
        _memory = memory;
        _skills = skills;
        _host = host;
        _configPath = configPath;
        _useTools = useTools;
        _requireApproval = requireApproval;
        _enabledNames = new HashSet<string>(enabledServers);
        _appTools = appTools;
        Server = server;

        _disabled = disabledTools is null ? [] : new HashSet<string>(disabledTools);
        if (disabledTools is null)
        {
            if (!appControlEnabled) _disabled.UnionWith(AppToolNames());
            if (!builtinEnabled) _disabled.UnionWith(BuiltinToolNames());
        }

        _profiles = profiles is null ? [] : profiles.ToDictionary(p => p.Key, p => new List<string>(p.Value));
        RefreshProfileNames();
        RebuildGroups();
    }

    // Lokalny serwer MCP z narzędziami sterującymi aplikacją (dla zewnętrznych klientów); null = niedostępny
    public AppServerViewModel? Server { get; }

    // Model w czacie może sterować aplikacją (imagestudio__*): modele, prompty, rozmiar, generowanie, galeria.
    // Włączone, gdy jest włączone choć jedno z tych narzędzi; ustawienie zmienia wszystkie naraz
    public bool AppControlEnabled
    {
        get => AppToolNames().Any(n => !_disabled.Contains(n));
        set => SetGroupEnabled(AppToolNames(), value);
    }

    // Wbudowane narzędzia (builtin__*): data i czas, pamięć między sesjami, pliki w folderze roboczym
    public bool BuiltinEnabled
    {
        get => BuiltinToolNames().Any(n => !_disabled.Contains(n));
        set => SetGroupEnabled(BuiltinToolNames(), value);
    }

    public bool HasBuiltin => _builtin is not null;
    public bool HasSkills => _skills is not null;

    // Zgody na wywołania narzędzi (na sesję i na stałe); czat pyta tylko o narzędzia bez zgody
    public ToolPermissions Permissions { get; } = new();

    public string PermissionsSummary => Loc.T("tools.permissions.summary", Permissions.SessionCount, Permissions.Always.Count);

    [RelayCommand]
    private void ForgetPermissions() => Permissions.Forget();

    public ObservableCollection<McpServerItem> Servers { get; } = [];

    // Grupy narzędzi z polami włącz/wyłącz (zestawy wbudowane, sterowanie aplikacją, połączone serwery MCP)
    public ObservableCollection<ToolGroupItem> ToolGroups { get; } = [];

    // Nazwy wyłączonych narzędzi (do zapisu stanu)
    public IReadOnlyCollection<string> DisabledTools => _disabled;

    // Główny przełącznik: wyłączony = model nie dostaje żadnych narzędzi, choć serwery pozostają połączone
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tools), nameof(Summary), nameof(SystemNote))]
    private bool _useTools;

    [ObservableProperty]
    private bool _requireApproval;

    [ObservableProperty]
    private string _info = "";

    // Narzędzia wysyłane do modelu: z włączonych grup, bez wyłączonych pojedynczo
    public IReadOnlyList<ToolDefinition> Tools => !UseTools
        ? []
        : [.. _host.Tools.Where(d => !_disabled.Contains(d.Name)), .. AppToolDefinitions(), .. BuiltinToolDefinitions()];

    // Zapamiętane informacje trafiają do instrukcji systemowej, gdy któreś narzędzie ich grupy jest włączone
    public string? SystemNote => UseTools ? _builtin?.SystemNoteFor(name => !_disabled.Contains(name)) : null;

    // Ostrzeżenie, gdy modelowi wystawiamy więcej narzędzi niż limit z Ustawień (zakładka MCP)
    public string LimitWarning => UseTools && _limits.ExceedsTools(Tools.Count) ? Loc.T("tools.warn.limit", Tools.Count, _limits.MaxTools) : "";

    public string Summary => UseTools ? Loc.T("tools.summary.on", Tools.Count) : Loc.T("tools.summary.off");

    // Koszt narzędzi w każdym żądaniu (szacunek, bo modele liczą tokeny po swojemu)
    public int ToolTokens => Tools.Sum(t => TokenEstimator.Estimate(t));

    public string TokensSummary => UseTools ? Loc.T("tools.tokens.summary", TokenEstimator.Short(ToolTokens)) : "";

    // Wyszukiwanie po nazwie i opisie (bez rozróżniania wielkości liter); puste = wszystkie narzędzia widoczne
    [ObservableProperty]
    private string _toolSearch = "";

    partial void OnToolSearchChanged(string value) => ApplySearch();

    // Sortowanie grup malejąco po rozmiarze (tokenach wszystkich narzędzi grupy) zamiast kolejności domyślnej
    [ObservableProperty]
    private bool _sortBySize;

    partial void OnSortBySizeChanged(bool value) => RebuildGroups();

    public ObservableCollection<string> ProfileNames { get; } = [];

    public IReadOnlyDictionary<string, List<string>> Profiles => _profiles;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyProfileCommand), nameof(DeleteProfileCommand))]
    private string? _selectedProfile;

    [ObservableProperty]
    private string _profileName = "";

    private bool HasSelectedProfile => SelectedProfile is not null;

    // Zapis zestawu wyłączonych narzędzi; ta sama nazwa nadpisuje profil
    [RelayCommand]
    private void SaveProfile()
    {
        var name = ProfileName.Trim();
        if (name.Length == 0) return;

        _profiles[name] = _disabled.ToList();
        RefreshProfileNames();
        SelectedProfile = name;
        OnPropertyChanged(nameof(Profiles));
    }

    // Przywraca zapisany zestaw: wszystko poza nim jest włączone; nazwy, których już nie ma, są pomijane
    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
    private void ApplyProfile()
    {
        if (SelectedProfile is not { } name || !_profiles.TryGetValue(name, out var names)) return;

        _disabled.Clear();
        _disabled.UnionWith(names);
        RebuildGroups();
        OnToolsChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { } name) return;

        _profiles.Remove(name);
        SelectedProfile = null;
        RefreshProfileNames();
        OnPropertyChanged(nameof(Profiles));
    }

    private void RefreshProfileNames()
    {
        ProfileNames.Clear();
        foreach (var name in _profiles.Keys.Order()) ProfileNames.Add(name);
    }

    // Ukrywa narzędzia i grupy, których nazwa ani opis nie pasują do wyszukiwania
    private void ApplySearch()
    {
        var query = ToolSearch.Trim();
        foreach (var group in ToolGroups)
        {
            foreach (var tool in group.Tools)
                tool.IsVisible = query.Length == 0
                                 || tool.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                                 || tool.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
            group.IsVisible = query.Length == 0 || group.Tools.Any(t => t.IsVisible);
        }
    }

    private IEnumerable<ToolDefinition> AppToolDefinitions() =>
        _appTools is null
            ? []
            : _appTools.Definitions.Select(d => d with { Name = AppControlTools.ChatPrefix + d.Name }).Where(d => !_disabled.Contains(d.Name));

    private IEnumerable<ToolDefinition> BuiltinToolDefinitions() =>
        _builtin is null
            ? []
            : _builtin.Definitions.Select(d => d with { Name = BuiltinTools.ChatPrefix + d.Name }).Where(d => !_disabled.Contains(d.Name));

    private IEnumerable<string> AppToolNames() =>
        _appTools?.AllDefinitions.Select(d => AppControlTools.ChatPrefix + d.Name) ?? [];

    private IEnumerable<string> BuiltinToolNames() =>
        _builtin?.Definitions.Select(d => BuiltinTools.ChatPrefix + d.Name) ?? [];

    // Włącza albo wyłącza wszystkie podane narzędzia naraz (zmiana z kodu, np. testy i stare przełączniki grup)
    private void SetGroupEnabled(IEnumerable<string> names, bool enabled)
    {
        foreach (var name in names.ToList())
        {
            if (enabled) _disabled.Remove(name);
            else _disabled.Add(name);
        }

        RebuildGroups();
        OnToolsChanged();
    }

    // Zmiana pojedynczego narzędzia z listy grup
    private void OnToolToggled(ToolItem item)
    {
        if (item.IsEnabled) _disabled.Remove(item.Name);
        else _disabled.Add(item.Name);
        OnToolsChanged();
    }

    // Zmiana zbiorcza z pola grupy: zbiór wyłączonych zgadza się ze stanem wszystkich jej narzędzi
    private void OnGroupToggled(ToolGroupItem group)
    {
        foreach (var tool in group.Tools)
        {
            if (tool.IsEnabled) _disabled.Remove(tool.Name);
            else _disabled.Add(tool.Name);
        }

        OnToolsChanged();
    }

    private void OnToolsChanged()
    {
        OnPropertyChanged(nameof(Tools));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ToolTokens));
        OnPropertyChanged(nameof(TokensSummary));
        OnPropertyChanged(nameof(LimitWarning));
        OnPropertyChanged(nameof(SystemNote));
        OnPropertyChanged(nameof(AppControlEnabled));
        OnPropertyChanged(nameof(BuiltinEnabled));
        OnPropertyChanged(nameof(DisabledTools));
    }

    // Lista grup od nowa (po połączeniu albo rozłączeniu serwera MCP); rozwinięcia grup zostają
    private void RebuildGroups()
    {
        var expanded = ToolGroups.Where(g => g.IsExpanded).Select(g => g.Title).ToHashSet();
        var definitions = DefinitionsByName();
        var groups = new List<ToolGroupItem>();

        ToolItem Item(string name, string title, string description) =>
            new(name, title, description, definitions.TryGetValue(name, out var definition) ? TokenEstimator.Estimate(definition) : 0,
                !_disabled.Contains(name), OnToolToggled);

        ToolGroupItem Group(string title, string description, IEnumerable<ToolItem> tools) =>
            new(title, description, tools.ToList(), expanded.Contains(title), OnGroupToggled);

        if (_builtin is not null)
            foreach (var group in _builtin.Groups)
                groups.Add(Group(BuiltinGroupTitle(group.Id), "",
                                 group.Tools.Select(t => Item(BuiltinTools.ChatPrefix + t.Name, t.Name, t.Description))));

        // Narzędzia imagestudio__* podzielone według przeznaczenia (obrazy, wideo, głos...), każda grupa włączana osobno
        if (_appTools is not null)
            foreach (var category in ToolCategories.Order)
            {
                var tools = _appTools.AllDefinitions.Where(d => ToolCategories.For(d.Name) == category).ToList();
                if (tools.Count > 0)
                    groups.Add(Group(AppGroupTitle(category), Loc.T("tools.control.tip"),
                                     tools.Select(d => Item(AppControlTools.ChatPrefix + d.Name, d.Name, d.Description))));
            }

        foreach (var (server, tools) in _host.ServerTools.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
            groups.Add(Group(server, Loc.T("tools.group.mcp"),
                             tools.Select(d => Item(d.Name, d.Name.Contains("__") ? d.Name[(d.Name.IndexOf("__", StringComparison.Ordinal) + 2)..] : d.Name, d.Description))));

        if (SortBySize) groups = groups.OrderByDescending(g => g.TotalTokens).ToList();

        ToolGroups.Clear();
        foreach (var group in groups) ToolGroups.Add(group);
        ApplySearch();
    }

    // Pełne definicje wszystkich narzędzi (także wyłączonych) pod nazwami widocznymi dla modelu: do szacunku tokenów w panelu
    private Dictionary<string, ToolDefinition> DefinitionsByName()
    {
        var all = new Dictionary<string, ToolDefinition>();
        if (_builtin is not null)
            foreach (var d in _builtin.Definitions) all[BuiltinTools.ChatPrefix + d.Name] = d with { Name = BuiltinTools.ChatPrefix + d.Name };
        if (_appTools is not null)
            foreach (var d in _appTools.AllDefinitions) all[AppControlTools.ChatPrefix + d.Name] = d with { Name = AppControlTools.ChatPrefix + d.Name };
        foreach (var server in _host.ServerTools.Values)
            foreach (var d in server) all[d.Name] = d;
        return all;
    }

    private static string AppGroupTitle(string id) => id switch
    {
        "image" => Loc.T("tools.group.image"),
        "video" => Loc.T("tools.group.video"),
        "voice" => Loc.T("tools.group.voice"),
        "prompts" => Loc.T("tools.group.prompts"),
        "gallery" => Loc.T("tools.group.gallery"),
        "models" => Loc.T("tools.group.models"),
        _ => Loc.T("tools.group.app"),
    };

    private static string BuiltinGroupTitle(string id) => id switch
    {
        "datetime" => Loc.T("tools.group.datetime"),
        "memory" => Loc.T("tools.group.memory"),
        "files" => Loc.T("tools.group.files"),
        "skills" => Loc.T("tools.group.skills"),
        _ => id,
    };

    // Nazwy zaznaczonych serwerów, także tych, których chwilowo nie ma w mcp.json (żeby ich nie zgubić przy zapisie stanu)
    public IReadOnlyCollection<string> EnabledServers => _enabledNames;

    // Narzędzie wyłączone na liście nie jest wysyłane do modelu, a gdyby mimo to o nie poprosił, dostaje odmowę.
    // Narzędzia aplikacji (przedrostek imagestudio__) obsługuje AppControlTools, wbudowane BuiltinTools, reszta idzie do serwerów MCP.
    // Gdy czat wymaga zgody, użytkownik już zatwierdził to wywołanie, więc kosztowne nie pytają drugi raz.
    public Task<string> CallAsync(ToolCall call, CancellationToken ct = default)
    {
        if (_disabled.Contains(call.Name)) return Task.FromResult("Narzędzie jest wyłączone przez użytkownika.");

        if (call.Name.StartsWith(BuiltinTools.ChatPrefix, StringComparison.Ordinal) && _builtin is not null)
            return _builtin.CallAsync(call.Name[BuiltinTools.ChatPrefix.Length..], call.ArgumentsJson, alreadyApproved: RequireApproval);

        if (!call.Name.StartsWith(AppControlTools.ChatPrefix, StringComparison.Ordinal) || _appTools is null)
            return _host.CallAsync(call, ct);

        return _appTools.CallAsync(call.Name[AppControlTools.ChatPrefix.Length..], call.ArgumentsJson, alreadyApproved: RequireApproval);
    }

    // Wczytuje listę serwerów i łączy zaznaczone; wywoływane raz po starcie aplikacji
    public Task InitializeAsync() => TrackChangeAsync(InitializeCoreAsync);

    private async Task InitializeCoreAsync()
    {
        if (_initialized || _stop.IsCancellationRequested) return;
        _initialized = true;

        LoadServers();
        try
        {
            if (Server is not null) await Server.InitializeAsync();
            _stop.Token.ThrowIfCancellationRequested();
            await Task.WhenAll(Servers.Where(s => s.IsEnabled).Select(item => ConnectAsync(item, _stop.Token)));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private void LoadServers()
    {
        _config = McpConfig.Load(_configPath);
        Info = _config.Error ?? "";
        Servers.Clear();
        foreach (var (name, server) in _config.McpServers.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
            Servers.Add(new McpServerItem(name, server.Description, _enabledNames.Contains(name), OnServerToggled));
    }

    private async void OnServerToggled(McpServerItem item) =>
        await TrackChangeAsync(() => OnServerToggledCoreAsync(item));

    private async Task OnServerToggledCoreAsync(McpServerItem item)
    {
        if (_stop.IsCancellationRequested) return;
        if (item.IsEnabled)
        {
            _enabledNames.Add(item.Name);
            await ConnectAsync(item, _stop.Token);
        }
        else
        {
            _enabledNames.Remove(item.Name);
            await _host.DisconnectAsync(item.Name);
            item.Status = "";
            RebuildGroups();
            OnToolsChanged();
        }
    }

    private async Task ConnectAsync(McpServerItem item, CancellationToken ct = default)
    {
        item.Status = Loc.T("tools.connecting");
        try
        {
            var count = await _host.ConnectAsync(item.Name, _config.McpServers[item.Name], ct);
            item.Status = Loc.T("tools.connected", count);
            RebuildGroups();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            item.Status = Loc.T("msg.error", ex.Message);
            Log.LogError("Serwer MCP {Server}: {Message}", item.Name, ex.Message);
            _enabledNames.Remove(item.Name);
            item.SetEnabledSilently(false);
        }

        OnToolsChanged();
    }

    // Ponowne wczytanie mcp.json (po edycji) z połączeniem zaznaczonych serwerów od nowa
    [RelayCommand]
    private Task ReloadAsync() => TrackChangeAsync(ReloadCoreAsync);

    private async Task ReloadCoreAsync()
    {
        if (_stop.IsCancellationRequested) return;
        await _host.DisposeAsync();
        LoadServers();
        RebuildGroups();
        OnToolsChanged();
        await Task.WhenAll(Servers.Where(s => s.IsEnabled).Select(item => ConnectAsync(item, _stop.Token)));
    }

    // Podgląd tego, co model zapamiętał (memory.json) i folderu, w którym czyta i zapisuje pliki
    [RelayCommand]
    private void OpenMemory() => OpenPath(_memory?.FilePath, createFile: true);

    // Folder ze skilami (skills\<nazwa>\SKILL.md): można je tu przeglądać i edytować ręcznie
    [RelayCommand]
    private void OpenSkills()
    {
        if (_skills?.FolderPath is not { } folder) return;

        Directory.CreateDirectory(folder);
        OpenPath(folder, createFile: false);
    }

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        Task[] pending;
        lock (_pendingChangesLock) pending = _pendingChanges.ToArray();
        await Task.WhenAll(pending);
        await _host.DisposeAsync();
        if (Server is not null) await Server.DisposeAsync();
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
}
