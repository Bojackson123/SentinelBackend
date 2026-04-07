namespace SentinelBackend.Infrastructure;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Application.Notifications;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;

/// <summary>
/// Sends email notifications via the SendGrid API.
/// Falls back gracefully (logs + marks delivered) when not configured,
/// so the pipeline never stalls on a missing API key.
/// </summary>
public class SendGridEmailDispatcher : INotificationDispatcher
{
    private readonly SendGridOptions _options;
    private readonly ILogger<SendGridEmailDispatcher> _logger;
    private readonly SendGridClient? _client;

    public SendGridEmailDispatcher(
        IOptions<NotificationOptions> options,
        ILogger<SendGridEmailDispatcher> logger)
    {
        _options = options.Value.SendGrid;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            _client = new SendGridClient(_options.ApiKey);
    }

    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<NotificationDispatchResult> SendAsync(
        NotificationAttempt attempt,
        Alarm alarm,
        CancellationToken cancellationToken = default)
    {
        if (_client is null)
        {
            _logger.LogDebug(
                "SendGrid not configured — skipping email for alarm {AlarmId}",
                alarm.Id);
            return new NotificationDispatchResult(
                Accepted: true,
                ProviderMessageId: "skipped-sendgrid-not-configured");
        }

        var from = new EmailAddress(_options.FromEmail, _options.FromName);
        var to = new EmailAddress(attempt.Recipient);
        var subject = $"[Sentinel Alert] {alarm.AlarmType} — {alarm.Severity}";
        var body = BuildEmailBody(alarm, attempt);
        var msg = MailHelper.CreateSingleEmail(from, to, subject, body, htmlContent: null);

        var response = await _client.SendEmailAsync(msg, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            string? msgId = null;
            if (response.Headers.TryGetValues("X-Message-Id", out var values))
                msgId = values.FirstOrDefault();

            _logger.LogInformation(
                "Email sent to {Recipient} for alarm {AlarmId} via SendGrid (status={Status})",
                attempt.Recipient, alarm.Id, (int)response.StatusCode);

            return new NotificationDispatchResult(Accepted: true, ProviderMessageId: msgId);
        }

        var errorBody = await response.Body.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning(
            "SendGrid rejected email for alarm {AlarmId}: HTTP {Status} — {Body}",
            alarm.Id, (int)response.StatusCode, errorBody);

        return new NotificationDispatchResult(
            Accepted: false,
            ErrorMessage: $"HTTP {(int)response.StatusCode}: {errorBody}");
    }

    private static string BuildEmailBody(Alarm alarm, NotificationAttempt attempt) =>
        $"""
        Sentinel IoT Alert
        ==================
        Alarm Type:  {alarm.AlarmType}
        Severity:    {alarm.Severity}
        Device ID:   {alarm.DeviceId}
        Triggered:   {alarm.StartedAt:u}
        Escalation:  Level {attempt.EscalationLevel}

        Please log into the Sentinel dashboard to acknowledge or investigate this alarm.
        """;
}
