using System.IO;
using System.Runtime.InteropServices;
using ImageStudio.Composition;
using ImageStudio.Configuration;
using ImageStudio.Localization;
using ImageStudio.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;

namespace ImageStudio.Cli;

// Składa rdzeń aplikacji bez okna: ten sam kontener, ustawienia, klucze, dostawca i magazyn wyników co w oknie,
// ale bez MainViewModel, okien i odtwarzacza
public static class McpCliHost
{
    public static async Task<IStudioApi> CreateStudioAsync()
    {
        DotEnv.LoadDefaults();
        var services = new ServiceCollection().AddImageStudioCore().AddImageStudioInfrastructure().BuildServiceProvider();
        Loc.Set(services.GetRequiredService<AppSettings>().Language);

        var stateStore = services.GetRequiredService<IUserStateStore>();
        var storage = services.GetRequiredService<MediaStorageHost>();
        storage.Load(stateStore.Load());

        var provider = services.GetRequiredService<IAiProvider>();
        var library = new FileStudioLibrary(services.GetRequiredService<IGalleryStore>(), services.GetRequiredService<IVoiceGalleryStore>());
        var studio = new StudioApi(() => provider, storage, library, services.GetRequiredService<IPresetStore>(),
                                   services.GetRequiredService<ImageDownloadService>(), null,
                                   new StateFileDefaults(stateStore, services.GetRequiredService<AppSettings>().ProviderId));
        await studio.CatalogAsync();
        return studio;
    }

    // Limity MCP z state.json (te same, które zmienia okno w Ustawieniach, zakładka MCP)
    public static McpLimits LoadLimits() => McpLimits.From(new FileUserStateStore().Load());

    // Aplikacja okienkowa (WinExe) nie ma konsoli: dołączamy do konsoli rodzica, żeby wyniki było widać w terminalu.
    // Gdy stdout jest przekierowany (potok, plik) albo to build Debug z konsolą, nic nie robimy.
    public static void EnsureConsole()
    {
        if (GetStdHandle(-11) != IntPtr.Zero) return;
        if (!AttachConsole(uint.MaxValue)) return;

        var handle = CreateFile("CONOUT$", 0x40000000, 2, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return;

        var writer = new StreamWriter(new FileStream(new SafeFileHandle(handle, true), FileAccess.Write)) { AutoFlush = true };
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
