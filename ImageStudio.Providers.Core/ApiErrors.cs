using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImageStudio.Services;

// Rozpoznawanie błędów API, które odrzuca pojedynczy parametr żądania (modele różnią się obsługiwanymi parametrami,
// a katalogi nie zawsze to deklarują). Pozwala ponowić żądanie bez tego parametru zamiast kończyć błędem
public static partial class ApiErrors
{
    // "Model 'x' does not accept a aspect_ratio parameter." / "Unknown parameter: 'seed'." / "Unrecognized request argument supplied: seed"
    [GeneratedRegex(@"does not (?:accept|support) an? '?(?<p>[A-Za-z_][\w\.]*)'? parameter|unknown parameter:? '?(?<p>[A-Za-z_][\w\.]*)'?|unrecognized request argument supplied:? '?(?<p>[A-Za-z_][\w\.]*)'?",
                    RegexOptions.IgnoreCase)]
    private static partial Regex RejectedParameterPattern();

    // Nazwa parametru odrzuconego przez API albo null, gdy odpowiedź to inny błąd
    public static string? RejectedParameter(string responseBody)
    {
        var text = responseBody;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var error))
                text = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
                    ? message.GetString() ?? responseBody
                    : error.ToString();
        }
        catch (JsonException)
        {
            // odpowiedź nie jest JSON-em: szukamy w surowym tekście
        }

        var match = RejectedParameterPattern().Match(text);
        return match.Success ? match.Groups["p"].Value : null;
    }

    // Błąd HTTP jako wyjątek z czytelnym komunikatem: "HTTP 401: Incorrect API key provided" zamiast surowego JSON-a
    public static InvalidOperationException Failure(HttpStatusCode status, string responseBody) =>
        new($"HTTP {(int)status}: {Message(responseBody, status)}");

    // Tekst błędu z odpowiedzi API. Formaty: {"error":{"message":..}} (OpenAI, OpenRouter, Kilo), {"error":".."},
    // {"message":".."}, {"detail":".."} albo lista {"detail":[{"msg":..}]} (Mistral), {"type":"error","error":{"message":..}} (OpenCode).
    // Gdy to nie JSON albo nic z tego nie pasuje, zwraca skrócony surowy tekst, a przy pustej odpowiedzi nazwę statusu
    public static string Message(string responseBody, HttpStatusCode status = 0)
    {
        var fallback = string.IsNullOrWhiteSpace(responseBody)
            ? status == 0 ? "" : status.ToString()
            : responseBody.Trim() is { Length: > 300 } trimmed ? trimmed[..300] + "…" : responseBody.Trim();
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            return Extract(doc.RootElement) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static string? Extract(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                return node.GetString() is { Length: > 0 } text ? text : null;
            case JsonValueKind.Array:
                var parts = node.EnumerateArray().Select(Extract).OfType<string>().ToList();
                return parts.Count > 0 ? string.Join("; ", parts) : null;
            case JsonValueKind.Object:
                foreach (var name in new[] { "message", "msg", "error", "detail", "error_description" })
                    if (node.TryGetProperty(name, out var value) && Extract(value) is { } found)
                        return found;
                return null;
            default:
                return null;
        }
    }
}
