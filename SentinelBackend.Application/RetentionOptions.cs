namespace SentinelBackend.Application;

/// <summary>
/// Configuration for telemetry retention and archival policies.
/// </summary>
public class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Number of days to keep telemetry rows in hot SQL storage. Default: 90.</summary>
    public int HotRetentionDays { get; set; } = 90;

    /// <summary>Maximum number of rows to delete per purge cycle. Default: 10,000.</summary>
    public int PurgeBatchSize { get; set; } = 10_000;

    /// <summary>Interval in seconds between purge cycles. Default: 3600 (1 hour).</summary>
    public int PurgeIntervalSeconds { get; set; } = 3600;

    /// <summary>Whether to require a raw payload blob URI before purging a row. Default: true.</summary>
    public bool RequireArchiveBeforePurge { get; set; } = true;

    /// <summary>Number of days to keep failed ingress messages. Default: 30.</summary>
    public int FailedIngressRetentionDays { get; set; } = 30;
}
