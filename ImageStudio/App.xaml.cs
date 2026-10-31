using System.Windows;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = AppSettings.Load();
        var service = new ImageGenerationService(settings.BaseUrl, settings.ApiKey);
        var market = new MarketService();
        var viewModel = new MainViewModel(service, market, new WpfDialogService());
        var window = new MainWindow { DataContext = viewModel };
        window.Loaded += async (_, _) => await viewModel.InitializeAsync();
        window.Show();
    }
}
