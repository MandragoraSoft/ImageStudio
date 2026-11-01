using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

public sealed class ThemeTests
{
    [Fact]
    public void Tab_labels_follow_the_theme_and_title_bars_update_without_errors()
    {
        WpfHost.Run(() =>
        {
            ThemeService.Apply(ThemeService.Dark);
            var current = new SettingsResult("u", "m", 1536, "", "system");
            var window = new SettingsWindow
            {
                DataContext = new SettingsViewModel(current, [new AppSettingsTab(current)]),
                ShowInTaskbar = false,
            };
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var label = FindAll<TextBlock>(window).First(t => t.Text.StartsWith("Maksymalny"));
            Assert.Equal(Color.FromRgb(0xE6, 0xE7, 0xEB), ((SolidColorBrush)label.Foreground).Color);

            ThemeService.Apply(ThemeService.Light);  // otwarte okno dostaje też pasek tytułu w nowym motywie
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(Color.FromRgb(0x1F, 0x24, 0x30), ((SolidColorBrush)label.Foreground).Color);
            window.Close();
        });
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }
}
