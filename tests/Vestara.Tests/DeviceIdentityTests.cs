using Vestara.Persistence;
using Xunit;

namespace Vestara.Tests;

public class DeviceIdentityTests
{
    [Fact]
    public void SameStorageKey_ProducesSameDeviceId_AfterRestart()
    {
        using var temp = new TempDirectory();
        var storageKey = "storagekey_abc";

        var store1 = new DeviceIdStore(temp.Path, storageKey);
        var id1 = store1.GetOrCreateDeviceId();

        Assert.False(string.IsNullOrWhiteSpace(id1));
        Assert.True(Guid.TryParse(id1, out _));

        // Simulate restart with fresh store instance
        var store2 = new DeviceIdStore(temp.Path, storageKey);
        var id2 = store2.GetOrCreateDeviceId();

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void DifferentTokens_ProduceDifferentDeviceIds()
    {
        using var temp = new TempDirectory();

        var keyA = StorageKeyResolver.ComputeKey("token_AAA", "app1");
        var keyB = StorageKeyResolver.ComputeKey("token_BBB", "app1");

        var storeA = new DeviceIdStore(temp.Path, keyA);
        var storeB = new DeviceIdStore(temp.Path, keyB);

        var idA = storeA.GetOrCreateDeviceId();
        var idB = storeB.GetOrCreateDeviceId();

        Assert.NotEqual(idA, idB);
    }

    [Fact]
    public void Token_IsAbsentFromDeviceFilename()
    {
        using var temp = new TempDirectory();
        var rawToken = "vestara_token_secret_123456";
        var storageKey = StorageKeyResolver.ComputeKey(rawToken, null);

        var store = new DeviceIdStore(temp.Path, storageKey);
        store.GetOrCreateDeviceId();

        var files = Directory.GetFiles(temp.Path);
        Assert.Single(files);
        var filename = Path.GetFileName(files[0]);

        Assert.Equal($"vestara-device-{storageKey}.txt", filename);
        Assert.DoesNotContain(rawToken, filename);
    }

    [Fact]
    public void UnwritableStorage_UsesProcessScopedFallbackId()
    {
        var store = new DeviceIdStore("/inaccessible/directory/vestara/test", "key_unwritable");
        var id1 = store.GetOrCreateDeviceId();
        var id2 = store.GetOrCreateDeviceId();

        Assert.False(string.IsNullOrWhiteSpace(id1));
        Assert.Equal(id1, id2); // Consistent process lifetime fallback
    }
}
