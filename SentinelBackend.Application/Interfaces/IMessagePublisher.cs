namespace SentinelBackend.Application.Interfaces;

/// <summary>
/// Publishes messages to a message bus (e.g. Azure Service Bus).
/// </summary>
public interface IMessagePublisher
{
    Task PublishAsync(
        string queueOrTopic,
        object message,
        DateTimeOffset? scheduledEnqueueTime = null,
        CancellationToken cancellationToken = default);
}
