using ImageStudio.Configuration;

namespace ImageStudio.Tests;

// Znaczek magazynu w galerii: wpis z chmury to każdy StorageId inny niż brak albo "local"
public sealed class GalleryEntryStorageTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("local", false)]
    [InlineData("onedrive", true)]
    public void IsInCloud_is_true_only_for_cloud_storage(string? storageId, bool expected) =>
        Assert.Equal(expected, new GalleryEntry { StorageId = storageId }.IsInCloud);
}
