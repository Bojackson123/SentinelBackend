namespace SentinelBackend.Infrastructure;

using Microsoft.Azure.Devices.Provisioning.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SentinelBackend.Infrastructure.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddDbContext<SentinelDbContext>(options =>
            options.UseSqlServer(
                configuration["SqlConnectionString"]
                    ?? throw new InvalidOperationException(
                        "SqlConnectionString is not configured."
                    ),
                o => o.EnableRetryOnFailure()
            )
        );

        services.AddSingleton(sp =>
        {
            var connectionString =
                configuration["DpsConnectionString"]
                ?? throw new InvalidOperationException(
                    "DpsConnectionString is not configured."
                );
            return ProvisioningServiceClient.CreateFromConnectionString(connectionString);
        });

        return services;
    }
}