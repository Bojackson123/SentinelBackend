namespace SentinelBackend.Api.Tests;

using SentinelBackend.Tests.Shared;
using Xunit;

public class BlobArchiveServiceTests
{
    [Fact]
    public async Task ArchiveRawPayload_ReturnsUri()
    {
        var svc = new FakeBlobArchiveService();
        var uri = await svc.ArchiveRawPayloadAsync(
            "device-1", DateTime.UtcNow, "msg-1", "{\"value\":1}");

        Assert.NotNull(uri);
        Assert.Contains("device-1", uri);
        Assert.Contains("msg-1", uri);
    }

    [Fact]
    public async Task ArchiveRawPayload_Idempotent()
    {
        var svc = new FakeBlobArchiveService();
        var uri1 = await svc.ArchiveRawPayloadAsync(
            "device-1", DateTime.UtcNow, "msg-1", "{\"value\":1}");
        var uri2 = await svc.ArchiveRawPayloadAsync(
            "device-1", DateTime.UtcNow, "msg-1", "{\"value\":1}");

        Assert.Equal(uri1, uri2);
    }

    [Fact]
    public async Task GetRawPayload_ReturnsContent_AfterArchive()
    {
        var svc = new FakeBlobArchiveService();
        var json = "{\"value\":42}";
        var uri = await svc.ArchiveRawPayloadAsync(
            "device-1", DateTime.UtcNow, "msg-1", json);

        var content = await svc.GetRawPayloadAsync(uri!);
        Assert.Equal(json, content);
    }

    [Fact]
    public async Task GetRawPayload_ReturnsNull_WhenNotFound()
    {
        var svc = new FakeBlobArchiveService();
        var content = await svc.GetRawPayloadAsync("https://fake.blob.core.windows.net/raw-telemetry/no-such.json");
        Assert.Null(content);
    }

    [Fact]
    public async Task DeleteRawPayload_RemovesContent()
    {
        var svc = new FakeBlobArchiveService();
        var uri = await svc.ArchiveRawPayloadAsync(
            "device-1", DateTime.UtcNow, "msg-1", "{\"value\":1}");

        var deleted = await svc.DeleteRawPayloadAsync(uri!);
        Assert.True(deleted);

        var content = await svc.GetRawPayloadAsync(uri!);
        Assert.Null(content);
    }

    [Fact]
    public async Task DeleteRawPayload_ReturnsFalse_WhenNotFound()
    {
        var svc = new FakeBlobArchiveService();
        var deleted = await svc.DeleteRawPayloadAsync("https://fake.blob.core.windows.net/raw-telemetry/gone.json");
        Assert.False(deleted);
    }
}
