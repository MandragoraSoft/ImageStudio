using System.Runtime.CompilerServices;

// Testy dzielą jeden katalog danych (state.json, settings.json, zestawy), więc nie biegną równolegle (cały zestaw trwa ok. 3 s)
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ImageStudio.Tests;

// Testy nie mogą czytać ani zapisywać prawdziwych danych użytkownika (state.json, gallery.json, zestawy)
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void IsolateUserData() =>
        Environment.SetEnvironmentVariable("IMAGESTUDIO_DATA_DIR",
                                           Path.Combine(Path.GetTempPath(), "ImageStudio.Tests", Guid.NewGuid().ToString("N")));
}
