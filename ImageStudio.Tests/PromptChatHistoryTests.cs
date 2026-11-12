using ImageStudio.Configuration;
using ImageStudio.Localization;
using Microsoft.Extensions.DependencyInjection;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Rozmowy "Rozmowa AI" z edytora promptu: tryby zapisu, wznawianie, nowa rozmowa i zatrzymanie odpowiedzi
public sealed class PromptChatHistoryTests
{
    private static TestApp Create(PromptChatHistoryMode mode)
    {
        var app = TestApp.Create(provider: new FakeProvider { OnChat = (_, _) => "Odpowiedź" }, dialogs: new FakeDialogs());
        app.Dialogs.SettingsAnswer = r => r with { PromptChatHistory = mode };
        app.Vm.OpenSettingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return app;
    }

    private static ChatViewModel OpenGenerate(TestApp app)
    {
        app.Vm.EditGeneratePromptCommand.Execute(null);
        var chat = app.Dialogs.LastConversationFactory!(() => app.Dialogs.LastInitial!, (_, _) => { });
        chat.SelectedModel = "m";
        return chat;
    }

    private static ChatViewModel OpenEdit(TestApp app)
    {
        app.Vm.EditEditPromptCommand.Execute(null);
        var chat = app.Dialogs.LastConversationFactory!(() => app.Dialogs.LastInitial!, (_, _) => { });
        chat.SelectedModel = "m";
        return chat;
    }

    private static ChatViewModel OpenVideo(TestApp app)
    {
        app.Vm.EditVideoPromptCommand.Execute(null);
        var chat = app.Dialogs.LastConversationFactory!(() => app.Dialogs.LastInitial!, (_, _) => { });
        chat.SelectedModel = "m";
        return chat;
    }

    // Czat z narzędziami bez sesji edytora: testy przerwania tury z wykonanym narzędziem
    private static ChatViewModel ToolChat(FakeProvider provider, FakeToolHost host, IChatSessionStore store) =>
        new(() => provider, ["m"], (_, _) => { }, "", host, modelInfo: id => new ChatModelInfo(id, []) { SupportsTools = true }, sessionStore: store)
        {
            SelectedModel = "m",
            DiscardIncompleteTurns = true,
        };

    private static void Send(ChatViewModel chat, string text)
    {
        chat.Input = text;
        chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    }

    [Fact]
    public void Disk_mode_saves_the_conversation_apart_from_normal_chats_and_resumes_it()
    {
        Sta.Run(() =>
        {
            using var app = Create(PromptChatHistoryMode.Disk);
            Send(OpenGenerate(app), "pierwsze pytanie");

            Assert.Single(app.PromptChatSessions.Sessions);
            Assert.Empty(app.ChatSessions.Sessions);
            var reopened = OpenGenerate(app);
            Assert.Equal(["pierwsze pytanie", "Odpowiedź"], reopened.Messages.Select(m => m.Content));
        });
    }

    [Fact]
    public void Until_close_mode_resumes_in_memory_and_writes_nothing_to_disk()
    {
        Sta.Run(() =>
        {
            using var app = Create(PromptChatHistoryMode.UntilClose);
            Send(OpenGenerate(app), "pytanie");

            Assert.Empty(app.PromptChatSessions.Sessions);
            Assert.Empty(app.ChatSessions.Sessions);
            Assert.Equal(2, OpenGenerate(app).Messages.Count);
        });
    }

    [Fact]
    public void Off_mode_starts_each_editor_with_a_new_conversation_and_stores_nothing()
    {
        Sta.Run(() =>
        {
            using var app = Create(PromptChatHistoryMode.Off);
            Send(OpenGenerate(app), "pytanie");

            Assert.Empty(OpenGenerate(app).Messages);
            Assert.Empty(app.PromptChatSessions.Sessions);
            Assert.Empty(app.ChatSessions.Sessions);
        });
    }

    [Fact]
    public void Reopening_after_cancel_resumes_the_conversation_without_touching_prompt_fields()
    {
        Sta.Run(() =>
        {
            using var app = Create(PromptChatHistoryMode.Disk);
            app.Vm.EditPrompt = "oryginał";
            Send(OpenGenerate(app), "pytanie");

            app.Vm.EditPrompt = "zmieniony";
            var reopened = OpenGenerate(app);

            Assert.Equal(2, reopened.Messages.Count);
            Assert.Equal("zmieniony", app.Vm.EditPrompt);
        });
    }

    [Fact]
    public void New_conversation_keeps_prompt_fields_and_the_previous_saved_conversation()
    {
        Sta.Run(() =>
        {
            using var app = Create(PromptChatHistoryMode.Disk);
            var chat = OpenGenerate(app);
            Send(chat, "stare pytanie");
            app.Vm.EditPrompt = "pole";

            chat.NewChatCommand.Execute(null);

            Assert.Empty(chat.Messages);
            Assert.Equal("pole", app.Vm.EditPrompt);
            Assert.Empty(OpenGenerate(app).Messages);
            Assert.Single(app.PromptChatSessions.Sessions);

            Send(chat, "nowe pytanie");
            Assert.Equal(2, app.PromptChatSessions.Sessions.Count);
        });
    }

    [Fact]
    public async Task Stopping_a_reply_rolls_back_the_unfinished_turn_and_restores_the_draft()
    {
        await WpfHost.RunAsync(async () =>
        {
            using var app = Create(PromptChatHistoryMode.Disk);
            var chat = OpenGenerate(app);
            Send(chat, "pierwsze");
            app.Provider.Gate = ct => Task.Delay(Timeout.Infinite, ct);
            chat.Input = "szkic";

            var task = chat.SendCommand.ExecuteAsync(null);
            Assert.True(chat.IsSending);
            chat.StopCommand.Execute(null);
            await task;

            Assert.False(chat.IsSending);
            Assert.Equal(["pierwsze", "Odpowiedź"], chat.Messages.Select(m => m.Content));
            Assert.Equal("szkic", chat.Input);
            var saved = Assert.Single(app.PromptChatSessions.Sessions).Value;
            Assert.Equal(2, saved.Messages.Count);
            Assert.Equal(["pierwsze", "Odpowiedź"], OpenGenerate(app).Messages.Select(m => m.Content));
        });
    }

    [Fact]
    public void Stop_after_a_finished_tool_keeps_its_history_and_does_not_restore_the_question()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var provider = app.Provider;
            var host = new FakeToolHost();
            var store = new MemoryChatSessionStore();
            var chat = ToolChat(provider, host, store);
            // Pierwsza runda kończy się wywołaniem narzędzia; druga runda zostaje przerwana po wykonaniu narzędzia
            provider.OnReply = _ => new ChatReply("", [new ToolCall("c1", "fs__read", "{}")]);
            provider.Gate = async ct =>
            {
                if (provider.ChatCalls != 1) return;
                chat.StopCommand.Execute(null);
                await Task.Delay(Timeout.Infinite, ct);
            };

            chat.Input = "czytaj";
            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(["user", "assistant", "tool"], chat.Messages.Select(m => m.Role));
            Assert.Equal("wynik", chat.Messages[2].Content);
            Assert.Single(host.Calls);
            Assert.Null(chat.LastCompletedReply);
            Assert.Equal("", chat.Input);
            Assert.False(chat.IsSending);
            Assert.Equal(3, Assert.Single(store.Sessions).Value.Messages.Count);
        });
    }

    [Fact]
    public void Stop_during_a_tool_keeps_the_call_and_closes_it_with_a_stopped_result()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var provider = app.Provider;
            var host = new FakeToolHost();
            var chat = ToolChat(provider, host, new MemoryChatSessionStore());
            host.OnCall = _ =>
            {
                chat.StopCommand.Execute(null);
                throw new OperationCanceledException();
            };
            provider.OnReply = _ => new ChatReply("", [new ToolCall("c1", "fs__read", "{}")]);

            chat.Input = "czytaj";
            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(["user", "assistant", "tool"], chat.Messages.Select(m => m.Role));
            Assert.Equal("c1", chat.Messages[2].ToolCallId);
            Assert.Equal(Loc.T("chat.msg.toolStopped"), chat.Messages[2].Content);
            Assert.Equal(1, provider.ChatCalls);
            Assert.False(chat.IsSending);
        });
    }

    [Fact]
    public void Conversation_is_restored_in_a_new_container_that_shares_the_stores()
    {
        Sta.Run(() =>
        {
            using var first = Create(PromptChatHistoryMode.Disk);
            Send(OpenGenerate(first), "przed restartem");

            // Nowy kontener z tymi samymi magazynami stanu i rozmów, jak po ponownym uruchomieniu aplikacji
            using var second = TestApp.Create(services =>
            {
                services.AddSingleton<IUserStateStore>(first.State);
                services.AddSingleton(first.PromptChatSessions);
                services.AddSingleton<IPromptChatSessionStore>(first.PromptChatSessions);
            }, new FakeProvider { OnChat = (_, _) => "Odpowiedź" }, new FakeDialogs());

            var reopened = OpenGenerate(second);
            Assert.Equal(["przed restartem", "Odpowiedź"], reopened.Messages.Select(m => m.Content));
            Send(reopened, "po restarcie");

            var saved = Assert.Single(second.PromptChatSessions.Sessions).Value;
            Assert.Equal(4, saved.Messages.Count);
        });
    }

    [Fact]
    public void Generate_edit_and_video_conversations_keep_separate_saved_histories()
    {
        Sta.Run(() =>
        {
            using var app = Create(PromptChatHistoryMode.Disk);
            Send(OpenGenerate(app), "generowanie");
            Send(OpenEdit(app), "edycja");
            Send(OpenVideo(app), "wideo");

            Assert.Equal(3, app.PromptChatSessions.Sessions.Count);
            Assert.Equal(["generowanie", "Odpowiedź"], OpenGenerate(app).Messages.Select(m => m.Content));
            Assert.Equal(["edycja", "Odpowiedź"], OpenEdit(app).Messages.Select(m => m.Content));
            Assert.Equal(["wideo", "Odpowiedź"], OpenVideo(app).Messages.Select(m => m.Content));
        });
    }

    [Fact]
    public void Legacy_conversation_is_not_resumed_but_is_kept_on_disk()
    {
        Sta.Run(() =>
        {
            using var app = Create(PromptChatHistoryMode.Disk);
            var old = new ChatSessionData
            {
                Messages = [new ChatMessageData { Role = "user", Content = "dane innej zakładki" }],
            };
            app.PromptChatSessions.Save(old);
            app.State.State.PromptChatSessions["generate"] = old.Id;

            var chat = OpenGenerate(app);
            Assert.Empty(chat.Messages);
            Send(chat, "nowa rozmowa");
            Assert.Equal(old.Id, app.State.State.PromptChatSessions["generate"]);
            Assert.Equal("dane innej zakładki", app.PromptChatSessions.Load(old.Id)!.Messages[0].Content);
            Assert.Equal(["nowa rozmowa", "Odpowiedź"], OpenGenerate(app).Messages.Select(m => m.Content));
        });
    }

    [Fact]
    public async Task Closing_the_editor_window_during_a_reply_rolls_back_the_turn_and_keeps_the_draft()
    {
        await WpfHost.RunAsync(async () =>
        {
            var previous = WindowMemory.Store;
            WindowMemory.Store = new MemoryWindowStateStore();
            try
            {
                using var app = Create(PromptChatHistoryMode.Disk);
                app.Vm.EditGeneratePromptCommand.Execute(null);
                var factory = app.Dialogs.LastConversationFactory!;
                var initial = app.Dialogs.LastInitial!;
                ChatViewModel? chat = null;
                var window = new PromptEditorWindow("Edytor", initial, null, null, null, null, null,
                                                    (current, report) => chat = factory(current, report));
                window.Show();
                chat!.SelectedModel = "m";
                chat.Input = "szkic";
                app.Provider.Gate = ct => Task.Delay(Timeout.Infinite, ct);

                var task = chat.SendCommand.ExecuteAsync(null);
                Assert.True(chat.IsSending);
                window.Close();
                await task;

                Assert.False(chat.IsSending);
                Assert.Empty(chat.Messages);
                Assert.Equal("szkic", chat.Input);
                Assert.Empty(app.PromptChatSessions.Sessions);
            }
            finally
            {
                WindowMemory.Store = previous;
            }
        });
    }
}
