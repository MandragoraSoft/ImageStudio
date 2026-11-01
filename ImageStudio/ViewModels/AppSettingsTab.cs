using CommunityToolkit.Mvvm.ComponentModel;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

public sealed record ThemeOption(string Code, string Label);

// Zakładka "Aplikacja": ustawienia ogólne, niezależne od dostawcy
public sealed partial class AppSettingsTab : ObservableObject, ISettingsTab
{
    public string Title => "Aplikacja";
    public int Order => 0;

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemeService.System, "Systemowy"),
        new(ThemeService.Light, "Jasny"),
        new(ThemeService.Dark, "Ciemny"),
    ];

    [ObservableProperty]
    private int _maxSide;

    [ObservableProperty]
    private string _selectedTheme;

    public AppSettingsTab(SettingsResult current)
    {
        _maxSide = current.MaxSide;
        _selectedTheme = current.Theme;
    }

    public SettingsResult Apply(SettingsResult result) => result with { MaxSide = MaxSide, Theme = SelectedTheme };
}
