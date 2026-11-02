using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using ImageStudio.Composition;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;
using Microsoft.Extensions.DependencyInjection;
using ImageStudio.Localization;

namespace ImageStudio;

public partial class App : Application
{
    private IServiceProvider? _services;

    // Biblioteki dostawców, zakładek i grup narzędzi czatu leżą w podfolderach providers\, tabs\, tools\ i storage\ (patrz ImageStudio.csproj);
    // domyślny kontekst ich nie widzi sam
    static App()
    {
        string[] pluginDirs = [Path.Combine(AppContext.BaseDirectory, "providers"), Path.Combine(AppContext.BaseDirectory, "tabs"),
                               Path.Combine(AppContext.BaseDirectory, "tools"), Path.Combine(AppContext.BaseDirectory, "storage")];
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var dir in pluginDirs)
            {
                var path = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
    }

    // Zapis wyborów modeli i parametrów przy zamykaniu. Kontener nie jest zwalniany jawnie: proces się kończy,
    // a McpToolHost zamyka MainViewModel.Shutdown (kontener zgłasza błąd przy synchronicznym Dispose usług IAsyncDisposable)
    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<MainViewModel>()?.Shutdown();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DotEnv.LoadDefaults();

        var services = new ServiceCollection().AddImageStudioCore().AddImageStudioInfrastructure()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        _services = services;

        Loc.Set(services.GetRequiredService<AppSettings>().Language);
        WindowMemory.Store = services.GetRequiredService<IWindowStateStore>();
        services.GetRequiredService<IThemeService>().Apply(services.GetRequiredService<AppSettings>().Theme);

        var window = services.GetRequiredService<MainWindow>();
        var viewModel = services.GetRequiredService<MainViewModel>();
        window.Loaded += async (_, _) => await viewModel.InitializeAsync();
        window.Show();
    }
}
