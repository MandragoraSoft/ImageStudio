using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Localization;
using ImageStudio.Services;
using ImageStudio.Storage;

namespace ImageStudio.ViewModels;

public sealed partial class StorageField(StorageSetting setting) : ObservableObject
{
    public string Key { get; } = setting.Key;
    public string Label { get; } = Loc.T(setting.LabelKey);
    public StorageFieldKind Kind { get; } = setting.EffectiveKind;
    public bool IsTransient { get; } = setting.Transient;
    public bool IsFolder => Kind == StorageFieldKind.Folder;
    public bool IsMasked => Kind == StorageFieldKind.Password;
    public bool IsPlainText => !IsMasked;
    public string NoteText { get; } = setting.Note is { } note ? Loc.T(note.Key, note.Detail) : "";
    public bool HasNote => NoteText.Length > 0;

    [ObservableProperty]
    private string _value = setting.Value;
}

// Przycisk wtyczki (np. logowanie) w zakładce
public sealed class StorageActionItem(string label, IAsyncRelayCommand command)
{
    public string Label { get; } = label;
    public IAsyncRelayCommand Command { get; } = command;
}

// Wtyczka magazynu z polami do edycji (pozycja listy wyboru). Przyciski działają jak przy logowaniu ChatGPT:
// niedostępne w złym stanie (Zaloguj po zalogowaniu, Wyloguj bez sesji) i w trakcie logowania, z przyciskiem Anuluj
public sealed partial class StorageOption : ObservableObject, IStorageInteraction, IDisposable
{
    private readonly IMediaStoragePlugin _plugin;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<string?>? _prompt;

    public StorageOption(IMediaStoragePlugin plugin)
    {
        _plugin = plugin;
        Id = plugin.Id;
        Name = plugin.DisplayName;
        Fields = plugin.Settings.Select(s => new StorageField(s)).ToList();
        HelpText = plugin.HelpKey is { } key ? Loc.T(key) : "";
        Actions = plugin.Actions.Select(a => new StorageActionItem(
            Loc.T(a.LabelKey), new AsyncRelayCommand(() => RunAsync(a), () => !IsBusy && _plugin.CanRun(a.Key)))).ToList();
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        SubmitPromptCommand = new RelayCommand(() => _prompt?.TrySetResult(PromptValue.Trim()));
        Refresh();
        _plugin.StatusChanged += OnPluginStatusChanged;
    }

    public void Dispose() => _plugin.StatusChanged -= OnPluginStatusChanged;

    // Wysyłka w tle zmienia stan wtyczki: odświeżenie na wątku okna; w trakcie akcji stan ustawia RunAsync
    private void OnPluginStatusChanged(object? sender, EventArgs e) => OnUi(() => { if (!IsBusy) Refresh(); });

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<StorageField> Fields { get; }
    public IReadOnlyList<StorageActionItem> Actions { get; }
    public string HelpText { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand SubmitPromptCommand { get; }

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private bool _isBusy;

    // Pytanie wtyczki w trakcie akcji (np. kod 2FA)
    [ObservableProperty]
    private bool _isPrompting;

    [ObservableProperty]
    private string _promptLabel = "";

    [ObservableProperty]
    private string _promptMessage = "";

    [ObservableProperty]
    private string _promptValue = "";

    // Stan wtyczki do wyświetlenia: błąd osobno (kolor ostrzeżenia), reszta jako zwykły status
    private void Refresh(string? error = null)
    {
        var status = _plugin.Status;
        var isError = error is not null || status?.Key == "storage.status.error";
        ErrorText = error ?? (isError ? Format(status) : "");
        StatusText = isError ? "" : Format(status);
        foreach (var action in Actions) action.Command.NotifyCanExecuteChanged();
    }

    private static string Format(StorageStatus? status) => status is null ? "" : Loc.T(status.Key, status.Detail);

    // Wtyczka dostaje bieżące (jeszcze niezapisane) wartości pól, np. Client ID wpisany tuż przed logowaniem
    // Pola nietrwałe (hasło, e-mail logowania) idą tylko do akcji; Configure dostaje resztę (np. Client ID, foldery)
    private async Task RunAsync(StorageAction action)
    {
        IsBusy = true;
        ErrorText = "";
        StatusText = Loc.T(action.WorkingKey ?? "storage.status.working");
        foreach (var item in Actions) item.Command.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        try
        {
            _plugin.Configure(Fields.Where(f => !f.IsTransient).ToDictionary(f => f.Key, f => f.Value));
            var inputs = Fields.Where(f => f.IsTransient).ToDictionary(f => f.Key, f => f.Value);
            await _plugin.RunActionAsync(action.Key, inputs, this, _cts.Token);
            foreach (var field in Fields.Where(f => f.IsTransient)) field.Value = ""; // hasło nie zostaje w oknie po sukcesie
            IsBusy = false;
            Refresh();
        }
        catch (OperationCanceledException)
        {
            IsBusy = false;
            Refresh();
        }
        catch (Exception ex)
        {
            IsBusy = false;
            Refresh(ex.Message);
        }
        finally
        {
            EndPrompt();
            _cts.Dispose();
            _cts = null;
        }
    }

    async Task<string?> IStorageInteraction.AskAsync(StoragePrompt prompt, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            _prompt = answer;
            PromptLabel = Loc.T(prompt.LabelKey);
            PromptMessage = prompt.Message is { } m ? Loc.T(m.Key, m.Detail) : "";
            PromptValue = "";
            IsPrompting = true;
        });
        using var registration = ct.Register(() => answer.TrySetCanceled(ct));
        try { return await answer.Task; }
        finally { OnUi(EndPrompt); }
    }

    private void EndPrompt()
    {
        _prompt = null;
        IsPrompting = false;
        PromptValue = "";
        PromptLabel = "";
        PromptMessage = "";
    }

    // Wtyczka może pytać z wątku tła: zmiany właściwości wracają na wątek okna (jak Activity.Notify)
    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }
}

// Zakładka "Zapis": wtyczka magazynu wyników (lista jak przy dostawcy AI) i jej ustawienia, np. foldery zapisu plików
public sealed partial class StorageSettingsTab : ObservableObject, ISettingsTab, IDisposable
{
    public string Title => Loc.T("settings.tab.storage");
    public int Order => 7;

    public IReadOnlyList<StorageOption> Options { get; }

    [ObservableProperty]
    private StorageOption? _selected;

    public StorageSettingsTab(SettingsResult current, MediaStorageHost host)
    {
        Options = host.Plugins.Select(p => new StorageOption(p)).ToList();
        _selected = Options.FirstOrDefault(o => o.Id == (current.StorageId ?? host.Active.Id)) ?? Options[0];
    }

    public SettingsResult Apply(SettingsResult result) => result with
    {
        StorageId = Selected?.Id ?? result.StorageId,
        StorageConfig = Options.ToDictionary(o => o.Id, o => o.Fields.Where(f => !f.IsTransient).ToDictionary(f => f.Key, f => f.Value)),
    };

    public void Dispose()
    {
        foreach (var option in Options) option.Dispose();
    }
}
