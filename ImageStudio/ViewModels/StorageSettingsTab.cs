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
    public bool IsFolder { get; } = setting.IsFolder;
    public bool IsMasked { get; } = setting.Masked;
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
public sealed partial class StorageOption : ObservableObject
{
    private readonly IMediaStoragePlugin _plugin;
    private CancellationTokenSource? _cts;

    public StorageOption(IMediaStoragePlugin plugin)
    {
        _plugin = plugin;
        Id = plugin.Id;
        Name = plugin.DisplayName;
        Fields = plugin.Settings.Select(s => new StorageField(s)).ToList();
        HelpText = plugin.HelpKey is { } key ? Loc.T(key) : "";
        Actions = plugin.Actions.Select(a => new StorageActionItem(
            Loc.T(a.LabelKey), new AsyncRelayCommand(() => RunAsync(a.Key), () => !IsBusy && _plugin.CanRun(a.Key)))).ToList();
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        Refresh();
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<StorageField> Fields { get; }
    public IReadOnlyList<StorageActionItem> Actions { get; }
    public string HelpText { get; }
    public IRelayCommand CancelCommand { get; }

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private bool _isBusy;

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
    private async Task RunAsync(string key)
    {
        IsBusy = true;
        ErrorText = "";
        StatusText = Loc.T("storage.status.working");
        foreach (var action in Actions) action.Command.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        try
        {
            _plugin.Configure(Fields.ToDictionary(f => f.Key, f => f.Value));
            await _plugin.RunActionAsync(key, _cts.Token);
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
            _cts.Dispose();
            _cts = null;
        }
    }
}

// Zakładka "Zapis": wtyczka magazynu wyników (lista jak przy dostawcy AI) i jej ustawienia, np. foldery zapisu plików
public sealed partial class StorageSettingsTab : ObservableObject, ISettingsTab
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
        StorageConfig = Options.ToDictionary(o => o.Id, o => o.Fields.ToDictionary(f => f.Key, f => f.Value)),
    };
}
