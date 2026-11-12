using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.Localization;

namespace ImageStudio.ViewModels;

public sealed record ThemeOption(string Code, string Label);

public sealed record PromptChatModeOption(PromptChatHistoryMode Mode, string Label);

// Zakładka "Aplikacja": ustawienia ogólne, niezależne od dostawcy
public sealed partial class AppSettingsTab : ObservableObject, ISettingsTab
{
    private readonly Func<UserDataCleanupSelection, Task>? _clearUserData;

    public string Title => Loc.T("settings.tab.app");
    public int Order => 0;

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemeService.System, Loc.T("settings.theme.system")),
        new(ThemeService.Light, Loc.T("settings.theme.light")),
        new(ThemeService.Dark, Loc.T("settings.theme.dark")),
    ];

    // Język interfejsu: "system" (polski w systemie polskim, inaczej angielski), "pl", "en"; zmiana działa od razu po zapisie
    public IReadOnlyList<ThemeOption> Languages { get; } =
    [
        new(Loc.System, Loc.T("settings.language.system")),
        new(Loc.Polish, Loc.T("settings.language.pl")),
        new(Loc.English, Loc.T("settings.language.en")),
    ];

    [ObservableProperty]
    private string _selectedLanguage;

    [ObservableProperty]
    private int _maxSide;

    [ObservableProperty]
    private string _selectedTheme;

    // Odpowiedzi czatu napływają na bieżąco (SSE) zamiast po zakończeniu generowania
    [ObservableProperty]
    private bool _useStreaming;

    [ObservableProperty]
    private bool _hideToTrayOnClose;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectedDataCommand))]
    private bool _clearSettingsAndPrompts = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectedDataCommand))]
    private bool _clearChatHistoryAndMemory = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectedDataCommand))]
    private bool _clearPresets = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectedDataCommand))]
    private bool _clearGalleryIndexes = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectedDataCommand))]
    private bool _clearMcpConfiguration = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectedDataCommand))]
    private bool _clearWindowLayouts = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectedDataCommand))]
    private bool _clearProviderLoginsAndKeys;

    [ObservableProperty]
    private string _galleryArchiveFolder;

    // Gdzie zapamiętywana jest rozmowa z edytora promptu (Generowanie, Edycja, Wideo)
    public IReadOnlyList<PromptChatModeOption> PromptChatModes { get; } =
    [
        new(PromptChatHistoryMode.Disk, Loc.T("settings.promptChat.disk")),
        new(PromptChatHistoryMode.UntilClose, Loc.T("settings.promptChat.untilClose")),
        new(PromptChatHistoryMode.Off, Loc.T("settings.promptChat.off")),
    ];

    [ObservableProperty]
    private PromptChatHistoryMode _selectedPromptChatHistory;

    public AppSettingsTab(SettingsResult current, Func<UserDataCleanupSelection, Task>? clearUserData = null)
    {
        _clearUserData = clearUserData;
        _maxSide = current.MaxSide;
        _selectedTheme = current.Theme;
        _useStreaming = current.UseStreaming;
        _hideToTrayOnClose = current.HideToTrayOnClose;
        _selectedLanguage = current.Language;
        _selectedPromptChatHistory = current.PromptChatHistory;
        _galleryArchiveFolder = string.IsNullOrWhiteSpace(current.GalleryArchiveFolder)
            ? UserState.DefaultGalleryArchiveFolder
            : current.GalleryArchiveFolder;
    }

    private bool HasSelectedDataToClear => _clearUserData is not null &&
        (ClearSettingsAndPrompts || ClearChatHistoryAndMemory || ClearPresets || ClearGalleryIndexes ||
         ClearMcpConfiguration || ClearWindowLayouts || ClearProviderLoginsAndKeys);

    [RelayCommand(CanExecute = nameof(HasSelectedDataToClear))]
    private Task ClearSelectedDataAsync() => _clearUserData?.Invoke(new UserDataCleanupSelection(
        ClearSettingsAndPrompts, ClearChatHistoryAndMemory, ClearPresets, ClearGalleryIndexes,
        ClearMcpConfiguration, ClearWindowLayouts, ClearProviderLoginsAndKeys)) ?? Task.CompletedTask;

    public SettingsResult Apply(SettingsResult result) => result with
    {
        MaxSide = MaxSide,
        Theme = SelectedTheme,
        UseStreaming = UseStreaming,
        HideToTrayOnClose = HideToTrayOnClose,
        Language = SelectedLanguage,
        GalleryArchiveFolder = GalleryArchiveFolder,
        PromptChatHistory = SelectedPromptChatHistory,
    };
}
