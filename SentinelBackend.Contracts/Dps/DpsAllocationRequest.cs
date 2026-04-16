// PumpBackend.Contracts/Dps/DpsAllocationRequest.cs
namespace SentinelBackend.Contracts.Dps;

public class DpsAllocationRequest
{
    public string? RegistrationId { get; set; }
    public DeviceRuntimeContext DeviceRuntimeContext { get; set; } = default!;
    public IEnumerable<string> LinkedHubs { get; set; } = [];
}

public class DeviceRuntimeContext
{
    public string RegistrationId { get; set; } = default!;
    public string? CurrentIotHubHostName { get; set; }
    public string? CurrentDeviceId { get; set; }
}