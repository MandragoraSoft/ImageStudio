using ImageStudio.Configuration;
using ImageStudio.Storage;

namespace ImageStudio.Services;

// Sekrety wtyczek magazynu w SecretStore (DPAPI), żeby biblioteki wtyczek nie znały formatu pliku
public sealed class StorageSecrets(ISecretStore secrets) : IStorageSecrets
{
    public string? Load(string name) => secrets.Load(name);
    public void Save(string value, string name) => secrets.Save(value, name);
}
