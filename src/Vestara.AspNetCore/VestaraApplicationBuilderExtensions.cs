using Microsoft.AspNetCore.Builder;
using Vestara.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods for adding Vestara middleware to the application pipeline.
/// </summary>
public static class VestaraApplicationBuilderExtensions
{
    /// <summary>
    /// Adds Vestara middleware to the application pipeline for request and failure observation.
    /// </summary>
    /// <param name="app">The <see cref="IApplicationBuilder"/> to add the middleware to.</param>
    /// <returns>The <see cref="IApplicationBuilder"/> for chaining.</returns>
    public static IApplicationBuilder UseVestara(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<VestaraMiddleware>();
    }
}
