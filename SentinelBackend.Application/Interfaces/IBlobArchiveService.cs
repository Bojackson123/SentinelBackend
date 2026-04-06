namespace SentinelBackend.Application.Interfaces;

/// <summary>
/// Abstracts raw telemetry payload archiving and retrieval against blob storage.
/// </summary>
public interface IBlobArchiveService
{
    /// <summary>
    /// Archives a raw JSON payload and returns the blob URI, or null if the upload fails.
    /// Idempotent: returns the existing URI if the blob already exists.
    /// </summary>
    Task<string?> ArchiveRawPayloadAsync(
        string deviceId,
        DateTime timestampUtc,
        string messageId,
        string json,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a raw payload by its blob URI. Returns null if not found.
    /// </summary>
    Task<string?> GetRawPayloadAsync(
        string blobUri,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a raw payload blob by URI. Returns true if deleted, false if not found.
    /// </summary>
    Task<bool> DeleteRawPayloadAsync(
        string blobUri,
        CancellationToken cancellationToken = default);
}
