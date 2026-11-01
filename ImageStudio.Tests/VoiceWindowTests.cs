using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Zakładka Głos w prawdziwym oknie: widok zakładki, panel wyników zamiast panelu obrazu, przyciski wyniku
public sealed class VoiceWindowTests
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

    private static void Flush(Dispatcher d) => d.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    [Fact]
    public void Voice_tab_shows_its_panel_with_two_model_pickers_and_replaces_the_image_output_with_results()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var vm = app.Vm;
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            Flush(window.Dispatcher);

            var output = FindAll<OutputPanel>(window).Single();
            var results = FindAll<VoiceResults>(window).Single();
            Assert.True(output.IsVisible);
            Assert.False(results.IsVisible);

            vm.ActiveTab = 4;
            Flush(window.Dispatcher);

            Assert.False(output.IsVisible);
            Assert.True(results.IsVisible);
            Assert.Same(vm.Voice, results.DataContext);
            var panel = FindAll<UserControl>(window).Single(c => c.GetType().Name == "VoicePanel");
            Assert.Same(vm.Voice, panel.DataContext);
            Assert.Equal(2, FindAll<ModelPicker>(panel).Count());

            // listy mikrofonu i wyjścia pokazują nazwy urządzeń, nie zapis rekordu
            foreach (var expander in FindAll<Expander>(panel)) expander.IsExpanded = true;
            Flush(window.Dispatcher);
            var texts = FindAll<TextBlock>(panel).Select(t => t.Text).ToList();
            Assert.Contains("Mikrofon testowy", texts);
            Assert.Contains("Wyjście dźwięku", texts);
            Assert.Contains("Słuchawki", FindAll<ComboBox>(panel).SelectMany(c => c.Items.OfType<AudioDevice>()).Select(d => d.Name));  // lista wyjść w sekcji mowy
            var speech = FindAll<Expander>(panel).First(e => e.Header as string == "Mowa z tekstu");
            Assert.Contains("Wyjście dźwięku", FindAll<TextBlock>(speech).Select(t => t.Text));  // wyjście należy do sekcji mowy z tekstu
            Assert.Contains("Jak w tekście (bez tłumaczenia)", texts);  // pole języka mowy
            Assert.DoesNotContain(texts, t => t.Contains("AudioDevice {"));

            vm.ActiveTab = 0;
            Flush(window.Dispatcher);
            Assert.True(output.IsVisible);
            Assert.False(results.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void Result_card_offers_play_save_copy_and_remove_and_the_empty_hint_disappears()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var vm = app.Vm;
            vm.ActiveTab = 4;
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            Flush(window.Dispatcher);

            var results = FindAll<VoiceResults>(window).Single();
            var hint = (TextBlock)results.FindName("EmptyHint");
            Assert.True(hint.IsVisible);

            var file = Path.Combine(Path.GetTempPath(), "is-card-" + Guid.NewGuid().ToString("N") + ".mp3");
            File.WriteAllBytes(file, [1]);
            try
            {
                vm.Voice.Items.Add(new VoiceItem(true, "t", "tekst wyniku", file, "tts/a", 0.01m));
                Flush(window.Dispatcher);

                Assert.False(hint.IsVisible);
                var buttons = FindAll<Button>((ListBox)results.FindName("ResultList")).Where(b => b.IsVisible).Select(b => b.Content as string).ToList();
                Assert.Equal(["▶ Odtwórz", "Otwórz w odtwarzaczu", "Zapisz jako...", "Kopiuj tekst", "Do syntezy", "Usuń"], buttons);
                Assert.Contains(FindAll<TextBox>(results), t => t.Text == "tekst wyniku");
            }
            finally
            {
                File.Delete(file);
            }

            window.Close();
        });
    }
}
