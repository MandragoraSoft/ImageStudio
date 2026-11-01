using System.Net;
using System.Text;
using System.Text.Json;
using ImageStudio.Services;

namespace ImageStudio.Tests;

// Żądanie wideo UberLLM: pola wg schematu modelu (GET /v1/models/<id>), a gdy API mimo to odrzuci parametr, ponawianie bez niego
public sealed class RejectedParameterTests
{
    [Theory]
    [InlineData("{\"error\":{\"message\":\"Model 'wan-2-6-flash-image-to-video' does not accept a aspect_ratio parameter.\",\"type\":\"invalid_request_error\"}}", "aspect_ratio")]
    [InlineData("{\"error\":{\"message\":\"Unknown parameter: 'seed'.\"}}", "seed")]
    [InlineData("{\"error\":{\"message\":\"Unrecognized request argument supplied: negative_prompt\"}}", "negative_prompt")]
    [InlineData("Model x does not support an duration parameter", "duration")]
    public void Rejected_parameter_name_is_read_from_common_error_messages(string body, string expected)
    {
        Assert.Equal(expected, ApiErrors.RejectedParameter(body));
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"Rate limit exceeded\"}}")]
    [InlineData("{\"error\":\"Invalid API key\"}")]
    [InlineData("")]
    [InlineData("<html>502</html>")]
    public void Other_errors_are_not_mistaken_for_a_rejected_parameter(string body)
    {
        Assert.Null(ApiErrors.RejectedParameter(body));
    }

    private sealed record Seen(string Method, string Path, JsonElement? Body, string? IdempotencyKey);

    // Lokalny serwer: odpowiada kolejno zadanymi statusami i zapisuje każde żądanie (metoda, ścieżka, ciało, Idempotency-Key)
    private sealed class ScriptedServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public List<Seen> Requests { get; } = [];
        public IEnumerable<JsonElement> PostBodies => Requests.Where(r => r.Method == "POST").Select(r => r.Body!.Value);
        public string Url { get; }

        public ScriptedServer(params (int Status, string Body)[] script)
        {
            var port = new Random().Next(40001, 60000);
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                foreach (var (status, body) in script)
                {
                    var context = await _listener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync();
                    Requests.Add(new Seen(context.Request.HttpMethod, context.Request.Url!.AbsolutePath,
                                          text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : null,
                                          context.Request.Headers["Idempotency-Key"]));
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

    private const string NoSchema = "{\"error\":\"not found\"}";
    private const string AspectRejected = "{\"error\":{\"message\":\"Model 'wan' does not accept a aspect_ratio parameter.\",\"code\":\"invalid_request\"}}";
    private const string Overloaded = "{\"error\":{\"message\":\"model overloaded\"}}";

    // parameters z GET /v1/models/wan/wan-2-6-flash-image-to-video (odpowiedź prawdziwego API)
    private const string WanFlashModel = """
    {"id":"wan/wan-2-6-flash-image-to-video","parameters":{"anyOf":[{"required":["image_url"]},{"required":["input_images"]}],
     "properties":{"duration_seconds":{"enum":[5,10,15],"type":"number"},"image_url":{"type":"string"},
                   "input_images":{"items":{"type":"string"},"maxItems":8,"type":"array"},
                   "model":{"const":"wan/wan-2-6-flash-image-to-video","type":"string"},"prompt":{"type":"string"}},
     "required":["model","prompt"],"type":"object"}}
    """;

    private static List<(byte[] Bytes, string Mime)> Picture() => [(TestImages.Png(4, 4), "image/png")];

    [Fact]
    public async Task Request_follows_the_model_schema_from_the_first_attempt()
    {
        using var server = new ScriptedServer((200, WanFlashModel), (503, Overloaded));
        using var service = new ImageGenerationService(server.Url, "key");
        var notes = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateVideoAsync("wan/wan-2-6-flash-image-to-video", "ruch", 7, "16:9", Picture(), notes.Add));
        await server.Finished;

        Assert.Equal(("GET", "/v1/models/wan/wan-2-6-flash-image-to-video"), (server.Requests[0].Method, server.Requests[0].Path));
        var post = server.Requests[1];
        Assert.Equal("/v1/video/generations", post.Path);
        var body = post.Body!.Value;
        Assert.False(body.TryGetProperty("aspect_ratio", out _));  // model go nie oferuje
        Assert.False(body.TryGetProperty("duration", out _));
        Assert.Equal(5, body.GetProperty("duration_seconds").GetInt32());  // 7 s -> najbliższe z 5/10/15
        Assert.StartsWith("data:image/png;base64,", body.GetProperty("image_url").GetString());
        Assert.False(body.TryGetProperty("image", out _));
        Assert.False(string.IsNullOrEmpty(post.IdempotencyKey));
        Assert.Contains(notes, n => n.Contains("5, 10, 15") && n.Contains("5 s"));
        Assert.Contains(notes, n => n.Contains("proporcji"));
    }

    [Fact]
    public async Task Schema_is_fetched_once_per_model()
    {
        using var server = new ScriptedServer((200, WanFlashModel), (503, Overloaded), (503, Overloaded));
        using var service = new ImageGenerationService(server.Url, "key");

        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.GenerateVideoAsync("wan/wan-2-6-flash-image-to-video", "ruch", 5, "16:9", Picture()));
        await server.Finished;

        Assert.Equal(["GET", "POST", "POST"], server.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task Without_a_schema_the_documented_names_are_used_and_a_rejected_parameter_is_dropped_and_remembered()
    {
        // schemat niedostępny (404) nie jest zapamiętywany, więc drugie wywołanie pyta o niego jeszcze raz
        using var server = new ScriptedServer((404, NoSchema), (400, AspectRejected), (503, Overloaded), (404, NoSchema), (503, Overloaded));
        using var service = new ImageGenerationService(server.Url, "key");
        var notes = new List<string>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateVideoAsync("x/y", "ruch", 5, "16:9", Picture(), notes.Add));
        Assert.Contains("503", failure.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateVideoAsync("x/y", "ruch", 5, "16:9", Picture()));
        await server.Finished;

        var posts = server.PostBodies.ToList();
        Assert.Equal("16:9", posts[0].GetProperty("aspect_ratio").GetString());  // nazwy z dokumentacji
        Assert.Equal(5, posts[0].GetProperty("duration_seconds").GetInt32());
        Assert.True(posts[0].TryGetProperty("image_url", out _));
        Assert.False(posts[1].TryGetProperty("aspect_ratio", out _));  // ponowione bez odrzuconego
        Assert.Equal(5, posts[1].GetProperty("duration_seconds").GetInt32());
        Assert.False(posts[2].TryGetProperty("aspect_ratio", out _));  // zapamiętane dla modelu
        Assert.Contains(notes, n => n.Contains("aspect_ratio"));

        var keys = server.Requests.Where(r => r.Method == "POST").Select(r => r.IdempotencyKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());  // inne ciało żądania = nowy klucz (inaczej 409 idempotency_key_reused)
    }

    [Fact]
    public async Task Required_fields_and_unrelated_errors_are_not_swallowed()
    {
        using var server = new ScriptedServer((404, NoSchema), (400, "{\"error\":{\"message\":\"Model 'x' does not accept a prompt parameter.\"}}"));
        using var service = new ImageGenerationService(server.Url, "key");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateVideoAsync("x/y", "p", 5, "16:9", []));

        Assert.Contains("prompt", failure.Message);
        await server.Finished;
        Assert.Single(server.PostBodies);  // brak ponawiania dla parametrów podstawowych
    }
}
