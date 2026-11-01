using System.Net;
using System.Text.Json;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

// Pętla narzędzi w czacie
public class ChatToolLoopTests
{
    private static ToolCall Call(string id = "c1", string name = "fs__read", string args = """{"path":"a.txt"}""") =>
        new(id, name, args);

    private static (ChatViewModel Vm, FakeProvider Provider, FakeToolHost Host, List<ToolCall> Confirms) Create(
        bool requireApproval = false, bool approve = true)
    {
        var provider = new FakeProvider();
        var host = new FakeToolHost { RequireApproval = requireApproval };
        var confirms = new List<ToolCall>();
        var vm = new ChatViewModel(() => provider, ["m1"], (_, _) => { }, "", host,
                                   call => { confirms.Add(call); return approve; })
        {
            SelectedModel = "m1",
        };
        return (vm, provider, host, confirms);
    }

    // Pierwsza odpowiedź prosi o narzędzie, każda kolejna jest tekstem
    private static Func<IReadOnlyList<ChatMessage>, ChatReply> ThenAnswer(params ToolCall[] firstCalls) =>
        history => history.Any(m => m.Role == "tool")
            ? new ChatReply("gotowe", [])
            : new ChatReply("", firstCalls);

    [Fact]
    public async Task Tool_call_is_executed_and_result_is_sent_back()
    {
        var (vm, provider, host, _) = Create();
        provider.OnReply = ThenAnswer(Call());
        host.OnCall = _ => "treść pliku";
        vm.Input = "przeczytaj a.txt";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(["user", "assistant", "tool", "assistant"], vm.Messages.Select(m => m.Role));
        Assert.Equal("c1", vm.Messages[1].ToolCalls![0].Id);
        Assert.Equal("c1", vm.Messages[2].ToolCallId);
        Assert.Equal("fs__read", vm.Messages[2].ToolName);
        Assert.Equal("treść pliku", vm.Messages[2].Content);
        Assert.Equal("gotowe", vm.Messages[3].Content);
        Assert.Single(host.Calls);
        // drugie zapytanie niesie wynik narzędzia, a narzędzia są przekazywane za każdym razem
        Assert.Equal(2, provider.ChatCalls);
        Assert.Contains(provider.LastMessages, m => m.Role == "tool" && m.Content == "treść pliku");
        Assert.NotNull(provider.LastTools);
    }

    [Fact]
    public async Task Several_calls_in_one_reply_each_get_a_result()
    {
        var (vm, provider, host, _) = Create();
        provider.OnReply = ThenAnswer(Call("a"), Call("b", args: "{}"));
        vm.Input = "dwa pliki";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(["a", "b"], host.Calls.Select(c => c.Id));
        Assert.Equal(["a", "b"], vm.Messages.Where(m => m.Role == "tool").Select(m => m.ToolCallId));
    }

    [Fact]
    public async Task Rejected_call_is_not_executed_and_model_is_told()
    {
        var (vm, provider, host, confirms) = Create(requireApproval: true, approve: false);
        provider.OnReply = ThenAnswer(Call());
        vm.Input = "przeczytaj";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Single(confirms);
        Assert.Empty(host.Calls);
        Assert.Contains("odrzucił", vm.Messages.First(m => m.Role == "tool").Content);
    }

    [Fact]
    public async Task Approved_call_is_executed()
    {
        var (vm, provider, host, confirms) = Create(requireApproval: true, approve: true);
        provider.OnReply = ThenAnswer(Call());
        vm.Input = "przeczytaj";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Single(confirms);
        Assert.Single(host.Calls);
    }

    [Fact]
    public async Task Without_approval_requirement_nobody_is_asked()
    {
        var (vm, provider, host, confirms) = Create(requireApproval: false);
        provider.OnReply = ThenAnswer(Call());
        vm.Input = "przeczytaj";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(confirms);
        Assert.Single(host.Calls);
    }

    [Fact]
    public async Task Approval_required_but_no_confirm_callback_means_refusal()
    {
        var provider = new FakeProvider { OnReply = ThenAnswer(Call()) };
        var host = new FakeToolHost { RequireApproval = true };
        var vm = new ChatViewModel(() => provider, ["m1"], (_, _) => { }, "", host) { SelectedModel = "m1", Input = "x" };

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Tool_exception_becomes_text_for_the_model()
    {
        var (vm, provider, host, _) = Create();
        provider.OnReply = ThenAnswer(Call());
        host.OnCall = _ => throw new IOException("brak dostępu");
        vm.Input = "przeczytaj";

        await vm.SendCommand.ExecuteAsync(null);

        var tool = vm.Messages.First(m => m.Role == "tool");
        Assert.StartsWith("Błąd narzędzia", tool.Content);
        Assert.Contains("brak dostępu", tool.Content);
        Assert.Equal("gotowe", vm.Messages[^1].Content);
    }

    [Fact]
    public async Task No_tools_are_offered_when_the_host_has_none()
    {
        var (vm, provider, host, _) = Create();
        host.Tools = [];
        provider.OnChat = (_, _) => "zwykła odpowiedź";
        vm.Input = "cześć";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Null(provider.LastTools);
    }

    [Fact]
    public async Task Endless_tool_requests_are_cut_off_with_a_valid_history()
    {
        var (vm, provider, host, _) = Create();
        provider.OnReply = _ => new ChatReply("", [Call(Guid.NewGuid().ToString("N"))]);
        vm.Input = "w kółko";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(8, host.Calls.Count);
        Assert.Contains("Przerwano", vm.Messages[^1].Content);
        // każda prośba o narzędzie ma swój wynik, więc historię da się wysłać ponownie
        foreach (var assistant in vm.Messages.Where(m => m.HasToolCalls))
            foreach (var call in assistant.ToolCalls!)
                Assert.Contains(vm.Messages, m => m.Role == "tool" && m.ToolCallId == call.Id);
    }

    [Fact]
    public async Task Failure_after_a_tool_ran_keeps_the_conversation()
    {
        var (vm, provider, host, _) = Create();
        provider.OnReply = history => history.Any(m => m.Role == "tool")
            ? throw new InvalidOperationException("HTTP 503")
            : new ChatReply("", [Call()]);
        vm.Input = "przeczytaj";

        await vm.SendCommand.ExecuteAsync(null);

        // narzędzie już zadziałało, więc ponowienie z pola uruchomiłoby je drugi raz
        Assert.Equal(3, vm.Messages.Count);
        Assert.Equal("", vm.Input);
        Assert.Single(host.Calls);
    }

    [Fact]
    public async Task Failure_before_any_tool_restores_the_input()
    {
        var (vm, provider, _, _) = Create();
        provider.OnReply = _ => throw new InvalidOperationException("HTTP 400");
        vm.Input = "pytanie";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(vm.Messages);
        Assert.Equal("pytanie", vm.Input);
    }

    [Fact]
    public void Tool_messages_are_labelled_for_the_conversation_view()
    {
        var request = new ChatMessage("assistant", "", [Call()]);
        var result = new ChatMessage("tool", "ok", ToolCallId: "c1", ToolName: "fs__read");

        Assert.True(request.HasToolCalls);
        Assert.Equal("Model (wywołuje narzędzie)", request.RoleLabel);
        Assert.Equal("→ fs__read({\"path\":\"a.txt\"})", request.DisplayText);
        Assert.Equal("Narzędzie fs__read", result.RoleLabel);
        Assert.Equal("ok", result.DisplayText);
        Assert.Equal("tekst", new ChatMessage("assistant", "tekst").DisplayText);
    }
}

// Format żądań i odpowiedzi z narzędziami (OpenAI-compatible)
public class ChatToolApiTests
{
    private static HttpClient Client(StubHandler handler) => new(handler) { BaseAddress = new Uri("https://example.test") };

    [Fact]
    public async Task Tools_are_sent_as_function_definitions()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}""");
        using var http = Client(handler);
        var tools = new[] { new ToolDefinition("fs__read", "Czyta plik", """{"type":"object","properties":{"path":{"type":"string"}}}""") };

        await ImageGenerationService.ChatAsync(http, "/v1/chat/completions", "m", [new ChatMessage("user", "x")], tools, default);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var function = body.RootElement.GetProperty("tools")[0];
        Assert.Equal("function", function.GetProperty("type").GetString());
        Assert.Equal("fs__read", function.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Czyta plik", function.GetProperty("function").GetProperty("description").GetString());
        Assert.Equal("string", function.GetProperty("function").GetProperty("parameters")
            .GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
    }

    [Fact]
    public async Task No_tools_means_no_tools_field()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}""");
        using var http = Client(handler);

        await ImageGenerationService.ChatAsync(http, "/v1/chat/completions", "m", [new ChatMessage("user", "x")], [], default);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Tool_calls_in_the_reply_are_parsed()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":null,"tool_calls":[
              {"id":"call_1","type":"function","function":{"name":"fs__read","arguments":"{\"path\":\"a.txt\"}"}},
              {"id":"call_2","type":"function","function":{"name":"fs__list","arguments":{"dir":"."}}},
              {"type":"function","function":{"name":"fs__noargs"}}]}}]}
            """);
        using var http = Client(handler);

        var reply = await ImageGenerationService.ChatAsync(http, "/v1/chat/completions", "m", [new ChatMessage("user", "x")], null, default);

        Assert.Equal("", reply.Content);
        Assert.Equal(3, reply.ToolCalls.Count);
        Assert.Equal(new ToolCall("call_1", "fs__read", """{"path":"a.txt"}"""), reply.ToolCalls[0]);
        Assert.Equal("""{"dir":"."}""", reply.ToolCalls[1].ArgumentsJson);
        Assert.Equal("{}", reply.ToolCalls[2].ArgumentsJson);
        Assert.False(string.IsNullOrEmpty(reply.ToolCalls[2].Id));
    }

    [Fact]
    public async Task Tool_conversation_is_serialized_with_tool_calls_and_tool_call_id()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}""");
        using var http = Client(handler);
        var history = new[]
        {
            new ChatMessage("user", "przeczytaj"),
            new ChatMessage("assistant", "", [new ToolCall("c1", "fs__read", """{"path":"a.txt"}""")]),
            new ChatMessage("tool", "treść", ToolCallId: "c1", ToolName: "fs__read"),
        };

        await ImageGenerationService.ChatAsync(http, "/v1/chat/completions", "m", history, null, default);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        var assistant = messages[1];
        Assert.Equal(JsonValueKind.Null, assistant.GetProperty("content").ValueKind);
        var call = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("c1", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("fs__read", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"path":"a.txt"}""", call.GetProperty("function").GetProperty("arguments").GetString());
        var tool = messages[2];
        Assert.Equal("tool", tool.GetProperty("role").GetString());
        Assert.Equal("c1", tool.GetProperty("tool_call_id").GetString());
        Assert.Equal("treść", tool.GetProperty("content").GetString());
        Assert.False(tool.TryGetProperty("tool_calls", out _));
    }
}

// Konfiguracja serwerów MCP i panel serwerów
public sealed class McpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "imagestudio-tests-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "mcp.json");

    public McpTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Config_reads_local_and_remote_servers_in_claude_desktop_format()
    {
        File.WriteAllText(ConfigPath, """
            {
              // komentarze są dozwolone
              "mcpServers": {
                "fs": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\dane"], "env": { "A": "1" } },
                "zdalny": { "url": "https://example.test/mcp", "headers": { "Authorization": "Bearer x" } },
              }
            }
            """);

        var config = McpConfig.Load(ConfigPath);

        Assert.Null(config.Error);
        Assert.Equal(["fs", "zdalny"], config.McpServers.Keys.Order());
        var fs = config.McpServers["fs"];
        Assert.Equal("npx", fs.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "C:\\dane"], fs.Args);
        Assert.Equal("1", fs.Env["A"]);
        Assert.Equal("npx -y @modelcontextprotocol/server-filesystem C:\\dane", fs.Description);
        var remote = config.McpServers["zdalny"];
        Assert.Equal("https://example.test/mcp", remote.Url);
        Assert.Equal("Bearer x", remote.Headers["Authorization"]);
        Assert.Equal("https://example.test/mcp", remote.Description);
    }

    [Fact]
    public void Config_missing_file_is_empty_and_invalid_json_reports_an_error()
    {
        Assert.Empty(McpConfig.Load(ConfigPath).McpServers);

        File.WriteAllText(ConfigPath, "{ nie json");
        var broken = McpConfig.Load(ConfigPath);

        Assert.Empty(broken.McpServers);
        Assert.StartsWith("mcp.json:", broken.Error);
    }

    [Fact]
    public void EnsureFile_creates_a_valid_empty_config_once()
    {
        McpConfig.EnsureFile(ConfigPath);
        File.AppendAllText(ConfigPath, " ");
        var written = File.ReadAllText(ConfigPath);

        McpConfig.EnsureFile(ConfigPath);

        Assert.Equal(written, File.ReadAllText(ConfigPath));
        Assert.Null(McpConfig.Load(ConfigPath).Error);
    }

    [Theory]
    [InlineData("fs", "read_file", "fs__read_file")]
    [InlineData("moj serwer", "git.status", "moj_serwer__git_status")]
    [InlineData("zażółć", "narzędzie", "za______narz_dzie")]
    public void Model_names_contain_only_allowed_characters(string server, string tool, string expected)
    {
        Assert.Equal(expected, McpToolHost.ModelName(server, tool));
    }

    [Fact]
    public void Model_names_are_at_most_64_characters()
    {
        Assert.Equal(64, McpToolHost.ModelName(new string('s', 40), new string('t', 40)).Length);
    }

    [Fact]
    public async Task Unknown_tool_is_reported_as_text()
    {
        await using var host = new McpToolHost();

        var result = await host.CallAsync(new ToolCall("1", "nie_ma__takiego", "{}"));

        Assert.Contains("Nieznane narzędzie", result);
    }

    [Fact]
    public async Task Server_list_comes_from_config_and_keeps_names_missing_from_it()
    {
        File.WriteAllText(ConfigPath, """{"mcpServers":{"b":{"command":"x"},"a":{"url":"https://example.test"}}}""");
        var vm = new ToolsViewModel(new McpToolHost(), useTools: false, requireApproval: true, enabledServers: ["usunięty"], ConfigPath);

        await vm.InitializeAsync();

        Assert.Equal(["a", "b"], vm.Servers.Select(s => s.Name));
        Assert.All(vm.Servers, s => Assert.False(s.IsEnabled));
        Assert.Contains("usunięty", vm.EnabledServers);
        Assert.Empty(vm.Tools);
        Assert.Equal("Narzędzia wyłączone", vm.Summary);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Server_that_fails_to_start_is_switched_off_with_an_error_status()
    {
        File.WriteAllText(ConfigPath, """{"mcpServers":{"zepsuty":{"command":"to-polecenie-nie-istnieje-imagestudio"}}}""");
        var vm = new ToolsViewModel(new McpToolHost(), useTools: true, requireApproval: true, enabledServers: ["zepsuty"], ConfigPath);

        await vm.InitializeAsync();

        var server = Assert.Single(vm.Servers);
        Assert.False(server.IsEnabled);
        Assert.StartsWith("Błąd:", server.Status);
        Assert.DoesNotContain("zepsuty", vm.EnabledServers);
        Assert.Empty(vm.Tools);
        await vm.DisposeAsync();
    }
}
