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
}
