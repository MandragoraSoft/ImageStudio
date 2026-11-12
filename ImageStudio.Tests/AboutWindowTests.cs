using System.Reflection;
using System.Reflection.Emit;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Localization;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

public sealed class AboutWindowTests
{
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    [Fact]
    public void Window_uses_application_version_and_updates_text_when_language_changes()
    {
        WpfHost.Run(() =>
        {
            var window = new AboutWindow { DataContext = new AboutViewModel(), ShowInTaskbar = false };
            try
            {
                Loc.Set("pl");
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var version = typeof(ImageStudio.App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
                Assert.Same(Application.Current.FindResource("WindowBase"), window.Style);
                Assert.Equal("O aplikacji", window.Title);
                Assert.Contains(FindAll<TextBlock>(window), t => t.Text == "Wersja: " + version);
                Assert.Contains(FindAll<TextBlock>(window), t => t.Text == Loc.T("about.description"));

                Loc.Set("en");
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal("About", window.Title);
                Assert.Contains(FindAll<TextBlock>(window), t => t.Text == "Version: " + version);
                Assert.Contains(FindAll<TextBlock>(window), t => t.Text == Loc.T("about.description"));
                Assert.Contains(FindAll<Button>(window), b => Equals(b.Content, "Close"));
            }
            finally
            {
                window.Close();
                Loc.Set("pl");
            }
        });
    }

    [Fact]
    public void Version_preserves_prerelease_and_build_metadata()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("AboutVersionTest"), AssemblyBuilderAccess.Run);
        var ctor = typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(ctor, ["2.3.4-beta.1+build.7"]));

        Assert.Equal("2.3.4-beta.1+build.7", AboutViewModel.ReadVersion(assembly));
    }
}
