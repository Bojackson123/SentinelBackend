namespace SentinelBackend.Infrastructure;

using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Identity;
using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Provisioning.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentinelBackend.Application.Dps;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Application.Notifications;
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
        services.AddScoped<IAlarmService, AlarmService>();
        services.AddScoped<INotificationService, NotificationService>();

        // Notification options (Twilio / SendGrid credentials + test overrides)
        services.Configure<NotificationOptions>(
            configuration.GetSection(NotificationOptions.SectionName));

        // Channel dispatchers — registered as INotificationDispatcher collection.
        // SendGridEmailDispatcher handles Email; TwilioSmsDispatcher handles Sms.
        // Both fall back gracefully when credentials are absent.
        services.AddSingleton<INotificationDispatcher, SendGridEmailDispatcher>();
        services.AddSingleton<INotificationDispatcher, TwilioSmsDispatcher>();

        // IoT Hub RegistryManager for twin updates
        services.AddSingleton(sp =>
        {
            var connectionString = GetIotHubServiceConnectionString(configuration);
            return RegistryManager.CreateFromConnectionString(connectionString);
        });
        services.AddScoped<IDeviceTwinService, DeviceTwinService>();

        // IoT Hub ServiceClient for direct method invocation
        services.AddSingleton(sp =>
        {
            var connectionString = GetIotHubServiceConnectionString(configuration);
            return ServiceClient.CreateFromConnectionString(connectionString);
        });
        services.AddScoped<IDirectMethodService, DirectMethodService>();

        // Azure Service Bus
        var serviceBusConnectionString = configuration["ServiceBusConnectionString"];
        if (!string.IsNullOrWhiteSpace(serviceBusConnectionString))
        {
            services.AddSingleton(new ServiceBusClient(serviceBusConnectionString));
            services.AddSingleton<IMessagePublisher, ServiceBusMessagePublisher>();
        }

        return services;
    }

    private static string GetIotHubServiceConnectionString(IConfiguration configuration)
    {
        var connectionString =
            configuration["IoTHubServiceConnectionString"]
            ?? configuration["IoTHubConnectionString"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "IoTHubServiceConnectionString is not configured. "
                    + "Set IoTHubServiceConnectionString (or legacy alias IoTHubConnectionString) "
                    + "to an IoT Hub service connection string for twin/direct-method operations."
            );
        }

        try
        {
            IotHubConnectionStringBuilder.Create(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                "IoTHubServiceConnectionString is invalid for IoT Hub service operations. "
                    + "Do not use IoTHubEventHubConnectionString for twin/direct-method APIs.",
                ex
            );
        }

        return connectionString;
    }
}