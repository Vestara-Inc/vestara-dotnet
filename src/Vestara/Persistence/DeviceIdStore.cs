using System.Collections.Concurrent;

namespace Vestara.Persistence;

public sealed class DeviceIdStore
{
    private static readonly ConcurrentDictionary<string, string> ProcessFallbackDeviceIds = new(StringComparer.Ordinal);
    private readonly string? _storageDirectory;
    private readonly string _storageKey;

    public DeviceIdStore(string? storageDirectory, string storageKey)
    {
        _storageDirectory = storageDirectory;
        _storageKey = storageKey;
    }

    private string GetFallbackDeviceId()
    {
        return ProcessFallbackDeviceIds.GetOrAdd(_storageKey, _ => Guid.NewGuid().ToString("D"));
    }

    public string GetOrCreateDeviceId()
    {
        var directory = ResolveDirectory();
        if (directory == null)
        {
            return GetFallbackDeviceId();
        }

        var filePath = Path.Combine(directory, $"vestara-device-{_storageKey}.txt");

        try
        {
            if (File.Exists(filePath))
            {
                var existingId = File.ReadAllText(filePath).Trim();
                if (!string.IsNullOrWhiteSpace(existingId) && Guid.TryParse(existingId, out _))
                {
                    return existingId;
                }
            }

            Directory.CreateDirectory(directory);
            var newId = Guid.NewGuid().ToString("D");
            File.WriteAllText(filePath, newId);
            return newId;
        }
        catch
        {
            // If persistent storage is unwritable or permissions fail, return process fallback
            return GetFallbackDeviceId();
        }
    }

    private string? ResolveDirectory()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_storageDirectory))
            {
                return _storageDirectory;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                return null;
            }

            return Path.Combine(localAppData, "Vestara");
        }
        catch
        {
            return null;
        }
    }
}
