namespace SentinelBackend.Infrastructure;

using Microsoft.Extensions.Logging;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;

/// <summary>
/// No-op dispatcher that logs delivery attempts without sending.
/// Serves as the default until real channel providers (SendGrid, Twilio, etc.)
/// are configured based on product decisions.
/// </summary>
public class LoggingNotificationDispatcher : INotificationDispatcher
{
    private readonly ILogger<LoggingNotificationDispatcher> _logger;

    public LoggingNotificationDispatcher(ILogger<LoggingNotificationDispatcher> logger)
    {
        _logger = logger;
    }

    public NotificationChannel Channel => NotificationChannel.Email;

    public Task<NotificationDispatchResult> SendAsync(
        NotificationAttempt attempt,
        Alarm alarm,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[NO-OP] Would send {Channel} notification to {Recipient} for alarm {AlarmId} " +
            "(type={AlarmType}, severity={Severity})",
            attempt.Channel,
            attempt.Recipient,
            alarm.Id,
            alarm.AlarmType,
            alarm.Severity);

        return Task.FromResult(new NotificationDispatchResult(
            Accepted: true,
            ProviderMessageId: $"noop-{Guid.NewGuid():N}"));
    }
}
