namespace SentinelBackend.Infrastructure;

using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using SentinelBackend.Application.Interfaces;

public class BlobArchiveService : IBlobArchiveService
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobArchiveService> _logger;

    public BlobArchiveService(
        BlobContainerClient container,
        ILogger<BlobArchiveService> logger)
    {
        _container = container;
        _logger = logger;
    }

    public async Task<string?> ArchiveRawPayloadAsync(
        string deviceId,
        DateTime timestampUtc,
        string messageId,
        string json,
        CancellationToken cancellationToken = default)
    {
        var blobName = $"{deviceId}/{timestampUtc:yyyy/MM/dd}/{messageId}.json";
        var blobClient = _container.GetBlobClient(blobName);

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            await blobClient.UploadAsync(stream, overwrite: false, cancellationToken);
            return blobClient.Uri.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Blob already exists (replay) — return existing URI
            _logger.LogDebug("Raw payload blob already exists for {MessageId}", messageId);
            return blobClient.Uri.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to archive raw payload for {MessageId}", messageId);
            return null;
        }
    }

    public async Task<string?> GetRawPayloadAsync(
        string blobUri,
        CancellationToken cancellationToken = default)
    {
        var blobName = ExtractBlobName(blobUri);
        if (blobName is null) return null;

        var blobClient = _container.GetBlobClient(blobName);
        try
        {
            var response = await blobClient.DownloadContentAsync(cancellationToken);
            return response.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> DeleteRawPayloadAsync(
        string blobUri,
        CancellationToken cancellationToken = default)
    {
        var blobName = ExtractBlobName(blobUri);
        if (blobName is null) return false;

        var blobClient = _container.GetBlobClient(blobName);
        var response = await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
        return response.Value;
    }

    private string? ExtractBlobName(string blobUri)
    {
        var containerUriString = _container.Uri.ToString().TrimEnd('/') + "/";
        if (!blobUri.StartsWith(containerUriString, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Blob URI {BlobUri} does not belong to container {ContainerUri}",
                blobUri, _container.Uri);
            return null;
        }
        return blobUri[containerUriString.Length..];
    }
}
