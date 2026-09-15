using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Vestara.Models;
using Vestara.Persistence;
using Vestara.Queue;

namespace Vestara.Transport;

public sealed class HttpUploader
{
    private static readonly HashSet<HttpStatusCode> PoisonCodes = new()
    {
        HttpStatusCode.BadRequest,          // 400
        HttpStatusCode.Unauthorized,        // 401
        HttpStatusCode.Forbidden,           // 403
        HttpStatusCode.NotFound,            // 404
        HttpStatusCode.RequestEntityTooLarge,// 413
        HttpStatusCode.UnprocessableEntity  // 422
    };

    private readonly HttpClient _httpClient;
    private readonly EventQueue _queue;
    private readonly FileEventStorage _storage;
    private readonly ISystemClock _clock;
    private readonly string _token;
    private readonly Uri _ingestUri;
    private readonly SemaphoreSlim _concurrencyGate = new(1, 1);

    private int _consecutiveFailures;
    private DateTimeOffset _nextRetryAt = DateTimeOffset.MinValue;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan CrashBudgetTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public int ConsecutiveFailures => _consecutiveFailures;
    public DateTimeOffset NextRetryAt => _nextRetryAt;

    public HttpUploader(
        HttpClient httpClient,
        EventQueue queue,
        FileEventStorage storage,
        string token,
        Uri baseUri,
        ISystemClock? clock = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _clock = clock ?? SystemClock.Instance;

        var normalizedBase = baseUri.ToString().TrimEnd('/');
        _ingestUri = new Uri($"{normalizedBase}/v1/ingest");
    }

    public async Task<bool> FlushBatchAsync(
        bool ignoreBackoff = false,
        bool isCrashBudget = false,
        CancellationToken cancellationToken = default)
    {
        if (!ignoreBackoff && _clock.UtcNow < _nextRetryAt)
        {
            return false;
        }

        var baseTimeout = isCrashBudget ? CrashBudgetTimeout : RequestTimeout;
        var effectiveBudget = baseTimeout > TimeSpan.Zero
            ? baseTimeout
            : (isCrashBudget ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(15));

        using var budgetCts = isCrashBudget ? new CancellationTokenSource(effectiveBudget) : null;
        using var linkedCts = isCrashBudget
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetCts!.Token)
            : null;

        var gateToken = isCrashBudget ? linkedCts!.Token : cancellationToken;
        bool gateAcquired = false;

        try
        {
            try
            {
                await _concurrencyGate.WaitAsync(gateToken).ConfigureAwait(false);
                gateAcquired = true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (!ignoreBackoff && _clock.UtcNow < _nextRetryAt)
            {
                return false;
            }

            var batch = _queue.GetBatch(maxBatchSize: 100);
            if (batch.Count == 0)
            {
                return true;
            }

            var queueIds = batch.Select(i => i.QueueId).ToList();
            bool resolvedBatch = false;

            try
            {
                using var requestTimeoutCts = isCrashBudget ? null : new CancellationTokenSource(effectiveBudget);
                using var requestLinkedCts = isCrashBudget
                    ? null
                    : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestTimeoutCts!.Token);

                var effectiveToken = isCrashBudget ? linkedCts!.Token : requestLinkedCts!.Token;

                var events = batch.Select(i => i.Event).ToList();
                var requestBody = JsonSerializer.Serialize(new { events });
                using var request = new HttpRequestMessage(HttpMethod.Post, _ingestUri)
                {
                    Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("X-SDK-Token", _token);

                using var response = await _httpClient.SendAsync(request, effectiveToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync(effectiveToken).ConfigureAwait(false);
                    if (TryParseAck(responseJson, out var accepted, out var rejected) &&
                        accepted == batch.Count && rejected == 0)
                    {
                        // Strict Full ACK
                        resolvedBatch = true;
                        _queue.AcknowledgeSuccessful(queueIds);
                        _queue.SnapshotAndPersist(_storage, isFatal: false);
                        ResetBackoff();
                        return true;
                    }
                    else
                    {
                        // Partial ACK or parsing failure: release batch, advance backoff
                        resolvedBatch = true;
                        HandleTransientFailure(queueIds);
                        return false;
                    }
                }

                if (PoisonCodes.Contains(response.StatusCode))
                {
                    // Poison response: remove exact batch and reset backoff so subsequent events proceed
                    resolvedBatch = true;
                    _queue.RemovePoison(queueIds);
                    _queue.SnapshotAndPersist(_storage, isFatal: false);
                    ResetBackoff();
                    return false;
                }

                // 5xx or other non-success status
                resolvedBatch = true;
                HandleTransientFailure(queueIds);
                return false;
            }
            catch (Exception)
            {
                // Any serialization, CTS creation, transport, timeout, or body read failure
                if (!resolvedBatch)
                {
                    HandleTransientFailure(queueIds);
                }
                return false;
            }
        }
        finally
        {
            if (gateAcquired)
            {
                _concurrencyGate.Release();
            }
        }
    }

    public async Task FlushAllAsync(CancellationToken cancellationToken = default)
    {
        while (_queue.Count > 0)
        {
            var remainingBefore = _queue.Count;
            await FlushBatchAsync(ignoreBackoff: true, isCrashBudget: false, cancellationToken).ConfigureAwait(false);
            if (_queue.Count == 0 || _queue.Count >= remainingBefore)
            {
                // Cannot make forward progress; stop to avoid loop hang
                break;
            }
        }
    }

    private void HandleTransientFailure(List<string> queueIds)
    {
        _queue.ReleaseInFlight(queueIds);
        _queue.SnapshotAndPersist(_storage, isFatal: false);
        AdvanceBackoff();
    }

    private void AdvanceBackoff()
    {
        _consecutiveFailures++;
        // 10s initial, 2x multiplier, 300s max
        // 1 -> 10, 2 -> 20, 3 -> 40, 4 -> 80, 5 -> 160, 6+ -> 300
        var delaySeconds = 10.0 * Math.Pow(2, _consecutiveFailures - 1);
        if (delaySeconds > 300.0)
        {
            delaySeconds = 300.0;
        }

        _nextRetryAt = _clock.UtcNow.AddSeconds(delaySeconds);
    }

    private void ResetBackoff()
    {
        _consecutiveFailures = 0;
        _nextRetryAt = DateTimeOffset.MinValue;
    }

    private static bool TryParseAck(string json, out int accepted, out int rejected)
    {
        accepted = 0;
        rejected = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Object)
            {
                root = dataElem;
            }

            var hasAccepted = root.TryGetProperty("accepted", out var acceptedElem) && acceptedElem.TryGetInt32(out accepted);
            var hasRejected = root.TryGetProperty("rejected", out var rejectedElem) && rejectedElem.TryGetInt32(out rejected);

            return hasAccepted && hasRejected;
        }
        catch
        {
            return false;
        }
    }
}
