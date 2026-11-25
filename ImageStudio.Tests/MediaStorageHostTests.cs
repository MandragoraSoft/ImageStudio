using ImageStudio.Services;

namespace ImageStudio.Tests;

// Właściciel wpisu galerii: operacje na pliku idą do wtyczki, która go zapisała, nie do aktywnej
public sealed class MediaStorageHostTests
{
    [Fact]
    public void Owner_is_the_plugin_that_wrote_the_entry()
    {
        var local = new FixedIdStoragePlugin("local");
        var cloud = new FixedIdStoragePlugin("cloud");
        var host = new MediaStorageHost([local, cloud]);
        host.Apply("cloud", null);

        Assert.Same(cloud, host.Owner("cloud"));
        Assert.Same(local, host.Owner("local"));
        Assert.Same(cloud, host.Active);
    }

    [Fact]
    public void Legacy_and_unknown_entries_belong_to_local()
    {
        var cloud = new FixedIdStoragePlugin("cloud");
        var local = new FixedIdStoragePlugin("local");
        var host = new MediaStorageHost([cloud, local]); // local nie jest pierwsza, a aktywna jest cloud

        Assert.Same(local, host.Owner(null));
        Assert.Same(local, host.Owner(""));
        Assert.Same(local, host.Owner("zniknela"));
    }
}
