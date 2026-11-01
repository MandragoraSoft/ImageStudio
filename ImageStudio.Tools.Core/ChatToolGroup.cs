using System.Text.Json;

namespace ImageStudio.Services;

// Jedno narzędzie grupy: nazwa dla modelu, opis, schemat parametrów (JSON Schema) i informacja, czy zmienia dane użytkownika
// (zapis wymaga zgody, jeśli czat jej jeszcze nie zebrał)
public sealed record ChatToolInfo(string Name, string Description, string Schema, bool Writes = false);

// Grupa wbudowanych narzędzi czatu w osobnej bibliotece (ImageStudio.Tools.*), jak dostawcy w ImageStudio.Providers.*.
// Aplikacja składa grupy w BuiltinToolRegistry; błędy argumentów to ArgumentException z komunikatem dla modelu
public interface IChatToolGroup
{
    string Id { get; }
    IReadOnlyList<ChatToolInfo> Tools { get; }

    // Wynik jako tekst dla modelu
    string Call(string name, JsonElement arguments);

    // Tekst dopisywany do instrukcji systemowej (np. zapamiętane informacje); null = nic
    string? SystemNote => null;
}

// Odczyt argumentów wywołania i składanie schematów, wspólne dla grup
public static class ToolArgs
{
    public static string Schema(params (string Name, string Property, bool Required)[] properties)
    {
        var props = string.Join(",", properties.Select(p => $"\"{p.Name}\":{p.Property}"));
        var required = string.Join(",", properties.Where(p => p.Required).Select(p => $"\"{p.Name}\""));
        return $$"""{"type":"object","properties":{{{props}}},"required":[{{required}}]}""";
    }

    public static string? OptString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? OptInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    public static string RequiredString(JsonElement args, string name) =>
        OptString(args, name) ?? throw new ArgumentException($"Brak wymaganego argumentu: {name}");
}
