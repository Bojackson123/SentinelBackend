using Azure.Identity;
using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Provisioning.Service;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Dps;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Application.Notifications;
using SentinelBackend.Application.Services;
using SentinelBackend.Infrastructure;
using SentinelBackend.Infrastructure.Dps;
using SentinelBackend.Infrastructure.IoTHub;
using SentinelBackend.Infrastructure.Persistence;
using SentinelBackend.Infrastructure.Repositories;
using SentinelBackend.Infrastructure.Workers;
using SentinelBackend.Simulator.Components;
using SentinelBackend.Simulator.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Key Vault ────────────────────────────────────────────────────
var keyVaultUrl = builder.Configuration["KeyVaultUrl"];
if (!string.IsNullOrEmpty(keyVaultUrl))
{
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultUrl), new DefaultAzureCredential());
}

var sqlConn = builder.Configuration["SqlConnectionString"]
    ?? throw new InvalidOperationException(
        "SqlConnectionString not configured. Set via Key Vault or appsettings.");

// ── Blazor Server ────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── EF Core ──────────────────────────────────────────────────────
builder.Services.AddDbContextFactory<SentinelDbContext>(options =>
    options.UseSqlServer(sqlConn, o => o.EnableRetryOnFailure()));
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<SentinelDbContext>>().CreateDbContext());

// ── Manufacturing + DPS + IoT Hub provisioning services ──────────
builder.Services.Configure<DpsOptions>(opts =>
{
    opts.IotHubHostName = builder.Configuration["DpsIotHubHostName"] ?? string.Empty;
    opts.EnrollmentGroupPrimaryKey = builder.Configuration["DpsEnrollmentPrimaryKey"] ?? string.Empty;
    opts.WebhookSecret = builder.Configuration["DpsWebhookSecret"] ?? string.Empty;
});
builder.Services.AddScoped<IDeviceRepository, DeviceRepository>();
builder.Services.AddScoped<IDpsEnrollmentService, DpsEnrollmentService>();
builder.Services.AddScoped<IDpsAllocationService, DpsAllocationService>();
builder.Services.AddScoped<IManufacturingBatchService, ManufacturingBatchService>();

builder.Services.AddSingleton(sp =>
{
    var dpsConnectionString = builder.Configuration["DpsConnectionString"]
        ?? throw new InvalidOperationException("DpsConnectionString is not configured.");
    return ProvisioningServiceClient.CreateFromConnectionString(dpsConnectionString);
});

builder.Services.AddSingleton(sp =>
{
    var iotHubConnectionString = GetIotHubServiceConnectionString(builder.Configuration);
    return RegistryManager.CreateFromConnectionString(iotHubConnectionString);
});
builder.Services.AddScoped<IDeviceTwinService, DeviceTwinService>();

// ── Domain services (for alarm evaluation during simulation) ———— //
builder.Services.AddScoped<IAlarmService, AlarmService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

// Notification channel dispatchers — SendGrid (email) + Twilio (SMS).
// Both degrade gracefully when credentials are absent: attempts are marked
// delivered with a "skipped-*-not-configured" provider message ID.
builder.Services.Configure<NotificationOptions>(
    builder.Configuration.GetSection(NotificationOptions.SectionName));
builder.Services.AddSingleton<INotificationDispatcher, SendGridEmailDispatcher>();
builder.Services.AddSingleton<INotificationDispatcher, TwilioSmsDispatcher>();

// Background worker that polls and dispatches pending notification attempts.
builder.Services.AddHostedService<NotificationDispatchWorker>();

// ── Simulator engine ─────────────────────────────────────────────
builder.Services.AddSingleton<TelemetrySimulatorService>();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string GetIotHubServiceConnectionString(IConfiguration configuration)
{
    var connectionString =
        configuration["IoTHubServiceConnectionString"]
        ?? configuration["IoTHubConnectionString"];

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "IoTHubServiceConnectionString is not configured. "
                + "Set IoTHubServiceConnectionString (or legacy alias IoTHubConnectionString) "
                + "to an IoT Hub service connection string for simulator provisioning and cleanup.");
    }

    try
    {
        IotHubConnectionStringBuilder.Create(connectionString);
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException)
    {
        throw new InvalidOperationException(
            "IoTHubServiceConnectionString is invalid for IoT Hub service operations. "
                + "Do not use IoTHubEventHubConnectionString for provisioning/cleanup APIs.",
            ex);
    }

    return connectionString;
}
