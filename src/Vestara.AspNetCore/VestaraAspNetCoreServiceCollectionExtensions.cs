using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring Vestara ASP.NET Core services.
/// </summary>
public static class VestaraAspNetCoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds Vestara ASP.NET Core integration services.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddVestaraAspNetCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services;
    }
}
