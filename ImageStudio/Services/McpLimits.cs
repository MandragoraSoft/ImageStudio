using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Limity narzędzi MCP (konfigurowane w Ustawieniach, zakładka MCP; zapis w state.json, więc czyta je też CLI):
// MaxTools: powyżej tej liczby wystawionych narzędzi aplikacja ostrzega (słabsze modele i klienci gubią się przy wielu narzędziach),
// ListLimit: domyślna liczba wyników narzędzi list_*, MaxListLimit: górna granica parametru limit.
public sealed class McpLimits
{
    public const int DefaultMaxTools = 30;
    public const int DefaultListLimit = 30;
    public const int DefaultMaxListLimit = 100;

    public int MaxTools { get; private set; } = DefaultMaxTools;
    public int ListLimit { get; private set; } = DefaultListLimit;
    public int MaxListLimit { get; private set; } = DefaultMaxListLimit;

    // Zmiana limitów (np. z Ustawień); okno odświeża ostrzeżenia
    public event Action? Changed;

    public static McpLimits From(UserState state)
    {
        var limits = new McpLimits();
        limits.Set(state.McpMaxTools ?? DefaultMaxTools, state.McpListLimit ?? DefaultListLimit, state.McpMaxListLimit ?? DefaultMaxListLimit);
        return limits;
    }

    public void Capture(UserState state)
    {
        state.McpMaxTools = MaxTools;
        state.McpListLimit = ListLimit;
        state.McpMaxListLimit = MaxListLimit;
    }

    // Wartości spoza zakresu są przycinane: liczba narzędzi 5-500, górna granica list 10-1000, domyślna lista 1..górna granica
    public static (int MaxTools, int ListLimit, int MaxListLimit) Normalize(int maxTools, int listLimit, int maxListLimit)
    {
        var max = Math.Clamp(maxListLimit, 10, 1000);
        return (Math.Clamp(maxTools, 5, 500), Math.Clamp(listLimit, 1, max), max);
    }

    public void Set(int maxTools, int listLimit, int maxListLimit)
    {
        var (tools, list, max) = Normalize(maxTools, listLimit, maxListLimit);
        if ((tools, list, max) == (MaxTools, ListLimit, MaxListLimit)) return;

        (MaxTools, ListLimit, MaxListLimit) = (tools, list, max);
        Changed?.Invoke();
    }

    public bool ExceedsTools(int count) => count > MaxTools;

    // Limit z parametru narzędzia (null = domyślny) w granicach ustawień
    public int ListSize(int? requested) => Math.Clamp(requested ?? ListLimit, 1, MaxListLimit);
}
