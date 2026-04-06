namespace SentinelBackend.Api.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

[ApiController]
[Route("api/devices/{serialNumber}")]
[Authorize(Policy = "CompanyOrInternal")]
public class AssignmentsController : ControllerBase
{
    private readonly SentinelDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IDeviceTwinService _twinService;

    public AssignmentsController(SentinelDbContext db, ITenantContext tenant, IDeviceTwinService twinService)
    {
        _db = db;
        _tenant = tenant;
        _twinService = twinService;
    }

    // Allowed status transitions for assignment
    private static readonly Dictionary<DeviceStatus, DeviceStatus[]> _allowedTransitions = new()
    {
        [DeviceStatus.Manufactured] = [DeviceStatus.Unprovisioned, DeviceStatus.Decommissioned],
        [DeviceStatus.Unprovisioned] = [DeviceStatus.Assigned, DeviceStatus.Decommissioned],
        [DeviceStatus.Assigned] = [DeviceStatus.Active, DeviceStatus.Decommissioned],
        [DeviceStatus.Active] = [DeviceStatus.Decommissioned],
        [DeviceStatus.Decommissioned] = [],
    };

    /// <summary>POST /api/devices/{serialNumber}/assign</summary>
    [HttpPost("assign")]
    public async Task<IActionResult> AssignDevice(
        string serialNumber,
        [FromBody] AssignDeviceRequest request,
        CancellationToken cancellationToken)
    {
        // Homeowner cannot assign devices
        if (_tenant.IsHomeowner)
            return Forbid();

        var device = await _db.Devices
            .Include(d => d.Assignments)
            .FirstOrDefaultAsync(d => d.SerialNumber == serialNumber, cancellationToken);

        if (device is null)
            return NotFound("Device not found.");

        // Enforce valid status transitions — cannot assign decommissioned
        if (device.Status == DeviceStatus.Decommissioned)
            return BadRequest("Cannot assign a decommissioned device.");

        if (device.Status == DeviceStatus.Active)
            return BadRequest("Cannot assign a device that is already active.");

        // Enforce one active assignment per device
        var activeAssignment = device.Assignments
            .FirstOrDefault(a => a.UnassignedAt == null);

        if (activeAssignment is not null)
            return Conflict("Device already has an active assignment. Unassign first.");

        var site = await _db.Sites
            .Include(s => s.Customer)
            .FirstOrDefaultAsync(s => s.Id == request.SiteId, cancellationToken);

        if (site is null)
            return BadRequest("Site not found.");

        // Company users can only assign to sites belonging to their company
        if (_tenant.IsCompanyUser && site.Customer.CompanyId != _tenant.CompanyId)
            return Forbid();

        var userId = _tenant.UserId;

        var assignment = new DeviceAssignment
        {
            DeviceId = device.Id,
            SiteId = request.SiteId,
            AssignedByUserId = userId,
            AssignedAt = DateTime.UtcNow,
        };

        _db.DeviceAssignments.Add(assignment);

        // Transition: Unprovisioned/Manufactured → Assigned
        if (device.Status is DeviceStatus.Unprovisioned or DeviceStatus.Manufactured)
        {
            device.Status = DeviceStatus.Assigned;
        }

        device.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        // Trigger twin desired property update with site timezone
        if (device.DeviceId is not null)
        {
            await _twinService.SetDesiredPropertiesAsync(
                device.DeviceId,
                new Dictionary<string, object>
                {
                    ["siteTimezone"] = site.Timezone,
                    ["siteId"] = site.Id,
                },
                cancellationToken);
        }

        return Ok(new { AssignmentId = assignment.Id, device.SerialNumber, request.SiteId });
    }

    /// <summary>POST /api/devices/{serialNumber}/unassign</summary>
    [HttpPost("unassign")]
    public async Task<IActionResult> UnassignDevice(
        string serialNumber,
        [FromBody] UnassignDeviceRequest request,
        CancellationToken cancellationToken)
    {
        // Homeowner cannot unassign devices
        if (_tenant.IsHomeowner)
            return Forbid();

        var device = await _db.Devices
            .Include(d => d.Assignments)
                .ThenInclude(a => a.Site)
                    .ThenInclude(s => s.Customer)
            .FirstOrDefaultAsync(d => d.SerialNumber == serialNumber, cancellationToken);

        if (device is null)
            return NotFound("Device not found.");

        var activeAssignment = device.Assignments
            .FirstOrDefault(a => a.UnassignedAt == null);

        if (activeAssignment is null)
            return BadRequest("Device has no active assignment.");

        // Company users can only unassign from their own company's sites
        if (_tenant.IsCompanyUser && activeAssignment.Site.Customer.CompanyId != _tenant.CompanyId)
            return Forbid();

        var userId = _tenant.UserId;

        activeAssignment.UnassignedAt = DateTime.UtcNow;
        activeAssignment.UnassignedByUserId = userId;
        activeAssignment.UnassignmentReason = request.Reason;

        device.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { Message = "Device unassigned.", activeAssignment.Id });
    }
}

public class AssignDeviceRequest
{
    public int SiteId { get; set; }
}

public class UnassignDeviceRequest
{
    public UnassignmentReason Reason { get; set; }
}
