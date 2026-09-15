using System.Text.Json;
using Vestara.Models;

namespace Vestara.Persistence;

public sealed class FileEventStorage
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    private readonly string? _storageDirectory;
    private readonly string _storageKey;
    private readonly object _lock = new();
    private long _lastWrittenRevision = -1;

    public bool IsPersistent { get; private set; }
    public long LastWrittenRevision => _lastWrittenRevision;

    public FileEventStorage(string? storageDirectory, string storageKey)
    {
        _storageDirectory = storageDirectory;
        _storageKey = storageKey;
        IsPersistent = TryInitializeDirectory();
    }

    private bool TryInitializeDirectory()
    {
        try
        {
            var dir = GetDirectoryPath();
            if (dir == null)
            {
                return false;
            }

            Directory.CreateDirectory(dir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string? GetDirectoryPath()
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

    private string? GetTargetFilePath()
    {
        var dir = GetDirectoryPath();
        if (dir == null)
        {
            return null;
        }

        return Path.Combine(dir, $"vestara-queue-{_storageKey}.json");
    }

    public void SaveEvents(IReadOnlyList<QueuedEvent> events, long revision, bool isFatal = false)
    {
        if (!IsPersistent)
        {
            return;
        }

        var targetPath = GetTargetFilePath();
        if (targetPath == null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (directory == null)
        {
            return;
        }

        var tempPath = Path.Combine(directory, $"vestara-queue-{_storageKey}.json.tmp.{Guid.NewGuid():n}");

        lock (_lock)
        {
            if (revision >= 0 && revision <= _lastWrittenRevision)
            {
                // Stale-write rejection: a newer or equal queue state has already been committed to disk
                return;
            }

            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, events, SerializerOptions);
                    if (isFatal)
                    {
                        stream.Flush(flushToDisk: true);
                    }
                    else
                    {
                        stream.Flush();
                    }
                }

                File.Move(tempPath, targetPath, overwrite: true);
                if (revision >= 0)
                {
                    _lastWrittenRevision = revision;
                }
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Ignore cleanup failures
                }
            }
        }
    }


    public List<QueuedEvent> LoadEvents()
    {
        if (!IsPersistent)
        {
            return new List<QueuedEvent>();
        }

        var targetPath = GetTargetFilePath();
        if (targetPath == null || !File.Exists(targetPath))
        {
            return new List<QueuedEvent>();
        }

        lock (_lock)
        {
            try
            {
                var json = File.ReadAllText(targetPath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<QueuedEvent>();
                }

                var loaded = JsonSerializer.Deserialize<List<QueuedEvent>>(json, SerializerOptions);
                if (loaded == null)
                {
                    return new List<QueuedEvent>();
                }

                foreach (var item in loaded)
                {
                    item.IsInFlight = false;
                }

                return loaded;
            }
            catch (JsonException)
            {
                // Best-effort quarantine
                try
                {
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var corruptPath = $"{targetPath}.corrupt.{timestamp}";
                    File.Move(targetPath, corruptPath, overwrite: true);
                }
                catch
                {
                    try
                    {
                        File.Delete(targetPath);
                    }
                    catch
                    {
                        // Ignore deletion failure
                    }
                }

                return new List<QueuedEvent>();
            }
            catch
            {
                return new List<QueuedEvent>();
            }
        }
    }
}
