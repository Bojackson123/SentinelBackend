namespace SentinelBackend.Tests.Shared;

using SentinelBackend.Application.Interfaces;

/// <summary>
/// In-memory fake for IBlobArchiveService used in controller and integration tests.
/// </summary>
public class FakeBlobArchiveService : IBlobArchiveService
{
    private readonly Dictionary<string, string> _blobs = new();

    public Task<string?> ArchiveRawPayloadAsync(
        string deviceId,
        DateTime timestampUtc,
        string messageId,
        string json,
        CancellationToken cancellationToken = default)
    {
        var uri = $"https://fake.blob.core.windows.net/raw-telemetry/{deviceId}/{timestampUtc:yyyy/MM/dd}/{messageId}.json";
        _blobs.TryAdd(uri, json);
        return Task.FromResult<string?>(uri);
    }

    public Task<string?> GetRawPayloadAsync(
        string blobUri,
        CancellationToken cancellationToken = default)
    {
        _blobs.TryGetValue(blobUri, out var content);
        return Task.FromResult(content);
    }

    public Task<bool> DeleteRawPayloadAsync(
        string blobUri,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_blobs.Remove(blobUri));
    }
}
