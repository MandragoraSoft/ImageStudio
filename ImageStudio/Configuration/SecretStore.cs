using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ImageStudio.Configuration;

// Sekrety szyfrowane DPAPI (zakres: bieżący użytkownik Windows), osobny plik na każdego providera.
// Plik nie jest w repozytorium ani w katalogu aplikacji, więc nie trafi do builda ani do git.
public static class SecretStore
{
    private static string FilePath(string name) =>
        Path.Combine(AppPaths.UserDataDir, name == "uberllm" ? "secrets.bin" : $"secrets-{name}.bin");

    public static string? Load(string name = "uberllm")
    {
        var path = FilePath(name);
        if (!File.Exists(path)) return null;

        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public static void Save(string value, string name = "uberllm")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Delete(name);
            return;
        }

        Directory.CreateDirectory(AppPaths.UserDataDir);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath(name), bytes);
    }

    private static void Delete(string name)
    {
        var path = FilePath(name);
        if (File.Exists(path)) File.Delete(path);
    }
}

public static class AppPaths
{
    public static string UserDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageStudio");
}
