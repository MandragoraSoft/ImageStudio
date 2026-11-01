using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Zakładka Wideo ma filtry modeli jak Generowanie i Edycja; selektor nie gubi wyboru, gdy filtr ukryje wybrany model
public sealed class VideoFiltersTests
{
    private static ImageModelInfo Video(string id, string owner, params string[] inputs) => new(id, owner, false, inputs.Length == 0 ? ["text"] : inputs);

    private static TestApp Loaded()
    {
        var app = TestApp.Create();
        app.Provider.ImageCatalog = new ImageModels([], []);
        app.Provider.VideoCatalog =
        [
            Video("wan/wan-text", "wan"),
            Video("wan/wan-image", "wan", "text", "image"),
            Video("bytedance/seedance-image", "bytedance", "text", "image"),
        ];
        app.Vm.InitializeAsync().GetAwaiter().GetResult();
        return app;
    }

    private static IEnumerable<string> Visible(MainViewModel vm) => vm.VideoModelsView.Cast<ModelOption>().Select(m => m.Id).ToList();

    [Fact]
    public void Video_models_use_the_shared_search_provider_and_image_filters()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();
            var vm = app.Vm;
            Assert.Equal(3, Visible(vm).Count());

            vm.SearchText = "seedance";
            Assert.Equal(["bytedance/seedance-image"], Visible(vm));

            vm.SearchText = "";
            vm.SelectedProvider = "wan";
            Assert.Equal(["wan/wan-image", "wan/wan-text"], Visible(vm).Order());

            vm.SelectedProvider = MainViewModel.AllProviders;
            vm.InputImageOnly = true;
            Assert.Equal(["bytedance/seedance-image", "wan/wan-image"], Visible(vm).Order());
        });
    }

    [Fact]
    public void Preferred_only_applies_to_video_models_and_owners_include_them()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();
            var vm = app.Vm;
            app.Get<ImageStudio.Configuration.ModelPreferences>().For("fake").Preferred.Add("wan/wan-text");

            vm.PreferredOnly = true;

            Assert.Equal(["wan/wan-text"], Visible(vm));
            Assert.Contains("bytedance", vm.Providers);
        });
    }

    [Fact]
    public void Filtering_never_changes_the_selected_video_model()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();
            var vm = app.Vm;
            vm.SelectedVideoModel = vm.VideoModels.Single(m => m.Id == "wan/wan-text");

            vm.SearchText = "seedance";  // ukrywa wybrany model

            Assert.Equal("wan/wan-text", vm.SelectedVideoModel!.Id);
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

    private static void Flush(Dispatcher d) => d.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    [Fact]
    public void Video_tab_has_the_filters_button_and_the_picker_keeps_a_selection_hidden_by_the_filter()
    {
        WpfHost.Run(() =>
        {
            using var app = Loaded();
            var vm = app.Vm;
            vm.ActiveTab = 2;
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            Flush(window.Dispatcher);

            var videoPanel = FindAll<UserControl>(window).First(c => c.GetType().Name == "VideoPanel");
            var picker = FindAll<ModelPicker>(videoPanel).First();
            Assert.True(picker.HasFilters);
            Assert.Same(vm.VideoModelsView, picker.ItemsSource);
            FindAll<Expander>(picker).First().IsExpanded = true;
            Flush(window.Dispatcher);
            Assert.True(FindAll<ToggleButton>(picker).Any(t => t.Content as string == "Filtry modeli"));

            // filtr ukrywa wybrany model: lista traci zaznaczenie, ale wybór (i nagłówek) zostają
            vm.SelectedVideoModel = vm.VideoModels.Single(m => m.Id == "wan/wan-text");
            Flush(window.Dispatcher);
            vm.SearchText = "seedance";
            Flush(window.Dispatcher);
            Assert.Equal("wan/wan-text", vm.SelectedVideoModel!.Id);
            Assert.Same(vm.SelectedVideoModel, picker.SelectedItem);

            // wybór z listy nadal działa
            var list = FindAll<ListBox>(picker).First();
            list.SelectedItem = list.Items.Cast<ModelOption>().Single(m => m.Id == "bytedance/seedance-image");
            Flush(window.Dispatcher);
            Assert.Equal("bytedance/seedance-image", vm.SelectedVideoModel!.Id);
            window.Close();
        });
    }

    [Fact]
    public void Filters_toggle_and_reload_buttons_share_the_secondary_look_and_the_toggle_highlights_when_on()
    {
        WpfHost.Run(() =>
        {
            using var app = Loaded();
            var vm = app.Vm;
            vm.ActiveTab = 0;
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            Flush(window.Dispatcher);

            var picker = FindAll<ModelPicker>(window).First(p => p.HasFilters);
            FindAll<Expander>(picker).First().IsExpanded = true;
            Flush(window.Dispatcher);
            var toggle = FindAll<ToggleButton>(picker).First(t => t.Content as string == "Filtry modeli");
            var reload = FindAll<Button>(picker).First(b => (b.Content as string)?.Contains("Odśwież") == true);

            Assert.Same(Application.Current.Resources["FilterToggleButton"], toggle.Style);
            Assert.Same(Application.Current.Resources["SecondaryButton"], reload.Style);
            Assert.Equal(toggle.Padding, reload.Padding);

            var border = FindAll<Border>(toggle).First();
            var off = ((SolidColorBrush)border.Background).Color;
            toggle.IsChecked = true;
            Flush(window.Dispatcher);
            Assert.NotEqual(off, ((SolidColorBrush)FindAll<Border>(toggle).First().Background).Color);
            window.Close();
        });
    }
}
