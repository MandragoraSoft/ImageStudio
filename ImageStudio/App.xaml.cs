using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    // Biblioteki dostawców leżą w podfolderze providers\ (patrz ImageStudio.csproj); domyślny kontekst ich nie widzi sam
    static App()
    {
        var providersDir = Path.Combine(AppContext.BaseDirectory, "providers");
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(providersDir, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }

    // Zapis wyborów modeli i parametrów przy zamykaniu
    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Shutdown();
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
