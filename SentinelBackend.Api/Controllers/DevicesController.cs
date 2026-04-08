namespace SentinelBackend.Api.Controllers;

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Contracts;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

[ApiController]
[Route("api/devices")]
[Authorize(Policy = "AllAuthenticated")]
public class DevicesController : ControllerBase
{
    private readonly SentinelDbContext _db;
    private readonly ITenantContext _tenant;

    public DevicesController(SentinelDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>GET /api/devices — tenant-scoped device list</summary>
    [HttpGet]
    public async Task<IActionResult> GetDevices(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var query = _tenant.ApplyScope(_db.Devices.AsNoTracking());

        var total = await query.CountAsync(cancellationToken);
        var devices = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.Id,
                d.DeviceId,
                d.SerialNumber,
                d.HardwareRevision,
                d.FirmwareVersion,
                Status = d.Status.ToString(),
                d.ProvisionedAt,
                d.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page, pageSize, devices });
    }

    /// <summary>GET /api/devices/{deviceId} — device detail</summary>
    [HttpGet("{deviceId}")]
    public async Task<IActionResult> GetDevice(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var device = await _tenant.ApplyScope(
                _db.Devices.AsNoTracking()
                    .Include(d => d.LatestState)
                    .Include(d => d.ConnectivityState))
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);

        if (device is null)
            return NotFound();

        return Ok(new
        {
            device.Id,
            device.DeviceId,
            device.SerialNumber,
            device.HardwareRevision,
            device.FirmwareVersion,
            Status = device.Status.ToString(),
            device.ProvisionedAt,
            device.ManufacturedAt,
            device.CreatedAt,
            LatestState = device.LatestState is null ? null : new
            {
                device.LatestState.LastTelemetryTimestampUtc,
                device.LatestState.PanelVoltage,
                device.LatestState.PumpCurrent,
                device.LatestState.PumpRunning,
                device.LatestState.HighWaterAlarm,
                device.LatestState.TemperatureC,
                device.LatestState.SignalRssi,
                device.LatestState.RuntimeSeconds,
                device.LatestState.ReportedCycleCount,
                device.LatestState.DerivedCycleCount,
            },
            Connectivity = device.ConnectivityState is null ? null : new
            {
                device.ConnectivityState.LastMessageReceivedAt,
                device.ConnectivityState.LastTelemetryReceivedAt,
                device.ConnectivityState.IsOffline,
                device.ConnectivityState.LastMessageType,
            },
        });
    }

    /// <summary>GET /api/devices/{deviceId}/state — latest telemetry state</summary>
    [HttpGet("{deviceId}/state")]
    public async Task<ActionResult<DeviceStateResponse>> GetState(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var device = await _tenant.ApplyScope(
                _db.Devices.AsNoTracking().Include(d => d.LatestState))
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);

        var state = device?.LatestState;

        if (state is null)
            return NotFound();

        return Ok(new DeviceStateResponse
        {
            DeviceId = deviceId,
            LastTelemetryTimestampUtc = state.LastTelemetryTimestampUtc,
            PanelVoltage = state.PanelVoltage,
            PumpCurrent = state.PumpCurrent,
            PumpRunning = state.PumpRunning,
            HighWaterAlarm = state.HighWaterAlarm,
            TemperatureC = state.TemperatureC,
            SignalRssi = state.SignalRssi,
            RuntimeSeconds = state.RuntimeSeconds,
            ReportedCycleCount = state.ReportedCycleCount,
            DerivedCycleCount = state.DerivedCycleCount,
            UpdatedAt = state.UpdatedAt,
        });
    }

    /// <summary>GET /api/devices/{deviceId}/telemetry — paginated telemetry history</summary>
    [HttpGet("{deviceId}/telemetry")]
    public async Task<IActionResult> GetTelemetry(
        string deviceId,
        [FromQuery] DateTime? after,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 200) pageSize = 50;

        var device = await _tenant.ApplyScope(_db.Devices.AsNoTracking())
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);

        if (device is null)
            return NotFound();

        var query = _tenant.ApplyScope(
            _db.TelemetryHistory.AsNoTracking()
                .Where(t => t.DeviceId == device.Id));

        // Cursor pagination by timestamp
        if (after.HasValue)
        {
            query = query.Where(t => t.TimestampUtc < after.Value);
        }

        var rows = await query
            .OrderByDescending(t => t.TimestampUtc)
            .Take(pageSize)
            .Select(t => new
            {
                t.MessageId,
                t.MessageType,
                t.TimestampUtc,
                t.PanelVoltage,
                t.PumpCurrent,
                t.PumpRunning,
                t.HighWaterAlarm,
                t.TemperatureC,
                t.SignalRssi,
                t.RuntimeSeconds,
                t.ReportedCycleCount,
                t.FirmwareVersion,
            })
            .ToListAsync(cancellationToken);

        return Ok(rows);
    }

    private static readonly HashSet<string> ValidCommandTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "reboot", "ping", "capture-snapshot", "run-self-test", "sync-now", "clear-fault"
    };

    /// <summary>PATCH /api/devices/{deviceId}/desired-properties — update twin desired properties</summary>
    [HttpPatch("{deviceId}/desired-properties")]
    [Authorize(Policy = "CompanyOrInternal")]
    public async Task<IActionResult> PatchDesiredProperties(
        string deviceId,
        [FromBody] Dictionary<string, object> properties,
        [FromServices] IDeviceTwinService twinService,
        CancellationToken cancellationToken)
    {
        if (properties is null || properties.Count == 0)
            return BadRequest(new { error = "At least one property is required." });

        var device = await _tenant.ApplyScope(_db.Devices.AsNoTracking())
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);

        if (device is null)
            return NotFound();

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
        var now = DateTime.UtcNow;
        var logs = new List<DesiredPropertyLog>();

        foreach (var (key, value) in properties)
        {
            logs.Add(new DesiredPropertyLog
            {
                DeviceId = device.Id,
                PropertyName = key,
                NewValue = JsonSerializer.Serialize(value),
                RequestedByUserId = userId,
                RequestedAt = now,
            });
        }

        try
        {
            await twinService.SetDesiredPropertiesAsync(deviceId, properties, cancellationToken);

            foreach (var log in logs)
                log.Success = true;
        }
        catch (Exception ex)
        {
            foreach (var log in logs)
            {
                log.Success = false;
                log.ErrorMessage = ex.Message;
            }

            _db.DesiredPropertyLogs.AddRange(logs);
            await _db.SaveChangesAsync(cancellationToken);

            return StatusCode(502, new { error = "Failed to update device twin.", detail = ex.Message });
        }

        _db.DesiredPropertyLogs.AddRange(logs);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { updated = properties.Keys });
    }

    /// <summary>POST /api/devices/{deviceId}/commands/{commandType} — submit async command</summary>
    [HttpPost("{deviceId}/commands/{commandType}")]
    [Authorize(Policy = "CompanyOrInternal")]
    public async Task<IActionResult> SubmitCommand(
        string deviceId,
        string commandType,
        [FromServices] IMessagePublisher? messagePublisher,
        CancellationToken cancellationToken)
    {
        if (!ValidCommandTypes.Contains(commandType))
            return BadRequest(new { error = $"Unsupported command type: {commandType}" });

        var device = await _tenant.ApplyScope(_db.Devices)
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);

        if (device is null)
            return NotFound();

        if (device.Status == DeviceStatus.Decommissioned)
            return BadRequest(new { error = "Cannot send commands to a decommissioned device." });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
        var now = DateTime.UtcNow;

        var command = new CommandLog
        {
            DeviceId = device.Id,
            CommandType = commandType,
            Status = CommandStatus.Pending,
            RequestedByUserId = userId,
            RequestedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.CommandLogs.Add(command);
        await _db.SaveChangesAsync(cancellationToken);

        if (messagePublisher is not null)
        {
            await messagePublisher.PublishAsync(
                Api.Workers.CommandExecutorWorker.QueueName,
                new Api.Workers.CommandMessage(command.Id),
                cancellationToken: cancellationToken);
        }

        return AcceptedAtAction(
            nameof(GetCommand),
            new { deviceId, commandId = command.Id },
            new
            {
                commandId = command.Id,
                commandType,
                status = command.Status.ToString(),
                requestedAt = command.RequestedAt,
            });
    }

    /// <summary>GET /api/devices/{deviceId}/commands/{commandId} — query command status</summary>
    [HttpGet("{deviceId}/commands/{commandId:int}")]
    public async Task<IActionResult> GetCommand(
        string deviceId,
        int commandId,
        CancellationToken cancellationToken)
    {
        var device = await _tenant.ApplyScope(_db.Devices.AsNoTracking())
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);

        if (device is null)
            return NotFound();

        var command = await _db.CommandLogs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == commandId && c.DeviceId == device.Id, cancellationToken);

        if (command is null)
            return NotFound();

        return Ok(new
        {
            commandId = command.Id,
            commandType = command.CommandType,
            status = command.Status.ToString(),
            requestedAt = command.RequestedAt,
            requestedByUserId = command.RequestedByUserId,
            sentAt = command.SentAt,
            completedAt = command.CompletedAt,
            responseJson = command.ResponseJson,
            errorMessage = command.ErrorMessage,
        });
    }

    /// <summary>GET /api/devices/{deviceId}/commands — list commands for a device</summary>
    [HttpGet("{deviceId}/commands")]
    public async Task<IActionResult> GetCommands(
        string deviceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var device = await _tenant.ApplyScope(_db.Devices.AsNoTracking())
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);

        if (device is null)
            return NotFound();

        var query = _db.CommandLogs.AsNoTracking()
            .Where(c => c.DeviceId == device.Id);

        var total = await query.CountAsync(cancellationToken);
        var commands = await query
            .OrderByDescending(c => c.RequestedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                commandId = c.Id,
                commandType = c.CommandType,
                status = c.Status.ToString(),
                requestedAt = c.RequestedAt,
                sentAt = c.SentAt,
                completedAt = c.CompletedAt,
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page, pageSize, commands });
    }

    /// <summary>GET /api/devices/{deviceId}/telemetry/{messageId}/raw — retrieve raw archived payload</summary>
    [HttpGet("{deviceId}/telemetry/{messageId}/raw")]
    [Authorize(Policy = "InternalOnly")]
    public async Task<IActionResult> GetRawPayload(
        string deviceId,
        string messageId,
        [FromServices] IBlobArchiveService archiveService,
        CancellationToken cancellationToken)
    {
        var device = await _tenant.ApplyScope(_db.Devices.AsNoTracking())
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);

        if (device is null)
            return NotFound();

        var record = await _db.TelemetryHistory
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.DeviceId == device.Id && t.MessageId == messageId, cancellationToken);

        if (record is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(record.RawPayloadBlobUri))
            return NotFound(new { error = "Raw payload not archived for this message." });

        var content = await archiveService.GetRawPayloadAsync(record.RawPayloadBlobUri, cancellationToken);
        if (content is null)
            return NotFound(new { error = "Raw payload blob not found in archive." });

        return Content(content, "application/json");
    }
}