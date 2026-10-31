using System.Windows;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    // Zapis wyborów modeli i parametrów przy zamykaniu
    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.SaveState();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = AppSettings.Load();
        ThemeService.Apply(settings.Theme);
        var provider = ProviderRegistry.Create(settings.ProviderId, settings);
        var downloads = new ImageDownloadService();
        var viewModel = new MainViewModel(provider, new WpfDialogService(), downloads, settings);
        _viewModel = viewModel;
        var window = new MainWindow { DataContext = viewModel };
        window.Loaded += async (_, _) => await viewModel.InitializeAsync();
        window.Show();
    }
}
