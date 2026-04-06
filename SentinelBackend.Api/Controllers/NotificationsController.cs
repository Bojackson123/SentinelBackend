namespace SentinelBackend.Api.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

[ApiController]
[Route("api/notifications")]
[Authorize(Policy = "AllAuthenticated")]
public class NotificationsController : ControllerBase
{
    private readonly SentinelDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly INotificationService _notificationService;

    public NotificationsController(
        SentinelDbContext db,
        ITenantContext tenant,
        INotificationService notificationService)
    {
        _db = db;
        _tenant = tenant;
        _notificationService = notificationService;
    }

    /// <summary>GET /api/notifications — list notification incidents</summary>
    [HttpGet]
    public async Task<IActionResult> GetNotifications(
        [FromQuery] string? status,
        [FromQuery] int? alarmId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var query = _db.NotificationIncidents
            .AsNoTracking()
            .Where(n => !n.Device.IsDeleted);

        // Apply tenant scoping through device ownership
        if (_tenant.IsCompanyUser)
            query = query.Where(n => n.CompanyId == _tenant.CompanyId);
        else if (_tenant.IsHomeowner)
            query = query.Where(n => n.CustomerId == _tenant.CustomerId);
        // Internal users see all

        if (Enum.TryParse<NotificationIncidentStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(n => n.Status == parsedStatus);
        }

        if (alarmId.HasValue)
        {
            query = query.Where(n => n.AlarmId == alarmId.Value);
        }

        var total = await query.CountAsync(cancellationToken);
        var incidents = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new
            {
                n.Id,
                n.AlarmId,
                n.DeviceId,
                Status = n.Status.ToString(),
                n.CurrentEscalationLevel,
                n.AcknowledgedAt,
                n.CreatedAt,
                n.UpdatedAt,
                AttemptCount = n.Attempts.Count,
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page, pageSize, incidents });
    }

    /// <summary>GET /api/notifications/{incidentId} — get incident detail</summary>
    [HttpGet("{incidentId:int}")]
    public async Task<IActionResult> GetNotificationDetail(
        int incidentId,
        CancellationToken cancellationToken)
    {
        var incident = await _db.NotificationIncidents
            .AsNoTracking()
            .Where(n => n.Id == incidentId)
            .Select(n => new
            {
                n.Id,
                n.AlarmId,
                n.DeviceId,
                n.SiteId,
                n.CustomerId,
                n.CompanyId,
                Status = n.Status.ToString(),
                n.CurrentEscalationLevel,
                n.MaxAttempts,
                n.AcknowledgedAt,
                n.AcknowledgedByUserId,
                n.CreatedAt,
                n.UpdatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (incident is null) return NotFound();
        return Ok(incident);
    }

    /// <summary>GET /api/notifications/{incidentId}/attempts — get delivery attempts</summary>
    [HttpGet("{incidentId:int}/attempts")]
    public async Task<IActionResult> GetAttempts(
        int incidentId,
        CancellationToken cancellationToken)
    {
        var exists = await _db.NotificationIncidents
            .AnyAsync(n => n.Id == incidentId, cancellationToken);
        if (!exists) return NotFound();

        var attempts = await _db.NotificationAttempts
            .AsNoTracking()
            .Where(a => a.NotificationIncidentId == incidentId)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new
            {
                a.Id,
                Channel = a.Channel.ToString(),
                Status = a.Status.ToString(),
                a.Recipient,
                a.AttemptNumber,
                a.EscalationLevel,
                a.ScheduledAt,
                a.SentAt,
                a.DeliveredAt,
                a.FailedAt,
                a.ErrorMessage,
                a.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Ok(attempts);
    }

    /// <summary>GET /api/notifications/{incidentId}/escalations — get escalation history</summary>
    [HttpGet("{incidentId:int}/escalations")]
    public async Task<IActionResult> GetEscalations(
        int incidentId,
        CancellationToken cancellationToken)
    {
        var exists = await _db.NotificationIncidents
            .AnyAsync(n => n.Id == incidentId, cancellationToken);
        if (!exists) return NotFound();

        var escalations = await _db.EscalationEvents
            .AsNoTracking()
            .Where(e => e.NotificationIncidentId == incidentId)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new
            {
                e.Id,
                e.FromLevel,
                e.ToLevel,
                e.Reason,
                e.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Ok(escalations);
    }

    /// <summary>POST /api/notifications/{incidentId}/acknowledge</summary>
    [HttpPost("{incidentId:int}/acknowledge")]
    public async Task<IActionResult> Acknowledge(
        int incidentId,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var incident = await _notificationService.AcknowledgeIncidentAsync(
            incidentId, userId, cancellationToken);

        if (incident is null) return NotFound();

        return Ok(new { incident.Id, Status = incident.Status.ToString() });
    }
}
