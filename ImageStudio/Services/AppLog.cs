using Microsoft.Extensions.Logging;

namespace ImageStudio.Services;

// Logger aplikacji: wyjście na konsolę (w Debug projekt ma okno konsoli, patrz ImageStudio.csproj)
public static class AppLog
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(b => b
        .SetMinimumLevel(LogLevel.Information)
        .AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        }));

    public static ILogger<T> For<T>() => Factory.CreateLogger<T>();
}
