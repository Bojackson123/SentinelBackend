namespace SentinelBackend.Tests.Shared;

using SentinelBackend.Application.Interfaces;

/// <summary>
/// No-op message publisher for tests that don't need Service Bus behavior.
/// Records published messages for assertion.
/// </summary>
public class FakeMessagePublisher : IMessagePublisher
{
    public List<(string Queue, object Message, DateTimeOffset? Scheduled)> Published { get; } = [];

    public Task PublishAsync(
        string queueOrTopic,
        object message,
        DateTimeOffset? scheduledEnqueueTime = null,
        CancellationToken cancellationToken = default)
    {
        Published.Add((queueOrTopic, message, scheduledEnqueueTime));
        return Task.CompletedTask;
    }
}
