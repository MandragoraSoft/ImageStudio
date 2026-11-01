using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

public sealed class SettingsTabsTests
{
    private static SettingsResult Current() => new("u", "m", 1536, "k", "dark", "openrouter", "or-key");

    private sealed class FakeTab(string title, int order, Func<SettingsResult, SettingsResult>? apply = null) : ISettingsTab
    {
        public string Title { get; } = title;
        public int Order { get; } = order;
        public SettingsResult Apply(SettingsResult result) => apply?.Invoke(result) ?? result;
    }

    [Fact]
    public void Tabs_are_ordered_and_the_result_combines_all_of_them()
    {
        var vm = new SettingsViewModel(Current(), [
            new FakeTab("B", 20, r => r with { MaxSide = r.MaxSide + 1 }),
            new FakeTab("A", 10, r => r with { Theme = "light" }),
        ]);

        Assert.Equal(["A", "B"], vm.Tabs.Select(t => t.Title));
        Assert.Equal("A", vm.SelectedTab.Title);
        var result = vm.ToResult();
        Assert.Equal("light", result.Theme);
        Assert.Equal(1537, result.MaxSide);
        Assert.Equal("k", result.ApiKey);  // wartości, których żadna zakładka nie zmienia, zostają
    }

    [Fact]
    public void Settings_window_requires_at_least_one_tab()
    {
        Assert.Throws<ArgumentException>(() => new SettingsViewModel(Current(), []));
    }

    [Fact]
    public void App_and_provider_tabs_write_their_own_values()
    {
        var current = Current();
        var app = new AppSettingsTab(current) { MaxSide = 1024, SelectedTheme = "light" };
        var provider = new ProviderSettingsTab(current);
        provider.OpenRouter.ApiKey = " nowy ";
        var vm = new SettingsViewModel(current, [provider, app]);

        var result = vm.ToResult();

        Assert.Equal(1024, result.MaxSide);
        Assert.Equal("light", result.Theme);
        Assert.Equal("nowy", result.OpenRouterKey);
        Assert.Equal("openrouter", result.ProviderId);
    }

    private static void Flush(Dispatcher dispatcher) => dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Find<T>(child) is { } nested) return nested;
        }
        return null;
    }

    [Fact]
    public void Window_shows_a_view_for_each_tab_and_rejects_a_tab_without_view()
    {
        WpfHost.Run(() =>
        {
            var current = Current();
            var app1 = new AppSettingsTab(current);
            var provider = new ProviderSettingsTab(current);
            var vm = new SettingsViewModel(current, [app1, provider]);
            var window = new SettingsWindow { DataContext = vm, ShowInTaskbar = false };
            window.EnsureTabViews();
            window.Show();
            Flush(window.Dispatcher);

            Assert.NotNull(Find<AppSettingsTabView>(window));
            vm.SelectedTab = provider;
            Flush(window.Dispatcher);
            Assert.NotNull(Find<ProviderSettingsTabView>(window));
            // wybór dostawcy podmienia panel konfiguracji (ChatGPT: logowanie zamiast adresów i klucza UberLLM)
            provider.SelectedProvider = "chatgpt";
            Flush(window.Dispatcher);
            Assert.Equal("chatgpt", provider.SelectedProvider);
            Assert.IsType<ChatGptSettingsViewModel>(provider.ActiveProvider);
            Assert.NotNull(Find<ChatGptSettingsView>(window));
            Assert.Null(Find<UberLlmSettingsView>(window));
            window.Close();

            // zakładka bez DataTemplate w oknie: wyjątek przy otwarciu
            var broken = new SettingsWindow
            {
                DataContext = new SettingsViewModel(current, [new FakeTab("X", 0)]),
                ShowInTaskbar = false,
            };
            var error = Assert.Throws<InvalidOperationException>(broken.EnsureTabViews);
            Assert.Contains("nie ma widoku", error.Message);
        });
    }
}
