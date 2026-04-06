using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
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
    builder.Configuration["IoTHubEventHubConnectionString"]);

builder.Services.AddSingleton(processor);

// Raw telemetry archive container
var rawArchiveContainer = new BlobContainerClient(
    builder.Configuration["StorageConnectionString"],
    "raw-telemetry");
builder.Services.AddSingleton(rawArchiveContainer);

builder.Services.AddDbContext<SentinelDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration["SqlConnectionString"],
        o => o.EnableRetryOnFailure()
    )
);

builder.Services.AddScoped<IAlarmService, AlarmService>();

builder.Services.AddHostedService<TelemetryIngestionWorker>();

var host = builder.Build();
host.Run();