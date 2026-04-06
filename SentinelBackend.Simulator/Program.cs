using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Infrastructure;
using SentinelBackend.Infrastructure.Persistence;
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

// ── Domain services (for alarm evaluation during simulation) ─────
builder.Services.AddScoped<IAlarmService, AlarmService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddSingleton<INotificationDispatcher, LoggingNotificationDispatcher>();

// ── Simulator engine ─────────────────────────────────────────────
builder.Services.AddSingleton<TelemetrySimulatorService>();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
