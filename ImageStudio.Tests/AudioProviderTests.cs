using System.Net;
using System.Text;
using System.Text.Json;
using ImageStudio.Services;

namespace ImageStudio.Tests;

// Dźwięk po HTTP: modele, głosy, synteza mowy i rozpoznawanie u UberLLM i OpenAI (lokalny serwer udaje API)
public sealed class AudioProviderTests
{
    private sealed record Seen(string Method, string Path, string? ContentType, byte[] Body, string? Authorization)
    {
        public string Text => Encoding.UTF8.GetString(Body);
        public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
    }

    private sealed record Reply(int Status, byte[] Body, string ContentType = "application/json", Dictionary<string, string>? Headers = null)
    {
        public static Reply Json(string json, int status = 200) => new(status, Encoding.UTF8.GetBytes(json));
    }

    // Serwer odpowiada wg ścieżki i zapisuje każde żądanie
    private sealed class Api : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<string, string, Reply> _route;
        public List<Seen> Requests { get; } = [];
        public string Url { get; }

        public Api(Func<string, string, Reply> route)
        {
            _route = route;
            Url = $"http://127.0.0.1:{TestPorts.Free()}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }

                using var buffer = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(buffer);
                var request = context.Request;
                var path = request.Url!.PathAndQuery;
                Requests.Add(new Seen(request.HttpMethod, path, request.ContentType, buffer.ToArray(), request.Headers["Authorization"]));

                var reply = _route(request.HttpMethod, path);
                context.Response.StatusCode = reply.Status;
                context.Response.ContentType = reply.ContentType;
                foreach (var (name, value) in reply.Headers ?? []) context.Response.AddHeader(name, value);
                await context.Response.OutputStream.WriteAsync(reply.Body);
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    private const string SpeechList = """
    {"data":[
      {"id":"google/tts-gemini","available":true,"endpoints":["/v1/audio/speech"],"owned_by":"Google","display_name":"Gemini TTS",
       "pricing":{"list_usd":0.2,"unit":"1K characters","usd":0.15}},
      {"id":"canopy-labs/tts-orpheus","available":true,"endpoints":["/v1/audio/speech"],"owned_by":"Canopy Labs",
       "pricing":{"list_usd":0.0625,"unit":"1K characters","usd":0.0625}},
      {"id":"x/off","available":false,"endpoints":["/v1/audio/speech"]},
      {"id":"x/elsewhere","available":true,"endpoints":["/v1/audio/transcriptions"]}]}
    """;

    private static UberLlmProvider Uber(Api api) => new(api.Url, "klucz", api.Url);

    [Fact]
    public async Task UberLlm_speech_models_are_the_available_ones_with_price_from_the_listing()
    {
        using var api = new Api((_, _) => Reply.Json(SpeechList));
        using var provider = Uber(api);

        var models = await provider.GetSpeechModelsAsync();

        Assert.Equal(["canopy-labs/tts-orpheus", "google/tts-gemini"], models.Select(m => m.Id));
        var gemini = models[1];
        Assert.Equal("Google", gemini.OwnedBy);
        Assert.Equal("Gemini TTS", gemini.Description);
        Assert.Equal(0.15m, gemini.Pricing!.PriceNow);
        Assert.Equal("1K characters", gemini.Pricing.Unit);
        Assert.Equal(25m, gemini.Pricing.DiscountPct);  // (0,20 - 0,15) / 0,20
        Assert.Contains("type=speech", api.Requests.Single().Path);
        Assert.Equal("Bearer klucz", api.Requests.Single().Authorization);
    }

    [Fact]
    public async Task UberLlm_transcription_models_use_the_transcription_type()
    {
        using var api = new Api((_, _) => Reply.Json("""{"data":[{"id":"fal/wizper","available":true,"endpoints":["/v1/audio/transcriptions"],"pricing":{"usd":0.0048,"unit":"minute"}}]}"""));
        using var provider = Uber(api);

        var models = await provider.GetTranscriptionModelsAsync();

        Assert.Equal("fal/wizper", models.Single().Id);
        Assert.Equal("minute", models.Single().Pricing!.Unit);
        Assert.Contains("type=transcription", api.Requests.Single().Path);
    }

    [Fact]
    public async Task Voices_come_from_the_model_schema_and_are_fetched_once()
    {
        using var api = new Api((_, _) => Reply.Json("""{"id":"m","parameters":{"properties":{"voice":{"enum":["dan","jess"],"type":"string"}}}}"""));
        using var provider = Uber(api);

        Assert.Equal(["dan", "jess"], await provider.GetSpeechVoicesAsync("m"));
        Assert.Equal(["dan", "jess"], await provider.GetSpeechVoicesAsync("m"));
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task Model_without_a_voice_list_or_schema_has_no_voices()
    {
        using var api = new Api((_, path) => path.EndsWith("/no-schema") ? Reply.Json("{}", 404) : Reply.Json("""{"parameters":{"properties":{"input":{}}}}"""));
        using var provider = Uber(api);

        Assert.Empty(await provider.GetSpeechVoicesAsync("free-voice"));
        Assert.Empty(await provider.GetSpeechVoicesAsync("no-schema"));
    }

    [Fact]
    public async Task Synthesize_sends_the_request_and_returns_the_file_with_cost_from_the_header()
    {
        var audio = new byte[] { 1, 2, 3, 4 };
        using var api = new Api((_, _) => new Reply(200, audio, "audio/mpeg", new() { ["X-UberLLM-Cost-USD"] = "0.0123" }));
        using var provider = Uber(api);

        var result = await provider.SynthesizeAsync("google/tts", "Witaj świecie", "jess", 1.25, "mp3");

        Assert.Equal(audio, result.Bytes);
        Assert.Equal("mp3", result.Extension);
        Assert.Equal(0.0123m, result.CostUsd);
        var seen = api.Requests.Single();
        Assert.Equal("POST", seen.Method);
        Assert.Equal("/v1/audio/speech", seen.Path);
        var body = seen.Json;
        Assert.Equal("google/tts", body.GetProperty("model").GetString());
        Assert.Equal("Witaj świecie", body.GetProperty("input").GetString());
        Assert.Equal("jess", body.GetProperty("voice").GetString());
        Assert.Equal(1.25, body.GetProperty("speed").GetDouble());
        Assert.Equal("mp3", body.GetProperty("response_format").GetString());
    }

    [Fact]
    public async Task Synthesize_leaves_out_voice_and_normal_speed_and_takes_the_extension_from_the_response()
    {
        using var api = new Api((_, _) => new Reply(200, [9], "audio/wav"));
        using var provider = Uber(api);

        var result = await provider.SynthesizeAsync("m", "tekst", null, 1.0, "mp3");

        Assert.Equal("wav", result.Extension);  // serwer odpowiedział innym formatem niż prosiliśmy
        Assert.Null(result.CostUsd);
        var body = api.Requests.Single().Json;
        Assert.False(body.TryGetProperty("voice", out _));
        Assert.False(body.TryGetProperty("speed", out _));
    }

    [Fact]
    public async Task Synthesize_retries_once_with_the_format_the_model_supports()
    {
        const string wavOnly = """{"error":{"message":"response_format \"mp3\" is not supported by model \"tts-orpheus\". Supported formats: wav.","code":"invalid_request"}}""";
        using var api = new Api((_, _) => new Reply(200, [1], "audio/wav"));
        using var rejecting = new Api((_, _) => Reply.Json(wavOnly, 400));
        using var provider = Uber(rejecting);

        // pierwszy serwer zawsze odrzuca: format się nie zmienia po ponowieniu, więc błąd wraca do użytkownika
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SynthesizeAsync("m", "t", null, null, "wav"));
        Assert.Contains("Supported formats: wav", ex.Message);
        Assert.Single(rejecting.Requests);

        var calls = 0;
        using var picky = new Api((_, _) => ++calls == 1 ? Reply.Json(wavOnly, 400) : new Reply(200, [4, 5], "audio/wav"));
        using var second = Uber(picky);
        var result = await second.SynthesizeAsync("m", "t", null, null, "mp3");

        Assert.Equal("wav", result.Extension);
        Assert.Equal([4, 5], result.Bytes);
        Assert.Equal(["mp3", "wav"], picky.Requests.Select(r => r.Json.GetProperty("response_format").GetString()));
    }

    [Theory]
    [InlineData("response_format \"mp3\" is not supported. Supported formats: wav.", "wav")]
    [InlineData("Supported formats: MP3, wav", "mp3")]
    [InlineData("rate limited", null)]
    public void Supported_format_is_read_from_the_error_message(string error, string? expected)
    {
        Assert.Equal(expected, OpenAiAudio.SupportedFormat(error));
    }

    [Fact]
    public async Task Synthesize_error_carries_the_status_and_the_body()
    {
        using var api = new Api((_, _) => Reply.Json("""{"error":{"code":"wrong_endpoint"}}""", 400));
        using var provider = Uber(api);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SynthesizeAsync("m", "t", null, null, "mp3"));

        Assert.Contains("HTTP 400", ex.Message);
        Assert.Contains("wrong_endpoint", ex.Message);
    }

    [Fact]
    public async Task Transcribe_uploads_the_file_as_a_form_and_reads_text_and_cost()
    {
        var path = Path.Combine(Path.GetTempPath(), "is-rec-" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("RIFF-test-audio"));
        try
        {
            using var api = new Api((_, _) => Reply.Json("""{"text":"cześć wszystkim","duration":3.5,"usage":{"type":"duration","seconds":3.5,"cost_usd":0.0007}}"""));
            using var provider = Uber(api);

            var result = await provider.TranscribeAsync("fal/wizper", path, "pl", "Kowalski", default);

            Assert.Equal("cześć wszystkim", result.Text);
            Assert.Equal(0.0007m, result.CostUsd);
            var seen = api.Requests.Single();
            Assert.Equal("/v1/audio/transcriptions", seen.Path);
            Assert.StartsWith("multipart/form-data", seen.ContentType);
            var form = seen.Text;
            foreach (var expected in new[] { "name=model", "fal/wizper", "name=language", "pl", "name=prompt", "Kowalski", "name=response_format", "json",
                                              "name=file", Path.GetFileName(path), "RIFF-test-audio" })
                Assert.Contains(expected, form);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Transcribe_without_language_and_prompt_sends_only_model_format_and_file_and_falls_back_to_the_cost_header()
    {
        var path = Path.Combine(Path.GetTempPath(), "is-rec-" + Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(path, [1]);
        try
        {
            using var api = new Api((_, _) => new Reply(200, Encoding.UTF8.GetBytes("""{"text":"ok"}"""), "application/json",
                                                         new() { ["X-UberLLM-Cost-USD"] = "0.002" }));
            using var provider = Uber(api);

            var result = await provider.TranscribeAsync("m", path, "  ", null, default);

            Assert.Equal(0.002m, result.CostUsd);
            Assert.DoesNotContain("name=language", api.Requests.Single().Text);
            Assert.DoesNotContain("name=prompt", api.Requests.Single().Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenAi_lists_models_by_name_and_offers_the_fixed_voices()
    {
        using var api = new Api((_, _) => Reply.Json("""{"data":[{"id":"gpt-4o"},{"id":"tts-1"},{"id":"gpt-4o-mini-tts"},{"id":"whisper-1"},{"id":"gpt-4o-transcribe"}]}"""));
        using var provider = new OpenAiProvider(api.Url, "sk-test");

        Assert.Equal(["gpt-4o-mini-tts", "tts-1"], (await provider.GetSpeechModelsAsync()).Select(m => m.Id));
        Assert.Equal(["gpt-4o-transcribe", "whisper-1"], (await provider.GetTranscriptionModelsAsync()).Select(m => m.Id));
        Assert.Contains("alloy", await provider.GetSpeechVoicesAsync("tts-1"));
    }

    [Fact]
    public async Task OpenAi_never_appends_the_negative_prompt_to_the_prompt_and_reports_it_unsupported()
    {
        using var api = new Api((_, _) => Reply.Json("""{"created":1,"data":[{"b64_json":"AQID"}]}"""));
        using var provider = new OpenAiProvider(api.Url, "sk-test");
        var outcomes = new List<NegativePromptOutcome>();

        await provider.GenerateImageAsync("gpt-image-1", "kot", "1024x1024", new ImageOptions(null, "bez ludzi", null, outcomes.Add));

        Assert.DoesNotContain("bez ludzi", api.Requests.Single().Text);
        Assert.Equal([NegativePromptOutcome.Unsupported], outcomes);
    }

    [Fact]
    public async Task OpenAi_synthesizes_through_the_same_endpoint_without_a_cost()
    {
        using var api = new Api((_, _) => new Reply(200, [5, 6], "audio/mpeg"));
        using var provider = new OpenAiProvider(api.Url + "/", "sk-test");

        var result = await provider.SynthesizeAsync("tts-1", "hello", "alloy", null, "mp3");

        Assert.Equal([5, 6], result.Bytes);
        Assert.Null(result.CostUsd);
        Assert.Equal("/v1/audio/speech", api.Requests.Single().Path);
        Assert.Equal("Bearer sk-test", api.Requests.Single().Authorization);
    }

    // Model tylko z Image API: chat/completions odsyła 404, dostawca ponawia na /api/v1/images i pamięta to dla modelu
    [Fact]
    public async Task OpenRouter_falls_back_to_the_image_api_when_chat_completions_refuses_the_model()
    {
        using var api = new Api((_, path) => path == "/api/v1/chat/completions"
            ? Reply.Json("""{"error":{"message":"meta/muse-image is an image generation model and cannot be used with the chat/completions endpoint. Use the /api/v1/images endpoint instead.","code":404}}""", 404)
            : Reply.Json("""{"created":1,"data":[{"b64_json":"AQID"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2,"cost":0.02}}"""));
        using var provider = new OpenRouterProvider("k", api.Url);

        var first = await provider.GenerateImageAsync("meta/muse-image", "kot", "1024x1024", new ImageOptions(7, null, null));
        var second = await provider.GenerateImageAsync("meta/muse-image", "pies", "1024x1024", null);

        Assert.Equal([1, 2, 3], first.Bytes);
        Assert.Equal(0.02m, first.CostUsd);
        Assert.Equal([1, 2, 3], second.Bytes);
        Assert.Equal(["/api/v1/chat/completions", "/api/v1/images", "/api/v1/images"], api.Requests.Select(r => r.Path));
        Assert.Contains("prompt\":\"kot", api.Requests[1].Text);
        Assert.Contains("size\":\"1024x1024", api.Requests[1].Text);
        Assert.Contains("seed\":7", api.Requests[1].Text);
    }

    [Fact]
    public async Task OpenRouter_never_appends_the_negative_prompt_to_the_prompt_and_reports_it_unsupported()
    {
        using var api = new Api((_, path) => path == "/api/v1/chat/completions"
            ? Reply.Json("""{"error":{"message":"meta/muse-image is an image generation model and cannot be used with the chat/completions endpoint. Use the /api/v1/images endpoint instead.","code":404}}""", 404)
            : Reply.Json("""{"created":1,"data":[{"b64_json":"AQID"}]}"""));
        using var provider = new OpenRouterProvider("k", api.Url);
        var outcomes = new List<NegativePromptOutcome>();

        await provider.GenerateImageAsync("meta/muse-image", "kot", "1024x1024", new ImageOptions(null, "bez ludzi", null, outcomes.Add));

        Assert.All(api.Requests, r => Assert.DoesNotContain("bez ludzi", r.Text));
        Assert.Equal([NegativePromptOutcome.Unsupported], outcomes);
    }

    [Fact]
    public async Task OpenRouter_chat_models_carry_the_context_length_from_the_catalog()
    {
        using var api = new Api((_, path) => Reply.Json("""
            {"data":[{"id":"openai/gpt-x","architecture":{"input_modalities":["text"],"output_modalities":["text"]},
                      "supported_parameters":[],"context_length":200000}]}
            """));
        using var provider = new OpenRouterProvider("k", api.Url);

        var models = await provider.GetChatModelsAsync();

        Assert.Equal(200000, models.Single(m => m.Id == "openai/gpt-x").ContextLength);
    }

    [Fact]
    public async Task A_provider_without_audio_returns_empty_lists_and_refuses_to_synthesize()
    {
        using IAiProvider provider = new OpenRouterProvider("k");

        Assert.Empty(await provider.GetSpeechModelsAsync());
        Assert.Empty(await provider.GetTranscriptionModelsAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SynthesizeAsync("m", "t", null, null, "mp3"));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscribeAsync("m", "plik.wav", null, null));
    }

    [Fact]
    public async Task Reload_brings_the_audio_models_into_the_catalog()
    {
        using var api = new Api((_, path) => path.Contains("type=speech") ? Reply.Json(SpeechList)
                                            : path.Contains("type=transcription") ? Reply.Json("""{"data":[{"id":"fal/wizper","available":true,"endpoints":["/v1/audio/transcriptions"]}]}""")
                                            : Reply.Json("""{"data":[]}"""));
        using IAiProvider provider = Uber(api);

        var catalog = await provider.ReloadAsync();

        Assert.Equal(2, catalog.Speech!.Count);
        Assert.Equal("fal/wizper", catalog.Transcription!.Single().Id);
    }
}
