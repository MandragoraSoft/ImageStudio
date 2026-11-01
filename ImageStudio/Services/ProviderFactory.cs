using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Tworzy dostawcę AI wg ustawień (wstrzykiwana do MainViewModel, żeby zmiana dostawcy nie wołała statycznego rejestru)
public interface IProviderFactory
{
    IAiProvider Create(string providerId, AppSettings settings);
}

public sealed class ProviderFactory : IProviderFactory
{
    public IAiProvider Create(string providerId, AppSettings settings) => ProviderRegistry.Create(providerId, settings);
}
