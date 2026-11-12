using System.Windows;
using ImageStudio.Services;

namespace ImageStudio.Views;

public sealed class WpfApplicationLifetime : IApplicationLifetime
{
    public void Exit()
    {
        var application = Application.Current;
        var window = application?.Windows.OfType<MainWindow>().FirstOrDefault() ?? application?.MainWindow as MainWindow;
        try
        {
            window?.ExitApplication();
        }
        finally
        {
            application?.Shutdown();
        }
    }
}
