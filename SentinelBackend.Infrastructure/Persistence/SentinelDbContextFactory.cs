namespace SentinelBackend.Infrastructure.Persistence;

using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public class SentinelDbContextFactory : IDesignTimeDbContextFactory<SentinelDbContext>
{
    public SentinelDbContext CreateDbContext(string[] args)
    {
        // Allow a direct connection string override for local/design-time usage (e.g. running migrations).
        // Set EF_CONNECTION_STRING in your shell before running dotnet ef commands.
        var connectionString = Environment.GetEnvironmentVariable("EF_CONNECTION_STRING");

        if (connectionString is null)
        {
            var kvUrl = Environment.GetEnvironmentVariable("KeyVaultUrl")
                ?? throw new InvalidOperationException("KeyVaultUrl environment variable is not set.");

            var client = new SecretClient(new Uri(kvUrl), new DefaultAzureCredential());

            connectionString = client
                .GetSecret("SqlConnectionString")
                .Value.Value;
        }

        var options = new DbContextOptionsBuilder<SentinelDbContext>()
            .UseSqlServer(connectionString, o => o.EnableRetryOnFailure())
            .Options;

        return new SentinelDbContext(options);
    }
}