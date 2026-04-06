namespace SentinelBackend.Application.Interfaces;

using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;

/// <summary>
/// Sends notifications through a specific channel (email, SMS, push, voice).
/// Implementations are pluggable per channel — Phase 6 scaffolding uses a
/// no-op / logging dispatcher; real channel integrations are plugged in
/// once product decisions on providers are finalized.
/// </summary>
public interface INotificationDispatcher
{
    NotificationChannel Channel { get; }

    /// <summary>
    /// Sends a notification and returns true if delivery was accepted by the provider.
    /// </summary>
    Task<NotificationDispatchResult> SendAsync(
        NotificationAttempt attempt,
        Alarm alarm,
        CancellationToken cancellationToken = default);
}

public record NotificationDispatchResult(
    bool Accepted,
    string? ProviderMessageId = null,
    string? ErrorMessage = null);
