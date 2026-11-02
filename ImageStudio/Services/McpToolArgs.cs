using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageStudio.Services;

// Wspólne dla zestawów narzędzi MCP: schemat JSON, czytanie argumentów i serializacja wyników
internal static class McpToolArgs
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Schemat obiektu z listą (nazwa, schemat właściwości, wymagana)
    public static string Schema(params (string Name, string Property, bool Required)[] properties)
    {
        var props = string.Join(",", properties.Select(p => $"\"{p.Name}\":{p.Property}"));
        var required = string.Join(",", properties.Where(p => p.Required).Select(p => $"\"{p.Name}\""));
        return $$"""{"type":"object","properties":{{{props}}},"required":[{{required}}]}""";
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    // Wynik listy z ostrzeżeniem o ucięciu: źródło dostało limit + 1, więc nadmiar oznacza, że są kolejne wyniki.
    // Bez ucięcia zwracamy zwykłą tablicę, z ucięciem obiekt {items, truncated, warning}
    public static string Paged<T>(IReadOnlyList<T> fetched, int limit, int max) =>
        fetched.Count <= limit
            ? Serialize(fetched)
            : Serialize(new
            {
                items = fetched.Take(limit),
                truncated = true,
                warning = $"Pokazano {limit} wyników, są kolejne. Zawęź query albo zwiększ limit (maks. {max}; zmiana w Ustawieniach, zakładka MCP).",
            });

    public static string RequiredString(JsonElement args, string name) =>
        OptString(args, name) is { Length: > 0 } value ? value : throw new ArgumentException($"Brak parametru \"{name}\".");

    public static string? OptString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? OptInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    public static double? OptDouble(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? number
            : null;

    public static int RequiredInt(JsonElement args, string name) =>
        OptInt(args, name) ?? throw new ArgumentException($"Brak parametru \"{name}\" (liczba całkowita).");

    // Tablica tekstów (np. ścieżki lub adresy); brak parametru = pusta lista
    public static IReadOnlyList<string> OptStrings(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
}

// Zestaw narzędzi MCP: nazwy surowe (bez przedrostka), definicje zależne od dostawcy i wywołanie po nazwie.
// Implementują go narzędzia sterujące oknem (AppControlTools) i bezgłowe operacje (StudioTools), a serwer MCP i czat
// widzą je jako jedną listę (CompositeToolSet).
public interface IMcpToolSet
{
    // Narzędzia widoczne dla modelu przy aktywnym dostawcy
    IReadOnlyList<ToolDefinition> Definitions { get; }

    // Wszystkie narzędzia niezależnie od dostawcy (lista w ustawieniach czatu)
    IReadOnlyList<ToolDefinition> AllDefinitions { get; }

    // Czy narzędzie wymaga zgody użytkownika (koszt albo operacja nieodwracalna)
    bool IsCostly(string name);

    // alreadyApproved: użytkownik zatwierdził to wywołanie wcześniej (zgoda w czacie), więc nie pytamy drugi raz
    Task<string> CallAsync(string name, string argumentsJson, bool alreadyApproved);
}

// Łączy zestawy w jeden
public sealed class CompositeToolSet(params IMcpToolSet[] sets) : IMcpToolSet
{
    // Przy powtórzonej nazwie wygrywa pierwszy zestaw (np. list_models okna z kolejnością preferowanych przed wersją bez okna)
    public IReadOnlyList<ToolDefinition> Definitions => sets.SelectMany(s => s.Definitions).DistinctBy(d => d.Name).ToList();
    public IReadOnlyList<ToolDefinition> AllDefinitions => sets.SelectMany(s => s.AllDefinitions).DistinctBy(d => d.Name).ToList();

    public bool IsCostly(string name) => sets.Any(s => s.IsCostly(name));

    public Task<string> CallAsync(string name, string argumentsJson, bool alreadyApproved) =>
        (sets.FirstOrDefault(s => s.AllDefinitions.Any(d => d.Name == name)) ?? throw new ArgumentException($"Nieznane narzędzie: {name}"))
        .CallAsync(name, argumentsJson, alreadyApproved);
}
