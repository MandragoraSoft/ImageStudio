using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

// Sesje czatu (zapis, incognito, otwieranie, usuwanie) oraz przycisk Stop
public sealed class SessionTests
{
    private static ChatViewModel Chat(TestApp app, string reply = "odpowiedź")
    {
        var chat = app.Vm.Chat;
        chat.SelectedModel = "m1";
        app.Provider.OnChat = (_, _) => reply;
        return chat;
    }

    private static void Send(ChatViewModel chat, string text)
    {
        chat.Input = text;
        chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    }

    // ---- zapis ----

    [Fact]
    public void Answer_saves_the_session_and_lists_it_with_a_title_from_the_first_question()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = Chat(app);

            Send(chat, "  Jak   działa cache?  ");

            var saved = Assert.Single(app.ChatSessions.Sessions.Values);
            Assert.Equal("Jak działa cache?", saved.Title);
            Assert.Equal(["user", "assistant"], saved.Messages.Select(m => m.Role));
            Assert.Equal("m1", saved.Model);
            var info = Assert.Single(chat.Sessions);
            Assert.Equal(saved.Id, info.Id);
            Assert.Same(info, chat.SelectedSession);
            Assert.Equal("Jak działa cache?", chat.SessionLabel);
        });
    }

    [Fact]
    public void Long_titles_are_shortened_and_the_second_answer_updates_the_same_session()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = Chat(app);

            Send(chat, new string('x', 100));
            Send(chat, "drugie pytanie");

            var saved = Assert.Single(app.ChatSessions.Sessions.Values);
            Assert.Equal(48, saved.Title.Length);
            Assert.EndsWith("...", saved.Title);
            Assert.Equal(4, saved.Messages.Count);
            Assert.Single(chat.Sessions);
        });
    }

    [Fact]
    public void Empty_conversation_is_not_saved()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = Chat(app);

            chat.NewChatCommand.Execute(null);

            Assert.Equal(0, app.ChatSessions.SaveCalls);
            Assert.Empty(chat.Sessions);
        });
    }

    // ---- incognito ----

    [Fact]
    public void Incognito_session_is_never_saved_or_listed()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = Chat(app);
            Send(chat, "zwykłe pytanie");
            Assert.Single(app.ChatSessions.Sessions);

            chat.NewIncognitoChatCommand.Execute(null);
            Assert.True(chat.IsIncognito);
            Assert.Empty(chat.Messages);
            Assert.Equal("Sesja incognito: rozmowa nie jest zapisywana", chat.SessionLabel);

            Send(chat, "tajne pytanie");
            Send(chat, "kolejne tajne");

            Assert.Equal(1, app.ChatSessions.SaveCalls);  // tylko ta pierwsza, zwykła
            Assert.Single(app.ChatSessions.Sessions);
            Assert.DoesNotContain(app.ChatSessions.Sessions.Values, s => s.Messages.Any(m => m.Content.Contains("tajne")));
            Assert.Equal(4, chat.Messages.Count);
            Assert.Single(chat.Sessions);
        });
    }

    [Fact]
    public void Leaving_incognito_discards_it_and_new_session_is_saved_again()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = Chat(app);
            chat.NewIncognitoChatCommand.Execute(null);
            Send(chat, "tajne");

            chat.NewChatCommand.Execute(null);

            Assert.False(chat.IsIncognito);
            Assert.Empty(chat.Messages);
            Send(chat, "jawne");
            var saved = Assert.Single(app.ChatSessions.Sessions.Values);
            Assert.Equal("jawne", saved.Title);
        });
    }

    // ---- otwieranie, usuwanie, ponowne uruchomienie ----

    [Fact]
    public void Opening_a_saved_session_restores_messages_reasoning_usage_tool_calls_and_images()
    {
        Sta.Run(() =>
        {
            var png = TestImages.Png(8, 8);
            var store = new MemoryChatSessionStore();
            store.Sessions["aa"] = new ChatSessionData
            {
                Id = "aa", Title = "Stara rozmowa", Model = "m2",
                Messages =
                [
                    new() { Role = "user", Content = "co na zdjęciu?", Images = [new ChatImageData("image/png", Convert.ToBase64String(png))] },
                    new() { Role = "assistant", Content = "", ToolCalls = [new ToolCall("c1", "fs__read", "{}")], Reasoning = "myślę", Usage = new TokenUsage(10, 2, 8) },
                    new() { Role = "tool", Content = "wynik", ToolCallId = "c1", ToolName = "fs__read" },
                    new() { Role = "assistant", Content = "kot", Usage = new TokenUsage(30, 3, 20) },
                ],
            };
            using var app = TestApp.Create(s => s.AddSingleton<IChatSessionStore>(store));
            var chat = app.Vm.Chat;
            chat.Models.Add("m2");
            Assert.Single(chat.Sessions);  // lista z poprzedniego uruchomienia

            chat.SelectedSession = chat.Sessions[0];

            Assert.Equal(["user", "assistant", "tool", "assistant"], chat.Messages.Select(m => m.Role));
            Assert.Equal(png, chat.Messages[0].Images!.Single().Bytes);
            Assert.True(chat.Messages[1].HasToolCalls);
            Assert.Equal("myślę", chat.Messages[1].Reasoning);
            Assert.Equal(new TokenUsage(30, 3, 20), chat.Messages[3].Usage);
            Assert.Equal("fs__read", chat.Messages[2].ToolName);
            Assert.Equal("m2", chat.SelectedModel);
            Assert.Equal("Stara rozmowa", chat.SessionLabel);
            Assert.False(chat.IsIncognito);
        });
    }

    [Fact]
    public void Continuing_an_opened_session_updates_it_instead_of_creating_a_new_one()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = Chat(app);
            Send(chat, "pierwsze");
            chat.NewChatCommand.Execute(null);
            Send(chat, "inne");
            Assert.Equal(2, chat.Sessions.Count);

            chat.SelectedSession = chat.Sessions.Single(s => s.Title == "pierwsze");
            Send(chat, "ciąg dalszy");

            Assert.Equal(2, app.ChatSessions.Sessions.Count);
            var first = app.ChatSessions.Sessions.Values.Single(s => s.Title == "pierwsze");
            Assert.Equal(4, first.Messages.Count);
            Assert.Equal("pierwsze", chat.Sessions[0].Title);  // ostatnio używana na górze
        });
    }

    [Fact]
    public void Deleting_the_current_session_removes_it_and_starts_a_new_one()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = Chat(app);
            Send(chat, "do usunięcia");
            Assert.True(chat.DeleteSessionCommand.CanExecute(null));

            chat.DeleteSessionCommand.Execute(null);

            Assert.Empty(app.ChatSessions.Sessions);
            Assert.Empty(chat.Sessions);
            Assert.Empty(chat.Messages);
            Assert.False(chat.DeleteSessionCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Deleting_another_session_keeps_the_stored_one_that_was_not_selected()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = Chat(app);
            Send(chat, "stara");
            chat.NewChatCommand.Execute(null);
            Send(chat, "bieżąca");
            chat.SelectedSession = chat.Sessions.Single(s => s.Title == "stara");  // otwiera starą
            chat.DeleteSessionCommand.Execute(null);                              // i usuwa ją

            Assert.Equal(["bieżąca"], app.ChatSessions.Sessions.Values.Select(s => s.Title));
            Assert.Empty(chat.Messages);  // usunięta była otwarta, więc zaczyna się nowa
        });
    }

    [Fact]
    public void Session_missing_on_disk_disappears_from_the_list_when_opened()
    {
        Sta.Run(() =>
        {
            var store = new MemoryChatSessionStore();
            store.Sessions["bb"] = new ChatSessionData { Id = "bb", Title = "znika", Messages = [new() { Role = "user", Content = "x" }] };
            using var app = TestApp.Create(s => s.AddSingleton<IChatSessionStore>(store));
            var chat = app.Vm.Chat;
            store.Sessions.Clear();  // plik zniknął po wczytaniu listy

            chat.SelectedSession = chat.Sessions.Single();

            Assert.Empty(chat.Sessions);
            Assert.Empty(chat.Messages);
        });
    }

    // ---- Stop ----

    [Fact]
    public void Stop_is_available_only_while_a_response_is_running()
    {
        Sta.Run(() =>
        {
            var provider = new StreamingFakeProvider();
            using var app = TestApp.Create(provider: provider);
            var chat = Chat(app);
            var duringStream = new List<bool>();
            provider.Deltas.Add(new ChatDelta("x", null));
            provider.AfterDelta = _ => duringStream.Add(chat.StopCommand.CanExecute(null) && !chat.SendCommand.CanExecute(null)
                                                       && !chat.CanChangeSession && !chat.NewChatCommand.CanExecute(null));
            Assert.False(chat.StopCommand.CanExecute(null));

            Send(chat, "pytanie");

            Assert.Equal([true], duringStream);
            Assert.False(chat.StopCommand.CanExecute(null));
            Assert.True(chat.CanChangeSession);
        });
    }

    [Fact]
    public void Stop_during_a_stream_keeps_the_text_that_arrived_and_saves_it()
    {
        Sta.Run(() =>
        {
            var provider = new StreamingFakeProvider();
            using var app = TestApp.Create(provider: provider);
            var chat = Chat(app);
            provider.Deltas.Add(new ChatDelta("Połowa odpo", null));
            provider.AfterDelta = _ => chat.StopCommand.Execute(null);
            provider.Gate = ct => Task.Delay(Timeout.Infinite, ct);  // odpowiedź "trwa", aż ktoś ją przerwie

            Send(chat, "pytanie");

            Assert.Equal(["pytanie", "Połowa odpo"], chat.Messages.Select(m => m.Content));
            Assert.False(chat.IsSending);
            Assert.Equal("Zatrzymano odpowiedź", app.Vm.Status);
            var saved = Assert.Single(app.ChatSessions.Sessions.Values);
            Assert.Equal("Połowa odpo", saved.Messages.Last().Content);
        });
    }

    [Fact]
    public void Stop_before_any_text_restores_the_question_and_reports_no_error()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = Chat(app);
            app.Provider.Gate = async ct =>
            {
                chat.StopCommand.Execute(null);
                await Task.Delay(Timeout.Infinite, ct);
            };

            Send(chat, "pytanie");

            Assert.Empty(chat.Messages);
            Assert.Equal("pytanie", chat.Input);
            Assert.Equal("Zatrzymano odpowiedź", app.Vm.Status);
            Assert.Empty(app.ChatSessions.Sessions);
        });
    }

    [Fact]
    public void Stop_passes_the_cancellation_to_running_tools()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var provider = app.Provider;
            var host = new BlockingToolHost();
            var chat = new ChatViewModel(() => provider, ["m1"], (_, _) => { }, "", host) { SelectedModel = "m1" };
            host.OnStart = () => chat.StopCommand.Execute(null);
            provider.OnReply = _ => new ChatReply("", [new ToolCall("c1", "fs__read", "{}")]);

            chat.Input = "czytaj";
            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.True(host.Cancelled);
            Assert.False(chat.IsSending);
        });
    }

    private sealed class BlockingToolHost : IToolHost
    {
        public IReadOnlyList<ToolDefinition> Tools { get; } = [new ToolDefinition("fs__read", "czyta", "{\"type\":\"object\",\"properties\":{}}")];
        public bool RequireApproval => false;
        public Action? OnStart { get; set; }
        public bool Cancelled { get; private set; }

        public async Task<string> CallAsync(ToolCall call, CancellationToken ct = default)
        {
            OnStart?.Invoke();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            return "";
        }
    }

    // ---- magazyn plikowy ----

    [Fact]
    public void File_store_round_trips_lists_newest_first_and_deletes()
    {
        var store = new FileChatSessionStore();
        var id1 = Guid.NewGuid().ToString("N");
        var id2 = Guid.NewGuid().ToString("N");
        store.Save(new ChatSessionData
        {
            Id = id1, Title = "starsza", UpdatedUtc = DateTime.UtcNow.AddMinutes(-5),
            Messages = [new() { Role = "user", Content = "zażółć gęślą jaźń", Usage = new TokenUsage(1, 2, null) }],
        });
        store.Save(new ChatSessionData { Id = id2, Title = "nowsza", UpdatedUtc = DateTime.UtcNow });

        var list = store.List().Where(i => i.Id == id1 || i.Id == id2).ToList();
        Assert.Equal(["nowsza", "starsza"], list.Select(i => i.Title));
        var loaded = store.Load(id1)!;
        Assert.Equal("zażółć gęślą jaźń", loaded.Messages[0].Content);
        Assert.Equal(new TokenUsage(1, 2, null), loaded.Messages[0].Usage);

        store.Delete(id1);
        store.Delete(id2);
        Assert.Null(store.Load(id1));
        Assert.DoesNotContain(store.List(), i => i.Id == id1 || i.Id == id2);
    }

    [Fact]
    public void File_store_rejects_ids_that_could_leave_the_sessions_directory_and_skips_corrupt_files()
    {
        var store = new FileChatSessionStore();
        var outside = Path.Combine(AppPaths.UserDataDir, "poza.json");
        Directory.CreateDirectory(AppPaths.UserDataDir);
        File.WriteAllText(outside, "{}");

        Assert.Null(store.Load(@"..\poza"));
        store.Save(new ChatSessionData { Id = @"..\zly", Title = "x" });
        store.Delete(@"..\poza");
        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(AppPaths.UserDataDir, "zly.json")));

        var dir = Path.Combine(AppPaths.UserDataDir, "sessions");
        Directory.CreateDirectory(dir);
        var bad = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(dir, bad + ".meta.json"), "{nie json");
        File.WriteAllText(Path.Combine(dir, bad + ".json"), "{nie json");
        Assert.DoesNotContain(store.List(), i => i.Id == bad);
        Assert.Null(store.Load(bad));
        File.Delete(Path.Combine(dir, bad + ".meta.json"));
        File.Delete(Path.Combine(dir, bad + ".json"));
        File.Delete(outside);
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

    [Fact]
    public void Conversation_shows_stop_instead_of_send_while_sending_and_marks_incognito()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = app.Vm.Chat;
            var window = new Window { Content = new ChatConversation { DataContext = chat }, ShowInTaskbar = false };
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Button Find(string label) => FindAll<Button>(window).Single(b => (b.Content as string) == label);
            Border Badge() => FindAll<Border>(window).Single(b => b.Child is TextBlock t && t.Text.StartsWith("Incognito"));

            Assert.True(Find("Wyślij").IsVisible);
            Assert.False(Find("Stop").IsVisible);
            Assert.False(Badge().IsVisible);

            chat.IsSending = true;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.False(Find("Wyślij").IsVisible);
            Assert.True(Find("Stop").IsVisible);
            Assert.True(Find("Stop").IsEnabled);

            chat.IsSending = false;
            chat.NewIncognitoChatCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(Find("Wyślij").IsVisible);
            Assert.True(Badge().IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void Chat_panel_lists_sessions_and_has_new_incognito_and_delete_buttons()
    {
        WpfHost.Run(() =>
        {
            var store = new MemoryChatSessionStore();
            store.Sessions["cc"] = new ChatSessionData { Id = "cc", Title = "Zapisana rozmowa", Messages = [new() { Role = "user", Content = "x" }] };
            using var app = TestApp.Create(s => s.AddSingleton<IChatSessionStore>(store));
            app.Vm.ActiveTab = 3;
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var buttons = FindAll<Button>(window).Select(b => b.Content as string).ToList();
            Assert.Contains("Nowa sesja", buttons);
            Assert.Contains("Incognito", buttons);
            Assert.Contains("Usuń sesję", buttons);
            var list = FindAll<ListBox>(window).Single(l => ReferenceEquals(l.ItemsSource, app.Vm.Chat.Sessions));
            Assert.Single(list.Items);
            window.Close();
        });
    }
}
