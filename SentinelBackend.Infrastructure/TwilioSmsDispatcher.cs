namespace SentinelBackend.Infrastructure;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Application.Notifications;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using Twilio.Clients;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

/// <summary>
/// Sends SMS notifications via the Twilio API.
/// Falls back gracefully (logs + marks delivered) when credentials are not configured,
/// so the pipeline never stalls on a missing account SID or auth token.
/// </summary>
public class TwilioSmsDispatcher : INotificationDispatcher
{
    private readonly TwilioOptions _options;
    private readonly ILogger<TwilioSmsDispatcher> _logger;
    private readonly TwilioRestClient? _client;

    public TwilioSmsDispatcher(
        IOptions<NotificationOptions> options,
        ILogger<TwilioSmsDispatcher> logger)
    {
        _options = options.Value.Twilio;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_options.AccountSid)
            && !string.IsNullOrWhiteSpace(_options.AuthToken))
        {
            _client = new TwilioRestClient(_options.AccountSid, _options.AuthToken);
        }
    }

    public NotificationChannel Channel => NotificationChannel.Sms;

    public async Task<NotificationDispatchResult> SendAsync(
        NotificationAttempt attempt,
        Alarm alarm,
        CancellationToken cancellationToken = default)
    {
        if (_client is null || string.IsNullOrWhiteSpace(_options.FromNumber))
        {
            _logger.LogDebug(
                "Twilio not configured — skipping SMS for alarm {AlarmId}",
                alarm.Id);
            return new NotificationDispatchResult(
                Accepted: true,
                ProviderMessageId: "skipped-twilio-not-configured");
        }

        var message = await MessageResource.CreateAsync(
            to: new PhoneNumber(attempt.Recipient),
            from: new PhoneNumber(_options.FromNumber),
            body: BuildSmsBody(alarm),
            client: _client);

        if (message.ErrorCode is null)
        {
            _logger.LogInformation(
                "SMS sent to {Recipient} for alarm {AlarmId} via Twilio (sid={Sid})",
                attempt.Recipient, alarm.Id, message.Sid);
            return new NotificationDispatchResult(Accepted: true, ProviderMessageId: message.Sid);
        }

        _logger.LogWarning(
            "Twilio rejected SMS for alarm {AlarmId}: [{Code}] {Message}",
            alarm.Id, message.ErrorCode, message.ErrorMessage);
        return new NotificationDispatchResult(
            Accepted: false,
            ErrorMessage: $"[{message.ErrorCode}] {message.ErrorMessage}");
    }

    private static string BuildSmsBody(Alarm alarm) =>
        $"Sentinel: {alarm.AlarmType} ({alarm.Severity}) on device {alarm.DeviceId} at {alarm.StartedAt:u}. Log in to acknowledge.";
}
