using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Localization;
using ImageStudio.Services;
using ImageStudio.Views;

namespace ImageStudio.Tests;

public sealed class OutgoingRequestsFooterTests
{
    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
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

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static string InlineText(TextBlock block) =>
        string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text));

    [Fact]
    public async Task Footer_tracks_worker_requests_and_cancels_only_the_selected_one()
    {
        await WpfHost.RunAsync(async () =>
        {
            using var app = TestApp.Create();
            var vm = app.Vm;
            var registry = app.Get<OutgoingRequestRegistry>();
            using var client = new HttpClient(registry.CreateHandler(new BlockingHandler()));
            var window = app.Get<MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            var indicator = (StackPanel)window.FindName("ActivityIndicator");
            var toggle = (ToggleButton)window.FindName("RequestsToggle");
            var list = (ItemsControl)window.FindName("RequestsList");
            var spinner = FindAll<Spinner>(indicator).Single();
            var uiThread = Environment.CurrentManagedThreadId;
            var collectionThreads = new List<int>();
            ((System.Collections.Specialized.INotifyCollectionChanged)vm.OutgoingRequests.Requests).CollectionChanged +=
                (_, _) => collectionThreads.Add(Environment.CurrentManagedThreadId);

            Assert.False(spinner.IsVisible);
            var first = Task.Run(() => client.GetAsync("https://user:password@first.example/private-secret?token=secret"));
            var second = Task.Run(() => client.PostAsync("https://second.example/other?key=secret", null));
            await Until(() => vm.OutgoingRequests.Count == 2);
            Assert.True(spinner.IsVisible);
            Assert.True(toggle.IsVisible);
            Assert.Same(vm.OutgoingRequests.Requests, list.ItemsSource);
            Assert.All(collectionThreads, id => Assert.Equal(uiThread, id));

            var requests = vm.OutgoingRequests.Requests.ToArray();
            Assert.Equal("first.example", requests.Single(r => r.Info.Method == "GET").Info.Host);
            Assert.Equal("second.example", requests.Single(r => r.Info.Method == "POST").Info.Host);
            Assert.DoesNotContain("secret", string.Join(" ", requests.Select(r => r.Info.ToString())));
            Assert.DoesNotContain("password", string.Join(" ", requests.Select(r => r.Info.ToString())));

            toggle.IsChecked = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var buttons = FindAll<Button>(list).Where(b => b.Command is not null).ToArray();
            Assert.Equal(2, buttons.Length);
            var selected = requests.Single(r => r.Info.Host == "first.example");
            Assert.Contains(FindAll<TextBlock>(list), t => InlineText(t).Contains("first.example"));
            Assert.Contains(FindAll<TextBlock>(list), t => InlineText(t).Contains("second.example"));
            Assert.All(buttons, b => Assert.Equal("Anuluj", b.Content));
            Loc.Set("en");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.All(buttons, b => Assert.Equal("Cancel", b.Content));
            Loc.Set("pl");
            buttons.Single(b => ReferenceEquals(b.Command, selected.CancelCommand)).Command.Execute(null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await Until(() => vm.OutgoingRequests.Count == 1);
            Assert.False(second.IsCompleted);
            Assert.Equal("second.example", Assert.Single(vm.OutgoingRequests.Requests).Info.Host);

            using (app.Get<IActivityTracker>().Begin("Operacja"))
            {
                Assert.True(spinner.IsVisible);
                Assert.Single(FindAll<Spinner>(indicator));
                vm.OutgoingRequests.Requests[0].CancelCommand.Execute(null);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
                await Until(() => vm.OutgoingRequests.Count == 0);
                Assert.True(spinner.IsVisible);
                Assert.False(((Popup)window.FindName("RequestsPopup")).IsOpen);
                Assert.False(vm.OutgoingRequests.IsOpen);
            }
            await Until(() => !spinner.IsVisible);
            Assert.False(toggle.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void Footer_request_controls_follow_language_changes()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var window = app.Get<MainWindow>();
            try
            {
                var toggle = (ToggleButton)window.FindName("RequestsToggle");
                window.ShowInTaskbar = false;
                window.Show();
                Loc.Set("en");
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.StartsWith("Requests: ", InlineText((TextBlock)toggle.Content));
                Loc.Set("pl");
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.StartsWith("Żądania: ", InlineText((TextBlock)toggle.Content));
            }
            finally
            {
                Loc.Set("pl");
                window.Close();
            }
        });
    }
}
