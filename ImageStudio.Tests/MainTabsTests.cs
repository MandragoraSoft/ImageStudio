using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ImageStudio.ViewModels;
using ImageStudio.Tabs.Chat;
using ImageStudio.Tabs.Edit;
using ImageStudio.Tabs.Generate;
using ImageStudio.Tabs.Voice;

namespace ImageStudio.Tests;

public sealed class MainTabsTests
{
    private sealed class UnknownTab : IMainTab
    {
        public string Title => "Nieznana";
        public int Order => 99;
        public object Content => new object();
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

    [Fact]
    public void Built_in_tabs_come_first_in_order_and_extra_tabs_follow()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create(services => services.AddSingleton<IMainTab>(new UnknownTab()));
            var vm = app.Vm;

            Assert.Equal(["Generowanie", "Edycja", "Wideo", "Czat", "Głos", "Nieznana"], app.Tabs.Select(t => t.Title));
            Assert.Same(vm, ((GenerateTab)app.Tabs[0]).Content);
            Assert.Same(vm.Chat, ((ChatTab)app.Tabs[3]).Content);
            Assert.Same(vm.Voice, ((VoiceTab)app.Tabs[4]).Content);
        });
    }

    [Fact]
    public void Window_builds_a_view_per_injected_tab_and_keeps_the_same_instance_when_switching()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var vm = app.Vm;
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var tabs = (TabControl)window.FindName("MainTabs");
            Assert.Equal(5, tabs.Items.Count);
            Assert.Equal(["Generowanie", "Edycja", "Wideo", "Czat", "Głos"],
                         tabs.Items.Cast<TabItem>().Select(t => (string)t.Header));

            var first = FindAll<GenerationPanel>(window).Single();
            Assert.Same(vm, first.DataContext);

            vm.ActiveTab = 1;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.NotEmpty(FindAll<EditPanel>(window));
            vm.ActiveTab = 0;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Same(first, FindAll<GenerationPanel>(window).Single());

            vm.ActiveTab = 3;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Same(vm.Chat, FindAll<ChatPanel>(window).Single().DataContext);
            window.Close();
        });
    }

    [Fact]
    public void Window_rejects_a_tab_without_view()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create(services => services.AddSingleton<IMainTab>(new UnknownTab()));

            var error = Assert.ThrowsAny<Exception>(() => app.Get<ImageStudio.MainWindow>());

            Assert.Contains("nie ma widoku", (error.InnerException ?? error).Message + error.Message);
        });
    }
}
