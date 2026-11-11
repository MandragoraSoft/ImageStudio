using System.Globalization;

namespace ImageStudio.Services;

// Szacunek tokenów bez tokenizera modelu: ok. 4 znaki na token. Używany tam, gdzie API nie podało zużycia
// (wskaźnik kontekstu w czacie, narzędzia). Przybliżenie, nie licznik: wynik pokazujemy z "~".
public static class TokenEstimator
{
    public static int Estimate(string? text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    public static int Estimate(ToolDefinition tool) =>
        Estimate(tool.Name) + Estimate(tool.Description) + Estimate(tool.ParametersJson);

    // Krótki zapis liczby tokenów: 950, 54k, 1.3M
    public static string Short(int tokens)
    {
        if (tokens < 1000) return tokens.ToString(CultureInfo.InvariantCulture);
        if (tokens < 1_000_000)
            return Math.Round(tokens / 1000.0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "k";
        return Math.Round(tokens / 1_000_000.0, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture) + "M";
    }
}
