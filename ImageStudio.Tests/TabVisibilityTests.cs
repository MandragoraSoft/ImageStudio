using ImageStudio.Services;

namespace ImageStudio.Tests;

// Zakładki bez żadnego pasującego modelu są ukrywane po załadowaniu katalogu dostawcy
public sealed class TabVisibilityTests
{
    private static ImageModelInfo Image(string id) => new(id, "o", false, ["text"]);

    [Fact]
    public void Tabs_without_models_are_hidden_and_the_active_one_moves_to_a_visible_tab()
    {
        Sta.Run(() =>
        {
            var provider = new FakeProvider
            {
                ImageCatalog = new ImageModels([Image("gen")], []),  // bez modeli edycji
                VideoCatalog = [],
                ChatCatalog = [new ChatModelInfo("c", [])],
            };
            using var app = TestApp.Create(provider: provider);
            app.Vm.ActiveTab = 2;  // Wideo

            app.Vm.InitializeAsync().GetAwaiter().GetResult();

            Assert.Equal([true, false, false, true, false], app.Vm.TabVisibility);
            Assert.Equal(0, app.Vm.ActiveTab);
        });
    }

    [Fact]
    public void Voice_tab_is_visible_with_either_speech_or_transcription_models()
    {
        Sta.Run(() =>
        {
            var provider = new FakeProvider { ImageCatalog = new ImageModels([], []), VideoCatalog = [], ChatCatalog = [] };
            provider.TranscriptionCatalog.Add(new AudioModelInfo("fal/wizper", "fal.ai"));
            using var app = TestApp.Create(provider: provider);

            app.Vm.InitializeAsync().GetAwaiter().GetResult();

            Assert.Equal([false, false, false, false, true], app.Vm.TabVisibility);
            Assert.Equal(4, app.Vm.ActiveTab);
        });
    }

    [Fact]
    public void When_the_catalog_has_no_models_at_all_every_tab_stays_visible()
    {
        Sta.Run(() =>
        {
            var provider = new FakeProvider { ImageCatalog = new ImageModels([], []), VideoCatalog = [], ChatCatalog = [] };
            using var app = TestApp.Create(provider: provider);

            app.Vm.InitializeAsync().GetAwaiter().GetResult();

            Assert.All(app.Vm.TabVisibility, Assert.True);
        });
    }

    [Fact]
    public void Hidden_tabs_collapse_in_the_main_window_and_come_back_after_a_reload()
    {
        WpfHost.Run(() =>
        {
            var provider = new FakeProvider { ImageCatalog = new ImageModels([Image("gen")], [Image("edit")]), VideoCatalog = [], ChatCatalog = [new ChatModelInfo("c", [])] };
            using var app = TestApp.Create(provider: provider);
            app.Vm.InitializeAsync().GetAwaiter().GetResult();
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var tabs = window.MainTabs.Items.OfType<System.Windows.Controls.TabItem>().ToList();

            Assert.Equal([true, true, false, true, false], tabs.Select(t => t.Visibility == System.Windows.Visibility.Visible));

            provider.VideoCatalog = [new ImageModelInfo("v", "o", false, ["text"])];
            app.Vm.InitializeAsync().GetAwaiter().GetResult();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            Assert.True(tabs[2].Visibility == System.Windows.Visibility.Visible);
            window.Close();
        });
    }
}
