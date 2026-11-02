using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ImageStudio.Services;

// Lokalny serwer MCP (Streamable HTTP, tylko 127.0.0.1) wystawiający narzędzia sterujące aplikacją zewnętrznym klientom
// (np. Claude Desktop, własne agenty). Wymaga tokenu (nagłówek Authorization: Bearer) i odrzuca żądania z obcym Origin,
// więc strona w przeglądarce nie może sterować aplikacją. Kosztowne narzędzia pytają o zgodę w oknie aplikacji.
public sealed class McpAppServer : IAsyncDisposable
{
    private static readonly ILogger Log = AppLog.For<McpAppServer>();

    private readonly IMcpToolSet _tools;
    private readonly McpLimits? _limits;
    private readonly Func<Func<Task<string>>, Task<string>> _runOnUi;
    private WebApplication? _app;

    // runOnUi wykonuje akcję w wątku interfejsu (narzędzia dotykają widoków); w testach może wywołać ją wprost
    public McpAppServer(IMcpToolSet tools, Func<Func<Task<string>>, Task<string>> runOnUi, McpLimits? limits = null)
    {
        _tools = tools;
        _limits = limits;
        _runOnUi = runOnUi;
    }

    public bool IsRunning => _app is not null;

    public static string EndpointFor(int port) => $"http://127.0.0.1:{port}/mcp";

    public async Task StartAsync(int port, string token)
    {
        if (_app is not null) await StopAsync();

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();

        Configure(builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!IsAllowedOrigin(context.Request.Headers.Origin.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!TokenMatches(context.Request.Headers.Authorization.ToString(), token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        });
        app.MapMcp("/mcp");

        await app.StartAsync();
        _app = app;
    }

    // Ten sam serwer na stdio (klient uruchamia proces i rozmawia przez standardowe wejście i wyjście). Na stdout trafia wyłącznie
    // protokół, więc dziennik (AppLog) idzie na stderr. Kończy się, gdy klient zamknie wejście albo anulowano ct.
    public async Task RunStdioAsync(CancellationToken ct)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        Configure(builder.Services.AddMcpServer().WithStdioServerTransport());
        await builder.Build().RunAsync(ct);
    }

    // Wspólne dla HTTP i stdio: informacje o serwerze oraz obsługa listy i wywołań narzędzi (z dziennikiem akcji)
    private void Configure(IMcpServerBuilder builder) => builder
        .WithListToolsHandler((_, _) =>
        {
            var definitions = _tools.Definitions;
            if (_limits?.ExceedsTools(definitions.Count) == true)
                Log.LogWarning("MCP: wystawiono {Count} narzędzi, powyżej limitu {Max} (Ustawienia, zakładka MCP): słabsze modele i klienci mogą się gubić",
                               definitions.Count, _limits.MaxTools);
            Log.LogInformation("MCP odebrano: tools/list; wysłano {Count} narzędzi: {Names}", definitions.Count,
                               string.Join(", ", definitions.Select(d => d.Name)));
            return ValueTask.FromResult(new ListToolsResult
            {
                Tools = definitions.Select(d => new Tool
                {
                    Name = d.Name,
                    Description = d.Description,
                    InputSchema = JsonSerializer.Deserialize<JsonElement>(d.ParametersJson),
                }).ToList(),
            });
        })
        .WithCallToolHandler(async (context, _) =>
        {
            var request = context.Params ?? throw new InvalidOperationException("Brak parametrów wywołania.");
            var arguments = request.Arguments is null ? "{}" : JsonSerializer.Serialize(request.Arguments);
            Log.LogInformation("MCP odebrano: tools/call {Tool} (argumenty: {Args})", request.Name, AppLog.Preview(arguments));
            var clock = Stopwatch.StartNew();
            try
            {
                var text = await _runOnUi(() => _tools.CallAsync(request.Name, arguments, alreadyApproved: false));
                Log.LogInformation("MCP wysłano: {Tool} w {Ms} ms, wynik ({Chars} zn.): {Result}", request.Name, clock.ElapsedMilliseconds,
                                   text.Length, AppLog.Preview(text));
                return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
            }
            catch (Exception ex)
            {
                Log.LogWarning("MCP wysłano: {Tool} w {Ms} ms, błąd: {Error}", request.Name, clock.ElapsedMilliseconds, AppLog.Preview(ex.Message));
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
            }
        })
        .Services.Configure<McpServerOptions>(options => options.ServerInfo = new Implementation { Name = "ImageStudio", Version = "1.0" });

    public async Task StopAsync()
    {
        if (_app is not { } app) return;

        _app = null;
        await app.StopAsync();
        await app.DisposeAsync();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    // Brak Origin (klient natywny) jest dozwolony; przeglądarka wysyła Origin, który musi być lokalny
    internal static bool IsAllowedOrigin(string origin) =>
        origin.Length == 0
        || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Host is "localhost" or "127.0.0.1" or "[::1]");

    internal static bool TokenMatches(string header, string token) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes("Bearer " + token));
}
