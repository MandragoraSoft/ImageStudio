using System.IO;
using System.Text;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageStudio.Services;

// Wspólne dla dostawców zgodnych z formatem OpenAI (/chat/completions) i podziału promptu na system + koncept
public static class OpenAiCompat
{
    // Wspólne dla providerów: /chat/completions (format OpenAI) z pełną historią i opcjonalnymi narzędziami
    public static async Task<ChatReply> ChatAsync(HttpClient http, string path, string model,
                                                    IReadOnlyList<ChatMessage> messages,
                                                    IReadOnlyList<ToolDefinition>? tools, CancellationToken ct,
                                                    ChatOptions? options = null)
    {
        var body = BuildChatBody(model, messages, tools, options);

        using var response = await http.PostAsJsonAsync(path, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.Failure(response.StatusCode, text);

        using var doc = JsonDocument.Parse(text);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";

        var calls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                // Argumenty to zwykle tekst JSON; część serwerów zwraca obiekt
                var arguments = function.TryGetProperty("arguments", out var a)
                    ? a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : a.GetRawText()
                    : "";
                var id = call.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
                    ? i.GetString()!
                    : Guid.NewGuid().ToString("N");
                calls.Add(new ToolCall(id, function.GetProperty("name").GetString()!,
                                       string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments));
            }
        }

        return new ChatReply(content, calls, ReadReasoning(message), ReadUsage(doc.RootElement));
    }

    // Rozmiar okna kontekstowego z elementu JSON modelu w katalogu: pierwsza dodatnia liczba z pól znanych dostawcom; inaczej null
    public static int? ReadContextLength(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object) return null;

        static int? Positive(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n > 0 ? n : null;

        foreach (var name in new[] { "context_length", "max_context_length", "context_window" })
            if (model.TryGetProperty(name, out var value) && Positive(value) is { } found) return found;

        return model.TryGetProperty("top_provider", out var top) && top.ValueKind == JsonValueKind.Object
               && top.TryGetProperty("context_length", out var nested) ? Positive(nested) : null;
    }

    // Zużycie z pola usage; tokeny z cache raportują różne API: prompt_tokens_details.cached_tokens (OpenAI, OpenRouter, Venice),
    // prompt_cache_hit_tokens (DeepSeek), cache_read_input_tokens (styl Anthropic) lub cached_tokens
    private static TokenUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

        static int? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

        var input = Int(usage, "prompt_tokens") ?? Int(usage, "input_tokens");
        var output = Int(usage, "completion_tokens") ?? Int(usage, "output_tokens");
        var cached = usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? Int(details, "cached_tokens")
            : null;
        cached ??= Int(usage, "prompt_cache_hit_tokens") ?? Int(usage, "cache_read_input_tokens") ?? Int(usage, "cached_tokens");

        return input is null && output is null && cached is null ? null : new TokenUsage(input, output, cached);
    }

    private static Task<HttpResponseMessage> SendStreamAsync(HttpClient http, string path, object body, CancellationToken ct) =>
        http.SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) },
                       HttpCompletionOption.ResponseHeadersRead, ct);

    // Ciało żądania czatu: model, historia, narzędzia i (gdy podano) sterowanie rozumowaniem
    private static Dictionary<string, object?> BuildChatBody(string model, IReadOnlyList<ChatMessage> messages,
                                                              IReadOnlyList<ToolDefinition>? tools, ChatOptions? options)
    {
        var body = new Dictionary<string, object?> { ["model"] = model, ["messages"] = messages.Select(ToWire).ToList() };
        if (tools is { Count: > 0 })
            body["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = JsonSerializer.Deserialize<JsonElement>(t.ParametersJson),
                },
            }).ToList();

        ApplyReasoning(body, options);
        return body;
    }

    // Odpowiedź strumieniowa (SSE): linie "data: {...}" z kawałkami delta, na końcu kawałek z usage (stream_options) i "[DONE]".
    // Komentarze ": ping" są pomijane. Wywołania narzędzi składają się z fragmentów wg indeksu
    public static async Task<ChatReply> ChatStreamAsync(HttpClient http, string path, string model,
                                                        IReadOnlyList<ChatMessage> messages,
                                                        IReadOnlyList<ToolDefinition>? tools, Action<ChatDelta> onDelta,
                                                        CancellationToken ct = default, ChatOptions? options = null)
    {
        var body = BuildChatBody(model, messages, tools, options);
        body["stream"] = true;
        body["stream_options"] = new { include_usage = true };

        var response = await SendStreamAsync(http, path, body, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Nie każde API zna stream_options (zużycie tokenów): ponawiamy bez nich
            var error = await response.Content.ReadAsStringAsync(ct);
            var status = response.StatusCode;
            response.Dispose();
            if (!error.Contains("stream_options", StringComparison.OrdinalIgnoreCase)) throw ApiErrors.Failure(status, error);

            body.Remove("stream_options");
            response = await SendStreamAsync(http, path, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                var retryError = await response.Content.ReadAsStringAsync(ct);
                var retryStatus = response.StatusCode;
                response.Dispose();
                throw ApiErrors.Failure(retryStatus, retryError);
            }
        }

        using var _ = response;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
        TokenUsage? usage = null;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(ApiErrors.Message(error.GetRawText()));
            if (ReadUsage(root) is { } u) usage = u;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0) continue;
            if (!choices[0].TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) continue;

            // Kawałki bywają samymi spacjami, więc nie przycinamy ich ani nie pomijamy "pustych"
            var text = StringProperty(delta, "content");
            var think = StringProperty(delta, "reasoning_content") ?? StringProperty(delta, "reasoning");
            AddToolCallFragments(delta, calls);

            if (text is { Length: > 0 }) content.Append(text);
            if (think is { Length: > 0 }) reasoning.Append(think);
            if (text is { Length: > 0 } || think is { Length: > 0 }) onDelta(new ChatDelta(text, think));
        }

        var toolCalls = calls.Values.Where(c => c.Name is not null)
            .Select(c => new ToolCall(c.Id ?? Guid.NewGuid().ToString("N"), c.Name!, c.Args.Length == 0 ? "{}" : c.Args.ToString()))
            .ToList();
        return new ChatReply(content.ToString(), toolCalls, reasoning.Length > 0 ? reasoning.ToString() : null, usage);
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // tool_calls w strumieniu: pierwszy fragment niesie id i nazwę, kolejne dopisują argumenty; łączy je index
    private static void AddToolCallFragments(JsonElement delta, SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)> calls)
    {
        if (!delta.TryGetProperty("tool_calls", out var fragments) || fragments.ValueKind != JsonValueKind.Array) return;

        foreach (var fragment in fragments.EnumerateArray())
        {
            var index = fragment.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : calls.Count;
            calls.TryGetValue(index, out var current);
            var args = current.Args ?? new StringBuilder();
            var id = StringProperty(fragment, "id") ?? current.Id;
            var name = current.Name;
            if (fragment.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                name = StringProperty(function, "name") ?? name;
                if (StringProperty(function, "arguments") is { } part) args.Append(part);
            }

            calls[index] = (id, name, args);
        }
    }

    // Poziom i włączenie rozumowania wg parametru, który model deklaruje; bez options żądanie jest bez zmian
    private static void ApplyReasoning(Dictionary<string, object?> body, ChatOptions? options)
    {
        switch (options?.Parameter)
        {
            case "reasoning_effort":
                // Wyłączenie = brak parametru (model używa domyślnego poziomu)
                if (options.Enabled) body["reasoning_effort"] = options.Effort ?? ReasoningSupport.DefaultEffort;
                break;
            case "reasoning":
                body["reasoning"] = options.Enabled
                    ? new { effort = options.Effort ?? ReasoningSupport.DefaultEffort, exclude = false }
                    : new { enabled = false };
                break;
            case "include_reasoning":
                body["include_reasoning"] = options.Enabled;
                break;
        }
    }

    // Rozumowanie z odpowiedzi: reasoning_content (DeepSeek, Venice), reasoning (OpenRouter), a w ostateczności reasoning_details
    private static string? ReadReasoning(JsonElement message)
    {
        foreach (var name in new[] { "reasoning_content", "reasoning" })
        {
            if (message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }

        if (message.TryGetProperty("reasoning_details", out var details) && details.ValueKind == JsonValueKind.Array)
        {
            var parts = details.EnumerateArray()
                .Select(d => d.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()
                           : d.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x));
            var joined = string.Join("\n", parts);
            return joined.Length > 0 ? joined : null;
        }

        return null;
    }

    // Wiadomość w formacie API: asystent z prośbami o narzędzia niesie tool_calls, wynik narzędzia tool_call_id
    private static Dictionary<string, object?> ToWire(ChatMessage m)
    {
        var wire = new Dictionary<string, object?> { ["role"] = m.Role };
        if (m.Role == "tool")
        {
            wire["tool_call_id"] = m.ToolCallId;
            wire["content"] = m.Content;
        }
        else if (m.HasToolCalls)
        {
            wire["content"] = string.IsNullOrEmpty(m.Content) ? null : m.Content;
            wire["tool_calls"] = m.ToolCalls!.Select(call => new
            {
                id = call.Id,
                type = "function",
                function = new { name = call.Name, arguments = call.ArgumentsJson },
            }).ToList();
        }
        else if (m.Role == "user" && m.HasImages)
        {
            // Wiadomość z obrazami: lista części (tekst, potem obrazy jako data URI)
            var parts = new List<object>();
            if (!string.IsNullOrEmpty(m.Content)) parts.Add(new { type = "text", text = m.Content });
            parts.AddRange(m.Images!.Select(image => new { type = "image_url", image_url = new { url = image.DataUri } }));
            wire["content"] = parts;
        }
        else
        {
            wire["content"] = m.Content;
        }

        return wire;
    }

    // Stały kontrakt trzech niezależnych części promptu.
    public const string SplitFormat =
        "The input has three strictly separate parts: SYSTEM, REQUEST and NEGATIVE. " +
        "\"system\" = GENERATION CONFIGURATION: how the generator must render the result (style, technique, lighting, camera, " +
        "quality, format, constraints). Never describe the scene there. " +
        "\"concept\" = REQUEST / EXPECTED RESULT: what the generator must depict (subject, scene, action). Never put technical settings there. " +
        "\"negative\" = NEGATIVE: what the generator should avoid, independent of system and concept. " +
        "Earlier iterations of this prompt may be given as previous turns: keep what they already established unless the instruction says otherwise. " +
        "Reply with exactly one valid JSON object and nothing else (no code fences, no comments) with these keys: " +
        "{\"system\": \"...\", \"concept\": \"...\", \"negative\": \"...\", \"changes\": \"...\"}. " +
        "All four values must be strings. \"changes\" = one short sentence saying what you changed. Use \"\" for an empty system or negative, never null; concept must not be empty. " +
        "Apply the instruction to all three parts when it concerns the whole prompt (e.g. translation). " +
        "Keep the input language unless the instruction asks for another one.";

    // Ile ostatnich kroków historii trafia do modelu jako kontekst
    public const int MaxHistory = 6;

    private static string Turn(string system, string concept, string? negative) =>
        $"GENERATION CONFIGURATION (system):\n{system}\n\nEXPECTED RESULT (concept):\n{concept}\n\nNEGATIVE (negative):\n{negative ?? ""}";

    // Wiadomości dla modelu: polecenie + kontrakt, poprzednie kroki jako rozmowa, bieżący stan na końcu
    public static List<object> BuildEnhanceMessages(string instruction, string system, string concept,
                                                    IReadOnlyList<PromptRevision>? history, string negative = "")
    {
        var messages = new List<object> { new { role = "system", content = $"{instruction}\n\n{SplitFormat}" } };
        foreach (var step in (history ?? []).TakeLast(MaxHistory))
        {
            messages.Add(new { role = "user", content = $"INSTRUCTION: {step.Instruction}\n\n{Turn(step.InSystem, step.InConcept, step.InNegative)}" });
            messages.Add(new
            {
                role = "assistant",
                content = JsonSerializer.Serialize(new { system = step.OutSystem, concept = step.OutConcept, negative = step.OutNegative ?? "", changes = step.Changes ?? "" }),
            });
        }
        messages.Add(new { role = "user", content = $"INSTRUCTION: {instruction}\n\n{Turn(system, concept, negative)}" });
        return messages;
    }

    // Wspólne dla providerów: przebudowa promptu z historią; jsonMode dodaje response_format (z ponowieniem bez niego,
    // gdy endpoint go odrzuci). Wynik jest walidowany: koncept nie może być pusty
    public static async Task<PromptParts> EnhanceAsync(HttpClient http, string path, string model, string instruction,
                                                       string system, string concept,
                                                       IReadOnlyDictionary<string, object> parameters,
                                                       IReadOnlyList<PromptRevision>? history, bool jsonMode,
                                                       CancellationToken ct, string negative = "")
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = BuildEnhanceMessages(instruction, system, concept, history, negative),
        };
        foreach (var (key, value) in parameters) body[key] = value;
        if (jsonMode) body["response_format"] = new { type = "json_object" };

        var (ok, text) = await PostAsync(http, path, body, ct);
        if (!ok && jsonMode && text.Contains("response_format", StringComparison.OrdinalIgnoreCase))
        {
            body.Remove("response_format");
            (ok, text) = await PostAsync(http, path, body, ct);
        }
        if (!ok) throw new InvalidOperationException(text);

        using var doc = JsonDocument.Parse(text);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString() ?? "";
        return ParseSplit(content);
    }

    private static async Task<(bool Ok, string Text)> PostAsync(HttpClient http, string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode ? (true, text) : (false, ApiErrors.Failure(response.StatusCode, text).Message);
    }

    public static PromptParts ParseSplit(string content)
    {
        try
        {
            using var parsed = JsonDocument.Parse(content);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Odpowiedź modelu musi być obiektem JSON.");
            string Required(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()!.Trim()
                    : throw new InvalidOperationException($"Odpowiedź modelu wymaga pola tekstowego „{name}”.");
            var system = Required("system");
            var concept = Required("concept");
            var negative = Required("negative");
            if (string.IsNullOrWhiteSpace(concept))
                throw new InvalidOperationException("Model nie zwrócił promptu (concept).");
            var changes = root.TryGetProperty("changes", out var ch) && ch.ValueKind == JsonValueKind.String
                ? ch.GetString()!.Trim()
                : null;
            return new PromptParts(system, concept, string.IsNullOrWhiteSpace(changes) ? null : changes, negative);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Odpowiedź modelu nie jest poprawnym JSON-em.", ex);
        }
    }
}
