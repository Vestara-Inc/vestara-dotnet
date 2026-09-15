using System.Collections;
using System.Text;

namespace Vestara.Logging;

public static class LogStateSanitizer
{
    public const int MaxMessageBytes = 32 * 1024;   // 32 KiB
    public const int MaxStackBytes = 128 * 1024;    // 128 KiB
    public const int MaxPayloadBytes = 256 * 1024;  // 256 KiB
    private const int MaxCollectionElements = 50;
    private const int MaxRecursionDepth = 5;

    private static readonly string[] SensitiveKeySubstrings = new[]
    {
        "password",
        "passwd",
        "passcode",
        "token",
        "secret",
        "authorization",
        "cookie",
        "credential",
        "api_key",
        "apikey",
        "private_key",
        "access_token",
        "refresh_token",
        "connection_string",
        "connectionstring"
    };

    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var lower = key.ToLowerInvariant();
        return SensitiveKeySubstrings.Any(s => lower.Contains(s));
    }

    public static string TruncateUtf8(string? s, int maxBytes)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= maxBytes)
        {
            return s;
        }

        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[s.Length];
        decoder.Convert(bytes, 0, maxBytes, chars, 0, chars.Length, false, out _, out var charsUsed, out _);
        return new string(chars, 0, charsUsed);
    }

    public static Dictionary<string, object?> SanitizeDictionary(IDictionary? dictionary)
    {
        if (dictionary == null)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return SanitizeDictionaryInternal(dictionary, 0, visited);
    }

    public static Dictionary<string, object?> SanitizeDictionary(Dictionary<string, object?>? dictionary)
    {
        return SanitizeDictionary((IEnumerable<KeyValuePair<string, object?>>?)dictionary);
    }

    public static Dictionary<string, object?> SanitizeDictionary(IEnumerable<KeyValuePair<string, object?>>? dictionary)
    {
        if (dictionary == null)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            IEnumerator<KeyValuePair<string, object?>>? enumerator = null;
            try
            {
                enumerator = dictionary.GetEnumerator();
            }
            catch
            {
                return result;
            }

            try
            {
                int count = 0;
                while (count < MaxCollectionElements)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = enumerator.MoveNext();
                    }
                    catch
                    {
                        break;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    count++;

                    KeyValuePair<string, object?> kvp;
                    try
                    {
                        kvp = enumerator.Current;
                    }
                    catch
                    {
                        continue;
                    }

                    string? key = null;
                    try
                    {
                        key = kvp.Key;
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(key) || key == "{OriginalFormat}")
                    {
                        continue;
                    }

                    if (IsSensitiveKey(key))
                    {
                        result[key] = "[REDACTED]";
                        continue;
                    }

                    result[key] = SanitizeValueInternal(kvp.Value, 0, visited);
                }
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }
        catch
        {
            // Hostile collection failure must not escape
        }

        return result;
    }

    public static Dictionary<string, object?> SanitizeState(IEnumerable<KeyValuePair<string, object?>> state)
    {
        return SanitizeDictionary(state);
    }

    public static void NormalizeAndBoundPayload(Dictionary<string, object?> payload, bool isFatal = false)
    {
        if (payload == null)
        {
            return;
        }

        string origMsg = payload.TryGetValue("message", out var mVal) && mVal is string ms ? ms : string.Empty;
        string origType = payload.TryGetValue("type", out var tVal) && tVal is string ts ? ts : (isFatal ? "crash" : "log");
        string origLevel = payload.TryGetValue("level", out var lVal) && lVal is string ls ? ls : (isFatal ? "fatal" : "error");
        string? origReq = payload.TryGetValue("requestId", out var rVal) && rVal is string rs ? rs : null;
        string origStack = payload.TryGetValue("stack", out var stVal) && stVal is string sts ? sts : string.Empty;

        try
        {
            // 1. Enforce message byte limit
            if (!string.IsNullOrEmpty(origMsg))
            {
                payload["message"] = TruncateUtf8(origMsg, MaxMessageBytes);
            }

            // 2. Enforce breadcrumbs max 100
            if (payload.TryGetValue("breadcrumbs", out var bcVal) && bcVal is IList bcList)
            {
                if (bcList.Count > 100)
                {
                    var trimmedBc = new List<object?>(100);
                    int skip = bcList.Count - 100;
                    for (int i = skip; i < bcList.Count; i++)
                    {
                        trimmedBc.Add(bcList[i]);
                    }
                    payload["breadcrumbs"] = trimmedBc;
                }
            }

            // 3. Enforce stack_trace max 100 frames and 128 KiB
            if (payload.TryGetValue("stack_trace", out var stObj) && stObj is IList stList)
            {
                if (stList.Count > 100)
                {
                    var trimmedSt = new List<object?>(100);
                    for (int i = 0; i < 100; i++)
                    {
                        trimmedSt.Add(stList[i]);
                    }
                    payload["stack_trace"] = trimmedSt;
                    stList = trimmedSt;
                }

                try
                {
                    var stBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(stList);
                    if (stBytes.Length > MaxStackBytes)
                    {
                        var smallerList = new List<object?>();
                        foreach (var frame in stList)
                        {
                            smallerList.Add(frame);
                            if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(smallerList).Length > MaxStackBytes)
                            {
                                smallerList.RemoveAt(smallerList.Count - 1);
                                break;
                            }
                        }
                        payload["stack_trace"] = smallerList;
                    }
                }
                catch
                {
                    // Ignore serialization check failure
                }
            }

            // 4. Enforce stack string max 128 KiB
            if (!string.IsNullOrEmpty(origStack))
            {
                payload["stack"] = TruncateUtf8(origStack, MaxStackBytes);
            }

            // 5. Total event payload check <= 256 KiB
            byte[]? totalBytes = null;
            try
            {
                totalBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
            }
            catch
            {
                // Unserializable payload falls through to fallback
            }

            if (totalBytes != null && totalBytes.Length <= MaxPayloadBytes)
            {
                return;
            }

            // Tier 1: Strip non-core keys
            var coreKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "message", "level", "type", "stack", "stack_trace", "breadcrumbs",
                "requestId", "operationName", "stage", "method", "url", "statusCode", "status"
            };

            var keysToRemove = payload.Keys.Where(k => !coreKeys.Contains(k)).ToList();
            foreach (var k in keysToRemove)
            {
                payload.Remove(k);
            }

            try
            {
                totalBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
                if (totalBytes.Length <= MaxPayloadBytes)
                {
                    return;
                }
            }
            catch { }

            // Tier 2: Strip breadcrumb data dictionaries
            if (payload.TryGetValue("breadcrumbs", out var bcObj2) && bcObj2 is IList bcList2)
            {
                var strippedList = new List<object?>(bcList2.Count);
                foreach (var item in bcList2)
                {
                    if (item is Models.Breadcrumb b)
                    {
                        strippedList.Add(new Models.Breadcrumb
                        {
                            Message = b.Message,
                            Category = b.Category,
                            Level = b.Level,
                            Timestamp = b.Timestamp,
                            RequestId = b.RequestId,
                            Data = null
                        });
                    }
                    else if (item is IDictionary dict)
                    {
                        var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (DictionaryEntry entry in dict)
                        {
                            var k = entry.Key?.ToString();
                            if (k != null && !string.Equals(k, "data", StringComparison.OrdinalIgnoreCase))
                            {
                                copy[k] = entry.Value;
                            }
                        }
                        strippedList.Add(copy);
                    }
                    else
                    {
                        strippedList.Add(item);
                    }
                }
                payload["breadcrumbs"] = strippedList;
                bcList2 = strippedList;

                try
                {
                    totalBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
                    if (totalBytes.Length <= MaxPayloadBytes)
                    {
                        return;
                    }
                }
                catch { }

                // Progressively drop breadcrumbs: 50 -> 25 -> 10 -> 5 -> 0
                int[] bcTargets = { 50, 25, 10, 5, 0 };
                foreach (var target in bcTargets)
                {
                    if (bcList2.Count > target)
                    {
                        var reduced = new List<object?>(target);
                        int skip = bcList2.Count - target;
                        for (int i = skip; i < bcList2.Count; i++)
                        {
                            reduced.Add(bcList2[i]);
                        }
                        if (target == 0)
                        {
                            payload.Remove("breadcrumbs");
                        }
                        else
                        {
                            payload["breadcrumbs"] = reduced;
                            bcList2 = reduced;
                        }

                        try
                        {
                            totalBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
                            if (totalBytes.Length <= MaxPayloadBytes)
                            {
                                return;
                            }
                        }
                        catch { }
                    }
                }
            }

            // Tier 3: Progressively drop stack_trace frames & truncate stack
            if (payload.TryGetValue("stack_trace", out var stObj2) && stObj2 is IList stList2)
            {
                int[] stTargets = { 50, 20, 10, 5, 0 };
                foreach (var target in stTargets)
                {
                    if (stList2.Count > target)
                    {
                        var reduced = new List<object?>(target);
                        for (int i = 0; i < target; i++)
                        {
                            reduced.Add(stList2[i]);
                        }
                        if (target == 0)
                        {
                            payload.Remove("stack_trace");
                        }
                        else
                        {
                            payload["stack_trace"] = reduced;
                            stList2 = reduced;
                        }

                        try
                        {
                            totalBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
                            if (totalBytes.Length <= MaxPayloadBytes)
                            {
                                return;
                            }
                        }
                        catch { }
                    }
                }
            }

            if (payload.TryGetValue("stack", out var stkObj) && stkObj is string stkStr)
            {
                int[] stackSizes = { 64 * 1024, 32 * 1024, 16 * 1024, 8 * 1024, 1024 };
                foreach (var size in stackSizes)
                {
                    payload["stack"] = TruncateUtf8(stkStr, size);
                    try
                    {
                        totalBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
                        if (totalBytes.Length <= MaxPayloadBytes)
                        {
                            return;
                        }
                    }
                    catch { }
                }
                payload.Remove("stack");
            }
        }
        catch
        {
            // Proceed to guaranteed emergency fallback
        }

        // Tier 4: Guaranteed minimal core payload fallback (message, type, level, requestId, bounded stack)
        ApplyMinimalEmergencyPayload(payload, origMsg, origType, origLevel, origReq, origStack, isFatal);
    }

    private static void ApplyMinimalEmergencyPayload(
        Dictionary<string, object?> payload,
        string origMsg,
        string origType,
        string origLevel,
        string? origReq,
        string origStack,
        bool isFatal)
    {
        try
        {
            payload.Clear();
            payload["message"] = TruncateUtf8(origMsg, 16 * 1024);
            payload["type"] = !string.IsNullOrWhiteSpace(origType) ? TruncateUtf8(origType, 256) : (isFatal ? "crash" : "log");
            payload["level"] = !string.IsNullOrWhiteSpace(origLevel) ? TruncateUtf8(origLevel, 64) : (isFatal ? "fatal" : "error");

            if (!string.IsNullOrWhiteSpace(origReq))
            {
                payload["requestId"] = TruncateUtf8(origReq, 128);
            }

            if (!string.IsNullOrWhiteSpace(origStack))
            {
                payload["stack"] = TruncateUtf8(origStack, 32 * 1024);
            }

            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
            if (bytes.Length > MaxPayloadBytes)
            {
                payload["message"] = TruncateUtf8(origMsg, 1024);
                payload.Remove("stack");
            }
        }
        catch
        {
            payload.Clear();
            payload["message"] = "[PayloadFallback]";
            payload["type"] = isFatal ? "crash" : "log";
            payload["level"] = isFatal ? "fatal" : "error";
        }
    }

    private static Dictionary<string, object?> SanitizeDictionaryInternal(
        IDictionary dictionary,
        int depth,
        HashSet<object> visited)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (depth > MaxRecursionDepth)
        {
            return result;
        }

        if (!visited.Add(dictionary))
        {
            result["[circular]"] = "[CircularReference]";
            return result;
        }

        try
        {
            IDictionaryEnumerator? dictEnumerator = null;
            IEnumerator? genEnumerator = null;
            try
            {
                dictEnumerator = dictionary.GetEnumerator();
            }
            catch
            {
                try
                {
                    genEnumerator = ((IEnumerable)dictionary).GetEnumerator();
                }
                catch
                {
                    return result;
                }
            }

            var enumerator = (IEnumerator?)dictEnumerator ?? genEnumerator;
            if (enumerator == null)
            {
                return result;
            }

            try
            {
                int count = 0;
                while (count < MaxCollectionElements)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = enumerator.MoveNext();
                    }
                    catch
                    {
                        break;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    count++;

                    object? entryObj;
                    try
                    {
                        entryObj = enumerator.Current;
                    }
                    catch
                    {
                        continue;
                    }

                    object? rawKey = null;
                    object? rawVal = null;

                    if (entryObj is DictionaryEntry de)
                    {
                        rawKey = de.Key;
                        rawVal = de.Value;
                    }
                    else if (entryObj != null)
                    {
                        var entryType = entryObj.GetType();
                        if (entryType.IsGenericType && entryType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                        {
                            try
                            {
                                rawKey = entryType.GetProperty("Key")?.GetValue(entryObj);
                                rawVal = entryType.GetProperty("Value")?.GetValue(entryObj);
                            }
                            catch
                            {
                                continue;
                            }
                        }
                    }

                    string key;
                    try
                    {
                        key = rawKey?.ToString() ?? string.Empty;
                    }
                    catch
                    {
                        // Hostile key object ToString() threw!
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (IsSensitiveKey(key))
                    {
                        result[key] = "[REDACTED]";
                    }
                    else
                    {
                        result[key] = SanitizeValueInternal(rawVal, depth + 1, visited);
                    }
                }
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }
        catch
        {
            // Collection failure must not escape
        }
        finally
        {
            visited.Remove(dictionary);
        }

        return result;
    }

    public static object? SanitizeValue(object? value)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return SanitizeValueInternal(value, 0, visited);
    }

    private static object? SanitizeValueInternal(object? value, int depth, HashSet<object> visited)
    {
        if (value == null)
        {
            return null;
        }

        if (depth > MaxRecursionDepth)
        {
            return "[DepthLimitExceeded]";
        }

        switch (value)
        {
            case string s:
                return TruncateUtf8(s, MaxMessageBytes);
            case bool b:
                return b;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return value;
            case float or double or decimal:
                return value;
            case Guid guid:
                return guid.ToString("D");
            case DateTime dt:
                return dt.ToUniversalTime().ToString("o");
            case DateTimeOffset dto:
                return dto.ToUniversalTime().ToString("o");
            case TimeSpan ts:
                return ts.ToString("c");
            case IDictionary dictionary:
                return SanitizeDictionaryInternal(dictionary, depth + 1, visited);
            case IEnumerable enumerable and not string:
                if (!visited.Add(enumerable))
                {
                    return "[CircularReference]";
                }
                try
                {
                    var listResult = new List<object?>();
                    int elemCount = 0;
                    IEnumerator? enumerator = null;
                    try
                    {
                        enumerator = enumerable.GetEnumerator();
                    }
                    catch
                    {
                        return "[UnrepresentableObject]";
                    }

                    try
                    {
                        while (elemCount < MaxCollectionElements)
                        {
                            bool hasNext;
                            try
                            {
                                hasNext = enumerator.MoveNext();
                            }
                            catch
                            {
                                listResult.Add("[UnrepresentableObject]");
                                break;
                            }

                            if (!hasNext)
                            {
                                break;
                            }

                            elemCount++;

                            object? item = null;
                            try
                            {
                                item = enumerator.Current;
                            }
                            catch
                            {
                                listResult.Add("[UnrepresentableObject]");
                                continue;
                            }

                            listResult.Add(SanitizeValueInternal(item, depth + 1, visited));
                        }
                    }
                    finally
                    {
                        (enumerator as IDisposable)?.Dispose();
                    }
                    return listResult;
                }
                catch
                {
                    return "[UnrepresentableObject]";
                }
                finally
                {
                    visited.Remove(enumerable);
                }
            default:
                try
                {
                    var str = value.ToString();
                    return TruncateUtf8(str, MaxMessageBytes);
                }
                catch
                {
                    return "[UnrepresentableObject]";
                }
        }
    }

    public static Dictionary<string, object?> DeepCloneDictionary(Dictionary<string, object?> source)
    {
        var clone = new Dictionary<string, object?>(source.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in source)
        {
            clone[kvp.Key] = DeepCloneValue(kvp.Value);
        }
        return clone;
    }

    public static object? DeepCloneValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        switch (value)
        {
            case Models.Breadcrumb b:
                return new Models.Breadcrumb
                {
                    Message = b.Message,
                    Category = b.Category,
                    Level = b.Level,
                    Timestamp = b.Timestamp,
                    RequestId = b.RequestId,
                    Data = b.Data != null ? DeepCloneDictionary(b.Data) : null
                };
            case Dictionary<string, object?> dict:
                return DeepCloneDictionary(dict);
            case IDictionary idict:
                var dictResult = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (DictionaryEntry entry in idict)
                    {
                        string key;
                        try
                        {
                            key = entry.Key?.ToString() ?? string.Empty;
                        }
                        catch
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            dictResult[key] = DeepCloneValue(entry.Value);
                        }
                    }
                }
                catch
                {
                    // Fail safe
                }
                return dictResult;
            case List<object?> list:
                var listResult = new List<object?>(list.Count);
                foreach (var item in list)
                {
                    listResult.Add(DeepCloneValue(item));
                }
                return listResult;
            case IEnumerable enumerable and not string:
                var genList = new List<object?>();
                try
                {
                    foreach (var item in enumerable)
                    {
                        genList.Add(DeepCloneValue(item));
                    }
                }
                catch
                {
                    // Fail safe
                }
                return genList;
            default:
                // Value types and immutable strings are safe by value
                return value;
        }
    }
}
