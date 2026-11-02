using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageStudio.Configuration;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ImageStudio.Tests;

// Fakt uruchamiany tylko z IMAGESTUDIO_LIVE_GUI=1 i kluczem w UBERLLM_API_KEY: prawdziwy dostawca (płatne wywołania), więc w zwykłym
// przebiegu jest pominięty. Przykład:
//   $env:IMAGESTUDIO_LIVE_GUI = "1"; $env:UBERLLM_API_KEY = "..."; dotnet test ImageStudio.Tests --filter "FullyQualifiedName~LiveGuiE2ETests"
// Opcjonalnie: IMAGESTUDIO_E2E_CHAT, IMAGESTUDIO_E2E_EDIT (modele) i IMAGESTUDIO_E2E_OUT (katalog na zrzuty okna).
public sealed class LiveGuiFactAttribute : FactAttribute
{
    public LiveGuiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("IMAGESTUDIO_LIVE_GUI") != "1" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UBERLLM_API_KEY")))
            Skip = "Test na żywo (płatny): ustaw IMAGESTUDIO_LIVE_GUI=1 i UBERLLM_API_KEY.";
    }
}

// Przebieg użytkownika w prawdziwym oknie: czat z modelem obsługującym narzędzia dostaje obraz i prosi o jego poprawienie,
// model woła edit_image, a wynik trafia do galerii. Wszystko na wątku UI, z prawdziwym MainWindow zbudowanym z tego samego kontenera
public sealed class LiveGuiE2ETests
{
    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    // Pionowy obraz 384x512: biała plansza z czerwonym kwadratem pośrodku
    private static byte[] SourcePng()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 384, 512));
            dc.DrawRectangle(Brushes.Red, null, new Rect(112, 176, 160, 160));
        }

        var bitmap = new RenderTargetBitmap(384, 512, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // Kolor piksela (R, G, B) w środku obrazu z pliku
    private static (byte R, byte G, byte B) CenterPixel(string path, out int width, out int height)
    {
        var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        width = frame.PixelWidth;
        height = frame.PixelHeight;
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(width / 2, height / 2, 1, 1), pixel, 4, 0);
        return (pixel[2], pixel[1], pixel[0]);
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && match(found)) return found;
            if (Find(child, match) is { } nested) return nested;
        }

        return null;
    }

    private static void Idle(Window window) => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Snapshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("IMAGESTUDIO_E2E_OUT") is not { Length: > 0 } directory) return;

        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }

    [LiveGuiFact]
    public async Task Chat_model_asks_the_edit_model_to_improve_an_attached_image_and_the_window_shows_the_result()
    {
        var chatModel = Env("IMAGESTUDIO_E2E_CHAT", "anthropic/claude-haiku-5.5");
        var editModel = Env("IMAGESTUDIO_E2E_EDIT", "venice/grok-imagine-edit");
        var settings = new AppSettings("https://api.uberllm.dev", "https://uberllm.dev", 1536, Environment.GetEnvironmentVariable("UBERLLM_API_KEY")!, "system");
        TestApp? app = null;
        string transcript = "";

        try
        {
            await WpfHost.RunAsync(async () =>
            {
                app = TestApp.Create(services =>
                {
                    services.AddSingleton(settings);
                    services.AddSingleton<ISettingsStore>(new MemorySettingsStore(settings));
                    services.AddSingleton<IProviderFactory, ProviderFactory>();
                    services.AddSingleton<IAiProvider>(sp => sp.GetRequiredService<IProviderFactory>().Create("uberllm", settings));
                });
                var vm = app.Vm;
                await vm.InitializeAsync();

                // dobór modeli: czat z narzędziami i modelem edycji z katalogu dostawcy
                Assert.Contains(vm.Chat.Models, m => m == chatModel);
                Assert.Contains(vm.EditModels, m => m.Id == editModel);
                vm.Studio.SetDefaultModel("edit", editModel);
                vm.Chat.SelectedModel = chatModel;
                vm.Tools.UseTools = true;
                vm.Tools.AppControlEnabled = true;
                Assert.Contains(vm.Tools.Tools, t => t.Name == "imagestudio__edit_image");

                vm.Chat.Attachments.Add(new ChatImage(SourcePng(), "image/png"));
                vm.Chat.Input = "Popraw ten obraz: zmień kolor czerwonego kwadratu na niebieski, tło ma zostać białe. " +
                                "Użyj narzędzia do edycji obrazów i dokładnej ścieżki pliku z załączników, nie zgaduj ścieżek. Na końcu napisz jednym zdaniem, co zrobiłeś.";
                await vm.Chat.SendCommand.ExecuteAsync(null);

                transcript = string.Join("\n", vm.Chat.Messages.Select(m =>
                    $"[{m.Role}] {m.Content}" + (m.HasToolCalls ? " " + string.Join(", ", m.ToolCalls!.Select(c => $"{c.Name}({c.ArgumentsJson})")) : "")));

                // 1. model faktycznie wywołał narzędzie edycji i dostał wynik bez błędu
                var call = vm.Chat.Messages.FirstOrDefault(m => m.HasToolCalls && m.ToolCalls!.Any(c => c.Name == "imagestudio__edit_image"));
                Assert.True(call is not null, "Model nie wywołał imagestudio__edit_image.\n" + transcript);
                var result = vm.Chat.Messages.First(m => m.Role == "tool" && m.ToolName == "imagestudio__edit_image" && !m.Content.StartsWith("Błąd narzędzia"));
                Assert.Contains("\"Kind\":\"edit\"", result.Content);

                // 2. wynik w galerii: model edycji z ustawień, plik istnieje, proporcje źródła (3:4) zachowane, kwadrat jest niebieski
                var entry = Assert.Single(vm.Gallery, e => e.Kind == "edit");
                Assert.Equal(editModel, entry.Model);
                Assert.True(File.Exists(entry.FilePath), entry.FilePath);
                var (r, g, b) = CenterPixel(entry.FilePath, out var width, out var height);
                Assert.True(Math.Abs(width / (double)height - 0.75) < 0.02, $"Proporcje wyniku {width}x{height}, oczekiwano 3:4");
                Assert.True(b > r + 40, $"Środek obrazu nie jest niebieski: RGB({r},{g},{b})\n{transcript}");

                // 3. model podsumował wynik tekstem (bez pustej wiadomości na końcu)
                var last = vm.Chat.Messages[^1];
                Assert.Equal("assistant", last.Role);
                Assert.False(string.IsNullOrWhiteSpace(last.Content), "Pusta ostatnia odpowiedź.\n" + transcript);
                Assert.DoesNotContain("Błąd", vm.Status);

                // 4. prawdziwe okno pokazuje ten sam stan: rozmowę w zakładce Czat i wynik w galerii
                var window = app.Get<MainWindow>();
                window.Show();
                vm.ActiveTab = 3;
                Idle(window);
                var messages = Find<ItemsControl>(window, c => ReferenceEquals(c.ItemsSource, vm.Chat.Messages));
                Assert.NotNull(messages);
                Assert.Equal(vm.Chat.Messages.Count, messages!.Items.Count);
                Snapshot(window, "1-czat");

                // galeria jest w zwiniętym Expanderze: rozwijamy ją jak użytkownik, a kliknięcie wpisu pokazuje wynik w podglądzie
                vm.ActiveTab = 1;
                Idle(window);
                var expander = Find<Expander>(window, e => e.Header as string == vm.GalleryTotalLabel);
                Assert.NotNull(expander);
                expander!.IsExpanded = true;
                Idle(window);
                var gallery = Find<ListBox>(window, c => ReferenceEquals(c.ItemsSource, vm.Gallery));
                Assert.NotNull(gallery);
                Assert.Equal(1, gallery!.Items.Count);
                vm.SelectedGalleryEntry = entry;
                Idle(window);
                Assert.NotNull(vm.Image);
                Snapshot(window, "2-galeria");
                window.Close();
            });
        }
        finally
        {
            app?.Dispose();
        }
    }
}
