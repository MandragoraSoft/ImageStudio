using ImageStudio.Cli;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.Storage;
using ImageStudio.Storage.Local;
using ImageStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ImageStudio.Tests;

// Wiersz poleceń: imagestudio mcp ... (parsowanie, tools, call, zgoda --yes, serwer HTTP) na atrapie dostawcy
public sealed class McpCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-cli-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProvider _provider = new();
    private readonly MemoryLibrary _library = new();

    public McpCliTests()
    {
        Directory.CreateDirectory(_dir);
        _provider.OnEditImage = (_, _, _) => new ImageResult(TestImages.Png(8, 8), 0.02m);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Task<IStudioApi> Studio()
    {
        var storage = new LocalMediaStorage { Roots = { [MediaKind.Image] = _dir, [MediaKind.Video] = _dir, [MediaKind.Audio] = _dir } };
        IStudioApi studio = new StudioApi(() => _provider, storage, _library, new MemoryPresetStore())
        {
            Catalog = new ProviderCatalog(
                new ImageModels([new ImageModelInfo("gen/a", "own", false, ["text"])], [new ImageModelInfo("edit/a", "own", false, ["text", "image"])]),
                null, [], new Dictionary<string, ModelPricing> { ["edit/a"] = new(0.02m, null, null, "image", null, null) }, []),
        };
        return Task.FromResult(studio);
    }

    private async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await McpCli.RunAsync(args, output, error, Studio);
        return (code, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("mcp", "serve", "stdio", false)]
    [InlineData("MCP", "serve", "stdio", false)]
    [InlineData("mcp serve --http --port 9000 --token t --yes", "serve", "http", true)]
    [InlineData("mcp --http", "serve", "http", false)]
    [InlineData("mcp tools", "tools", "stdio", false)]
    [InlineData("mcp help", "help", "stdio", false)]
    [InlineData("mcp serve --help", "help", "stdio", false)]
    public void Parse_recognizes_commands_and_options(string line, string command, string transport, bool yes)
    {
        var options = McpCli.Parse(line.Split(' '));

        Assert.Null(options.Error);
        Assert.Equal((command, transport, yes), (options.Command, options.Transport, options.Yes));
    }

    [Fact]
    public void Parse_reads_port_token_and_call_arguments()
    {
        var serve = McpCli.Parse(["mcp", "--http", "--port", "9000", "--token", "abc"]);
        var call = McpCli.Parse(["mcp", "call", "list_models", """{"kind":"edit"}""", "--yes"]);
        var bare = McpCli.Parse(["mcp", "call", "list_gallery"]);

        Assert.Equal((9000, "abc"), (serve.Port, serve.Token));
        Assert.Equal(("call", "list_models", """{"kind":"edit"}""", true), (call.Command, call.Tool, call.Json, call.Yes));
        Assert.Equal("{}", bare.Json);
    }

    [Theory]
    [InlineData("mcp --port abc")]
    [InlineData("mcp --port 70000")]
    [InlineData("mcp --nieznana")]
    [InlineData("mcp tools extra")]
    [InlineData("mcp call")]
    [InlineData("mcp call a b c")]
    public void Parse_reports_bad_input(string line) => Assert.NotNull(McpCli.Parse(line.Split(' ')).Error);

    [Fact]
    public void Parse_rejects_invalid_json_arguments() =>
        Assert.Contains("JSON", McpCli.Parse(["mcp", "call", "list_models", "{zle"]).Error);

    [Fact]
    public async Task Help_prints_usage_and_succeeds()
    {
        var (code, output, _) = await Run("mcp", "help");

        Assert.Equal(0, code);
        Assert.Contains("mcp call <narzędzie>", output);
    }

    [Fact]
    public async Task Bad_arguments_exit_with_code_2_and_usage_on_stderr()
    {
        var (code, output, error) = await Run("mcp", "call");

        Assert.Equal((2, ""), (code, output));
        Assert.Contains("Użycie", error);
    }

    [Fact]
    public async Task Tools_lists_what_the_catalog_allows()
    {
        var (code, output, _) = await Run("mcp", "tools");

        var names = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(" - ")[0]).ToList();
        Assert.Equal(0, code);
        Assert.Contains("list_models", names);
        Assert.Contains("edit_image", names);
        Assert.DoesNotContain("generate_video", names);
        Assert.DoesNotContain("speak", names);
    }

    [Fact]
    public async Task Call_prints_the_result_on_stdout()
    {
        var (code, output, error) = await Run("mcp", "call", "list_models", """{"kind":"edit"}""");

        Assert.Equal(0, code);
        Assert.Contains("edit/a", output);
        Assert.Contains("$0,02/image", output);
        Assert.Equal("", error);
    }

    [Fact]
    public async Task Costly_calls_are_refused_without_yes_and_run_with_it()
    {
        var reference = Path.Combine(_dir, "ref.png");
        File.WriteAllBytes(reference, TestImages.Png(4, 4));
        var args = System.Text.Json.JsonSerializer.Serialize(new { prompt = "jaśniej", references = new[] { reference } });

        var refused = await Run("mcp", "call", "edit_image", args);

        Assert.Equal(3, refused.Code);
        Assert.Contains("--yes", refused.Err);
        Assert.Null(_provider.LastEdit);
        Assert.Empty(_library.MediaItems);

        var accepted = await Run("mcp", "call", "edit_image", args, "--yes");

        Assert.Equal(0, accepted.Code);
        Assert.Contains("\"Kind\":\"edit\"", accepted.Out);
        Assert.Equal("edit/a", _provider.LastEdit!.Value.Model);
        Assert.Single(_library.MediaItems);
    }

    [Fact]
    public async Task Tool_errors_exit_with_code_1_and_a_message()
    {
        var (code, output, error) = await Run("mcp", "call", "edit_image", """{"prompt":"x","references":["C:/nie/ma.png"]}""", "--yes");

        Assert.Equal((1, ""), (code, output));
        Assert.Contains("Plik nie istnieje", error);
    }

    [Fact]
    public async Task Unknown_tools_fail_cleanly()
    {
        var (code, _, error) = await Run("mcp", "call", "nie_ma");

        Assert.Equal(1, code);
        Assert.Contains("Nieznane narzędzie", error);
    }

    [Fact]
    public async Task Http_serve_prints_endpoint_and_token_and_answers_clients_until_cancelled()
    {
        var port = McpAppServerTests.FreePort();
        using var cts = new CancellationTokenSource();
        var error = new StringWriter();
        var serve = McpCli.RunAsync(["mcp", "--http", "--port", port.ToString(), "--yes"], new StringWriter(), error, Studio, cts.Token, () => "tok-cli");
        await Until(() => error.ToString().Contains("tok-cli"));

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(McpAppServer.EndpointFor(port)),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer tok-cli" },
        });
        await using (var client = await McpClient.CreateAsync(transport))
        {
            var tools = await client.ListToolsAsync();
            var result = await client.CallToolAsync("list_models", new Dictionary<string, object?> { ["kind"] = "edit" });

            Assert.Contains(tools, t => t.Name == "edit_image");
            Assert.Contains("edit/a", ((TextContentBlock)result.Content[0]).Text);
        }

        cts.Cancel();
        Assert.Equal(0, await serve);
        Assert.Contains(McpAppServer.EndpointFor(port), error.ToString());
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "serwer nie wystartował w czasie");
    }
}

// Ustawienia w plikach: tryb bez okna czyta i zapisuje state.json, a okno zapisuje to samo przy zmianie wyboru
public sealed class SettingsPersistenceTests
{
    [Fact]
    public void State_file_defaults_round_trip_every_kind()
    {
        var store = new MemoryUserStateStore();
        var defaults = new StateFileDefaults(store, "uberllm");

        foreach (var (kind, id) in new[] { ("generate", "g"), ("edit", "e"), ("video", "v"), ("speech", "s"), ("transcription", "t"), ("chat", "c") })
            defaults.SetModel(kind, id);

        Assert.Equal("uberllm", defaults.ProviderId);
        Assert.Equal(["g", "e", "v", "s", "t", "c"], new[] { "generate", "edit", "video", "speech", "transcription", "chat" }.Select(defaults.Model));
        Assert.Equal(("g", "e", "v"), (store.State.GenerationModel, store.State.EditModel, store.State.VideoModel));
        Assert.Equal(("s", "t", "c"), (store.State.VoiceSpeechModel, store.State.VoiceTranscriptionModel, store.State.EnhancerModel));
        Assert.Throws<ArgumentException>(() => defaults.SetModel("audio", "x"));
        Assert.Null(defaults.Model("audio"));
    }

    private static FakeProvider Provider() => new()
    {
        ImageCatalog = new ImageModels([new ImageModelInfo("gen/a", null, false, ["text"]), new ImageModelInfo("gen/b", null, false, ["text"])],
                                       [new ImageModelInfo("edit/a", null, false, ["text", "image"]), new ImageModelInfo("edit/b", null, false, ["text", "image"])]),
        VideoCatalog = [new ImageModelInfo("vid/a", null, false, ["text"]), new ImageModelInfo("vid/b", null, false, ["text"])],
        ChatCatalog = [new ChatModelInfo("chat/a", ["tools"])],
        PricingCatalog = new Dictionary<string, ModelPricing>(),
    };

    [Fact]
    public async Task Choosing_a_model_with_a_tool_selects_it_in_the_window_and_writes_the_state_file()
    {
        using var app = TestApp.Create(provider: Provider());
        await app.Vm.InitializeAsync();

        app.Vm.Studio.SetDefaultModel("edit", "edit/b");
        app.Vm.Studio.SetDefaultModel("video", "vid/b");

        var saved = app.Get<IUserStateStore>().Load();
        Assert.Equal("edit/b", app.Vm.SelectedEditModel!.Id);
        Assert.Equal("vid/b", app.Vm.SelectedVideoModel!.Id);
        Assert.Equal(("edit/b", "vid/b"), (saved.EditModel, saved.VideoModel));
        Assert.Equal("edit/b", app.Vm.Studio.GetDefaults().Models["edit"]);
    }

    [Fact]
    public async Task A_video_model_saved_in_the_file_returns_after_restart()
    {
        var memory = new MemoryUserStateStore();
        memory.State.VideoModel = "vid/b";
        using var app = TestApp.Create(s => s.AddSingleton<IUserStateStore>(memory), Provider());

        await app.Vm.InitializeAsync();

        Assert.Equal("vid/b", app.Vm.SelectedVideoModel!.Id);
    }

    [Fact]
    public async Task Changing_the_video_model_in_the_window_is_saved_at_once()
    {
        using var app = TestApp.Create(provider: Provider());
        await app.Vm.InitializeAsync();

        app.Vm.SelectedVideoModel = app.Vm.VideoModels.Single(m => m.Id == "vid/b");

        Assert.Equal("vid/b", app.Get<IUserStateStore>().Load().VideoModel);
    }
}

// Biblioteka plikowa CLI: zmiany trafiają od razu do magazynów galerii
public sealed class FileStudioLibraryTests
{
    private static GalleryEntry Entry(string kind) => new() { Kind = kind, FilePath = "C:\\x\\" + kind };

    [Fact]
    public void Entries_are_persisted_newest_first_and_removed_by_id()
    {
        var media = new MemoryGalleryStore();
        var voice = new MemoryVoiceGalleryStore();
        var library = new FileStudioLibrary(media, voice);
        var first = Entry("generate");
        var second = Entry("edit");
        var spoken = Entry("speech");

        library.AddMedia(first);
        library.AddMedia(second);
        library.AddVoice(spoken);

        Assert.Equal([second.Id, first.Id], media.Entries.Select(e => e.Id));
        Assert.Equal([spoken.Id], voice.Entries.Select(e => e.Id));
        Assert.Equal([second.Id, first.Id], new FileStudioLibrary(media, voice).Media.Select(e => e.Id));

        Assert.True(library.Remove(first.Id));
        Assert.True(library.Remove(spoken.Id));
        Assert.False(library.Remove("brak"));
        Assert.Equal([second.Id], media.Entries.Select(e => e.Id));
        Assert.Empty(voice.Entries);
    }

    [Fact]
    public void Clear_empties_only_the_chosen_gallery_and_the_list_is_capped()
    {
        var media = new MemoryGalleryStore();
        var voice = new MemoryVoiceGalleryStore();
        var library = new FileStudioLibrary(media, voice);
        for (var i = 0; i < GalleryStore.MaxEntries + 5; i++) library.AddMedia(Entry("generate"));
        library.AddVoice(Entry("speech"));

        Assert.Equal(GalleryStore.MaxEntries, library.Media.Count);

        library.Clear(voice: false);
        Assert.Empty(media.Entries);
        Assert.Single(voice.Entries);

        library.Clear(voice: true);
        Assert.Empty(voice.Entries);
    }
}
