using System.Net;
using System.Text;
using System.Text.Json;
using ImageStudio.Services;
using ImageStudio.Storage.Local;

namespace ImageStudio.Tests;

// Proporcje wyniku edycji: dostawca prosi o nie model (aspect_ratio / size wg schematu), a rdzeń dopasowuje wynik lokalnie
public sealed class EditAspectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-aspect-" + Guid.NewGuid().ToString("N"));

    public EditAspectTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static JsonElement Schema(string properties) =>
        JsonDocument.Parse("{\"type\":\"object\",\"properties\":" + properties + "}").RootElement.Clone();

    private const string Ratios = """{"aspect_ratio":{"enum":["auto","1:1","3:2","16:9","9:16","2:3","3:4","4:5"],"type":"string"}}""";
    private const string Sizes = """{"size":{"enum":["1024x1024","1024x768","768x1024","1536x1024","1024x1536"],"type":"string"}}""";

    [Theory]
    [InlineData(0.75, "3:4")]
    [InlineData(1.0, "1:1")]
    [InlineData(16.0 / 9, "16:9")]
    [InlineData(0.5, "9:16")]
    [InlineData(0.8, "4:5")]
    [InlineData(1.5, "3:2")]
    public void The_closest_allowed_aspect_ratio_is_chosen(double aspect, string expected)
    {
        var shape = ImageGenerationService.PickShape(Schema(Ratios), aspect);

        Assert.Equal(("aspect_ratio", expected), shape);
    }

    [Theory]
    [InlineData(0.75, "768x1024")]
    [InlineData(1.0, "1024x1024")]
    [InlineData(1.46, "1536x1024")]
    public void Size_is_the_fallback_when_the_model_has_no_aspect_ratio(double aspect, string expected) =>
        Assert.Equal(("size", expected), ImageGenerationService.PickShape(Schema(Sizes), aspect));

    [Fact]
    public void Aspect_ratio_wins_over_size_and_auto_is_never_sent()
    {
        var both = Schema("{\"aspect_ratio\":{\"enum\":[\"auto\",\"1:1\"]}," + Sizes[1..]);

        Assert.Equal(("aspect_ratio", "1:1"), ImageGenerationService.PickShape(both, 0.9));
        Assert.Null(ImageGenerationService.PickShape(Schema("{\"aspect_ratio\":{\"enum\":[\"auto\"]}}"), 0.75));
    }

    [Fact]
    public void Models_without_shape_fields_or_a_schema_get_nothing()
    {
        Assert.Null(ImageGenerationService.PickShape(null, 0.75));
        Assert.Null(ImageGenerationService.PickShape(Schema("{\"prompt\":{\"type\":\"string\"}}"), 0.75));
        Assert.Null(ImageGenerationService.PickShape(Schema(Ratios), 0));
    }

    // Lokalny serwer: odpowiada kolejno zadanymi statusami i zapisuje metodę, ścieżkę i surowe ciało (multipart jako tekst)
    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public List<(string Method, string Path, string Body)> Requests { get; } = [];
        public string Url { get; }

        public Server(params (int Status, string Body)[] script)
        {
            Url = $"http://127.0.0.1:{TestPorts.Free()}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                foreach (var (status, body) in script)
                {
                    var context = await _listener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.Latin1);
                    Requests.Add((context.Request.HttpMethod, context.Request.Url!.AbsolutePath, await reader.ReadToEndAsync()));
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public Task Finished => _loop;
        public void Dispose() => _listener.Close();
    }

    private string Picture()
    {
        var path = Path.Combine(_dir, "a.png");
        File.WriteAllBytes(path, TestImages.Png(4, 4));
        return path;
    }

    private static string Image => "{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(TestImages.Png(4, 4)) + "\"}]}";

    private static string ModelWith(string properties) => "{\"id\":\"m/x\",\"parameters\":{\"type\":\"object\",\"properties\":" + properties + "}}";

    [Fact]
    public async Task The_model_is_asked_for_the_requested_shape_in_the_edit_request()
    {
        using var server = new Server((200, ModelWith(Ratios)), (200, Image));
        using var service = new ImageGenerationService(server.Url, "key");

        await service.EditAsync("m/x", "ciemne włosy", [Picture()], new ImageEditOptions(0.75));
        await server.Finished;

        Assert.Equal(("GET", "/v1/models/m/x"), (server.Requests[0].Method, server.Requests[0].Path));
        var post = server.Requests[1];
        Assert.Equal("POST", post.Method);
        Assert.Contains("name=aspect_ratio", post.Body);
        Assert.Contains("3:4", post.Body);
        Assert.Contains("name=\"image[]\"", post.Body);
    }

    [Fact]
    public async Task A_rejected_shape_field_is_dropped_remembered_and_not_sent_again()
    {
        var rejected = "{\"error\":{\"message\":\"Model 'm/x' does not accept a aspect_ratio parameter.\"}}";
        using var server = new Server((200, ModelWith(Ratios)), (400, rejected), (200, Image), (200, Image));
        using var service = new ImageGenerationService(server.Url, "key");

        await service.EditAsync("m/x", "p", [Picture()], new ImageEditOptions(0.75));
        await service.EditAsync("m/x", "p", [Picture()], new ImageEditOptions(0.75));
        await server.Finished;

        var posts = server.Requests.Where(r => r.Method == "POST").ToList();
        Assert.Equal(3, posts.Count);
        Assert.Contains("name=aspect_ratio", posts[0].Body);
        Assert.DoesNotContain("name=aspect_ratio", posts[1].Body);
        Assert.DoesNotContain("name=aspect_ratio", posts[2].Body);
        Assert.Single(server.Requests, r => r.Method == "GET");
    }

    [Fact]
    public async Task Other_errors_are_not_swallowed()
    {
        using var server = new Server((200, ModelWith(Ratios)), (503, "{\"error\":{\"message\":\"model overloaded\"}}"));
        using var service = new ImageGenerationService(server.Url, "key");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EditAsync("m/x", "p", [Picture()], new ImageEditOptions(0.75)));

        Assert.Contains("overloaded", error.Message);
    }

    [Fact]
    public async Task Without_options_the_request_is_unchanged_and_needs_no_schema()
    {
        using var server = new Server((200, Image));
        using var service = new ImageGenerationService(server.Url, "key");

        await service.EditAsync("m/x", "p", [Picture()]);
        await server.Finished;

        var only = Assert.Single(server.Requests);
        Assert.Equal("POST", only.Method);
        Assert.DoesNotContain("aspect_ratio", only.Body);
    }

    [Fact]
    public async Task StudioApi_asks_the_provider_for_the_source_proportions_and_still_fits_the_result()
    {
        var provider = new FakeProvider { OnEditImage = (_, _, _) => new ImageResult(TestImages.Png(160, 90), null) };
        var studio = new StudioApi(() => provider, new LocalMediaStorage { Roots = { [Storage.MediaKind.Image] = _dir } }, new MemoryLibrary(), new MemoryPresetStore())
        {
            Catalog = new ProviderCatalog(new ImageModels([], [new ImageModelInfo("edit/a", null, false, ["image"])]), null, [], new Dictionary<string, ModelPricing>(), []),
        };
        var portrait = Path.Combine(_dir, "p.png");
        File.WriteAllBytes(portrait, TestImages.Png(30, 40));

        var result = await studio.EditImageAsync(new EditRequest("x", null, [portrait]));

        Assert.Equal(0.75, provider.LastEditOptions!.Aspect!.Value, 3);
        // dostawca zwrócił poziomy kadr 160x90, wynik wraca do pionowych proporcji źródła
        Assert.Equal((32, 40), TestImages.Size(result.Bytes!));
    }
}
