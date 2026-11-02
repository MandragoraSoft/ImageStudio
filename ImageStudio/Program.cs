using System.Text;
using ImageStudio.Cli;

namespace ImageStudio;

// Punkt wejścia: "ImageStudio.exe mcp ..." uruchamia serwer MCP bez okna, każde inne wywołanie okno aplikacji
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        PluginResolver.Register();
        if (McpCli.IsMcp(args))
        {
            McpCliHost.EnsureConsole();

            // Polskie znaki w wyjściu: UTF-8 bez BOM zamiast strony kodowej konsoli (BOM zepsułby też protokół stdio)
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            return McpCli.RunAsync(args, Console.Out, Console.Error, McpCliHost.CreateStudioAsync, cts.Token, limits: McpCliHost.LoadLimits()).GetAwaiter().GetResult();
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
