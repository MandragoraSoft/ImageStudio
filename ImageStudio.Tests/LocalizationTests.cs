using ImageStudio.Storage;
using ImageStudio.Storage.Local;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Configuration;
using ImageStudio.Localization;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

// Tłumaczenia PL/EN: słowniki są spójne, każdy użyty klucz istnieje, a zmiana języka odświeża okno w trakcie pracy
public sealed class LocalizationTests
{
    // Język jest stanem globalnym: test ustawia go tylko na czas using i zawsze wraca do polskiego (reszta testów zakłada polski)
    private sealed class UseLanguage : IDisposable
    {
        public UseLanguage(string language) => Loc.Set(language);
        public void Dispose() => Loc.Set(Loc.Polish);
    }

    private static string RepoRoot([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(RepoRoot(), pattern, SearchOption.AllDirectories)
                 .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                             && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                             && !f.Contains("ImageStudio.Tests"));

    // ---- słowniki ----

    [Fact]
    public void Both_dictionaries_have_exactly_the_same_keys_and_no_empty_texts()
    {
        var pl = Loc.Dictionary(Loc.Polish);
        var en = Loc.Dictionary(Loc.English);

        Assert.Empty(pl.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(pl.Keys));
        Assert.All(pl.Concat(en), pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"pusty tekst: {pair.Key}"));
    }

    [Fact]
    public void Placeholders_match_between_languages_so_formatting_never_breaks()
    {
        static IEnumerable<string> Placeholders(string text) =>
            Regex.Matches(text, @"\{(\d+)(?::[^}]*)?\}").Select(m => m.Groups[1].Value).Distinct().Order();

        foreach (var (key, pl) in Loc.Dictionary(Loc.Polish))
            Assert.True(Placeholders(pl).SequenceEqual(Placeholders(Loc.Dictionary(Loc.English)[key])), $"inne parametry w kluczu {key}");
    }

    [Fact]
    public void Every_key_used_in_xaml_or_code_exists_and_no_dictionary_key_is_unused()
    {
        var keys = Loc.Dictionary(Loc.Polish).Keys.ToHashSet();
        var used = new HashSet<string>();

        foreach (var file in SourceFiles("*.xaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{loc:T ([A-Za-z0-9.]+)\}"))
            {
                Assert.True(keys.Contains(m.Groups[1].Value), $"brak klucza {m.Groups[1].Value} ({Path.GetFileName(file)})");
                used.Add(m.Groups[1].Value);
            }

        // NullToLoc: klucz w ConverterParameter zamiast TargetNullValue
        foreach (var file in SourceFiles("*.xaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"NullToLoc\.Instance\}, ConverterParameter=([A-Za-z0-9.]+)"))
            {
                Assert.True(keys.Contains(m.Groups[1].Value), $"brak klucza {m.Groups[1].Value} ({Path.GetFileName(file)})");
                used.Add(m.Groups[1].Value);
            }

        foreach (var file in SourceFiles("*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"([a-z][A-Za-z0-9]*(?:\\.[A-Za-z0-9]+)+)\""))
                if (keys.Contains(m.Groups[1].Value)) used.Add(m.Groups[1].Value);
            // klucze składane w kodzie ("lang.{Code}") i wywołania Loc.T("...") sprawdza kolejna pętla

        foreach (var file in SourceFiles("*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "Loc\\.T\\(\"([^\"]+)\""))
                Assert.True(keys.Contains(m.Groups[1].Value), $"brak klucza {m.Groups[1].Value} ({Path.GetFileName(file)})");

        var unused = keys.Where(key => !used.Contains(key) && !key.StartsWith("lang.")).Order().ToList();
        Assert.True(unused.Count == 0, "nieużywane klucze: " + string.Join(", ", unused));
    }

    // ---- wybór języka ----

    [Fact]
    public void System_setting_follows_the_ui_language_of_windows_polish_for_polish_and_english_for_everything_else()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("pl-PL");
            Assert.Equal("pl", Loc.Resolve(Loc.System));
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            Assert.Equal("en", Loc.Resolve(Loc.System));
            Assert.Equal("pl", Loc.Resolve("pl"));   // wybór wprost wygrywa z językiem systemu
            Assert.Equal("en", Loc.Resolve("en"));
            Assert.Equal("en", Loc.Resolve("nonsense") == "pl" ? "pl" : "en");  // nieznana wartość = jak "system"
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Texts_follow_the_language_and_an_unknown_key_shows_the_key_itself()
    {
        Assert.Equal("Zapisz", Loc.T("common.save"));
        using (new UseLanguage(Loc.English))
        {
            Assert.Equal("Save", Loc.T("common.save"));
            Assert.Equal("Recording saved: x.mp3", Loc.T("voice.status.saved", "x.mp3"));
            Assert.Equal("brak.takiego.klucza", Loc.T("brak.takiego.klucza"));
        }
        Assert.Equal("Zapisz", Loc.T("common.save"));
    }

    [Fact]
    public void Changing_the_language_raises_the_event_once_and_setting_the_same_language_does_not()
    {
        var raised = 0;
        void Handler() => raised++;
        Loc.LanguageChanged += Handler;
        try
        {
            Loc.Set(Loc.Polish);
            Assert.Equal(0, raised);
            using (new UseLanguage(Loc.English)) Assert.Equal(1, raised);
            Assert.Equal(2, raised);  // powrót do polskiego
        }
        finally
        {
            Loc.LanguageChanged -= Handler;
            Loc.Set(Loc.Polish);
        }
    }

    // ---- teksty z kodu ----

    [Fact]
    public void Model_cost_tooltip_is_translated()
    {
        var model = new ModelOption("m", "o", false, ["text"], new ModelPricing(1.5m, 2m, 10m, ModelPricing.TokenUnit, null, null, OutputPrice: 3m));

        Assert.Contains("Wejście:", model.CostText);
        using (new UseLanguage(Loc.English))
        {
            Assert.Contains("Input:", model.CostText);
            Assert.Contains("per 1M tokens", model.CostText);
            Assert.DoesNotContain("Wejście", model.CostText);
            Assert.Contains("No price in the catalog", new ModelOption("m", "o", false, ["text"], null).CostText);
        }
    }

    [Fact]
    public void Voice_view_model_messages_and_labels_follow_the_language()
    {
        var provider = new FakeProvider();
        provider.SpeechCatalog.Add(new AudioModelInfo("tts/a", "A", new ModelPricing(0.1m, null, null, "1K characters", null, null)));
        using var app = TestApp.Create(provider: provider);
        app.Vm.InitializeAsync().GetAwaiter().GetResult();
        var voice = app.Vm.Voice;
        var dir = Path.Combine(Path.GetTempPath(), "is-loc-" + Guid.NewGuid().ToString("N"));
        voice.OutputDir = dir;
        app.Get<LocalMediaStorage>().Roots[MediaKind.Audio] = dir;
        provider.OnSynthesize = (_, _, _, _, _) => new AudioResult([1], "mp3", null);
        try
        {
            using (new UseLanguage(Loc.English))
            {
                voice.SpeechText = "abc";
                Assert.StartsWith("Estimate:", voice.SpeechEstimateLabel);
                Assert.Equal("As in the text (no translation)", voice.Languages[0].Name);
                Assert.Equal("Polish", voice.Languages.Single(l => l.Code == "pl").Name);
                Assert.StartsWith("Voice gallery: 0", voice.GalleryLabel);
                Assert.Equal("● Record from microphone", voice.RecordButtonText);

                voice.SynthesizeCommand.ExecuteAsync(null).GetAwaiter().GetResult();

                Assert.StartsWith("Recording saved:", app.Vm.Status);
                Assert.Equal("Speech", voice.Items.Single().Kind);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void System_default_device_name_is_translated()
    {
        Assert.Equal("Domyślne urządzenie systemu", new NAudioPlayer().OutputDevices()[0].Name);
        using (new UseLanguage(Loc.English)) Assert.Equal("System default device", new NAudioPlayer().OutputDevices()[0].Name);
    }

    // ---- ustawienie języka ----

    [Fact]
    public void Language_setting_is_offered_in_the_settings_tab_and_goes_into_the_result()
    {
        var current = new SettingsResult("u", "m", 1536, "k", "dark", Language: "en");
        var tab = new AppSettingsTab(current);

        Assert.Equal(["system", "pl", "en"], tab.Languages.Select(l => l.Code));
        Assert.Equal("en", tab.SelectedLanguage);

        tab.SelectedLanguage = "pl";
        Assert.Equal("pl", tab.Apply(current).Language);
    }

    [Fact]
    public async Task Saving_settings_applies_the_language_at_once_and_stores_it()
    {
        using var app = TestApp.Create();
        app.Get<FakeDialogs>().SettingsAnswer = r => r with { Language = "en" };
        try
        {
            await app.Vm.OpenSettingsCommand.ExecuteAsync(null);

            Assert.Equal("en", Loc.Language);
            Assert.Equal("en", app.Get<ISettingsStore>().Load().Language);
        }
        finally
        {
            Loc.Set(Loc.Polish);
        }
    }

    [Fact]
    public void Language_is_saved_in_the_user_settings_file_and_read_back()
    {
        AppSettings.Save("https://a", "https://m", 1536, "", "system", language: "en");
        Assert.Equal("en", AppSettings.Load().Language);

        AppSettings.Save("https://a", "https://m", 1536, "", "system", language: "system");
        Assert.Equal("system", AppSettings.Load().Language);
    }

    // ---- okno ----

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
    public void Switching_the_language_translates_the_open_main_window_menu_tabs_footer_and_voice_panel()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            Flush(window.Dispatcher);
            var tabs = (TabControl)window.FindName("MainTabs");
            var menu = FindAll<Menu>(window).Single();
            Assert.Equal("_Plik", ((MenuItem)menu.Items[0]).Header);
            Assert.Equal(["Generowanie", "Edycja", "Wideo", "Czat", "Głos"], tabs.Items.Cast<TabItem>().Select(t => (string)t.Header));
            Assert.Contains("Dostawca: ", FindAll<TextBlock>(window).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>()).Select(r => r.Text));

            try
            {
                Loc.Set(Loc.English);
                Flush(window.Dispatcher);

                Assert.Equal("_File", ((MenuItem)menu.Items[0]).Header);
                Assert.Equal("_Models", ((MenuItem)menu.Items[2]).Header);
                Assert.Equal(["Generate", "Edit", "Video", "Chat", "Voice"], tabs.Items.Cast<TabItem>().Select(t => (string)t.Header));
                Assert.Contains("Provider: ", FindAll<TextBlock>(window).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>()).Select(r => r.Text));

                app.Vm.ActiveTab = 4;
                Flush(window.Dispatcher);
                var panel = FindAll<UserControl>(window).Single(c => c.GetType().Name == "VoicePanel");
                var texts = FindAll<TextBlock>(panel).Select(t => t.Text).Concat(FindAll<Button>(panel).Select(b => b.Content as string ?? "")).ToList();
                Assert.Contains("Generate speech", texts);
                Assert.Contains("Recognize text", texts);
                Assert.Contains("Voice", texts);
                Assert.DoesNotContain("Generuj mowę", texts);

                Loc.Set(Loc.Polish);
                Flush(window.Dispatcher);
                Assert.Equal("_Plik", ((MenuItem)menu.Items[0]).Header);
                Assert.Contains("Generuj mowę", FindAll<Button>(panel).Select(b => b.Content as string));
            }
            finally
            {
                Loc.Set(Loc.Polish);
            }

            window.Close();
        });
    }

    // Widoczne teksty (treść, nagłówki, dymki) bez polskich znaków; łapie ekran, któremu zostały teksty na sztywno
    private static IEnumerable<string> VisibleTexts(DependencyObject root)
    {
        foreach (var element in FindAll<FrameworkElement>(root).Append((FrameworkElement)root))
        {
            if (element is TextBlock t) yield return t.Text;
            if (element is ContentControl { Content: string c }) yield return c;
            if (element is HeaderedContentControl { Header: string h }) yield return h;
            if (element is MenuItem { Header: string m }) yield return m;
            if (element.ToolTip is string tip) yield return tip;
        }
    }

    [Fact]
    public void Every_tab_and_the_result_panel_of_the_main_window_have_no_polish_text_in_english()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            Loc.Set(Loc.English);
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            try
            {
                var polish = new Regex("[ąćęłńóśźżĄĆĘŁŃÓŚŹŻ]");
                for (var tab = 0; tab < 5; tab++)
                {
                    app.Vm.ActiveTab = tab;
                    Flush(window.Dispatcher);
                    var left = VisibleTexts(window).Where(s => polish.IsMatch(s) && s != "Domyślne").Distinct().ToList();  // "Domyślne": nazwa urządzenia z atrapy odtwarzacza
                    Assert.True(left.Count == 0, $"zakładka {tab}: polskie teksty w trybie angielskim: " + string.Join(" | ", left));
                }
            }
            finally
            {
                Loc.Set(Loc.Polish);
            }
            window.Close();
        });
    }

    [Fact]
    public void Settings_window_is_translated_when_opened_in_english()
    {
        WpfHost.Run(() =>
        {
            try
            {
                Loc.Set(Loc.English);
                var current = new SettingsResult("u", "m", 1536, "k", "dark");
                var vm = new SettingsViewModel(current, [new AppSettingsTab(current), new AudioSettingsTab(current, [], [])]);
                var window = new ImageStudio.Views.SettingsWindow { DataContext = vm, ShowInTaskbar = false };
                window.EnsureTabViews();
                window.Show();
                Flush(window.Dispatcher);

                Assert.Equal("Settings", window.Title);
                Assert.Equal(["Application", "Audio"], vm.Tabs.Select(t => t.Title));
                var buttons = FindAll<Button>(window).Select(b => b.Content as string).ToList();
                Assert.Contains("Cancel", buttons);
                Assert.Contains("Save", buttons);
                Assert.Contains("Interface language", FindAll<TextBlock>(window).Select(t => t.Text));
                window.Close();
            }
            finally
            {
                Loc.Set(Loc.Polish);
            }
        });
    }
}
