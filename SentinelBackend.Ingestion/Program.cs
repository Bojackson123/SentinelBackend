using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Application.Notifications;
using SentinelBackend.Infrastructure;
using SentinelBackend.Infrastructure.Persistence;
using SentinelBackend.Ingestion;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddAzureKeyVault(
    new Uri(
        builder.Configuration["KeyVaultUrl"]
            ?? throw new InvalidOperationException("KeyVaultUrl is not configured.")
    ),
    new DefaultAzureCredential()
);

var blobClient = new BlobContainerClient(
    builder.Configuration["StorageConnectionString"],
    "iot-checkpoints");

var processor = new EventProcessorClient(
    blobClient,
    "$Default",
    builder.Configuration["IoTHubEventHubConnectionString"],
    new EventProcessorClientOptions
    {
        MaximumWaitTime = TimeSpan.FromSeconds(5),
        PrefetchCount = 10,
        CacheEventCount = 10,
    });

builder.Services.AddSingleton(processor);

// Raw telemetry archive container
var rawArchiveContainer = new BlobContainerClient(
    builder.Configuration["StorageConnectionString"],
    "raw-telemetry");
builder.Services.AddSingleton(rawArchiveContainer);
builder.Services.AddSingleton<IBlobArchiveService>(sp =>
    new BlobArchiveService(
        sp.GetRequiredService<BlobContainerClient>(),
        sp.GetRequiredService<ILogger<BlobArchiveService>>()));

builder.Services.AddDbContext<SentinelDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration["SqlConnectionString"],
        o => o.EnableRetryOnFailure()
    )
);

builder.Services.AddScoped<IAlarmService, AlarmService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

// Bind notification options so NotificationService can resolve TestEmailRecipient / TestSmsRecipient
builder.Services.Configure<NotificationOptions>(
    builder.Configuration.GetSection(NotificationOptions.SectionName));

// Azure Service Bus (optional — enables event-driven offline detection)
var serviceBusConnectionString = builder.Configuration["ServiceBusConnectionString"];
if (!string.IsNullOrWhiteSpace(serviceBusConnectionString))
{
    builder.Services.AddSingleton(new ServiceBusClient(serviceBusConnectionString));
    builder.Services.AddSingleton<IMessagePublisher, ServiceBusMessagePublisher>();
}

builder.Services.AddHostedService<TelemetryIngestionWorker>();

var host = builder.Build();
host.Run();