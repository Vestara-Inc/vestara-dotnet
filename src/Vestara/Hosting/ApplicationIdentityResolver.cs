using System.Diagnostics;
using System.Reflection;

namespace Vestara.Hosting;

public static class ApplicationIdentityResolver
{
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet",
        "dotnet.exe",
        "testhost",
        "testhost.x86",
        "testhost.arm64",
        ".NET Service",
        "dotnet-service",
        "dotnet-app"
    };

    public static (string? ServiceName, string? AppIdentifier) ResolveIdentity(
        string? configuredServiceName,
        string? configuredAppIdentifier,
        string? hostApplicationName = null)
    {
        var resolvedService = configuredServiceName;
        var resolvedIdentifier = configuredAppIdentifier;

        if (string.IsNullOrWhiteSpace(resolvedService) && !string.IsNullOrWhiteSpace(hostApplicationName))
        {
            if (!GenericNames.Contains(hostApplicationName.Trim()))
            {
                resolvedService = hostApplicationName.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedService))
        {
            var entryName = Assembly.GetEntryAssembly()?.GetName().Name;
            if (!string.IsNullOrWhiteSpace(entryName) && !GenericNames.Contains(entryName.Trim()))
            {
                resolvedService = entryName.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedService))
        {
            try
            {
                var processName = Process.GetCurrentProcess().ProcessName;
                if (!string.IsNullOrWhiteSpace(processName) && !GenericNames.Contains(processName.Trim()))
                {
                    resolvedService = processName.Trim();
                }
            }
            catch
            {
                // Accessing process name can fail in constrained sandbox environments
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedIdentifier))
        {
            resolvedIdentifier = resolvedService;
        }

        // Final sanitization: clean out any generic names or empty strings
        if (!string.IsNullOrWhiteSpace(resolvedService) && GenericNames.Contains(resolvedService))
        {
            resolvedService = null;
        }

        if (!string.IsNullOrWhiteSpace(resolvedIdentifier) && GenericNames.Contains(resolvedIdentifier))
        {
            resolvedIdentifier = null;
        }

        return (
            string.IsNullOrWhiteSpace(resolvedService) ? null : resolvedService,
            string.IsNullOrWhiteSpace(resolvedIdentifier) ? null : resolvedIdentifier
        );
    }
}
