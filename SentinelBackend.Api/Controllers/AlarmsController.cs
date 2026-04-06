namespace SentinelBackend.Api.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

[ApiController]
[Route("api/alarms")]
[Authorize(Policy = "AllAuthenticated")]
public class AlarmsController : ControllerBase
{
    private readonly SentinelDbContext _db;
    private readonly ITenantContext _tenant;

    public AlarmsController(SentinelDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>GET /api/alarms</summary>
    [HttpGet]
    public async Task<IActionResult> GetAlarms(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var query = _tenant.ApplyScope(_db.Alarms.AsNoTracking());

        if (Enum.TryParse<AlarmStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(a => a.Status == parsedStatus);
        }

        var total = await query.CountAsync(cancellationToken);
        var alarms = await query
            .OrderByDescending(a => a.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id,
                a.DeviceId,
                a.AlarmType,
                Severity = a.Severity.ToString(),
                Status = a.Status.ToString(),
                SourceType = a.SourceType.ToString(),
                a.StartedAt,
                a.ResolvedAt,
                a.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page, pageSize, alarms });
    }

    /// <summary>POST /api/alarms/{alarmId}/acknowledge</summary>
    [HttpPost("{alarmId:int}/acknowledge")]
    public async Task<IActionResult> Acknowledge(int alarmId, CancellationToken cancellationToken)
    {
        var alarm = await _db.Alarms.FindAsync([alarmId], cancellationToken);
        if (alarm is null) return NotFound();

        if (alarm.Status != AlarmStatus.Active)
            return BadRequest($"Cannot acknowledge alarm in '{alarm.Status}' status.");

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        alarm.Status = AlarmStatus.Acknowledged;
        alarm.UpdatedAt = DateTime.UtcNow;

        _db.AlarmEvents.Add(new AlarmEvent
        {
            AlarmId = alarm.Id,
            EventType = "Acknowledged",
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { alarm.Id, Status = alarm.Status.ToString() });
    }

    /// <summary>POST /api/alarms/{alarmId}/suppress</summary>
    [HttpPost("{alarmId:int}/suppress")]
    public async Task<IActionResult> Suppress(
        int alarmId,
        [FromBody] SuppressAlarmRequest request,
        CancellationToken cancellationToken)
    {
        var alarm = await _db.Alarms.FindAsync([alarmId], cancellationToken);
        if (alarm is null) return NotFound();

        if (alarm.Status is AlarmStatus.Resolved or AlarmStatus.Suppressed)
            return BadRequest($"Cannot suppress alarm in '{alarm.Status}' status.");

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        alarm.Status = AlarmStatus.Suppressed;
        alarm.SuppressReason = request.Reason;
        alarm.SuppressedByUserId = userId;
        alarm.UpdatedAt = DateTime.UtcNow;

        _db.AlarmEvents.Add(new AlarmEvent
        {
            AlarmId = alarm.Id,
            EventType = "Suppressed",
            UserId = userId,
            Reason = request.Reason,
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { alarm.Id, Status = alarm.Status.ToString() });
    }

    /// <summary>POST /api/alarms/{alarmId}/resolve</summary>
    [HttpPost("{alarmId:int}/resolve")]
    public async Task<IActionResult> Resolve(
        int alarmId,
        [FromBody] ResolveAlarmRequest? request,
        CancellationToken cancellationToken)
    {
        var alarm = await _db.Alarms.FindAsync([alarmId], cancellationToken);
        if (alarm is null) return NotFound();

        if (alarm.Status == AlarmStatus.Resolved)
            return BadRequest($"Alarm is already resolved.");

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        alarm.Status = AlarmStatus.Resolved;
        alarm.ResolvedAt = DateTime.UtcNow;
        alarm.UpdatedAt = DateTime.UtcNow;

        _db.AlarmEvents.Add(new AlarmEvent
        {
            AlarmId = alarm.Id,
            EventType = "Resolved",
            UserId = userId,
            Reason = request?.Reason,
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { alarm.Id, Status = alarm.Status.ToString() });
    }

    /// <summary>GET /api/alarms/{alarmId}/events</summary>
    [HttpGet("{alarmId:int}/events")]
    public async Task<IActionResult> GetAlarmEvents(int alarmId, CancellationToken cancellationToken)
    {
        var exists = await _db.Alarms.AnyAsync(a => a.Id == alarmId, cancellationToken);
        if (!exists) return NotFound();

        var events = await _db.AlarmEvents
            .AsNoTracking()
            .Where(e => e.AlarmId == alarmId)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new
            {
                e.Id,
                e.EventType,
                e.UserId,
                e.Reason,
                e.MetadataJson,
                e.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Ok(events);
    }
}

public class SuppressAlarmRequest
{
    public string Reason { get; set; } = default!;
}

public class ResolveAlarmRequest
{
    public string? Reason { get; set; }
}
