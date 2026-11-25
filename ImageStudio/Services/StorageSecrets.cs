using System.Text.RegularExpressions;
using ImageStudio.Configuration;
using ImageStudio.Storage;

namespace ImageStudio.Services;

// Sekrety wtyczek magazynu w SecretStore (DPAPI), żeby biblioteki wtyczek nie znały formatu pliku. Nazwa musi zaczynać się
// od "storage-": wtyczka nie odczyta kluczy dostawców AI, a czyszczenie danych znajduje jej pliki (secrets-storage-*.bin)
public sealed class StorageSecrets(ISecretStore secrets) : IStorageSecrets
{
    internal const string NamePattern = "storage-[a-z0-9]+(?:-[a-z0-9]+)*";
    private static readonly Regex ValidName = new($"^{NamePattern}$", RegexOptions.CultureInvariant);

    public string? Load(string name) => secrets.Load(Check(name));
    public void Save(string value, string name) => secrets.Save(value, Check(name));

    private static string Check(string name) =>
        ValidName.IsMatch(name) ? name : throw new ArgumentException($"Niedozwolona nazwa sekretu wtyczki magazynu: {name}", nameof(name));
}
