using ImageStudio.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Konfiguracja jednego providera: każdy ma własny ViewModel i własny widok (szablon wybierany po typie)
public abstract partial class ProviderSettingsViewModel : ObservableObject
{
    public abstract string ProviderId { get; }

    // Zmienna środowiskowa z kluczem dostawcy (ma pierwszeństwo nad zapisanym sekretem); pusta, gdy dostawca jej nie ma
    public abstract string EnvVar { get; }

    // Ostrzeżenie w oknie ustawień; null, gdy zmienna nie jest ustawiona
    public string? EnvNotice => EnvVar.Length > 0 && Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 }
        ? Loc.T("provider.envOverride", EnvVar)
        : null;

    // Lista modeli tego dostawcy: false = wszystkie, true = tylko preferowane
    [ObservableProperty]
    private bool _preferredOnly;

    // Preferowany model tekstowy tego dostawcy wybierany w standardowym selektorze modeli (ModelPicker);
    // lista zawiera modele znane aplikacji (aktywny dostawca) oraz model domyślny dostawcy
    public ObservableCollection<string> TextModels { get; } = [];

    [ObservableProperty]
    private string? _defaultTextModel;

    public string? ProviderDefaultTextModel { get; init; }

    // Do zapisu: null, gdy wybrano model domyślny dostawcy (wtedy zmiana domyślnego w dostawcy działa od razu)
    public string? PreferredTextModelOrNull => DefaultTextModel == ProviderDefaultTextModel ? null : DefaultTextModel;

    public void SetTextModels(IEnumerable<string> known, string? preferred)
    {
        TextModels.Clear();
        foreach (var model in known.Append(ProviderDefaultTextModel).Append(preferred).OfType<string>().Distinct())
            TextModels.Add(model);
        DefaultTextModel = preferred ?? ProviderDefaultTextModel;
    }
}

public sealed partial class UberLlmSettingsViewModel : ProviderSettingsViewModel
{
    public UberLlmSettingsViewModel(string baseUrl, string marketUrl, string apiKey)
    {
        _baseUrl = baseUrl;
        _marketUrl = marketUrl;
        _apiKey = apiKey;
    }

    public override string ProviderId => UberLlmProvider.Identifier;
    public override string EnvVar => "UBERLLM_API_KEY";

    [ObservableProperty]
    private string _baseUrl;

    [ObservableProperty]
    private string _marketUrl;

    [ObservableProperty]
    private string _apiKey;
}

public sealed partial class OpenRouterSettingsViewModel : ProviderSettingsViewModel
{
    public OpenRouterSettingsViewModel(string apiKey)
    {
        _apiKey = apiKey;
    }

    public override string ProviderId => OpenRouterProvider.Identifier;
    public override string EnvVar => "OPENROUTER_API_KEY";

    [ObservableProperty]
    private string _apiKey;
}

// ChatGPT: logowanie kontem ChatGPT zamiast klucza API (przycisk w widoku otwiera przeglądarkę); sesja trafia do zapisu z oknem
public sealed partial class ChatGptSettingsViewModel : ProviderSettingsViewModel
{
    private readonly Func<ChatGptSession?, CancellationToken, Task<ChatGptSession>>? _signIn;
    private CancellationTokenSource? _cancel;

    public ChatGptSettingsViewModel(string sessionJson, Func<ChatGptSession?, CancellationToken, Task<ChatGptSession>>? signIn)
    {
        _session = ChatGptSession.Parse(sessionJson);
        _signIn = signIn;
    }

    public override string ProviderId => ChatGptProvider.Identifier;
    public override string EnvVar => "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(SessionJson), nameof(IsSignedIn), nameof(CanSignIn), nameof(CanSignOut))]
    private ChatGptSession _session;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn), nameof(CanSignOut), nameof(IsNotBusy))]
    private bool _isBusy;

    [ObservableProperty]
    private string _error = "";

    public string SessionJson => Session.ToJson();
    public bool IsSignedIn => Session.IsSignedIn;
    public bool IsNotBusy => !IsBusy;
    // Zaloguj działa tylko, gdy nie jesteś zalogowany; Wyloguj tylko, gdy jesteś (i nie trwa logowanie)
    public bool CanSignIn => !IsBusy && !IsSignedIn && _signIn is not null;
    public bool CanSignOut => !IsBusy && IsSignedIn;
    public string Status => Session.IsSignedIn ? Loc.T("provider.chatgpt.signedIn", Session.Email ?? "—") : Loc.T("provider.chatgpt.signedOut");

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        Error = "";
        IsBusy = true;
        _cancel = new CancellationTokenSource();
        try
        {
            Session = await _signIn!(Session, _cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // anulowane przez użytkownika
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancel?.Cancel();

    // Wylogowanie zostawia rejestrację (client_id i identyfikator instalacji), kasuje tokeny
    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private void SignOut()
    {
        Error = "";
        Session = Session.SignedOut();
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnSessionChanged(ChatGptSession value) => NotifyCommands();

    private void NotifyCommands()
    {
        SignInCommand.NotifyCanExecuteChanged();
        SignOutCommand.NotifyCanExecuteChanged();
    }
}

// Dostawca skonfigurowany samym kluczem API (stały adres): jeden ViewModel i jeden widok dla Mistrala, Kilo i OpenCode Go
public sealed partial class KeyOnlySettingsViewModel(string providerId, string envVar, string apiKey, string noteKey)
    : ProviderSettingsViewModel
{
    public override string ProviderId => providerId;
    public override string EnvVar => envVar;

    // Opis pod polem klucza (klucz tłumaczenia rozwiązywany przy tworzeniu okna)
    public string Note => Loc.T(noteKey);

    [ObservableProperty]
    private string _apiKey = apiKey;
}

public sealed partial class OpenAiSettingsViewModel : ProviderSettingsViewModel
{
    public OpenAiSettingsViewModel(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl;
        _apiKey = apiKey;
    }

    public override string ProviderId => OpenAiProvider.Identifier;
    public override string EnvVar => "OPENAI_API_KEY";

    // Adres bazowy: oficjalny OpenAI albo dowolny kompatybilny endpoint (proxy, serwer lokalny)
    [ObservableProperty]
    private string _baseUrl;

    [ObservableProperty]
    private string _apiKey;
}
