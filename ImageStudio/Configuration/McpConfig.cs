using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageStudio.Configuration;

// Jeden serwer MCP: proces lokalny (command, args, env) albo zdalny (url, headers); format jak w Claude Desktop
public sealed class McpServerConfig
{
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];
    public string? Url { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];

    [JsonIgnore]
    public string Description => Url is { Length: > 0 } ? Url : $"{Command} {string.Join(' ', Args)}".Trim();
}

// Serwery MCP z %LOCALAPPDATA%\ImageStudio\mcp.json: {"mcpServers": {"nazwa": {"command": "npx", "args": [...]}}}
// Plik edytuje użytkownik; nagłówki i zmienne env (w tym tokeny) są w nim zwykłym tekstem.
public sealed class McpConfig
{
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = [];

    // Błąd odczytu (np. zły JSON); pokazywany w panelu zamiast cichego pominięcia pliku
    [JsonIgnore]
    public string? Error { get; private set; }

    public static string DefaultPath => Path.Combine(AppPaths.UserDataDir, "mcp.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static McpConfig Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        if (!File.Exists(file)) return new McpConfig();

        try
        {
            return JsonSerializer.Deserialize<McpConfig>(File.ReadAllText(file), Options) ?? new McpConfig();
        }
        catch (JsonException ex)
        {
            return new McpConfig { Error = $"mcp.json: {ex.Message}" };
        }
    }

    // Tworzy pusty plik, żeby było co otworzyć w edytorze
    public static void EnsureFile(string? path = null)
    {
        var file = path ?? DefaultPath;
        if (File.Exists(file)) return;

        UserDataMutationGate.TryWrite(AppPaths.UserDataDir, file, () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{\n  \"mcpServers\": {\n  }\n}\n");
        });
    }
}
