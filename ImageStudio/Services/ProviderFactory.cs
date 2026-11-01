using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Tworzy dostawcę AI wg ustawień (wstrzykiwana do MainViewModel, żeby zmiana dostawcy nie wołała statycznego rejestru)
public interface IProviderFactory
{
    IAiProvider Create(string providerId, AppSettings settings);
}

// Każdy utworzony dostawca jest opakowany dekoratorem aktywności (spinner w stopce)
public sealed class ProviderFactory(IActivityTracker activity) : IProviderFactory
{
    public IAiProvider Create(string providerId, AppSettings settings) =>
        new ActivityAiProvider(ProviderRegistry.Create(providerId, settings), activity);
}
