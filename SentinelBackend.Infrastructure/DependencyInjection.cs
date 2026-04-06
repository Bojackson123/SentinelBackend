namespace SentinelBackend.Infrastructure;

using Microsoft.AspNetCore.Identity;
using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Provisioning.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SentinelBackend.Application.Dps;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Application.Services;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Infrastructure.Dps;
using SentinelBackend.Infrastructure.IoTHub;
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

        // ASP.NET Core Identity
        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            options.Password.RequireDigit = true;
            options.Password.RequiredLength = 8;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireLowercase = true;
            options.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<SentinelDbContext>()
        .AddDefaultTokenProviders();

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

        // IoT Hub RegistryManager for twin updates
        services.AddSingleton(sp =>
        {
            var connectionString =
                configuration["IoTHubServiceConnectionString"]
                ?? configuration["IoTHubEventHubConnectionString"]
                ?? throw new InvalidOperationException(
                    "IoTHubServiceConnectionString is not configured."
                );
            return RegistryManager.CreateFromConnectionString(connectionString);
        });
        services.AddScoped<IDeviceTwinService, DeviceTwinService>();

        // IoT Hub ServiceClient for direct method invocation
        services.AddSingleton(sp =>
        {
            var connectionString =
                configuration["IoTHubServiceConnectionString"]
                ?? configuration["IoTHubEventHubConnectionString"]
                ?? throw new InvalidOperationException(
                    "IoTHubServiceConnectionString is not configured."
                );
            return ServiceClient.CreateFromConnectionString(connectionString);
        });
        services.AddScoped<IDirectMethodService, DirectMethodService>();

        return services;
    }
}