using System.Security.Cryptography;
using System.Text;

namespace Vestara.Persistence;

public static class StorageKeyResolver
{
    public static string ComputeKey(string token, string? appIdentifier, string targetCategory = "dotnet_service")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var input = $"{token}:{appIdentifier ?? ""}:{targetCategory}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
