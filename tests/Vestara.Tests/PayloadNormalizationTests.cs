using System.Text;
using System.Text.Json;
using Vestara.Logging;
using Xunit;

namespace Vestara.Tests;

public class PayloadNormalizationTests
{
    private class CircularNode
    {
        public string Name { get; set; } = "node";
        public CircularNode? Next { get; set; }
    }

    [Fact]
    public void CircularObject_DoesNotThrow_AndIsRedactedSafely()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        var node1 = new CircularNode { Name = "1" };
        var node2 = new CircularNode { Name = "2" };
        node1.Next = node2;
        node2.Next = node1;

        var data = new Dictionary<string, object?>
        {
            ["cyclic"] = node1
        };

        // Should not throw or crash
        client.Log("info", "test cyclic", data);

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        Assert.True(events[0].Event.Payload.ContainsKey("cyclic"));
    }

    [Fact]
    public void SensitiveValues_ThroughManualLog_AreRedacted()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        var data = new Dictionary<string, object?>
        {
            ["password"] = "p@ssword123",
            ["secret_token"] = "sk-live-12345",
            ["api_key"] = "key-abc-xyz",
            ["safe_key"] = "safe_value"
        };

        client.Log("info", "login_attempt", data);

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var payload = events[0].Event.Payload;

        Assert.Equal("[REDACTED]", payload["password"]?.ToString());
        Assert.Equal("[REDACTED]", payload["secret_token"]?.ToString());
        Assert.Equal("[REDACTED]", payload["api_key"]?.ToString());
        Assert.Equal("safe_value", payload["safe_key"]?.ToString());
    }

    [Fact]
    public void SensitiveValues_ThroughBreadcrumbData_AreRedacted()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        client.AddBreadcrumb("auth_step", "auth", new Dictionary<string, object?>
        {
            ["access_token"] = "jwt.token.here",
            ["user_id"] = "user_123"
        });

        var breadcrumbs = client.Breadcrumbs.Snapshot();
        Assert.Single(breadcrumbs);
        var bc = breadcrumbs[0];

        Assert.NotNull(bc.Data);
        Assert.Equal("[REDACTED]", bc.Data["access_token"]?.ToString());
        Assert.Equal("user_123", bc.Data["user_id"]?.ToString());
    }

    [Fact]
    public void SensitiveValues_ThroughCaptureExceptionContext_AreRedacted()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        client.CaptureException(new InvalidOperationException("db error"), new Dictionary<string, object?>
        {
            ["connection_string"] = "Server=foo;Password=supersecret",
            ["credential"] = "admin:pass",
            ["query"] = "SELECT 1"
        });

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var payload = events[0].Event.Payload;

        Assert.Equal("[REDACTED]", payload["connection_string"]?.ToString());
        Assert.Equal("[REDACTED]", payload["credential"]?.ToString());
        Assert.Equal("SELECT 1", payload["query"]?.ToString());
    }

    [Fact]
    public void MultibyteUtf8_Message_ObeysByteLimit()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);

        // '€' is 3 bytes in UTF-8
        var euroString = new string('€', 20000); // 60,000 bytes
        client.Log("info", euroString);

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var msg = events[0].Event.Payload["message"]?.ToString() ?? string.Empty;

        var byteCount = Encoding.UTF8.GetByteCount(msg);
        Assert.True(byteCount <= LogStateSanitizer.MaxMessageBytes, $"Message bytes {byteCount} exceeded {LogStateSanitizer.MaxMessageBytes}");
    }

    [Fact]
    public void OversizedPayload_IsBoundedRatherThanProducingPoisonTelemetry()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);

        // Inject 500 KiB of optional data
        var hugeData = new Dictionary<string, object?>();
        for (int i = 0; i < 20; i++)
        {
            hugeData[$"huge_key_{i}"] = new string('A', 25000);
        }

        client.Log("error", "important failure message", hugeData);

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var payload = events[0].Event.Payload;

        // Core fields must be preserved
        Assert.Equal("important failure message", payload["message"]?.ToString());
        Assert.Equal("error", payload["level"]?.ToString());

        var totalBytes = JsonSerializer.SerializeToUtf8Bytes(payload).Length;
        Assert.True(totalBytes <= LogStateSanitizer.MaxPayloadBytes, $"Payload bytes {totalBytes} exceeded {LogStateSanitizer.MaxPayloadBytes}");
    }

    [Fact]
    public void StructuredStackFrames_Max100Frames()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        try
        {
            DeepRecursion(150);
        }
        catch (Exception ex)
        {
            client.CaptureException(ex);
        }

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var frames = events[0].Event.Payload["stack_trace"] as IEnumerable<object>;
        Assert.NotNull(frames);
        var frameList = frames.Cast<Dictionary<string, object?>>().ToList();
        Assert.True(frameList.Count <= 100);

        // Every frame must have non-empty function and file (fallback to (unknown) when no PDB)
        foreach (var frame in frameList)
        {
            var fn = frame["function"]?.ToString();
            var fl = frame["file"]?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(fn), "Stack frame function must be non-empty");
            Assert.False(string.IsNullOrWhiteSpace(fl), "Stack frame file must be non-empty");
        }
    }

    [Fact]
    public void CrashWith100LargeBreadcrumbs_ProducesPayloadUnder256KiB_PreservingCoreFields()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);

        // Add 100 breadcrumbs with 4 KiB data each (totaling > 400 KiB)
        for (int i = 0; i < 100; i++)
        {
            client.AddBreadcrumb($"breadcrumb step {i}", "navigation", new Dictionary<string, object?>
            {
                [$"huge_bc_data_{i}"] = new string('X', 4000)
            });
        }

        client.CaptureException(new InvalidOperationException("fatal crash with huge breadcrumbs"), isFatal: true);

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var evt = events[0].Event;
        Assert.Equal("crash", evt.EventType);
        Assert.Equal(Vestara.Models.EmissionOrigin.Crash, evt.Origin);

        var payload = evt.Payload;
        var totalBytes = JsonSerializer.SerializeToUtf8Bytes(payload).Length;
        Assert.True(totalBytes <= LogStateSanitizer.MaxPayloadBytes, $"Payload bytes {totalBytes} exceeded {LogStateSanitizer.MaxPayloadBytes}");

        // Core fields must be preserved
        Assert.Equal("fatal crash with huge breadcrumbs", payload["message"]?.ToString());
        Assert.Equal(typeof(InvalidOperationException).FullName, payload["type"]?.ToString());
    }

    [Fact]
    public void HostileDictionaryEnumerator_ClientLogDoesNotThrow_AndPayloadSerializes()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        var hostileDict = new HostileThrowingDictionary();

        // Must not throw
        var ex = Record.Exception(() => client.Log("info", "test hostile dict", hostileDict));
        Assert.Null(ex);

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var payload = events[0].Event.Payload;
        Assert.Equal("test hostile dict", payload["message"]?.ToString());

        var json = JsonSerializer.Serialize(payload);
        Assert.False(string.IsNullOrWhiteSpace(json));
    }

    [Fact]
    public void HostileNestedEnumerable_ClientAddBreadcrumbDoesNotThrow_AndPayloadSerializes()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        var hostileEnumerable = new HostileThrowingEnumerable();

        var data = new Dictionary<string, object?>
        {
            ["hostile_list"] = hostileEnumerable,
            ["valid_key"] = "valid_val"
        };

        // Must not throw
        var ex = Record.Exception(() => client.AddBreadcrumb("step with hostile data", "test", data));
        Assert.Null(ex);

        // Capture exception to verify breadcrumb serializes cleanly
        client.CaptureException(new InvalidOperationException("boom"));
        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var payload = events[0].Event.Payload;
        var json = JsonSerializer.Serialize(payload);
        Assert.False(string.IsNullOrWhiteSpace(json));
    }

    [Fact]
    public void HostileKeyToString_ClientCaptureExceptionDoesNotThrow_AndPayloadSerializes()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);

        var hostileTable = new System.Collections.Hashtable
        {
            [new HostileThrowingKey()] = "some_value",
            ["safe_key"] = "safe_value"
        };

        var customData = new Dictionary<string, object?>
        {
            ["nested_hostile_table"] = hostileTable
        };

        var exToCapture = new InvalidOperationException("exception with hostile data");
        exToCapture.Data[new HostileThrowingKey()] = "hostile_ex_data";
        exToCapture.Data["safe_key"] = hostileTable;

        var ex = Record.Exception(() => client.CaptureException(exToCapture, customData));
        Assert.Null(ex);

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var payload = events[0].Event.Payload;
        var json = JsonSerializer.Serialize(payload);
        Assert.False(string.IsNullOrWhiteSpace(json));
    }

    private void DeepRecursion(int depth)
    {
        if (depth <= 0)
        {
            throw new InvalidOperationException("deep recursion reached bottom");
        }
        DeepRecursion(depth - 1);
    }

    private sealed class HostileThrowingKey
    {
        public override string ToString() => throw new InvalidOperationException("Hostile key ToString failure");
    }

    private sealed class HostileThrowingEnumerable : System.Collections.IEnumerable
    {
        public System.Collections.IEnumerator GetEnumerator() => throw new InvalidOperationException("Hostile enumerator failure");
    }

    private sealed class HostileThrowingDictionary : IDictionary<string, object?>
    {
        public object? this[string key] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public ICollection<string> Keys => throw new NotImplementedException();
        public ICollection<object?> Values => throw new NotImplementedException();
        public int Count => 1;
        public bool IsReadOnly => true;
        public void Add(string key, object? value) => throw new NotImplementedException();
        public void Add(KeyValuePair<string, object?> item) => throw new NotImplementedException();
        public void Clear() => throw new NotImplementedException();
        public bool Contains(KeyValuePair<string, object?> item) => throw new NotImplementedException();
        public bool ContainsKey(string key) => throw new NotImplementedException();
        public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex) => throw new NotImplementedException();
        public bool Remove(string key) => throw new NotImplementedException();
        public bool Remove(KeyValuePair<string, object?> item) => throw new NotImplementedException();
        public bool TryGetValue(string key, out object? value) => throw new NotImplementedException();

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            throw new InvalidOperationException("Hostile dictionary GetEnumerator failure");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
