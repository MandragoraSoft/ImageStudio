using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Services;

namespace ImageStudio.Tests;

// Kontrolki interaktywne muszą być widoczne w obu motywach (kontrast wg WCAG): wypełnienie odróżnia je od panelu,
// obrys ma kontrast co najmniej 3:1, a tekst 4,5:1
public sealed class ThemeContrastTests
{
    private static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static Color Brush(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void Control_colors_stand_out_from_the_panels_they_sit_on(string theme)
    {
        WpfHost.Run(() =>
        {
            ThemeService.Apply(theme);

            foreach (var panel in new[] { "SurfaceBrush", "SurfaceAltBrush", "WindowBgBrush" })
            {
                Assert.True(Contrast(Brush("ControlBorderBrush"), Brush(panel)) >= 3.0,
                            $"{theme}: obrys kontrolki vs {panel} = {Contrast(Brush("ControlBorderBrush"), Brush(panel)):0.00}");
            }

            Assert.True(Contrast(Brush("ControlBrush"), Brush("SurfaceBrush")) >= 1.2,
                        $"{theme}: wypełnienie vs panel = {Contrast(Brush("ControlBrush"), Brush("SurfaceBrush")):0.00}");
            Assert.True(Contrast(Brush("TextPrimaryBrush"), Brush("ControlBrush")) >= 4.5);
            Assert.True(Contrast(Brush("TextPrimaryBrush"), Brush("ControlHoverBrush")) >= 4.5);
            Assert.True(Contrast(Brush("TextPrimaryBrush"), Brush("AccentSoftBrush")) >= 4.5);  // włączony przełącznik filtrów
            Assert.True(Contrast(Brush("AccentBrush"), Brush("SurfaceBrush")) >= 3.0);           // obrys na hover / po włączeniu
        });
    }

    [Fact]
    public void Secondary_button_and_filter_toggle_are_drawn_with_the_control_colors_in_dark_theme()
    {
        WpfHost.Run(() =>
        {
            ThemeService.Apply("dark");
            var button = new Button { Content = "Anuluj", Style = (Style)Application.Current.Resources["SecondaryButton"] };
            var toggle = new ToggleButton { Content = "Filtry modeli", Style = (Style)Application.Current.Resources["FilterToggleButton"], Margin = new Thickness(0, 8, 0, 0) };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(button);
            panel.Children.Add(toggle);
            var window = new Window { Content = new Border { Background = (Brush)Application.Current.Resources["SurfaceBrush"], Child = panel }, ShowInTaskbar = false };
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            foreach (Control control in new Control[] { button, toggle })
            {
                var face = FindAll<Border>(control).First();
                Assert.Equal(Brush("ControlBrush"), ((SolidColorBrush)face.Background).Color);
                Assert.Equal(Brush("ControlBorderBrush"), ((SolidColorBrush)face.BorderBrush).Color);
                Assert.NotEqual(Brush("SurfaceBrush"), ((SolidColorBrush)face.Background).Color);
            }
            window.Close();
        });
    }
}
