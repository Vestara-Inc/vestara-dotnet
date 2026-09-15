namespace Vestara.Transport;

public static class EndpointValidator
{
    private static readonly HashSet<string> AllowedHttpHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1"
    };

    public static Uri ValidateAndNormalize(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            throw new ArgumentException("Vestara API URL must not be empty.", nameof(rawUrl));
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid Vestara API URL: '{rawUrl}'. Must be an absolute URI.", nameof(rawUrl));
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme == "https")
        {
            return uri;
        }

        if (scheme == "http")
        {
            var host = uri.Host;
            // Explicitly reject IPv6 loopback [::1] or ::1
            if (host == "[::1]" || host == "::1")
            {
                throw new ArgumentException("HTTP to IPv6 loopback is not permitted. Use localhost or 127.0.0.1.", nameof(rawUrl));
            }

            if (AllowedHttpHosts.Contains(host))
            {
                return uri;
            }

            throw new ArgumentException($"Plain HTTP is only permitted for local development (localhost, 127.0.0.1). Remote endpoint '{rawUrl}' must use HTTPS.", nameof(rawUrl));
        }

        throw new ArgumentException($"Unsupported scheme '{uri.Scheme}' in Vestara API URL. Only HTTPS (and loopback HTTP) are allowed.", nameof(rawUrl));
    }
}
