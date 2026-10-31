using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

public sealed record ThemeOption(string Code, string Label);

public sealed partial class SettingsViewModel : ObservableObject
{
    public static IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemeService.System, "Systemowy"),
        new(ThemeService.Light, "Jasny"),
        new(ThemeService.Dark, "Ciemny"),
    ];

    [ObservableProperty]
    private string _baseUrl;

    [ObservableProperty]
    private string _marketUrl;

    [ObservableProperty]
    private int _maxSide;

    [ObservableProperty]
    private string _apiKey;

    [ObservableProperty]
    private string _selectedTheme;

    public SettingsViewModel(SettingsResult current)
    {
        _baseUrl = current.BaseUrl;
        _marketUrl = current.MarketUrl;
        _maxSide = current.MaxSide;
        _apiKey = current.ApiKey;
        _selectedTheme = current.Theme;
    }
}
