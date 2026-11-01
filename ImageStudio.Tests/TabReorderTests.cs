using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ImageStudio.Configuration;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

// Kolejność zakładek lewego panelu zmienia się przeciąganiem nagłówków; logika (ActiveTab, widoczność) zostaje w indeksach logicznych
public sealed class TabReorderTests
{
    private static ImageStudio.MainWindow Show(TestApp app, ImageStudio.MainWindow? window = null)
    {
        window ??= app.Get<ImageStudio.MainWindow>();
        window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        return window;
    }

    private static List<string> Order(ImageStudio.MainWindow window) =>
        window.MainTabs.Items.OfType<TabItem>().Select(t => t.Tag!.GetType().Name).ToList();

    [Fact]
    public void Moving_a_tab_changes_the_display_order_but_not_the_logical_indices_and_is_saved()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var window = Show(app);
            var items = window.MainTabs.Items.OfType<TabItem>().ToList();
            Assert.Equal(["GenerateTab", "EditTab", "VideoTab", "ChatTab", "VoiceTab"], Order(window));

            window.MoveTab(items[3], items[0]);  // Czat na początek

            Assert.Equal(["ChatTab", "GenerateTab", "EditTab", "VideoTab", "VoiceTab"], Order(window));
            Assert.Equal(Order(window), app.Vm.SavedTabOrder);
            Assert.Equal(Order(window), app.Get<IUserStateStore>().Load().TabOrder);

            // wybór zakładki po przestawieniu to nadal jej indeks logiczny (Czat = 3), nie pozycja na pasku
            window.MainTabs.SelectedItem = items[3];
            Assert.Equal(3, app.Vm.ActiveTab);
            window.MainTabs.SelectedItem = items[1];
            Assert.Equal(1, app.Vm.ActiveTab);

            // zmiana ActiveTab spoza okna wybiera właściwy nagłówek
            app.Vm.ActiveTab = 2;
            Assert.Same(items[2], window.MainTabs.SelectedItem);
            window.Close();
        });
    }

    [Fact]
    public void Moving_keeps_the_selected_tab_selected()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var window = Show(app);
            var items = window.MainTabs.Items.OfType<TabItem>().ToList();
            window.MainTabs.SelectedItem = items[1];

            window.MoveTab(items[0], items[4]);

            Assert.Same(items[1], window.MainTabs.SelectedItem);
            Assert.Equal(1, app.Vm.ActiveTab);
            window.Close();
        });
    }

    [Fact]
    public void Saved_order_is_restored_and_unknown_or_new_tabs_go_to_the_end()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            app.Vm.SaveTabOrder(["VoiceTab", "NieIstnieje", "ChatTab"]);

            var window = Show(app, new ImageStudio.MainWindow(app.Vm, app.Get<IEnumerable<IMainTab>>()));

            Assert.Equal(["VoiceTab", "ChatTab", "GenerateTab", "EditTab", "VideoTab"], Order(window));
            window.Close();
        });
    }

    [Fact]
    public void Hiding_follows_the_tab_not_its_position_after_a_move()
    {
        WpfHost.Run(() =>
        {
            var provider = new FakeProvider
            {
                ImageCatalog = new ImageStudio.Services.ImageModels([new("g", "o", false, ["text"])], []),
                VideoCatalog = [],
                ChatCatalog = [new ImageStudio.Services.ChatModelInfo("c", [])],
            };
            using var app = TestApp.Create(provider: provider);
            var window = Show(app);
            var items = window.MainTabs.Items.OfType<TabItem>().ToList();
            window.MoveTab(items[2], items[0]);  // Wideo (bez modeli) na początek

            app.Vm.InitializeAsync().GetAwaiter().GetResult();

            Assert.Equal(Visibility.Collapsed, items[2].Visibility);
            Assert.Equal(Visibility.Collapsed, items[1].Visibility);  // Edycja też bez modeli
            Assert.Equal(Visibility.Visible, items[0].Visibility);
            window.Close();
        });
    }
}
