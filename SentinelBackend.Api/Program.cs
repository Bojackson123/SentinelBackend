using System.Text;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using SentinelBackend.Api.Services;
using SentinelBackend.Api.Workers;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddAzureKeyVault(
    new Uri(
        builder.Configuration["KeyVaultUrl"]
            ?? throw new InvalidOperationException("KeyVaultUrl is not configured.")
    ),
    new DefaultAzureCredential()
);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

// JWT Bearer Authentication
var jwtSigningKey = builder.Configuration["JwtSigningKey"] ?? throw new InvalidOperationException("JWT signing key is not configured.");
var jwtIssuer = builder.Configuration["JwtIssuer"] ?? "SentinelBackend";
var jwtAudience = builder.Configuration["JwtAudience"] ?? "SentinelBackend";

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
        ClockSkew = TimeSpan.FromMinutes(1)
    };
});

// Authorization policies — tenant-aware
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("InternalOnly", policy =>
        policy.RequireRole("InternalAdmin", "InternalTech"))
    .AddPolicy("CompanyOrInternal", policy =>
        policy.RequireRole("InternalAdmin", "InternalTech", "CompanyAdmin", "CompanyTech"))
    .AddPolicy("AllAuthenticated", policy =>
        policy.RequireAuthenticatedUser());

builder.Services.AddHostedService<CommandExecutorWorker>();

var app = builder.Build();

await SeedIdentityRolesAsync(app.Services);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

static async Task SeedIdentityRolesAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var roleManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole>>();

    foreach (var role in Enum.GetNames<UserRole>())
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            var result = await roleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole(role));
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Failed to seed role '{role}': {errors}");
            }
        }
    }
}