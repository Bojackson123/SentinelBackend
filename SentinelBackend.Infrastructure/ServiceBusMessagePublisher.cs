namespace SentinelBackend.Infrastructure;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using SentinelBackend.Application.Interfaces;

public class ServiceBusMessagePublisher : IMessagePublisher
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusMessagePublisher> _logger;

    public ServiceBusMessagePublisher(
        ServiceBusClient client,
        ILogger<ServiceBusMessagePublisher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task PublishAsync(
        string queueOrTopic,
        object message,
        DateTimeOffset? scheduledEnqueueTime = null,
        CancellationToken cancellationToken = default)
    {
        await using var sender = _client.CreateSender(queueOrTopic);

        var json = JsonSerializer.Serialize(message);
        var sbMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
        };

        if (scheduledEnqueueTime.HasValue)
        {
            sbMessage.ScheduledEnqueueTime = scheduledEnqueueTime.Value;
        }

        await sender.SendMessageAsync(sbMessage, cancellationToken);

        _logger.LogDebug(
            "Published message to {Queue} (scheduled={Scheduled})",
            queueOrTopic, scheduledEnqueueTime?.ToString("O") ?? "immediate");
    }
}
