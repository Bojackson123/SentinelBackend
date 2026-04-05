namespace SentinelBackend.Infrastructure.Persistence;

using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public class SentinelDbContextFactory : IDesignTimeDbContextFactory<SentinelDbContext>
{
    public SentinelDbContext CreateDbContext(string[] args)
    {
        var kvUrl = Environment.GetEnvironmentVariable("KeyVaultUrl")
            ?? throw new InvalidOperationException("KeyVaultUrl environment variable is not set.");

        var client = new SecretClient(new Uri(kvUrl), new DefaultAzureCredential());

        var connectionString = client
            .GetSecret("SqlConnectionString")
            .Value.Value;

        var options = new DbContextOptionsBuilder<SentinelDbContext>()
            .UseSqlServer(connectionString, o => o.EnableRetryOnFailure())
            .Options;

        return new SentinelDbContext(options);
    }
}