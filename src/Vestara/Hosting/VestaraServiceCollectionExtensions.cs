using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vestara.Context;
using Vestara.Hosting;
using Vestara.Logging;

namespace Vestara;

public static class VestaraServiceCollectionExtensions
{
    /// <summary>
    /// Registers Vestara core observability services and hosted background loops.
    /// </summary>
    public static IServiceCollection AddVestara(
        this IServiceCollection services,
        Action<VestaraOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new VestaraOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<IVestaraCorrelationAccessor, VestaraCorrelationAccessor>();

        services.AddSingleton(sp =>
        {
            var hostEnv = sp.GetService<IHostEnvironment>();
            var correlation = sp.GetRequiredService<IVestaraCorrelationAccessor>();
            var httpClient = sp.GetService<HttpClient>();

            var client = new VestaraClient(
                options,
                httpClient: httpClient,
                correlationAccessor: correlation,
                hostApplicationName: hostEnv?.ApplicationName
            );

            VestaraSdk.SetClient(client);
            return client;
        });

        services.AddSingleton<ILoggerProvider>(sp =>
        {
            var client = sp.GetRequiredService<VestaraClient>();
            return new VestaraLoggerProvider(client);
        });

        services.AddHostedService<VestaraHostedService>();

        return services;
    }
}
