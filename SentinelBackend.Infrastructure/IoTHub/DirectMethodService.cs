namespace SentinelBackend.Infrastructure.IoTHub;

using Microsoft.Azure.Devices;
using Microsoft.Extensions.Logging;
using SentinelBackend.Application.Interfaces;

public class DirectMethodService : IDirectMethodService
{
    private readonly ServiceClient _serviceClient;
    private readonly ILogger<DirectMethodService> _logger;

    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);

    public DirectMethodService(ServiceClient serviceClient, ILogger<DirectMethodService> logger)
    {
        _serviceClient = serviceClient;
        _logger = logger;
    }

    public async Task<DirectMethodResult> InvokeAsync(
        string deviceId,
        string methodName,
        string? payloadJson = null,
        CancellationToken cancellationToken = default)
    {
        var method = new CloudToDeviceMethod(methodName)
        {
            ResponseTimeout = ResponseTimeout,
            ConnectionTimeout = ConnectionTimeout,
        };

        if (payloadJson is not null)
        {
            method.SetPayloadJson(payloadJson);
        }

        _logger.LogInformation(
            "Invoking direct method {MethodName} on device {DeviceId}",
            methodName, deviceId);

        var result = await _serviceClient.InvokeDeviceMethodAsync(
            deviceId, method, cancellationToken);

        _logger.LogInformation(
            "Direct method {MethodName} on device {DeviceId} returned status {Status}",
            methodName, deviceId, result.Status);

        return new DirectMethodResult(
            result.Status,
            result.GetPayloadAsJson());
    }
}
