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
                                                    IReadOnlyList<ToolDefinition>? tools, CancellationToken ct)
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

        using var response = await http.PostAsJsonAsync(path, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {text}");

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

        return new ChatReply(content, calls);
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

    public const string SplitFormat =
        "Split the input into two parts and reply with JSON only, no code fences: " +
        "{\"system\": \"...\", \"concept\": \"...\"}. " +
        "\"system\" = technical configuration (style, lighting, camera, quality, format, constraints), keep existing system text. " +
        "\"concept\" = what the user wants depicted, improved and vivid. Keep the input language.";

    public static PromptParts ParseSplit(string content, string fallbackSystem, string fallbackConcept)
    {
        var json = content.Trim();
        if (json.StartsWith("```"))
            json = json.Trim('`').Trim();
        if (json.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            json = json[4..].Trim();

        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            var system = root.TryGetProperty("system", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()!.Trim()
                : fallbackSystem.Trim();
            var concept = root.TryGetProperty("concept", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()!.Trim()
                : "";
            return new PromptParts(system, concept);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Nie-JSON albo JSON niebędący obiektem (np. sam tekst w cudzysłowie): traktujemy jak surową odpowiedź
            return new PromptParts(fallbackSystem.Trim(), content.Trim().Trim('"'));
        }
    }
}
