using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ImageStudio.Services;

// ChatGPT (plan Plus/Pro przez "Sign in with ChatGPT"): zapytania Responses API (POST /v1/responses) z tokenem OAuth zamiast
// klucza API. Trasa wymaga store=false i stream=true, odrzuca role system (idą w "instructions") oraz pola temperature,
// max_output_tokens i podobne, a nie obsługuje generowania obrazów, dźwięku ani wideo: tylko czat (z obrazami na wejściu).
// Ceny nie dotyczą (zużywa się plan), więc lista jest bez cen
public sealed class ChatGptProvider : OpenAiChatProvider
{
    public const string Identifier = "chatgpt";

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly ChatGptAuth _auth;
    private readonly Action<string> _saveSession;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private ChatGptSession _session;

    // saveSession dostaje JSON sesji po każdym odświeżeniu (refresh token jest wymieniany, więc stary przestaje być ważny)
    public ChatGptProvider(string sessionJson, Action<string>? saveSession = null, ChatGptAuth? auth = null,
                           string baseAddress = ChatGptAuth.Resource + "/")
        : base(baseAddress, "", "responses")
    {
        _session = ChatGptSession.Parse(sessionJson);
        _saveSession = saveSession ?? (_ => { });
        _auth = auth ?? new ChatGptAuth();
    }

    public override string Id => Identifier;
    public override string DisplayName => "ChatGPT";

    // Katalog modeli zależy od konta, więc nie ma modelu na sztywno: ulepszanie i czat biorą pierwszy z listy
    public override string? DefaultTextModel => null;

    public override async Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "models", null, HttpCompletionOption.ResponseContentRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw ApiErrors.Failure(response.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        return ParseModels(doc.RootElement);
    }

    // Katalog zalogowanego konta: models[] z slug, display_name i visibility ("list" = pokazywany); gdy API zwróci zwykłe data[].id, też działa
    public static IReadOnlyList<ChatModelInfo> ParseModels(JsonElement root)
    {
        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            return models.EnumerateArray()
                .Where(m => Text(m, "visibility") is null or "list" && Text(m, "slug") is not null)
                .Select(m => new ChatModelInfo(Text(m, "slug")!, [], null, "openai", ["text", "image"], Text(m, "display_name")))
                .ToList();

        return ModelEntries(root).Select(m => new ChatModelInfo(m.GetProperty("id").GetString()!, [], null, "openai", ["text", "image"]))
            .ToList();
    }

    public override Task<ChatReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages,
                                              IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default,
                                              ChatOptions? options = null) =>
        ChatStreamAsync(model, messages, tools, _ => { }, ct, options);

    // Odpowiedź jest zawsze strumieniowa (wymóg trasy); ChatAsync zbiera ją bez wywołań zwrotnych.
    // Sukces dopiero po response.completed; reasoning (options) nie jest sterowane, bo katalog nie deklaruje parametrów
    public override async Task<ChatReply> ChatStreamAsync(string model, IReadOnlyList<ChatMessage> messages,
                                                          IReadOnlyList<ToolDefinition>? tools, Action<ChatDelta> onDelta,
                                                          CancellationToken ct = default, ChatOptions? options = null)
    {
        using var response = await SendAsync(HttpMethod.Post, ChatPath, JsonContent.Create(BuildBody(model, messages, tools)),
                                             HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.Failure(response.StatusCode, await response.Content.ReadAsStringAsync(ct));

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var parser = new ResponsesStream(onDelta);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;
            parser.Handle(data);
        }

        return parser.Finish();
    }

    // Przebudowa promptu: ten sam kontrakt co w pozostałych dostawcach (JSON system/concept), ale bez trybu JSON i parametrów
    // próbkowania, których trasa nie przyjmuje
    public override async Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                               IReadOnlyDictionary<string, object> parameters,
                                                               IReadOnlyList<PromptRevision>? history = null,
                                                               CancellationToken ct = default)
    {
        var wire = JsonSerializer.SerializeToElement(OpenAiCompat.BuildEnhanceMessages(instruction, system, concept, history));
        var messages = wire.EnumerateArray()
            .Select(m => new ChatMessage(m.GetProperty("role").GetString()!, m.GetProperty("content").GetString() ?? ""))
            .ToList();

        var reply = await ChatAsync(model, messages, null, ct);
        var parts = OpenAiCompat.ParseSplit(reply.Content, system, concept);
        if (string.IsNullOrWhiteSpace(parts.Concept)) throw new InvalidOperationException("Model nie zwrócił promptu.");
        return parts;
    }

    // Ciało żądania Responses: role system wędrują do instructions, wywołania i wyniki narzędzi to osobne pozycje wejścia
    public static Dictionary<string, object?> BuildBody(string model, IReadOnlyList<ChatMessage> messages,
                                                        IReadOnlyList<ToolDefinition>? tools)
    {
        var input = new List<object>();
        var instructions = new List<string>();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "system":
                    instructions.Add(m.Content);
                    break;
                case "tool":
                    input.Add(new { type = "function_call_output", call_id = m.ToolCallId, output = m.Content });
                    break;
                case "assistant":
                    if (!string.IsNullOrEmpty(m.Content)) input.Add(new { role = "assistant", content = m.Content });
                    foreach (var call in m.ToolCalls ?? [])
                        input.Add(new { type = "function_call", call_id = call.Id, name = call.Name, arguments = call.ArgumentsJson });
                    break;
                default:
                    if (m.HasImages)
                    {
                        var parts = new List<object>();
                        if (!string.IsNullOrEmpty(m.Content)) parts.Add(new { type = "input_text", text = m.Content });
                        parts.AddRange(m.Images!.Select(i => new { type = "input_image", image_url = i.DataUri }));
                        input.Add(new { role = "user", content = parts });
                    }
                    else
                    {
                        input.Add(new { role = "user", content = m.Content });
                    }

                    break;
            }
        }

        var body = new Dictionary<string, object?> { ["model"] = model, ["input"] = input, ["store"] = false, ["stream"] = true };
        if (instructions.Count > 0) body["instructions"] = string.Join("\n\n", instructions);
        if (tools is { Count: > 0 })
            body["tools"] = tools.Select(t => new
            {
                type = "function",
                name = t.Name,
                description = t.Description,
                parameters = JsonSerializer.Deserialize<JsonElement>(t.ParametersJson),
            }).ToList();
        return body;
    }

    // Żądanie z aktualnym tokenem; token odnawiany z wyprzedzeniem, a odświeżenia są szeregowane (refresh token się obraca)
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content,
                                                      HttpCompletionOption completion, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));
        return await Http.SendAsync(request, completion, ct);
    }

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (!_session.IsSignedIn)
                throw new InvalidOperationException("Nie zalogowano do ChatGPT: zaloguj się w Ustawieniach (Dostawca AI → ChatGPT).");

            if (_session.AccessToken is null || _session.ExpiresAt is not { } expires || expires - DateTimeOffset.UtcNow < RefreshMargin)
            {
                _session = await _auth.RefreshAsync(_session, ct);
                _saveSession(_session.ToJson());
            }

            return _session.AccessToken!;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public override void Dispose()
    {
        _refreshGate.Dispose();
        base.Dispose();
    }

    // Zdarzenia SSE Responses API: tekst, rozumowanie (podsumowanie), wywołania funkcji i zużycie z response.completed
    private sealed class ResponsesStream(Action<ChatDelta> onDelta)
    {
        private readonly StringBuilder _content = new();
        private readonly StringBuilder _reasoning = new();
        private readonly List<ToolCall> _calls = [];
        private TokenUsage? _usage;
        private bool _completed;

        public void Handle(string data)
        {
            using var doc = JsonDocument.Parse(data);
            var e = doc.RootElement;
            switch (e.TryGetProperty("type", out var t) ? t.GetString() : null)
            {
                case "response.output_text.delta" when e.TryGetProperty("delta", out var d) && d.GetString() is { } text:
                    _content.Append(text);
                    onDelta(new ChatDelta(text, null));
                    break;
                case "response.reasoning_summary_text.delta" or "response.reasoning_text.delta"
                    when e.TryGetProperty("delta", out var r) && r.GetString() is { } thought:
                    _reasoning.Append(thought);
                    onDelta(new ChatDelta(null, thought));
                    break;
                case "response.output_item.done" when e.TryGetProperty("item", out var item):
                    AddCall(item);
                    break;
                case "response.completed" or "response.incomplete":
                    _completed = true;
                    if (e.TryGetProperty("response", out var response))
                    {
                        _usage = ReadUsage(response);
                        // Zapas: wywołania funkcji z końcowej listy, gdy nie przyszły osobnymi zdarzeniami
                        if (_calls.Count == 0 && response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                            foreach (var o in output.EnumerateArray()) AddCall(o);
                    }

                    break;
                case "response.failed":
                    var error = e.TryGetProperty("response", out var failed) && failed.TryGetProperty("error", out var fe) ? fe : default;
                    throw Failure(error);
                case "error":
                    throw Failure(e.TryGetProperty("error", out var inner) ? inner : e);
            }
        }

        public ChatReply Finish()
        {
            if (!_completed) throw new InvalidOperationException("Strumień ChatGPT zakończył się bez response.completed (połączenie przerwane albo limit planu).");
            return new ChatReply(_content.ToString(), _calls, _reasoning.Length > 0 ? _reasoning.ToString() : null, _usage);
        }

        private void AddCall(JsonElement item)
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "function_call") return;

            var id = item.TryGetProperty("call_id", out var c) && c.GetString() is { Length: > 0 } callId ? callId : Guid.NewGuid().ToString("N");
            if (_calls.Any(x => x.Id == id)) return;
            var arguments = item.TryGetProperty("arguments", out var a) ? a.GetString() : null;
            _calls.Add(new ToolCall(id, item.GetProperty("name").GetString()!, string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments));
        }

        private static TokenUsage? ReadUsage(JsonElement response)
        {
            if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

            static int? Int(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

            var cached = usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
                ? Int(details, "cached_tokens")
                : null;
            return new TokenUsage(Int(usage, "input_tokens"), Int(usage, "output_tokens"), cached);
        }

        // Błąd w strumieniu: kod (np. subscription_sharing_usage_limit_exceeded) i komunikat z API
        private static InvalidOperationException Failure(JsonElement error)
        {
            string? Text(string name) => error.ValueKind == JsonValueKind.Object && error.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

            var code = Text("code");
            var message = Text("message") ?? "nieznany błąd";
            return new InvalidOperationException(code is null ? message : $"{code}: {message}");
        }
    }
}
