using Azure.Identity;
using Scalar.AspNetCore;
using SentinelBackend.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddAzureKeyVault(
    new Uri(
        builder.Configuration["KeyVaultUrl"]
            ?? throw new InvalidOperationException("KeyVaultUrl is not configured.")
    ),
    new DefaultAzureCredential()
);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.MapControllers();

app.Run();