using Vestara.Persistence;
using Xunit;

namespace Vestara.Tests;

public class ScopedFallbackDeviceTests
{
    [Fact]
    public void UnwritableStorage_ScopesFallbackDeviceId_ByStorageKey()
    {
        // Using a file path as directory forces Directory.CreateDirectory to fail deterministically
        var tempFileAsDir = Path.GetTempFileName();
        try
        {
            var storeProjectA1 = new DeviceIdStore(tempFileAsDir, "key_project_A");
            var storeProjectA2 = new DeviceIdStore(tempFileAsDir, "key_project_A");
            var storeProjectB = new DeviceIdStore(tempFileAsDir, "key_project_B");

            var deviceA1 = storeProjectA1.GetOrCreateDeviceId();
            var deviceA2 = storeProjectA2.GetOrCreateDeviceId();
            var deviceB = storeProjectB.GetOrCreateDeviceId();

            // Same storage key within same process must return the same fallback ID
            Assert.Equal(deviceA1, deviceA2);

            // Different storage key within same process must return a distinct fallback ID
            Assert.NotEqual(deviceA1, deviceB);

            // Neither contains tokens or hardware identifiers
            Assert.True(Guid.TryParse(deviceA1, out _));
            Assert.True(Guid.TryParse(deviceB, out _));
            Assert.DoesNotContain("key_project", deviceA1);
            Assert.DoesNotContain("key_project", deviceB);
        }
        finally
        {
            if (File.Exists(tempFileAsDir))
            {
                File.Delete(tempFileAsDir);
            }
        }
    }
}
