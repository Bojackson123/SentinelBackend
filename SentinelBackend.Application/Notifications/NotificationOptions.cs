namespace SentinelBackend.Application.Notifications;

public class NotificationOptions
{
    public const string SectionName = "Notifications";

    public SendGridOptions SendGrid { get; set; } = new();
    public TwilioOptions Twilio { get; set; } = new();

    /// <summary>
    /// When set, overrides the resolved email recipient for all notification attempts.
    /// Useful for simulator and dev environments where device records have placeholder addresses.
    /// </summary>
    public string? TestEmailRecipient { get; set; }

    /// <summary>
    /// When set, an SMS attempt is created alongside each email attempt and sent to this number.
    /// Required for simulator testing since devices don't have real phone numbers stored.
    /// </summary>
    public string? TestSmsRecipient { get; set; }
}

public class SendGridOptions
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Sender address used in the From header.</summary>
    public string FromEmail { get; set; } = "alerts@sentinel.local";

    public string FromName { get; set; } = "Sentinel IoT Alerts";
}

public class TwilioOptions
{
    public string AccountSid { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>Twilio phone number in E.164 format, e.g. +15551234567.</summary>
    public string FromNumber { get; set; } = string.Empty;
}
