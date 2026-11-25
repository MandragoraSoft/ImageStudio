using System.Net.Http;

namespace ImageStudio.Storage.Mega;

// ATRAPA (etap 7a): prawdziwy adapter biblioteki MegaApiClient powstaje w etapie 7b
internal sealed class MegaApiClientAdapter : IMegaClient
{
    private const string Pending = "Adapter MEGA powstaje w etapie 7b";

    public MegaApiClientAdapter(HttpMessageHandler handler, TimeSpan timeout) { }

    public Task<string> LoginAsync(string email, string password, string? mfaCode, CancellationToken ct) => throw new NotSupportedException(Pending);
    public Task LogoutAsync(string session, CancellationToken ct) => throw new NotSupportedException(Pending);
    public Task UploadAsync(string session, string folder, string fileName, byte[] data, CancellationToken ct) => throw new NotSupportedException(Pending);
    public Task<byte[]?> DownloadAsync(string session, string folder, string fileName, CancellationToken ct) => throw new NotSupportedException(Pending);
    public Task DeleteAsync(string session, string folder, string fileName, CancellationToken ct) => throw new NotSupportedException(Pending);
}
