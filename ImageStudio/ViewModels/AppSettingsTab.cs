using CommunityToolkit.Mvvm.ComponentModel;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.Localization;

namespace ImageStudio.ViewModels;

public sealed record ThemeOption(string Code, string Label);

// Zakładka "Aplikacja": ustawienia ogólne, niezależne od dostawcy
public sealed partial class AppSettingsTab : ObservableObject, ISettingsTab
{
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
    private string _galleryArchiveFolder;

    public AppSettingsTab(SettingsResult current)
    {
        _maxSide = current.MaxSide;
        _selectedTheme = current.Theme;
        _useStreaming = current.UseStreaming;
        _selectedLanguage = current.Language;
        _galleryArchiveFolder = string.IsNullOrWhiteSpace(current.GalleryArchiveFolder)
            ? UserState.DefaultGalleryArchiveFolder
            : current.GalleryArchiveFolder;
    }

    public SettingsResult Apply(SettingsResult result) => result with
    {
        MaxSide = MaxSide,
        Theme = SelectedTheme,
        UseStreaming = UseStreaming,
        Language = SelectedLanguage,
        GalleryArchiveFolder = GalleryArchiveFolder,
    };
}
