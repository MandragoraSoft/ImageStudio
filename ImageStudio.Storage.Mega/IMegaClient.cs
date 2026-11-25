namespace ImageStudio.Storage.Mega;

// Dostęp do MEGA za małym interfejsem: testy podstawiają atrapę bez sieci, produkcja MegaApiClientAdapter.
// Sesja to nieprzezroczysty tekst (zserializowany token sesji MEGA) trzymany w sekretach; hasło nie wychodzi poza LoginAsync
public interface IMegaClient
{
    // Logowanie e-mailem i hasłem; mfaCode: kod 2FA albo null. Konto z 2FA bez kodu: MegaMfaRequiredException
    Task<string> LoginAsync(string email, string password, string? mfaCode, CancellationToken ct);

    // Unieważnia sesję na serwerze
    Task LogoutAsync(string session, CancellationToken ct);

    // Operacje w folderze aplikacji (tworzony przy pierwszym użyciu); Upload zastępuje plik o tej samej nazwie
    Task UploadAsync(string session, string folder, string fileName, byte[] data, CancellationToken ct);
    Task<byte[]?> DownloadAsync(string session, string folder, string fileName, CancellationToken ct);   // null = brak pliku
    Task DeleteAsync(string session, string folder, string fileName, CancellationToken ct);             // brak pliku = OK
}

public sealed class MegaMfaRequiredException() : Exception("Konto MEGA wymaga kodu logowania dwuetapowego.");
