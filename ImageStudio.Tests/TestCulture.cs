using System.Globalization;
using System.Runtime.CompilerServices;

namespace ImageStudio.Tests;

// Język "system" zależy od kultury maszyny: na CI (en-US) zapis ustawień przełączał interfejs na angielski i psuł testy.
// Testy zakładają polski, więc kulturę ustawiamy raz, dla wszystkich wątków (także wątków okien WPF).
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Init()
    {
        var pl = new CultureInfo("pl-PL");
        CultureInfo.DefaultThreadCurrentUICulture = pl;
        CultureInfo.CurrentUICulture = pl;
    }
}
