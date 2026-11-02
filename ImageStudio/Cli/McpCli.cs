using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using ImageStudio.Services;

namespace ImageStudio.Cli;

// Polecenie: imagestudio mcp ...  (serwer MCP bez okna oraz jednorazowe wywołania narzędzi z wiersza poleceń)
public sealed record CliOptions(string Command, string Transport = "stdio", int Port = 8765, string? Token = null, bool Yes = false,
                                string? Tool = null, string? Json = null, string? Error = null);

public static class McpCli
{
    public const string Usage = """
        ImageStudio MCP bez okna

          imagestudio mcp [serve] [--stdio | --http] [--port N] [--token T] [--yes]
              Uruchamia serwer MCP. Domyślnie stdio (klient uruchamia proces); --http nasłuchuje na 127.0.0.1:<port>
              z tokenem (z --token, zmiennej IMAGESTUDIO_MCP_TOKEN albo wylosowanym, wypisanym na stderr).
          imagestudio mcp tools
              Wypisuje narzędzia dostępne przy aktywnym dostawcy.
          imagestudio mcp call <narzędzie> [json] [--yes]
              Wywołuje jedno narzędzie i wypisuje wynik na stdout (json: argumenty, domyślnie {}).

          --yes   zgoda na operacje kosztowne i nieodwracalne (generowanie, usuwanie, eksport); bez niej są odrzucane.

        Ustawienia, klucze API, wybrany dostawca i modele są te same co w oknie aplikacji. Dziennik idzie na stderr.
        """;

    public static bool IsMcp(string[] args) => args.Length > 0 && string.Equals(args[0], "mcp", StringComparison.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args)
    {
        var rest = args.Skip(1).ToList();
        var yes = rest.RemoveAll(a => a is "--yes" or "-y") > 0;
        if (rest.Count == 0) return new CliOptions("serve", Yes: yes);
        if (rest[0] is "help" or "-h" or "--help" || rest.Contains("--help")) return new CliOptions("help");

        var command = rest[0] is "serve" or "tools" or "call" ? rest[0] : "serve";
        if (command == rest[0]) rest.RemoveAt(0);
        var options = new CliOptions(command, Yes: yes);

        if (command == "tools") return rest.Count == 0 ? options : options with { Error = $"Nieoczekiwany argument: {rest[0]}" };
        if (command == "call")
        {
            if (rest.Count is 0 or > 2) return options with { Error = "Użycie: mcp call <narzędzie> [json]" };
            var json = rest.Count == 2 ? rest[1] : "{}";
            try
            {
                using var _ = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                return options with { Error = $"Argumenty nie są poprawnym JSON: {ex.Message}" };
            }

            return options with { Tool = rest[0], Json = json };
        }

        for (var i = 0; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--stdio": options = options with { Transport = "stdio" }; break;
                case "--http": options = options with { Transport = "http" }; break;
                case "--port" when i + 1 < rest.Count && int.TryParse(rest[i + 1], out var port) && port is > 0 and < 65536:
                    options = options with { Port = port };
                    i++;
                    break;
                case "--token" when i + 1 < rest.Count:
                    options = options with { Token = rest[++i] };
                    break;
                default:
                    return options with { Error = $"Nieznana opcja albo zła wartość: {rest[i]}" };
            }
        }

        return options;
    }

    // studio tworzy rdzeń aplikacji (w produkcji McpCliHost.CreateStudioAsync, w testach atrapę); kod wyjścia 0 = sukces
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Func<Task<IStudioApi>> studio,
                                           CancellationToken ct = default, Func<string>? newToken = null)
    {
        var options = Parse(args);
        if (options.Command == "help")
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }

        if (options.Error is not null)
        {
            await error.WriteLineAsync(options.Error);
            await error.WriteLineAsync(Usage);
            return 2;
        }

        try
        {
            var api = await studio();
            bool Confirm(ToolCall call)
            {
                if (options.Yes) return true;
                error.WriteLine($"Odrzucono {call.Name}: operacja kosztuje albo jest nieodwracalna. Dodaj --yes, aby ją wykonać.");
                return false;
            }

            var tools = new StudioTools(api, Confirm);
            switch (options.Command)
            {
                case "tools":
                    foreach (var d in tools.Definitions) await output.WriteLineAsync($"{d.Name} - {d.Description}");
                    return 0;

                case "call":
                    var result = await tools.CallAsync(options.Tool!, options.Json!, alreadyApproved: false);
                    await output.WriteLineAsync(result);
                    return result.StartsWith("Użytkownik odrzucił", StringComparison.Ordinal) ? 3 : 0;

                default:
                    await using (var server = new McpAppServer(tools, action => action()))
                    {
                        if (options.Transport == "stdio")
                        {
                            await error.WriteLineAsync("Serwer MCP (stdio) działa. Zakończ zamykając wejście albo Ctrl+C.");
                            await server.RunStdioAsync(ct);
                        }
                        else
                        {
                            var token = options.Token ?? Environment.GetEnvironmentVariable("IMAGESTUDIO_MCP_TOKEN")
                                        ?? (newToken ?? RandomToken)();
                            await server.StartAsync(options.Port, token);
                            await error.WriteLineAsync($"Serwer MCP: {McpAppServer.EndpointFor(options.Port)}  token (Authorization: Bearer): {token}");
                            try
                            {
                                await Task.Delay(Timeout.Infinite, ct);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        }
                    }

                    return 0;
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException or HttpRequestException)
        {
            await error.WriteLineAsync($"Błąd: {ex.Message}");
            return 1;
        }
    }

    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
