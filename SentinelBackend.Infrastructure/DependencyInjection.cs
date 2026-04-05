namespace SentinelBackend.Infrastructure;

using Microsoft.Azure.Devices.Provisioning.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SentinelBackend.Application.Dps;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Application.Services;
using SentinelBackend.Infrastructure.Dps;
using SentinelBackend.Infrastructure.Persistence;
using SentinelBackend.Infrastructure.Repositories;

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

        services.Configure<DpsOptions>(opts =>
        {
            opts.IotHubHostName = configuration["DpsIotHubHostName"] ?? string.Empty;
            opts.EnrollmentGroupPrimaryKey = configuration["DpsEnrollmentPrimaryKey"] ?? string.Empty;
            opts.WebhookSecret = configuration["DpsWebhookSecret"] ?? string.Empty;
        });
        services.AddScoped<IDpsEnrollmentService, DpsEnrollmentService>();
        services.AddScoped<IDpsAllocationService, DpsAllocationService>();
        services.AddScoped<IManufacturingBatchService, ManufacturingBatchService>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();

        return services;
    }
}