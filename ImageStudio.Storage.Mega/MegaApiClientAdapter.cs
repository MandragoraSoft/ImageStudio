using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CG.Web.MegaApiClient;

namespace ImageStudio.Storage.Mega;

// Jedyny plik zależny od biblioteki MegaApiClient. Sesja to JSON {SessionId, MasterKey} (klucz główny konta, tylko w sekretach).
// Hasło przechodzi wyłącznie przez LoginAsync i nie trafia do pól, wyjątków ani logów.
internal sealed class MegaApiClientAdapter : IMegaClient
{
    private sealed record SessionDto(string SessionId, byte[] MasterKey);

    // Transport jest wspólny, a klient biblioteki jest podmieniany na świeży, gdy trzeba zalogować:
    // biblioteka po nieudanym wylogowaniu zostaje zalogowana i odrzuca kolejne logowania do restartu
    private readonly HttpClientWebClient _webClient;
    private CG.Web.MegaApiClient.MegaApiClient _client;
    // ponytail: jedna blokada na wszystkie wywołania (biblioteka nie jest pewna pod równoległość)
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _sessionInUse;

    public MegaApiClientAdapter(HttpMessageHandler handler, TimeSpan timeout)
    {
        _webClient = new HttpClientWebClient(handler, timeout);
        _client = NewClient();
    }

    private CG.Web.MegaApiClient.MegaApiClient NewClient() => new(_webClient);

    public async Task<string> LoginAsync(string email, string password, string? mfaCode, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _client = NewClient();
            var token = await Mfa(() => _client.LoginAsync(email, password, mfaCode));
            _sessionInUse = JsonSerializer.Serialize(new SessionDto(token.SessionId, token.MasterKey));
            return _sessionInUse;
        }
        finally { _gate.Release(); }
    }

    public async Task LogoutAsync(string session, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await Resume(session);
            await _client.LogoutAsync();
        }
        finally
        {
            // Także po nieudanym wylogowaniu: klient z poprzednią sesją nie może zostać zalogowany
            _sessionInUse = null;
            _client = NewClient();
        }
    }

    public async Task UploadAsync(string session, string folder, string fileName, byte[] data, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await Resume(session);
            var nodes = (await _client.GetNodesAsync()).ToList();
            var folderNode = FindFolder(nodes, folder) ?? await _client.CreateFolderAsync(folder, Root(nodes));
            using var stream = new MemoryStream(data);
            var uploaded = await _client.UploadAsync(stream, fileName, folderNode, null, null, ct);
            // Najpierw nowy plik, potem sprzątanie: nieudany upload nie kasuje poprzedniej wersji
            var replaced = FilesNamed(nodes, folderNode, fileName).Where(n => n.Id != uploaded.Id).ToList();
            await DeleteReplacedAsync(replaced);
        }
        finally { _gate.Release(); }
    }

    public async Task<byte[]?> DownloadAsync(string session, string folder, string fileName, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await Resume(session);
            var nodes = (await _client.GetNodesAsync()).ToList();
            var folderNode = FindFolder(nodes, folder);
            // Gdy jest kilka plików o tej nazwie, bierzemy najnowszy
            var file = folderNode is null
                ? null
                : FilesNamed(nodes, folderNode, fileName).OrderByDescending(n => n.CreationDate ?? DateTime.MinValue).FirstOrDefault();
            if (file is null) return null;
            using var remote = await _client.DownloadAsync(file, null, ct);
            using var memory = new MemoryStream();
            await remote.CopyToAsync(memory, ct);
            return memory.ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string session, string folder, string fileName, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await Resume(session);
            var nodes = (await _client.GetNodesAsync()).ToList();
            var folderNode = FindFolder(nodes, folder);
            if (folderNode is null) return;
            // moveToTrash: false, zgodnie z OneDrive i Google Drive; brak pliku to nie błąd
            foreach (var file in FilesNamed(nodes, folderNode, fileName)) await _client.DeleteAsync(file, false);
        }
        finally { _gate.Release(); }
    }

    // Wznawia sesję z zapisanego tekstu, gdy bieżąca w bibliotece to inna sesja
    private async Task Resume(string session)
    {
        if (session == _sessionInUse && _client.IsLoggedIn) return;
        var dto = JsonSerializer.Deserialize<SessionDto>(session) ?? throw new InvalidOperationException("Zapisana sesja MEGA jest uszkodzona.");
        if (_client.IsLoggedIn) _client = NewClient();
        await MfaVoid(() => _client.LoginAsync(new CG.Web.MegaApiClient.MegaApiClient.LogonSessionToken(dto.SessionId, dto.MasterKey)));
        _sessionInUse = session;
    }

    // Błąd kasowania starej wersji nie unieważnia udanego uploadu: stara kopia zostaje w chmurze (duplikat, bez utraty danych)
    private async Task DeleteReplacedAsync(IList<INode> replaced)
    {
        foreach (var old in replaced)
        {
            try { await _client.DeleteAsync(old, false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
    }

    private static INode Root(IList<INode> nodes) => nodes.First(n => n.Type == NodeType.Root);

    private static INode? FindFolder(IList<INode> nodes, string folder) =>
        nodes.FirstOrDefault(n => n.Type == NodeType.Directory && n.ParentId == Root(nodes).Id && n.Name == folder);

    private static IEnumerable<INode> FilesNamed(IList<INode> nodes, INode folderNode, string fileName) =>
        nodes.Where(n => n.Type == NodeType.File && n.ParentId == folderNode.Id && n.Name == fileName);

    // Konto z 2FA bez kodu zwraca ApiException z kodem TwoFactorAuthenticationError: mapujemy na wyjątek domenowy
    private static async Task<T> Mfa<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (ApiException ex) when (ex.ApiResultCode == ApiResultCode.TwoFactorAuthenticationError) { throw new MegaMfaRequiredException(); }
    }

    private static async Task MfaVoid(Func<Task> call)
    {
        try { await call(); }
        catch (ApiException ex) when (ex.ApiResultCode == ApiResultCode.TwoFactorAuthenticationError) { throw new MegaMfaRequiredException(); }
    }
}
