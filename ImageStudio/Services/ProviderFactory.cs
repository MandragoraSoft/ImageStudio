using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Tworzy dostawcę AI wg ustawień (wstrzykiwana do MainViewModel, żeby zmiana dostawcy nie wołała statycznego rejestru)
public interface IProviderFactory
{
    IAiProvider Create(string providerId, AppSettings settings);
}

// Każdy utworzony dostawca jest opakowany dekoratorem aktywności (spinner w stopce)
public sealed class ProviderFactory(IActivityTracker activity, ISecretStore secrets, OutgoingRequestRegistry? requests) : IProviderFactory
{
    public ProviderFactory(IActivityTracker activity, ISecretStore secrets) : this(activity, secrets, null) { }
    public IAiProvider Create(string providerId, AppSettings settings) =>
        new ActivityAiProvider(ProviderRegistry.Create(providerId, settings, secrets, requests is null ? null : () => requests.CreateHandler()), activity);
}
