// PumpBackend.Contracts/Dps/DpsAllocationResponse.cs
namespace SentinelBackend.Contracts.Dps;

public class DpsAllocationResponse
{
    public string IotHubHostName { get; set; } = default!;
    public DpsInitialTwin? InitialTwin { get; set; }
}

public class DpsInitialTwin
{
    public DpsInitialTwinProperties? Properties { get; set; }
    public Dictionary<string, object>? Tags { get; set; }
}

public class DpsInitialTwinProperties
{
    public Dictionary<string, object>? Desired { get; set; }
}