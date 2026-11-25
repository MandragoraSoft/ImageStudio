using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

// Szkielet okna głównego: menu na górze, stopka ze spinnerem pracy, przyciemnione okno odpowiedzi w czasie operacji
public sealed class ShellTests
{
    private static void Flush(Dispatcher d) => d.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static IEnumerable<MenuItem> AllItems(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var nested in AllItems(item)) yield return nested;
        }
    }

    [Fact]
    public void Main_window_has_a_classic_menu_wired_to_the_view_model_commands()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var vm = app.Vm;
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            Flush(window.Dispatcher);

            var menu = FindAll<Menu>(window).Single();
            Assert.Equal(["Plik", "Zestawy", "Modele", "Narzędzia", "Pomoc"],
                         menu.Items.OfType<MenuItem>().Select(m => ((string)m.Header).Replace("_", "")));

            var items = AllItems(menu).ToList();
            MenuItem Item(string header) => items.Single(i => i.Header is string h && h.Replace("_", "") == header);
            Assert.Same(vm.OpenSettingsCommand, Item("Ustawienia...").Command);
            Assert.Same(vm.ReloadModelsCommand, Item("Odśwież listy modeli").Command);
            Assert.Same(vm.OpenModelBrowserCommand, Item("Przeglądaj modele...").Command);
            Assert.Same(vm.SaveImageAsCommand, Item("Zapisz jako...").Command);
            Assert.Same(vm.ClearResultCommand, Item("Wyczyść wynik").Command);
            Assert.Same(vm.OpenPresetsCommand, Item("Zestawy...").Command);
            Assert.Same(vm.OpenArchiveManagerCommand, Item("Archiwum wyników...").Command);
            var help = Item("Pomoc");
            Assert.Same(vm.OpenAboutCommand, help.Command);
            help.Command.Execute(null);
            Assert.Equal(1, app.Dialogs.AboutCount);
            window.Close();
        });
    }

    [Fact]
    public void Closing_can_hide_to_tray_but_explicit_exit_closes_the_window()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            Flush(window.Dispatcher);

            app.Vm.HideToTrayOnClose = true;
            window.Close();
            Assert.False(window.IsVisible);

            window.ActivateFromTray();
            Assert.True(window.IsVisible);

            window.ExitApplication();
            Assert.False(window.IsVisible);
            Assert.Throws<InvalidOperationException>(window.ActivateFromTray);
        });
    }

    [Fact]
    public Task Settings_cannot_be_closed_while_cleanup_is_running() => WpfHost.RunAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new SettingsResult("url", "market", 1024, "", "system");
        var tab = new AppSettingsTab(result, _ => release.Task);
        var window = new SettingsWindow
        {
            DataContext = new SettingsViewModel(result, [tab]),
            ShowInTaskbar = false
        };
        window.Show();
        var cleanup = tab.ClearSelectedDataCommand.ExecuteAsync(null);
        try
        {
            Assert.False(((DockPanel)window.Content).IsEnabled);
            window.Close();
            Assert.True(window.IsVisible);
        }
        finally
        {
            release.TrySetResult();
            await cleanup;
            window.Close();
        }
    });

    [Fact]
    public void Footer_shows_the_spinner_and_label_only_while_an_operation_runs_and_dims_the_response_window()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            Flush(window.Dispatcher);
            var tracker = app.Get<IActivityTracker>();

            var statusBar = FindAll<StatusBar>(window).Single();
            var spinner = FindAll<Spinner>(statusBar).Single();
            var response = (Grid)VisualTreeHelper.GetParent(FindAll<OutputPanel>(window).Single());

            Assert.False(spinner.IsVisible);
            Assert.Equal(1.0, response.Opacity);

            using (tracker.Begin("Generowanie obrazu"))
            {
                Flush(window.Dispatcher);
                Assert.True(spinner.IsVisible);
                Assert.Equal(0.45, response.Opacity);
                Assert.Contains(FindAll<TextBlock>(statusBar), t => t.Text == "Generowanie obrazu");
            }

            Flush(window.Dispatcher);
            Assert.False(spinner.IsVisible);
            Assert.Equal(1.0, response.Opacity);
            window.Close();
        });
    }

    [Fact]
    public void Footer_shows_status_and_active_provider_and_the_old_progress_bar_is_gone()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            app.Vm.Status = "Gotowe do pracy";
            Flush(window.Dispatcher);

            var footer = FindAll<StatusBar>(window).Single();
            var texts = FindAll<TextBlock>(footer).Select(t => t.Text).ToList();
            Assert.Contains("Gotowe do pracy", texts);
            Assert.Contains("Dostawca: Fake", texts);
            Assert.Empty(FindAll<ProgressBar>(footer)); // pasek zużycia kontekstu w czacie to osobny, dozwolony ProgressBar
            window.Close();
        });
    }

    [Fact]
    public void Chat_conversation_shows_a_collapsed_reasoning_preview_only_for_messages_that_have_it()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = app.Vm.Chat;
            chat.Messages.Add(new ChatMessage("user", "pytanie"));
            chat.Messages.Add(new ChatMessage("assistant", "odpowiedź", Reasoning: "tok rozumowania"));
            chat.Messages.Add(new ChatMessage("assistant", "bez rozumowania"));
            var window = new Window { Content = new ChatConversation { DataContext = chat }, ShowInTaskbar = false };
            window.Show();
            Flush(window.Dispatcher);

            var expanders = FindAll<Expander>(window).Where(e => (string)e.Header == "Rozumowanie").ToList();
            Assert.Equal(3, expanders.Count);
            Assert.Equal(1, expanders.Count(e => e.IsVisible));
            Assert.All(expanders, e => Assert.False(e.IsExpanded));
            window.Close();
        });
    }
}
