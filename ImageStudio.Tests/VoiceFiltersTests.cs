using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Modele dźwięku mają te same filtry co reszta aplikacji (szukaj, dostawca, preferowane), a zakładki głównego okna nie mogą ich pomijać
public sealed class VoiceFiltersTests
{
    private static TestApp Loaded()
    {
        var provider = new FakeProvider();
        provider.SpeechCatalog.AddRange([new AudioModelInfo("google/tts-gemini", "Google"), new AudioModelInfo("venice/kokoro", "Venice AI"),
                                         new AudioModelInfo("x-ai/tts", "xAI")]);
        provider.TranscriptionCatalog.AddRange([new AudioModelInfo("venice/whisper", "Venice AI"), new AudioModelInfo("fal/wizper", "fal.ai")]);
        var app = TestApp.Create(provider: provider);
        app.Vm.InitializeAsync().GetAwaiter().GetResult();
        return app;
    }

    private static List<string> Speech(MainViewModel vm) => vm.Voice.SpeechModelsView.Cast<ModelOption>().Select(m => m.Id).ToList();
    private static List<string> Stt(MainViewModel vm) => vm.Voice.TranscriptionModelsView.Cast<ModelOption>().Select(m => m.Id).ToList();

    [Fact]
    public void Search_and_provider_filters_apply_to_each_voice_list()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();
            var vm = app.Vm;
            Assert.Equal(3, Speech(vm).Count);

            vm.SpeechFilter.SearchText = "kokoro";
            vm.TranscriptionFilter.SearchText = "kokoro";
            Assert.Equal(["venice/kokoro"], Speech(vm));
            Assert.Empty(Stt(vm));

            vm.SpeechFilter.SearchText = "";
            vm.TranscriptionFilter.SearchText = "";
            vm.SpeechFilter.SelectedProvider = "Venice AI";
            vm.TranscriptionFilter.SelectedProvider = "Venice AI";
            Assert.Equal(["venice/kokoro"], Speech(vm));
            Assert.Equal(["venice/whisper"], Stt(vm));
        });
    }

    [Fact]
    public void Providers_of_voice_models_are_offered_in_the_provider_filter()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();

            Assert.Contains("xAI", app.Vm.Providers);
            Assert.Contains("fal.ai", app.Vm.Providers);
        });
    }

    [Fact]
    public void The_image_filter_does_not_hide_audio_models()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();

            app.Vm.SpeechFilter.InputImageOnly = true;  // modele dźwięku nie mają wejścia obrazowego, ale to ich nie dotyczy

            Assert.Equal(3, Speech(app.Vm).Count);
            Assert.Equal(2, Stt(app.Vm).Count);
        });
    }

    [Fact]
    public void Preferred_only_keeps_the_starred_voice_models()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();
            app.Get<ModelPreferences>().For("fake").Preferred.Add("x-ai/tts");

            app.Vm.SpeechFilter.PreferredOnly = true;

            Assert.Equal(["x-ai/tts"], Speech(app.Vm));
        });
    }

    [Fact]
    public void A_filter_that_hides_the_chosen_model_does_not_change_the_choice()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();
            var voice = app.Vm.Voice;
            voice.SelectedSpeechModel = voice.SpeechModels.Single(m => m.Id == "google/tts-gemini");

            app.Vm.SpeechFilter.SearchText = "kokoro";

            Assert.Equal("google/tts-gemini", voice.SelectedSpeechModel!.Id);
        });
    }

    [Fact]
    public void Model_browser_lists_speech_and_transcription_models_with_their_kinds()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();

            ModelBrowserViewModel? browser = null;
            app.Get<FakeDialogs>().OnShowModelBrowser = b => browser = b;

            app.Vm.OpenModelBrowserCommand.Execute(null);

            Assert.NotNull(browser);
            Assert.Equal(["mowa"], browser!.Items.Single(i => i.Option.Id == "x-ai/tts").KindList);
            Assert.Equal(["transkrypcja"], browser.Items.Single(i => i.Option.Id == "fal/wizper").KindList);
            Assert.Contains("mowa", browser.Kinds);
            Assert.Contains("transkrypcja", browser.Kinds);
        });
    }

    // ---- widok ----

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
    public void Voice_pickers_have_the_filter_panel_without_the_image_option_and_it_drives_the_lists()
    {
        WpfHost.Run(() =>
        {
            using var app = Loaded();
            var vm = app.Vm;
            vm.ActiveTab = 4;
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            Flush(window.Dispatcher);

            var panel = FindAll<UserControl>(window).Single(c => c.GetType().Name == "VoicePanel");
            var pickers = FindAll<ModelPicker>(panel).ToList();
            Assert.Equal(2, pickers.Count);
            Assert.All(pickers, p =>
            {
                Assert.True(p.HasFilters);
                                Assert.False(p.ShowImageFilter);
            });
            Assert.Same(vm.SpeechFilter, pickers[0].FiltersContext);
            Assert.Same(vm.TranscriptionFilter, pickers[1].FiltersContext);
            Assert.Same(vm.Voice.SpeechModelsView, pickers[0].ItemsSource);
            Assert.Same(vm.Voice.TranscriptionModelsView, pickers[1].ItemsSource);

            var picker = pickers[0];
            FindAll<Expander>(picker).First().IsExpanded = true;
            Flush(window.Dispatcher);
            var toggle = FindAll<ToggleButton>(picker).First(t => t.Content as string == "Filtry modeli");
            toggle.IsChecked = true;
            Flush(window.Dispatcher);
            var image = FindAll<CheckBox>(picker).Single(c => c.Content as string == "Przyjmuje obraz");
            Assert.False(image.IsVisible);
            Assert.True(FindAll<RadioButton>(picker).Single(r => r.Content as string == "Ukryj").IsVisible);

            // szukanie w panelu filtrów zawęża listę modeli mowy
            var search = FindAll<TextBox>(picker).First(t => t.ToolTip as string == "Szukaj po nazwie modelu");
            search.Text = "kokoro";
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            System.Threading.Thread.Sleep(400);  // pole ma opóźnienie wiązania (Delay=250)
            Flush(window.Dispatcher);
            Assert.Equal("kokoro", vm.SpeechFilter.SearchText);
            Assert.Equal("", vm.TranscriptionFilter.SearchText);
            Assert.Equal(["venice/kokoro"], Speech(vm));
            window.Close();
        });
    }

    [Fact]
    public void Every_model_picker_in_the_main_window_tabs_has_filters_so_a_new_tab_cannot_skip_them()
    {
        WpfHost.Run(() =>
        {
            using var app = Loaded();
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            Flush(window.Dispatcher);

            var tabs = (TabControl)window.FindName("MainTabs");
            var seen = 0;
            for (var i = 0; i < tabs.Items.Count; i++)
            {
                app.Vm.ActiveTab = i;
                Flush(window.Dispatcher);
                foreach (var picker in FindAll<ModelPicker>(window))
                {
                    seen++;
                    Assert.True(picker.HasFilters, $"selektor „{picker.Header}” nie ma filtrów modeli");
                }
            }

            Assert.True(seen >= 6);  // generowanie, edycja, wideo, czat (2), głos (2) i ulepszanie
            window.Close();
        });
    }
}
