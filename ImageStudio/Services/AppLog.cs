using Microsoft.Extensions.Logging;

namespace ImageStudio.Services;

// Logger aplikacji: wyjście na konsolę (w Debug projekt ma okno konsoli, patrz ImageStudio.csproj)
public static class AppLog
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(b => b
        .SetMinimumLevel(LogLevel.Information)
        .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)  // stdout zostaje dla protokołu MCP po stdio i dla wyników CLI
        .AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        }));

    public static ILogger<T> For<T>() => Factory.CreateLogger<T>();

    // Fragment tekstu do dziennika: jedna linia, przycięta (argumenty i wyniki narzędzi bywają długie)
    public static string Preview(string? text, int max = 1000)
    {
        var line = (text ?? "").ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : $"{line[..max]}… (+{line.Length - max} zn.)";
    }
}
