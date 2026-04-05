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
        var state = await _db.LatestDeviceStates.FirstOrDefaultAsync(
            s => s.DeviceId == deviceId,
            cancellationToken
        );

        if (state is null)
            return NotFound();

        return Ok(
            new DeviceStateResponse
            {
                DeviceId = state.DeviceId,
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