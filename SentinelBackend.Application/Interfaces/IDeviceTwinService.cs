namespace SentinelBackend.Application.Interfaces;

public interface IDeviceTwinService
{
    Task SetDesiredPropertiesAsync(
        string deviceId,
        Dictionary<string, object> properties,
        CancellationToken cancellationToken = default);
}
