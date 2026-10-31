using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ImageStudio.Configuration;

// Sekrety szyfrowane DPAPI (zakres: bieżący użytkownik Windows).
// Plik nie jest w repozytorium ani w katalogu aplikacji, więc nie trafi do builda ani do git.
public static class SecretStore
{
    private static string FilePath => Path.Combine(AppPaths.UserDataDir, "secrets.bin");

    public static string? Load()
    {
        if (!File.Exists(FilePath)) return null;

        var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public static void Save(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Delete();
            return;
        }

        Directory.CreateDirectory(AppPaths.UserDataDir);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, bytes);
    }

    private static void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}

public static class AppPaths
{
    public static string UserDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageStudio");
}
