namespace SentinelBackend.Infrastructure.IoTHub;

using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using SentinelBackend.Application.Interfaces;

public class DeviceTwinService : IDeviceTwinService
{
    private readonly RegistryManager _registryManager;
    private readonly ILogger<DeviceTwinService> _logger;

    public DeviceTwinService(RegistryManager registryManager, ILogger<DeviceTwinService> logger)
    {
        _registryManager = registryManager;
        _logger = logger;
    }

    public async Task SetDesiredPropertiesAsync(
        string deviceId,
        Dictionary<string, object> properties,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var twin = await _registryManager.GetTwinAsync(deviceId, cancellationToken);

            var patch = new Twin();
            foreach (var (key, value) in properties)
            {
                patch.Properties.Desired[key] = JToken.FromObject(value);
            }

            await _registryManager.UpdateTwinAsync(deviceId, patch, twin.ETag, cancellationToken);

            _logger.LogInformation(
                "Updated twin desired properties for device {DeviceId}: {Properties}",
                deviceId, string.Join(", ", properties.Keys));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update twin for device {DeviceId}", deviceId);
            throw;
        }
    }
}
