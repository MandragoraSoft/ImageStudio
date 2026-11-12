using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Kontrolka trójstanowa: testy przez prawdziwe okno WPF (wątek UI)
public sealed class TriStateToggleTests
{
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    // Segmenty w kolejności od lewej: tylko, wszystkie, ukryj
    private static List<RadioButton> Segments(TriStateToggle toggle) => FindAll<RadioButton>(toggle).ToList();

    // Klik myszą (i spacja) wywołuje chronioną OnClick; samo podniesienie zdarzenia Click nie zaznacza RadioButton
    private static void Click(RadioButton segment) =>
        typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(segment, null);

    private static Window Show(UIElement content)
    {
        var window = new Window { Width = 420, Height = 120, ShowInTaskbar = false, Content = content };
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        return window;
    }

    [Fact]
    public void Toggle_defaults_to_any()
    {
        WpfHost.Run(() =>
        {
            var toggle = new TriStateToggle();
            var window = Show(toggle);

            Assert.Equal(TriState.Any, toggle.Value);
            var segments = Segments(toggle);
            Assert.Equal([false, true, false], segments.Select(s => s.IsChecked == true));
            window.Close();
        });
    }

    [Fact]
    public void Clicking_segment_updates_value()
    {
        WpfHost.Run(() =>
        {
            var toggle = new TriStateToggle();
            var window = Show(toggle);
            var segments = Segments(toggle);

            Click(segments[0]);
            Assert.Equal(TriState.Only, toggle.Value);

            Click(segments[2]);
            Assert.Equal(TriState.Exclude, toggle.Value);
            window.Close();
        });
    }

    [Fact]
    public void Setting_value_updates_segments()
    {
        WpfHost.Run(() =>
        {
            var toggle = new TriStateToggle();
            var window = Show(toggle);

            toggle.Value = TriState.Exclude;

            Assert.Equal([false, false, true], Segments(toggle).Select(s => s.IsChecked == true));
            window.Close();
        });
    }

    [Fact]
    public void Texts_come_from_properties()
    {
        WpfHost.Run(() =>
        {
            var toggle = new TriStateToggle { OnlyText = "Tylko", AnyText = "Wszystkie", ExcludeText = "Ukryj" };
            var window = Show(toggle);

            Assert.Equal(["Tylko", "Wszystkie", "Ukryj"], Segments(toggle).Select(s => (string)s.Content));
            window.Close();
        });
    }
}
