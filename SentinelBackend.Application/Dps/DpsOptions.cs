// PumpBackend.Infrastructure/Dps/DpsOptions.cs
// (also used by Application layer, move to Application if you prefer)
namespace SentinelBackend.Application.Dps;

public class DpsOptions
{
    public const string Section = "Dps";

    public string IotHubHostName { get; set; } = default!;
    public string EnrollmentGroupPrimaryKey { get; set; } = default!;
    public string WebhookSecret { get; set; } = default!;
}