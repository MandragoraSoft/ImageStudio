using System.Text.Json;
using System.Text.RegularExpressions;
using ImageStudio.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ImageStudio.Services;

// Połączenia z serwerami MCP i wywoływanie ich narzędzi. Używane z wątku UI (bez blokad).
// Nazwy narzędzi dla modelu to "serwer__narzędzie" (API modeli dopuszcza tylko [a-zA-Z0-9_-], do 64 znaków).
public sealed partial class McpToolHost : IAsyncDisposable
{
    private const int MaxResultChars = 20_000;

    private sealed record Connection(McpClient Client, List<ToolDefinition> Tools, List<string> ModelNames);

    private readonly Dictionary<string, Connection> _connections = [];
    private readonly Dictionary<string, (McpClient Client, string Tool)> _routes = [];

    public IReadOnlyList<ToolDefinition> Tools => _connections.Values.SelectMany(c => c.Tools).ToList();

    // Łączy z serwerem (zastępując poprzednie połączenie o tej nazwie); zwraca liczbę narzędzi
    public async Task<int> ConnectAsync(string name, McpServerConfig config, CancellationToken ct = default)
    {
        await DisconnectAsync(name);

        IClientTransport transport = config.Url is { Length: > 0 } url
            ? new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = name,
                Endpoint = new Uri(url),
                AdditionalHeaders = config.Headers,
            })
            : new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = name,
                Command = config.Command is { Length: > 0 } command
                    ? command
                    : throw new InvalidOperationException("Serwer musi mieć \"command\" albo \"url\"."),
                Arguments = config.Args,
                EnvironmentVariables = config.Env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
            });

        // npx przy pierwszym uruchomieniu pobiera pakiet, więc łączenie dostaje dużo czasu
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));

        var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        try
        {
            var definitions = new List<ToolDefinition>();
            var names = new List<string>();
            foreach (var tool in await client.ListToolsAsync(cancellationToken: timeout.Token))
            {
                var modelName = ModelName(name, tool.Name);
                if (_routes.ContainsKey(modelName)) continue; // kolizja po zamianie znaków: pierwsze narzędzie wygrywa

                var schema = tool.JsonSchema.ValueKind == JsonValueKind.Object
                    ? tool.JsonSchema.GetRawText()
                    : """{"type":"object","properties":{}}""";
                definitions.Add(new ToolDefinition(modelName, tool.Description ?? "", schema));
                names.Add(modelName);
                _routes[modelName] = (client, tool.Name);
            }

            _connections[name] = new Connection(client, definitions, names);
            return definitions.Count;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task DisconnectAsync(string name)
    {
        if (!_connections.Remove(name, out var connection)) return;

        foreach (var modelName in connection.ModelNames) _routes.Remove(modelName);
        await connection.Client.DisposeAsync();
    }

    public async Task<string> CallAsync(ToolCall call, CancellationToken ct = default)
    {
        if (!_routes.TryGetValue(call.Name, out var route)) return $"Nieznane narzędzie: {call.Name}";

        var arguments = string.IsNullOrWhiteSpace(call.ArgumentsJson)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(call.ArgumentsJson) ?? [];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var result = await route.Client.CallToolAsync(route.Tool, arguments, cancellationToken: timeout.Token);

        var text = string.Join("\n", result.Content.Select(block =>
            block is TextContentBlock t ? t.Text : $"[{block.Type}: pominięto]"));
        if (result.IsError == true) text = "Błąd narzędzia: " + text;
        return text.Length > MaxResultChars ? text[..MaxResultChars] + "\n…[obcięto]" : text;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _connections.Keys.ToList()) await DisconnectAsync(name);
    }

    // Nazwa widoczna dla modelu: tylko dozwolone znaki, maksymalnie 64
    internal static string ModelName(string server, string tool)
    {
        var name = $"{Unsafe().Replace(server, "_")}__{Unsafe().Replace(tool, "_")}";
        return name.Length > 64 ? name[..64] : name;
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex Unsafe();
}
