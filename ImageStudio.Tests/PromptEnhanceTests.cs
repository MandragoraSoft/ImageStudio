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
    public async Task Enhance_requests_json_mode_and_returns_changes()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply);
        using var http = Client(handler);

        var parts = await OpenAiCompat.EnhanceAsync(http, "/v1/chat/completions", "m", "i", "s", "kot",
                                                    new Dictionary<string, object>(), null, jsonMode: true, default);

        Assert.Equal("kot na dachu", parts.Concept);
        Assert.Equal("styl: foto", parts.System);
        Assert.Equal("tekst", parts.Negative);
        Assert.Equal("dodano dach", parts.Changes);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Enhance_without_json_mode_sends_no_response_format()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply);
        using var http = Client(handler);

        await OpenAiCompat.EnhanceAsync(http, "/v1/chat/completions", "m", "i", "s", "kot",
                                        new Dictionary<string, object>(), null, jsonMode: false, default);

        Assert.DoesNotContain("response_format", handler.RequestBody!);
    }

    [Fact]
    public async Task Enhance_retries_without_response_format_when_endpoint_rejects_it()
    {
        var handler = new RejectOnceHandler(Reply);
        using var http = Client(handler);

        var parts = await OpenAiCompat.EnhanceAsync(http, "/v1/chat/completions", "m", "i", "s", "kot",
                                                    new Dictionary<string, object>(), null, jsonMode: true, default);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("response_format", handler.Bodies[0]);
        Assert.DoesNotContain("response_format", handler.Bodies[1]);
        Assert.Equal("kot na dachu", parts.Concept);
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

    private sealed class RejectOnceHandler(string okBody) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return Bodies.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("unsupported parameter: response_format") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(okBody) };
        }
    }
}
