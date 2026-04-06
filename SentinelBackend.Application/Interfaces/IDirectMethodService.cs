namespace SentinelBackend.Application.Interfaces;

public interface IDirectMethodService
{
    /// <summary>
    /// Invokes a direct method on a device via IoT Hub.
    /// Returns the response payload and HTTP-like status code.
    /// </summary>
    Task<DirectMethodResult> InvokeAsync(
        string deviceId,
        string methodName,
        string? payloadJson = null,
        CancellationToken cancellationToken = default);
}

public record DirectMethodResult(int Status, string? ResponseJson);
