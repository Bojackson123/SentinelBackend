using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Processor;
using Azure.Storage.Blobs;
using SentinelBackend.Ingestion;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddAzureKeyVault(
    new Uri("https://sentinel-key-vault-dev.vault.azure.net/"),
    new DefaultAzureCredential());

var blobClient = new BlobContainerClient(
    builder.Configuration["StorageConnectionString"],
    "iot-checkpoints");

var processor = new EventProcessorClient(
    blobClient,
    "$Default",
    builder.Configuration["IoTHubEventHubConnectionString"]);

builder.Services.AddSingleton(processor);
builder.Services.AddHostedService<TelemetryIngestionWorker>();

var host = builder.Build();
host.Run();