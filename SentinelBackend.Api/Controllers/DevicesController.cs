namespace SentinelBackend.Api.Controllers;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Contracts;
using SentinelBackend.Infrastructure.Persistence;

[ApiController]
[Route("api/devices")]
public class DevicesController : ControllerBase
{
    private readonly SentinelDbContext _db;

    public DevicesController(SentinelDbContext db)
    {
        _db = db;
    }

    [HttpGet("{deviceId}/state")]
    public async Task<ActionResult<DeviceStateResponse>> GetState(
        string deviceId,
        CancellationToken cancellationToken
    )
    {
        var device = await _db.Devices
            .Include(d => d.LatestState)
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);

        var state = device?.LatestState;

        if (state is null)
            return NotFound();

        return Ok(
            new DeviceStateResponse
            {
                DeviceId = deviceId,
                LastSeenAt = state.LastSeenAt,
                PanelVoltage = state.PanelVoltage,
                PumpCurrent = state.PumpCurrent,
                HighWaterAlarm = state.HighWaterAlarm,
                TemperatureC = state.TemperatureC,
                SignalRssi = state.SignalRssi,
                RuntimeSeconds = state.RuntimeSeconds,
                CycleCount = state.CycleCount,
                UpdatedAt = state.UpdatedAt,
            }
        );
    }
}