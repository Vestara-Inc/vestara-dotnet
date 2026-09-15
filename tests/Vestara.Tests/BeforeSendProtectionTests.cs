using Vestara.Models;
using Xunit;

namespace Vestara.Tests;

public class BeforeSendProtectionTests
{
    [Fact]
    public void BeforeSend_MutateNestedDictionary_ThenThrow_PreservesOriginalUntouched()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false,
            BeforeSend = evt =>
            {
                if (evt.Payload.TryGetValue("nested", out var nestedObj) && nestedObj is Dictionary<string, object?> nested)
                {
                    nested["inner_key"] = "tampered_value";
                }
                throw new InvalidOperationException("User callback crashed!");
            }
        };

        using var client = new VestaraClient(options);
        var initialNested = new Dictionary<string, object?> { ["inner_key"] = "original_value" };
        client.Log("info", "test message", new Dictionary<string, object?> { ["nested"] = initialNested });

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);

        var payload = events[0].Event.Payload;
        var nestedResult = payload["nested"] as Dictionary<string, object?>;
        Assert.NotNull(nestedResult);
        Assert.Equal("original_value", nestedResult["inner_key"]?.ToString());
    }

    [Fact]
    public void BeforeSend_MutateNestedList_ThenThrow_PreservesOriginalUntouched()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false,
            BeforeSend = evt =>
            {
                if (evt.Payload.TryGetValue("list", out var listObj) && listObj is List<object?> list)
                {
                    list.Add("tampered_element");
                }
                throw new InvalidOperationException("User callback crashed!");
            }
        };

        using var client = new VestaraClient(options);
        client.Log("info", "test list", new Dictionary<string, object?>
        {
            ["list"] = new List<object?> { "item1", "item2" }
        });

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);

        var payload = events[0].Event.Payload;
        var listResult = payload["list"] as List<object?>;
        Assert.NotNull(listResult);
        Assert.Equal(2, listResult.Count);
        Assert.Equal("item1", listResult[0]?.ToString());
        Assert.Equal("item2", listResult[1]?.ToString());
    }

    [Fact]
    public void BeforeSend_CannotTamperCanonicalPlatformIdentity()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false,
            BeforeSend = evt =>
            {
                // Attempt to disguise as node or browser SDK
                evt.SdkName = "sdk-node";
                evt.OsName = "darwin";
                evt.TargetCategory = "node_service";
                evt.SdkVersion = "9.9.9";
                return evt;
            }
        };

        using var client = new VestaraClient(options);
        client.Log("info", "identity check");

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var evt = events[0].Event;

        // Canonical fields MUST be reasserted by SDK
        Assert.Equal("sdk-dotnet", evt.SdkName);
        Assert.Equal("0.1.0", evt.SdkVersion);
        Assert.Equal("dotnet", evt.OsName);
        Assert.Equal("dotnet_service", evt.TargetCategory);
    }

    [Fact]
    public void BeforeSend_CallbackGeneratedSensitiveData_IsSanitizedAgain()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false,
            BeforeSend = evt =>
            {
                // User callback introduces a sensitive secret
                evt.Payload["api_key"] = "sk-super-secret-key-123";
                evt.Payload["password"] = "admin_password";
                return evt;
            }
        };

        using var client = new VestaraClient(options);
        client.Log("info", "sanitization check");

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var payload = events[0].Event.Payload;

        Assert.Equal("[REDACTED]", payload["api_key"]?.ToString());
        Assert.Equal("[REDACTED]", payload["password"]?.ToString());
    }

    [Fact]
    public void BeforeSend_CannotTamperEventTypeOrEmissionOrigin()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false,
            BeforeSend = evt =>
            {
                // Attempt to mutate EventType and Origin
                evt.EventType = "crash";
                evt.Origin = EmissionOrigin.Crash;
                return evt;
            }
        };

        using var client = new VestaraClient(options);

        // 1. Ordinary log must remain event_type="log" and origin=OrdinaryLog
        client.Log("info", "test ordinary log");
        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var logEvt = events[0].Event;
        Assert.Equal("log", logEvt.EventType);
        Assert.Equal(EmissionOrigin.OrdinaryLog, logEvt.Origin);

        // 2. Non-fatal exception must remain event_type="log" and origin=Exception
        client.CaptureException(new InvalidOperationException("handled test error"), isFatal: false);
        var allEvents = client.Queue.GetAllForPersistence();
        Assert.Equal(2, allEvents.Count);
        var exEvt = allEvents[1].Event;
        Assert.Equal("log", exEvt.EventType);
        Assert.Equal(EmissionOrigin.Exception, exEvt.Origin);
    }

    [Fact]
    public void BeforeSend_MutateBreadcrumbAndNestedData_ThenThrow_PreservesOriginalBreadcrumbUntouched()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false,
            BeforeSend = evt =>
            {
                if (evt.Payload.TryGetValue("breadcrumbs", out var bcObj) && bcObj is System.Collections.IEnumerable bcs)
                {
                    foreach (var item in bcs)
                    {
                        if (item is Breadcrumb bc)
                        {
                            bc.Message = "tampered breadcrumb message";
                            if (bc.Data != null)
                            {
                                bc.Data["key1"] = "tampered_data_value";
                            }
                        }
                    }
                }
                throw new InvalidOperationException("User callback crashed!");
            }
        };

        using var client = new VestaraClient(options);
        var originalData = new Dictionary<string, object?> { ["key1"] = "original_data_value" };
        client.AddBreadcrumb("original breadcrumb message", "navigation", originalData);

        client.CaptureException(new InvalidOperationException("something exploded"));

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);

        var payload = events[0].Event.Payload;
        var bcsList = payload["breadcrumbs"] as System.Collections.IEnumerable;
        Assert.NotNull(bcsList);

        var bcList = bcsList.Cast<Breadcrumb>().ToList();
        Assert.Single(bcList);

        var bcResult = bcList[0];
        Assert.Equal("original breadcrumb message", bcResult.Message);
        Assert.NotNull(bcResult.Data);
        Assert.DoesNotContain("tampered", bcResult.Message);
        Assert.DoesNotContain("tampered", bcResult.Data["key1"]?.ToString() ?? string.Empty);
    }
}
