using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ImageStudio.Tests;

// Atrapa okna aplikacji: zapamiętuje wywołania, a wyniki są z góry zadane
internal sealed class FakeAppControl : IAppControl
{
    public List<string> Calls { get; } = [];
    public string[] Kinds { get; set; } = ["generate", "edit", "video"];
    public Func<string, Task<string>> OnGenerate { get; set; } = kind => Task.FromResult($"Gotowe: C:\\wyniki\\{kind}.png");

    public AppState GetState() => new("generate", "Fake", "model-a", null, null, "kot", "", "", "1024x1024", 0, false, "Gotowy");

    public IReadOnlyList<string> AvailableKinds() => Kinds;

    public IReadOnlyList<ModelSummary> ListModels(string kind, string? query, int limit)
    {
        Calls.Add($"list_models:{kind}:{query}:{limit}");
        return [new ModelSummary("model-a", "owner", "$0,04/szt.", true, true)];
    }

    public void SelectModel(string kind, string id) => Calls.Add($"select_model:{kind}:{id}");
    public void SetPrompt(string kind, string prompt, string? systemPrompt) => Calls.Add($"set_prompt:{kind}:{prompt}:{systemPrompt}");
    public void SetSize(int width, int height) => Calls.Add($"set_size:{width}x{height}");
    public void SetVideoOptions(int? durationSeconds, string? aspectRatio) => Calls.Add($"set_video_options:{durationSeconds}:{aspectRatio}");
    public void SwitchTab(string tab) => Calls.Add($"switch_tab:{tab}");
    public void AddReference(string path) => Calls.Add($"add_reference:{Path.GetFileName(path)}");
    public void ClearReferences() => Calls.Add("clear_references");

    public Task<string> GenerateAsync(string kind)
    {
        Calls.Add($"generate:{kind}");
        return OnGenerate(kind);
    }

    public Task AddReferenceFromUrlAsync(string url)
    {
        Calls.Add($"add_reference_url:{url}");
        return Task.CompletedTask;
    }

    public void RemoveReference(int index) => Calls.Add($"remove_reference:{index}");
    public void AddVideoAttachment(string path) => Calls.Add($"add_video_attachment:{Path.GetFileName(path)}");
    public void ClearVideoAttachments() => Calls.Add("clear_video_attachments");
    public void SetAspect(string ratio) => Calls.Add($"set_aspect:{ratio}");

    public Task ReloadModelsAsync()
    {
        Calls.Add("reload_models");
        return Task.CompletedTask;
    }
}

public sealed class AppControlToolsTests : IDisposable
{
    private readonly FakeAppControl _app = new();
    private readonly List<ToolCall> _confirms = [];
    private bool _approve = true;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "imagestudio-tests-" + Guid.NewGuid().ToString("N"));

    public AppControlToolsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AppControlTools Tools() => new(_app, call =>
    {
        _confirms.Add(call);
        return _approve;
    });

    [Fact]
    public void Definitions_cover_the_app_and_have_object_schemas()
    {
        var definitions = Tools().Definitions;

        Assert.Equal(
            ["add_reference", "add_video_attachment", "clear_references", "clear_video_attachments", "generate", "get_state",
             "list_models", "reload_models", "remove_reference", "select_model", "set_aspect", "set_prompt", "set_size",
             "set_video_options", "switch_tab"],
            definitions.Select(d => d.Name).Order());
        foreach (var definition in definitions)
        {
            using var schema = JsonDocument.Parse(definition.ParametersJson);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
        }
    }

    [Fact]
    public void Only_generate_is_costly()
    {
        var tools = Tools();

        Assert.True(tools.IsCostly("generate"));
        Assert.All(tools.Definitions.Where(d => d.Name != "generate"), d => Assert.False(tools.IsCostly(d.Name)));
        Assert.False(tools.IsCostly("nie_ma"));
    }

    [Fact]
    public async Task Get_state_returns_json_with_the_app_state()
    {
        var result = await Tools().CallAsync("get_state", "{}", false);

        using var json = JsonDocument.Parse(result);
        Assert.Equal("generate", json.RootElement.GetProperty("Tab").GetString());
        Assert.Equal("model-a", json.RootElement.GetProperty("GenerationModel").GetString());
        Assert.False(json.RootElement.TryGetProperty("EditModel", out _)); // null pomijane
    }

    [Fact]
    public async Task Provider_without_models_hides_image_tools_and_rejects_calls()
    {
        _app.Kinds = [];
        var tools = Tools();

        Assert.Equal(["get_state", "reload_models", "switch_tab"], tools.Definitions.Select(d => d.Name).Order());
        Assert.Equal(15, tools.AllDefinitions.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => tools.CallAsync("generate", """{"kind":"generate"}""", true));
    }

    [Fact]
    public async Task Kinds_narrow_to_what_the_provider_offers()
    {
        _app.Kinds = ["generate"];
        var tools = Tools();
        var names = tools.Definitions.Select(d => d.Name).ToList();

        Assert.Contains("set_size", names);
        Assert.DoesNotContain("set_video_options", names);
        Assert.Contains("""["generate"]""", tools.Definitions.Single(d => d.Name == "generate").ParametersJson);
        await Assert.ThrowsAsync<ArgumentException>(() => tools.CallAsync("list_models", """{"kind":"video"}""", false));
    }

    [Fact]
    public async Task List_models_passes_filters_and_clamps_the_limit()
    {
        await Tools().CallAsync("list_models", """{"kind":"edit","query":"flux","limit":500}""", false);
        await Tools().CallAsync("list_models", """{"kind":"video"}""", false);

        Assert.Equal(["list_models:edit:flux:100", "list_models:video::30"], _app.Calls);
    }

    [Fact]
    public async Task List_models_serializes_polish_text_without_escaping()
    {
        var result = await Tools().CallAsync("list_models", """{"kind":"generate"}""", false);

        Assert.Contains("$0,04/szt.", result);
        Assert.Contains("model-a", result);
    }

    [Fact]
    public async Task Simple_tools_call_through_to_the_app()
    {
        var tools = Tools();
        var reference = Path.Combine(_dir, "ref.png");
        File.WriteAllBytes(reference, [1]);

        await tools.CallAsync("select_model", """{"kind":"edit","id":"qwen/edit"}""", false);
        await tools.CallAsync("set_prompt", """{"kind":"generate","prompt":"kot","system_prompt":"realizm"}""", false);
        await tools.CallAsync("set_prompt", """{"kind":"video","prompt":"fala"}""", false);
        await tools.CallAsync("set_size", """{"width":768,"height":512}""", false);
        await tools.CallAsync("set_video_options", """{"duration":8,"aspect_ratio":"9:16"}""", false);
        await tools.CallAsync("switch_tab", """{"tab":"video"}""", false);
        await tools.CallAsync("add_reference", JsonSerializer.Serialize(new { path = reference }), false);
        await tools.CallAsync("clear_references", "{}", false);
        await tools.CallAsync("add_reference", """{"url":"https://x.test/a.png"}""", false);
        await tools.CallAsync("remove_reference", """{"index":1}""", false);
        await tools.CallAsync("add_video_attachment", JsonSerializer.Serialize(new { path = reference }), false);
        await tools.CallAsync("clear_video_attachments", "{}", false);
        await tools.CallAsync("set_aspect", """{"ratio":"source"}""", false);
        await tools.CallAsync("reload_models", "{}", false);

        Assert.Equal(
            ["select_model:edit:qwen/edit", "set_prompt:generate:kot:realizm", "set_prompt:video:fala:", "set_size:768x512",
             "set_video_options:8:9:16", "switch_tab:video", "add_reference:ref.png", "clear_references",
             "add_reference_url:https://x.test/a.png", "remove_reference:1", "add_video_attachment:ref.png", "clear_video_attachments",
             "set_aspect:source", "reload_models"],
            _app.Calls);
    }

    [Theory]
    [InlineData("select_model", """{"kind":"audio","id":"x"}""", "Nieznany rodzaj")]
    [InlineData("select_model", """{"kind":"edit"}""", "Brak parametru \"id\"")]
    [InlineData("set_prompt", """{"kind":"generate"}""", "Brak parametru \"prompt\"")]
    [InlineData("set_size", """{"width":10,"height":512}""", "zakresie")]
    [InlineData("set_size", """{"width":512}""", "Brak parametru \"height\"")]
    [InlineData("switch_tab", """{"tab":"ustawienia"}""", "Nieznana zakładka")]
    [InlineData("add_reference", """{"path":"C:\\nie\\ma\\takiego.png"}""", "nie istnieje")]
    [InlineData("nie_ma_takiego", "{}", "Nieznane narzędzie")]
    public async Task Invalid_arguments_are_rejected_before_touching_the_app(string tool, string arguments, string expected)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Tools().CallAsync(tool, arguments, false));

        Assert.Contains(expected, error.Message);
        Assert.Empty(_app.Calls);
    }

    [Fact]
    public async Task Generate_asks_for_confirmation_and_runs_when_approved()
    {
        var result = await Tools().CallAsync("generate", """{"kind":"generate"}""", alreadyApproved: false);

        var asked = Assert.Single(_confirms);
        Assert.Equal("generate", asked.Name);
        Assert.Equal(["generate:generate"], _app.Calls);
        Assert.Equal("Gotowe: C:\\wyniki\\generate.png", result);
    }

    [Fact]
    public async Task Generate_does_nothing_when_the_user_declines()
    {
        _approve = false;

        var result = await Tools().CallAsync("generate", """{"kind":"video"}""", alreadyApproved: false);

        Assert.Contains("odrzucił", result);
        Assert.Empty(_app.Calls);
    }

    [Fact]
    public async Task Generate_does_not_ask_again_when_the_chat_already_approved()
    {
        await Tools().CallAsync("generate", """{"kind":"edit"}""", alreadyApproved: true);

        Assert.Empty(_confirms);
        Assert.Equal(["generate:edit"], _app.Calls);
    }

    [Fact]
    public async Task Generate_failure_propagates_the_reason()
    {
        _app.OnGenerate = _ => throw new InvalidOperationException("Generowanie wymaga promptu i wybranego modelu.");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Tools().CallAsync("generate", """{"kind":"generate"}""", true));

        Assert.Contains("wymaga promptu", error.Message);
    }
}

// Narzędzia aplikacji w czacie: przedrostek, przełączniki i zgoda
public class AppToolsInChatTests
{
    private readonly FakeAppControl _app = new();
    private readonly List<ToolCall> _confirms = [];

    private ToolsViewModel Create(bool useTools = true, bool appControl = true, bool requireApproval = true) =>
        new(new McpToolHost(), useTools, requireApproval, [], null,
            new AppControlTools(_app, call =>
            {
                _confirms.Add(call);
                return true;
            }), appControl);

    [Fact]
    public void App_tools_get_the_chat_prefix_only_when_enabled()
    {
        Assert.Contains(Create().Tools, t => t.Name == "imagestudio__get_state");
        Assert.All(Create().Tools, t => Assert.StartsWith(AppControlTools.ChatPrefix, t.Name));
        Assert.Empty(Create(appControl: false).Tools);
        Assert.Empty(Create(useTools: false).Tools);
    }

    [Fact]
    public async Task Prefixed_call_reaches_the_app()
    {
        var tools = Create();

        var result = await tools.CallAsync(new ToolCall("1", "imagestudio__set_size", """{"width":512,"height":512}"""));

        Assert.Equal("Ustawiono rozmiar 512x512.", result);
        Assert.Equal(["set_size:512x512"], _app.Calls);
    }

    [Fact]
    public async Task Disabled_app_control_refuses_calls()
    {
        var tools = Create(appControl: false);

        var result = await tools.CallAsync(new ToolCall("1", "imagestudio__generate", """{"kind":"generate"}"""));

        Assert.Contains("wyłączone", result);
        Assert.Empty(_app.Calls);
    }

    [Fact]
    public async Task Chat_approval_covers_generation_but_without_it_the_app_asks()
    {
        var generate = new ToolCall("1", "imagestudio__generate", """{"kind":"generate"}""");

        await Create(requireApproval: true).CallAsync(generate);
        Assert.Empty(_confirms); // czat zapytał już wcześniej

        await Create(requireApproval: false).CallAsync(generate);
        Assert.Single(_confirms);
        Assert.Equal(["generate:generate", "generate:generate"], _app.Calls);
    }

    [Fact]
    public void Switching_app_control_updates_the_tool_count()
    {
        var tools = Create(appControl: false);
        Assert.Equal("Dostępne narzędzia: 0", tools.Summary);

        tools.AppControlEnabled = true;

        Assert.Equal($"Dostępne narzędzia: {tools.Tools.Count}", tools.Summary);
        Assert.True(tools.Tools.Count > 5);
    }
}

// Prawdziwy serwer MCP po HTTP (localhost) i klient SDK
public sealed class McpAppServerTests : IAsyncDisposable
{
    private const string Token = "test-token-123";

    private readonly FakeAppControl _app = new();
    private readonly List<ToolCall> _confirms = [];
    private bool _approve = true;
    private readonly McpAppServer _server;
    private readonly int _port = FreePort();

    public McpAppServerTests()
    {
        var tools = new AppControlTools(_app, call =>
        {
            _confirms.Add(call);
            return _approve;
        });
        _server = new McpAppServer(tools, action => action());
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<McpClient> ConnectAsync(string token = Token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(McpAppServer.EndpointFor(_port)),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        });
        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task Requests_without_the_right_token_are_rejected()
    {
        await _server.StartAsync(_port, Token);
        using var http = new HttpClient { BaseAddress = new Uri(McpAppServer.EndpointFor(_port)) };
        var body = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("", body)).StatusCode);

        http.DefaultRequestHeaders.Add("Authorization", "Bearer zly-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("", body)).StatusCode);
    }

    [Fact]
    public async Task Requests_from_a_foreign_origin_are_rejected_even_with_the_token()
    {
        await _server.StartAsync(_port, Token);
        using var http = new HttpClient { BaseAddress = new Uri(McpAppServer.EndpointFor(_port)) };
        http.DefaultRequestHeaders.Add("Authorization", "Bearer " + Token);
        http.DefaultRequestHeaders.Add("Origin", "https://zlosliwa-strona.example");

        var response = await http.PostAsync("", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("https://example.com", false)]
    [InlineData("null", false)]
    public void Origin_check(string origin, bool allowed)
    {
        Assert.Equal(allowed, McpAppServer.IsAllowedOrigin(origin));
    }

    [Fact]
    public async Task Client_lists_the_app_tools()
    {
        await _server.StartAsync(_port, Token);
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, t => t.Name == "get_state");
        Assert.Contains(tools, t => t.Name == "generate");
        Assert.Equal(new AppControlTools(_app, _ => true).Definitions.Count, tools.Count);
    }

    [Fact]
    public async Task Client_calls_a_tool_and_gets_text_back()
    {
        await _server.StartAsync(_port, Token);
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("set_size", new Dictionary<string, object?> { ["width"] = 640, ["height"] = 480 });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Ustawiono rozmiar 640x480.", ((TextContentBlock)result.Content[0]).Text);
        Assert.Equal(["set_size:640x480"], _app.Calls);
    }

    [Fact]
    public async Task Generation_from_a_client_needs_confirmation_in_the_app()
    {
        await _server.StartAsync(_port, Token);
        await using var client = await ConnectAsync();

        _approve = false;
        var declined = await client.CallToolAsync("generate", new Dictionary<string, object?> { ["kind"] = "generate" });
        Assert.Contains("odrzucił", ((TextContentBlock)declined.Content[0]).Text);
        Assert.Empty(_app.Calls);

        _approve = true;
        var accepted = await client.CallToolAsync("generate", new Dictionary<string, object?> { ["kind"] = "generate" });
        Assert.Contains("Gotowe", ((TextContentBlock)accepted.Content[0]).Text);
        Assert.Equal(["generate:generate"], _app.Calls);
        Assert.Equal(2, _confirms.Count);
    }

    [Fact]
    public async Task Tool_errors_come_back_as_error_results()
    {
        await _server.StartAsync(_port, Token);
        await using var client = await ConnectAsync();

        var invalid = await client.CallToolAsync("set_size", new Dictionary<string, object?> { ["width"] = 5, ["height"] = 5 });
        var unknown = await client.CallToolAsync("nie_ma", new Dictionary<string, object?>());

        Assert.True(invalid.IsError);
        Assert.Contains("zakresie", ((TextContentBlock)invalid.Content[0]).Text);
        Assert.True(unknown.IsError);
    }

    [Fact]
    public async Task Stopped_server_stops_listening()
    {
        await _server.StartAsync(_port, Token);
        Assert.True(_server.IsRunning);

        await _server.StopAsync();

        Assert.False(_server.IsRunning);
        using var tcp = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => tcp.ConnectAsync(IPAddress.Loopback, _port));
    }
}

// Panel serwera: start i stop, błąd zajętego portu, konfiguracja klienta
public sealed class AppServerViewModelTests : IAsyncDisposable
{
    private readonly FakeDialogs _dialogs = new();
    private readonly List<AppServerViewModel> _created = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _created) await vm.DisposeAsync();
    }

    private AppServerViewModel Create(bool enabled, int port)
    {
        var tools = new AppControlTools(new FakeAppControl(), _ => true);
        var vm = new AppServerViewModel(new McpAppServer(tools, action => action()), _dialogs, enabled, port, () => "tok-xyz");
        _created.Add(vm);
        return vm;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "Warunek nie został spełniony w czasie");
    }

    [Fact]
    public async Task Enabling_starts_the_server_and_disabling_stops_it()
    {
        var vm = Create(enabled: false, McpAppServerTests.FreePort());
        await vm.InitializeAsync();
        Assert.Equal("", vm.Status);

        vm.Enabled = true;
        await WaitUntil(() => vm.Status.StartsWith("Działa"));
        Assert.Contains(vm.Endpoint, vm.Status);

        vm.Enabled = false;
        await WaitUntil(() => vm.Status == "Zatrzymany");
    }

    [Fact]
    public async Task Saved_enabled_state_starts_the_server_on_initialize()
    {
        var vm = Create(enabled: true, McpAppServerTests.FreePort());

        await vm.InitializeAsync();

        Assert.StartsWith("Działa", vm.Status);
        Assert.True(vm.Enabled);
    }

    [Fact]
    public async Task Busy_port_switches_the_server_off_with_an_error()
    {
        var port = McpAppServerTests.FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        try
        {
            var vm = Create(enabled: false, port);

            vm.Enabled = true;
            await WaitUntil(() => vm.Status.StartsWith("Błąd"));

            Assert.False(vm.Enabled);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void Client_config_has_the_endpoint_and_bearer_token_and_can_be_copied()
    {
        var vm = Create(enabled: false, 40123);

        using var json = JsonDocument.Parse(vm.ClientConfig);
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("imagestudio");
        Assert.Equal("http://127.0.0.1:40123/mcp", server.GetProperty("url").GetString());
        Assert.Equal("Bearer tok-xyz", server.GetProperty("headers").GetProperty("Authorization").GetString());

        vm.CopyConfigCommand.Execute(null);

        Assert.Equal(vm.ClientConfig, _dialogs.CopiedText);
    }

    [Fact]
    public void Invalid_port_falls_back_to_the_default()
    {
        Assert.Equal(AppServerViewModel.DefaultPort, Create(false, 0).Port);
        Assert.Equal(AppServerViewModel.DefaultPort, Create(false, 70000).Port);
        Assert.Equal(40000, Create(false, 40000).Port);
    }
}
