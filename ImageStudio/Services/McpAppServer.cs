using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ImageStudio.Services;

// Lokalny serwer MCP (Streamable HTTP, tylko 127.0.0.1) wystawiający narzędzia sterujące aplikacją zewnętrznym klientom
// (np. Claude Desktop, własne agenty). Wymaga tokenu (nagłówek Authorization: Bearer) i odrzuca żądania z obcym Origin,
// więc strona w przeglądarce nie może sterować aplikacją. Kosztowne narzędzia pytają o zgodę w oknie aplikacji.
public sealed class McpAppServer : IAsyncDisposable
{
    private readonly AppControlTools _tools;
    private readonly Func<Func<Task<string>>, Task<string>> _runOnUi;
    private WebApplication? _app;

    // runOnUi wykonuje akcję w wątku interfejsu (narzędzia dotykają widoków); w testach może wywołać ją wprost
    public McpAppServer(AppControlTools tools, Func<Func<Task<string>>, Task<string>> runOnUi)
    {
        _tools = tools;
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

        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "ImageStudio", Version = "1.0" };
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = _tools.Definitions.Select(d => new Tool
                {
                    Name = d.Name,
                    Description = d.Description,
                    InputSchema = JsonSerializer.Deserialize<JsonElement>(d.ParametersJson),
                }).ToList(),
            }))
            .WithCallToolHandler(async (context, _) =>
            {
                var request = context.Params ?? throw new InvalidOperationException("Brak parametrów wywołania.");
                var arguments = request.Arguments is null ? "{}" : JsonSerializer.Serialize(request.Arguments);
                try
                {
                    var text = await _runOnUi(() => _tools.CallAsync(request.Name, arguments, alreadyApproved: false));
                    return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
                }
                catch (Exception ex)
                {
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
                }
            });

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
