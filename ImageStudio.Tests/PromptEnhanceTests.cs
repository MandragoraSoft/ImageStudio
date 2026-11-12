using System.Net;
using System.Text.Json;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public sealed class PromptEnhanceTests
{
    private const string Reply = """{"choices":[{"message":{"content":"{\"system\":\"styl: foto\",\"concept\":\"kot na dachu\",\"negative\":\"tekst\",\"changes\":\"dodano dach\"}"}}]}""";

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://example.test") };

    private static PromptRevision Step(int i) => new($"s{i}", $"c{i}", $"S{i}", $"C{i}", $"instr{i}", $"zmiana{i}", $"n{i}", $"N{i}");

    [Fact]
    public void Messages_replay_history_as_turns_and_keep_config_separate_from_concept()
    {
        var messages = OpenAiCompat.BuildEnhanceMessages("popraw", "styl", "kot", [Step(1), Step(2)], "napis");

        var json = JsonSerializer.SerializeToElement(messages);
        Assert.Equal(6, json.GetArrayLength());  // system + 2 x (user, assistant) + bieżący stan
        Assert.Equal("system", json[0].GetProperty("role").GetString());
        Assert.Contains("GENERATION CONFIGURATION", json[0].GetProperty("content").GetString());
        Assert.Contains("EXPECTED RESULT", json[0].GetProperty("content").GetString());
        Assert.Equal("assistant", json[2].GetProperty("role").GetString());
        Assert.Contains("\"concept\":\"C1\"", json[2].GetProperty("content").GetString());
        Assert.Contains("(negative):\nn1", json[1].GetProperty("content").GetString());
        Assert.Contains("\"negative\":\"N1\"", json[2].GetProperty("content").GetString());
        var last = json[5].GetProperty("content").GetString()!;
        Assert.Contains("INSTRUCTION: popraw", last);
        Assert.Contains("(system):\nstyl", last);
        Assert.Contains("(concept):\nkot", last);
        Assert.Contains("(negative):\nnapis", last);
    }

    [Fact]
    public void Contract_lets_the_instruction_change_language_for_both_parts()
    {
        Assert.Contains("unless the instruction asks for another one", OpenAiCompat.SplitFormat);
        Assert.Contains("all three parts", OpenAiCompat.SplitFormat);
        Assert.Contains("valid JSON object", OpenAiCompat.SplitFormat);
        Assert.Contains("\"system\"", OpenAiCompat.SplitFormat);
        Assert.Contains("\"concept\"", OpenAiCompat.SplitFormat);
        Assert.Contains("\"negative\"", OpenAiCompat.SplitFormat);
    }

    [Fact]
    public void Messages_keep_only_the_latest_history_steps()
    {
        var history = Enumerable.Range(1, 10).Select(Step).ToList();

        var json = JsonSerializer.SerializeToElement(OpenAiCompat.BuildEnhanceMessages("i", "s", "c", history));

        Assert.Equal(1 + OpenAiCompat.MaxHistory * 2 + 1, json.GetArrayLength());
        Assert.Contains("instr5", json[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Enhance_in_json_object_mode_returns_changes()
    {
        var handler = new SequenceHandler((HttpStatusCode.OK, Reply));
        using var http = Client(handler);

        var parts = await OpenAiCompat.EnhanceAsync(http, "/v1/chat/completions", "m", "i", "s", "kot",
                                                    new Dictionary<string, object>(), null, PromptResponseMode.JsonObject, default);

        Assert.Equal("kot na dachu", parts.Concept);
        Assert.Equal("styl: foto", parts.System);
        Assert.Equal("tekst", parts.Negative);
        Assert.Equal("dodano dach", parts.Changes);
        Assert.Equal("json_object", FormatType(handler.Bodies.Single()));
    }

    [Fact]
    public async Task Enhance_in_instruction_mode_sends_no_response_format()
    {
        var handler = new SequenceHandler((HttpStatusCode.OK, Reply));
        using var http = Client(handler);

        await OpenAiCompat.EnhanceAsync(http, "/v1/chat/completions", "m", "i", "s", "kot",
                                        new Dictionary<string, object>(), null, PromptResponseMode.Instruction, default);

        Assert.DoesNotContain("response_format", handler.Bodies.Single());
    }

    [Fact]
    public async Task Json_schema_mode_declares_strict_schema_with_all_properties_required()
    {
        var handler = new SequenceHandler((HttpStatusCode.OK, Reply));
        using var http = Client(handler);

        await OpenAiCompat.EnhanceAsync(http, "/v1/chat/completions", "m", "i", "s", "kot",
                                        new Dictionary<string, object>(), null, PromptResponseMode.JsonSchema, default);

        using var body = JsonDocument.Parse(handler.Bodies.Single());
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        var schema = format.GetProperty("json_schema").GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var required = schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Equal(new[] { "system", "concept", "negative", "changes" }, required);
        Assert.Equal(required.OrderBy(p => p), schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(p => p));
    }

    [Fact]
    public async Task Rejected_json_schema_falls_back_to_json_object_then_plain_text_once_each()
    {
        var handler = new SequenceHandler((HttpStatusCode.BadRequest, Rejected), (HttpStatusCode.BadRequest, Rejected), (HttpStatusCode.OK, Reply));
        using var http = Client(handler);

        var parts = await OpenAiCompat.EnhanceAsync(http, "/v1/chat/completions", "m", "i", "s", "kot",
                                                    new Dictionary<string, object>(), null, PromptResponseMode.JsonSchema, default);

        Assert.Equal(new[] { "json_schema", "json_object", "none" }, handler.Bodies.Select(FormatType));
        Assert.Equal("kot na dachu", parts.Concept);
    }

    [Fact]
    public async Task Rejected_json_object_falls_back_to_plain_text_when_error_is_plain_text()
    {
        var handler = new SequenceHandler((HttpStatusCode.BadRequest, "unsupported parameter: response_format"), (HttpStatusCode.OK, Reply));
        using var http = Client(handler);

        var parts = await OpenAiCompat.EnhanceAsync(http, "/v1/chat/completions", "m", "i", "s", "kot",
                                                    new Dictionary<string, object>(), null, PromptResponseMode.JsonObject, default);

        Assert.Equal(new[] { "json_object", "none" }, handler.Bodies.Select(FormatType));
        Assert.Equal("kot na dachu", parts.Concept);
    }

    [Fact]
    public async Task Fallback_stops_after_each_mode_is_tried_once()
    {
        var handler = new SequenceHandler((HttpStatusCode.BadRequest, Rejected), (HttpStatusCode.BadRequest, Rejected), (HttpStatusCode.BadRequest, Rejected));
        using var http = Client(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAiCompat.EnhanceAsync(http, "/v1/chat/completions", "m", "i", "s", "kot",
            new Dictionary<string, object>(), null, PromptResponseMode.JsonSchema, default));

        Assert.Equal(new[] { "json_schema", "json_object", "none" }, handler.Bodies.Select(FormatType));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid schema for response_format 'prompt_parts'","param":"response_format","code":"invalid_json_schema"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid value for response_format","param":"response_format","code":"invalid_value"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Malformed response_format","param":"response_format"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """["bad request"]""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided; response_format is not used","code":"invalid_api_key"}}""")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit reached, response_format not supported")]
    [InlineData(HttpStatusCode.InternalServerError, "response_format unsupported")]
    public async Task Errors_other_than_explicit_format_rejection_are_not_retried(HttpStatusCode status, string error)
    {
        var handler = new SequenceHandler((status, error), (HttpStatusCode.OK, Reply));
        using var http = Client(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAiCompat.EnhanceAsync(http, "/v1/chat/completions", "m", "i", "s", "kot",
            new Dictionary<string, object>(), null, PromptResponseMode.JsonSchema, default));

        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Rejected_schema_format_can_fall_back_when_error_names_json_schema()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.BadRequest, """{"error":{"message":"json_schema is not supported","param":"response_format"}}"""),
            (HttpStatusCode.OK, Reply));
        using var http = Client(handler);

        await OpenAiCompat.EnhanceAsync(http, "/v1/chat/completions", "m", "i", "s", "kot",
            new Dictionary<string, object>(), null, PromptResponseMode.JsonSchema, default);

        Assert.Equal(new[] { "json_schema", "json_object" }, handler.Bodies.Select(FormatType));
    }

    [Fact]
    public async Task Invalid_mode_and_response_format_override_fail_before_request()
    {
        var handler = new SequenceHandler((HttpStatusCode.OK, Reply));
        using var http = Client(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => OpenAiCompat.EnhanceAsync(http, "/", "m", "i", "s", "c",
            new Dictionary<string, object>(), null, (PromptResponseMode)99, default));
        await Assert.ThrowsAsync<ArgumentException>(() => OpenAiCompat.EnhanceAsync(http, "/", "m", "i", "s", "c",
            new Dictionary<string, object> { ["response_format"] = new { type = "json_object" } }, null, PromptResponseMode.Instruction, default));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Refusal_is_reported_without_retry()
    {
        const string refusal = """{"choices":[{"message":{"content":null,"refusal":"Nie mogę wykonać tej prośby."}}]}""";
        var handler = new SequenceHandler((HttpStatusCode.OK, refusal));
        using var http = Client(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAiCompat.EnhanceAsync(http, "/", "m", "i", "s", "c",
            new Dictionary<string, object>(), null, PromptResponseMode.JsonSchema, default));
        Assert.Equal("Nie mogę wykonać tej prośby.", error.Message);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public void Changes_must_be_text_when_present()
    {
        Assert.Throws<InvalidOperationException>(() => OpenAiCompat.ParseSplit("""{"system":"","concept":"c","negative":"","changes":5}"""));
        Assert.Null(OpenAiCompat.ParseSplit("""{"system":"","concept":"c","negative":"","changes":null}""").Changes);
        Assert.Null(OpenAiCompat.ParseSplit("""{"system":"","concept":"c","negative":""}""").Changes);
    }

    [Theory]
    [InlineData("""{"system":"","concept":"c","negative":""}""")]
    [InlineData("""{"system":"","concept":"c","negative":"","changes":null}""")]
    [InlineData("""{"system":"","concept":"c","negative":"","changes":"","extra":true}""")]
    [InlineData("""{"system":"","concept":"c","negative":"","changes":"","concept":"d"}""")]
    public async Task Strict_response_is_validated_against_schema_even_on_success(string content)
    {
        var response = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
        var handler = new SequenceHandler((HttpStatusCode.OK, response));
        using var http = Client(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAiCompat.EnhanceAsync(http, "/", "m", "i", "s", "c",
            new Dictionary<string, object>(), null, PromptResponseMode.JsonSchema, default));
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Invalid_content_is_not_retried_with_another_mode()
    {
        const string empty = """{"choices":[{"message":{"content":"{\"system\":\"s\",\"concept\":\"\",\"negative\":\"\",\"changes\":\"\"}"}}]}""";
        var handler = new SequenceHandler((HttpStatusCode.OK, empty), (HttpStatusCode.OK, Reply));
        using var http = Client(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => OpenAiCompat.EnhanceAsync(http, "/v1/chat/completions", "m", "i", "s", "kot",
            new Dictionary<string, object>(), null, PromptResponseMode.JsonSchema, default));

        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData("gpt-4o-mini", true)]
    [InlineData("gpt-4o-mini-2024-07-18", true)]
    [InlineData("gpt-4o-mini-2024-07-17", false)]
    [InlineData("gpt-4o-2024-08-06", true)]
    [InlineData("gpt-4o-2024-05-13", false)]
    [InlineData("gpt-4o-2025-01-01", false)]
    [InlineData("gpt-4o", false)]
    [InlineData("gpt-4.1", false)]
    public void Strict_schema_is_limited_to_documented_openai_models(string model, bool expected) =>
        Assert.Equal(expected, OpenAiProvider.SupportsStrictSchema(model));

    private static string FormatType(string body) =>
        JsonDocument.Parse(body).RootElement.TryGetProperty("response_format", out var format)
            ? format.GetProperty("type").GetString()!
            : "none";

    [Fact]
    public void Editor_conversation_uses_current_fields_and_blocks_only_set_prompt()
    {
        Sta.Run(() =>
        {
            var provider = new FakeProvider { OnChat = (_, _) => "odpowiedź" };
            var dialogs = new FakeDialogs();
            var vm = TestApp.CreateVm(provider: provider, dialogs: dialogs);
            vm.EnhancerModels.Add("m");
            vm.SelectedEnhancerModel = "m";
            vm.Models.Add(new ModelOption("image", "fake", false, ["text"], null));
            vm.Tools.AppControlEnabled = true;
            vm.Tools.UseTools = true;
            vm.GeneratePrompt = "oryginał";
            vm.EditGeneratePromptCommand.Execute(null);

            var current = new PromptEdit("styl", "kot", NegativePrompt: "napisy");
            var chat = dialogs.LastConversationFactory!(() => current, (_, _) => { });
            Assert.Contains(vm.Tools.Tools, t => t.Name == "imagestudio__set_prompt");
            Assert.DoesNotContain(chat.Tools!.Tools, t => t.Name == "imagestudio__set_prompt");
            Assert.Contains(chat.Tools.Tools, t => t.Name == "imagestudio__get_state");
            var denied = chat.Tools.CallAsync(new ToolCall("1", "imagestudio__set_prompt", """{"prompt":"zmiana"}""")).GetAwaiter().GetResult();
            Assert.Contains("niedostępne", denied);
            Assert.Equal("oryginał", vm.GeneratePrompt);
            vm.Tools.UseTools = false;
            var disabled = chat.Tools.CallAsync(new ToolCall("2", "imagestudio__get_state", "{}")).GetAwaiter().GetResult();
            Assert.Contains("wyłączone", disabled);
            vm.Tools.UseTools = true;
            chat.Input = "ulepsz";
            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Contains("kot", provider.LastMessages[0].Content);
            Assert.Contains("napisy", provider.LastMessages[0].Content);
            Assert.Empty(vm.Chat.Messages);
            provider.OnReply = messages => messages[^1].Role == "tool"
                ? new ChatReply("wynik po narzędziu", [])
                : new ChatReply("", [new ToolCall("3", "imagestudio__get_state", "{}")]);
            chat.Permissions.Allow("imagestudio__get_state", ApprovalChoice.Session);
            chat.Input = "sprawdź stan";
            chat.SendCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Contains(chat.Messages, message => message.Role == "tool" && message.ToolName == "imagestudio__get_state");
            Assert.Equal("wynik po narzędziu", chat.LastCompletedReply);
            chat.Permissions.Allow("builtin__get_datetime", ApprovalChoice.Session);
            Assert.False(vm.Chat.Permissions.IsAllowed("builtin__get_datetime"));
            chat.Permissions.Allow("builtin__get_datetime", ApprovalChoice.Always);
            Assert.Contains("builtin__get_datetime", vm.Tools.Permissions.Always);
            vm.EditEditPromptCommand.Execute(null);
            Assert.NotNull(dialogs.LastConversationFactory);
            vm.EditVideoPromptCommand.Execute(null);
            Assert.NotNull(dialogs.LastConversationFactory);
        });
    }

    [Fact]
    public void Rebuild_passes_history_to_the_model_and_returns_it_extended()
    {
        Sta.Run(() =>
        {
            var provider = new FakeProvider
            {
                EnhanceWithNegative = (_, _, concept, negative) => new PromptParts("styl", concept + "+", "dopisano", negative + "+"),
            };
            var dialogs = new FakeDialogs();
            var vm = TestApp.CreateVm(provider: provider, dialogs: dialogs);
            vm.EnhancerModels.Add("m");
            vm.SelectedEnhancerModel = "m";
            vm.EditGeneratePromptCommand.Execute(null);

            var first = dialogs.LastRebuild!(new PromptEdit("", "kot", "dodaj światło", NegativePrompt: "bez")).GetAwaiter().GetResult();
            var second = dialogs.LastRebuild!(first with { Instruction = "więcej detali" }).GetAwaiter().GetResult();

            Assert.Single(first.History!);
            Assert.Equal("dopisano", first.History![0].Changes);
            Assert.Equal("bez", first.History[0].InNegative);
            Assert.Equal("bez+", first.History[0].OutNegative);
            Assert.Equal("bez++", second.NegativePrompt);
            Assert.Equal("bez+", provider.LastEnhanceNegative);
            Assert.Equal(2, second.History!.Count);
            Assert.Equal("kot+", provider.LastHistory!.Single().OutConcept);
            Assert.Equal("więcej detali", second.History[1].Instruction);
        });
    }

    [Fact]
    public void Old_history_without_negative_fields_remains_usable()
    {
        var old = new PromptRevision("s", "c", "S", "C", "i");
        var messages = JsonSerializer.SerializeToElement(OpenAiCompat.BuildEnhanceMessages("i", "s", "c", [old]));
        using var output = JsonDocument.Parse(messages[2].GetProperty("content").GetString()!);
        Assert.Equal("", output.RootElement.GetProperty("negative").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rebuild_in_generation_and_editing_passes_and_returns_all_fields(bool generate)
    {
        Sta.Run(() =>
        {
            var provider = new FakeProvider { EnhanceWithNegative = (_, system, concept, negative) =>
                new PromptParts(system + "+", concept + "+", "zmiana", negative + "+") };
            var dialogs = new FakeDialogs();
            var vm = TestApp.CreateVm(provider, dialogs);
            vm.EnhancerModels.Add("m");
            vm.SelectedEnhancerModel = "m";
            if (generate) vm.EditGeneratePromptCommand.Execute(null);
            else vm.EditEditPromptCommand.Execute(null);
            var result = dialogs.LastRebuild!(new PromptEdit("s", "c", "i", NegativePrompt: "n")).GetAwaiter().GetResult();
            Assert.Equal(("s+", "c+", "n+"), (result.System, result.Prompt, result.NegativePrompt));
            Assert.Equal("n", provider.LastEnhanceNegative);
            Assert.Equal(("n", "n+"), (result.History![0].InNegative, result.History[0].OutNegative));
            dialogs.PromptEditResult = result;
            if (generate)
            {
                vm.EditGeneratePromptCommand.Execute(null);
                Assert.Equal(("s+", "c+", "n+"), (vm.GenerateSystemPrompt, vm.GeneratePrompt, vm.NegativePrompt));
                vm.EditGeneratePromptCommand.Execute(null);
            }
            else
            {
                vm.EditEditPromptCommand.Execute(null);
                Assert.Equal(("s+", "c+", "n+"), (vm.EditSystemPrompt, vm.EditPrompt, vm.EditNegativePrompt));
                vm.EditEditPromptCommand.Execute(null);
            }
            Assert.Equal("n+", dialogs.LastInitial!.History![0].OutNegative);

            var hidden = dialogs.LastRebuild!(new PromptEdit(null, "c", "i")).GetAwaiter().GetResult();
            Assert.Null(hidden.System);
            Assert.Null(hidden.NegativePrompt);
        });
    }

    private const string Rejected = """{"error":{"message":"Unsupported parameter: response_format is not supported with this model","type":"invalid_request_error","param":"response_format","code":"unsupported_parameter"}}""";

    // Kolejne odpowiedzi w kolejności wywołań (ostatnia się powtarza); zapisuje ciała żądań
    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var (status, body) = responses[Math.Min(_next++, responses.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
