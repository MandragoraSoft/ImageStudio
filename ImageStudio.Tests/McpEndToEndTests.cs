using System.Text.Json;
using ImageStudio.Services;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ImageStudio.Tests;

// Narzędzia MCP na prawdziwym MainViewModel (jak w aplikacji): z czatu i z klienta MCP po HTTP
public sealed class McpEndToEndTests
{
    private const string Token = "tok-e2e";

    private static FakeProvider Provider()
    {
        var provider = new FakeProvider
        {
            ImageCatalog = new ImageModels(
                [new ImageModelInfo("gen/a", "own", false, ["text"])],
                [new ImageModelInfo("edit/a", "own", false, ["text", "image"]), new ImageModelInfo("edit/b", "own", false, ["text", "image"])]),
            VideoCatalog = [new ImageModelInfo("vid/a", "own", false, ["text"])],
            ChatCatalog = [new ChatModelInfo("chat/a", ["tools"]) { SupportsTools = true }],
            PricingCatalog = new Dictionary<string, ModelPricing>
            {
                ["edit/a"] = new(0.05m, null, null, "image", null, null),
            },
        };
        return provider;
    }

    private static async Task<TestApp> LoadedAsync(FakeProvider? provider = null)
    {
        var app = TestApp.Create(provider: provider ?? Provider());
        await app.Vm.InitializeAsync();
        return app;
    }

    private static IReadOnlyList<string> Ids(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.GetProperty("Id").GetString()!).ToList();

    [Fact]
    public async Task Chat_sees_list_models_and_gets_only_edit_models()
    {
        using var app = await LoadedAsync();
        var tools = app.Vm.Tools;
        tools.UseTools = true;
        tools.AppControlEnabled = true;

        Assert.Contains(tools.Tools, t => t.Name == "imagestudio__list_models");
        var result = await tools.CallAsync(new ToolCall("1", "imagestudio__list_models", """{"kind":"edit"}"""));

        Assert.Equal(["edit/a", "edit/b"], Ids(result).Order());
        Assert.Matches(@"\$0[.,]05/image", result);
        Assert.DoesNotContain("gen/a", result);
    }

    [Fact]
    public async Task Chat_exposes_the_studio_tools_next_to_the_window_tools()
    {
        using var app = await LoadedAsync();
        var tools = app.Vm.Tools;
        tools.UseTools = true;
        tools.AppControlEnabled = true;

        var names = tools.Tools.Select(t => t.Name).ToList();

        Assert.Contains("imagestudio__get_state", names);
        Assert.Contains("imagestudio__generate_image", names);
        Assert.Contains("imagestudio__edit_image", names);
        Assert.Contains("imagestudio__generate_video", names);
        Assert.Contains("imagestudio__list_gallery", names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public async Task Mcp_client_lists_and_calls_the_same_tools_over_http()
    {
        using var app = await LoadedAsync();
        var set = new CompositeToolSet(new AppControlTools(app.Vm, _ => true), new StudioTools(app.Vm.Studio, _ => true));
        await using var server = new McpAppServer(set, action => action());
        var port = McpAppServerTests.FreePort();
        await server.StartAsync(port, Token);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(McpAppServer.EndpointFor(port)),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token },
        });
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        var edit = await client.CallToolAsync("list_models", new Dictionary<string, object?> { ["kind"] = "edit" });
        var video = await client.CallToolAsync("list_models", new Dictionary<string, object?> { ["kind"] = "video" });
        var state = await client.CallToolAsync("get_state", new Dictionary<string, object?>());

        Assert.Contains(tools, t => t.Name == "list_models");
        Assert.Contains(tools, t => t.Name == "generate_image");
        Assert.Equal(set.Definitions.Count, tools.Count);
        Assert.NotEqual(true, edit.IsError);
        Assert.Equal(["edit/a", "edit/b"], Ids(((TextContentBlock)edit.Content[0]).Text).Order());
        Assert.Equal(["vid/a"], Ids(((TextContentBlock)video.Content[0]).Text));
        Assert.Contains("Fake", ((TextContentBlock)state.Content[0]).Text);
    }

    [Fact]
    public async Task A_provider_without_edit_models_gets_a_clear_error_instead_of_an_empty_list()
    {
        var provider = Provider();
        provider.ImageCatalog = new ImageModels([new ImageModelInfo("gen/a", "own", false, ["text"])], []);
        using var app = await LoadedAsync(provider);
        var tools = app.Vm.Tools;
        tools.UseTools = true;
        tools.AppControlEnabled = true;

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => tools.CallAsync(new ToolCall("1", "imagestudio__list_models", """{"kind":"edit"}""")));

        Assert.Contains("nie ma modeli rodzaju edit", error.Message);
        Assert.DoesNotContain(tools.Tools, t => t.Name == "imagestudio__edit_image");
        Assert.Contains(tools.Tools, t => t.Name == "imagestudio__list_models");
    }
}

// Fragmenty do dziennika akcji MCP: jedna linia i przycięcie
public sealed class AppLogPreviewTests
{
    [Fact]
    public void Preview_keeps_short_text_and_flattens_lines()
    {
        Assert.Equal("a b", ImageStudio.Services.AppLog.Preview(" a\r\nb "));
        Assert.Equal("", ImageStudio.Services.AppLog.Preview(null));
    }

    [Fact]
    public void Preview_truncates_long_text_and_says_how_much_was_cut()
    {
        var preview = ImageStudio.Services.AppLog.Preview(new string('x', 30), 10);

        Assert.Equal(new string('x', 10) + "… (+20 zn.)", preview);
    }
}
