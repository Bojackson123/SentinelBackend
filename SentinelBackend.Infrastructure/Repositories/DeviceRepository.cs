// PumpBackend.Infrastructure/Repositories/DeviceRepository.cs
namespace SentinelBackend.Infrastructure.Repositories;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Infrastructure.Persistence;

public class DeviceRepository : IDeviceRepository
{
    private readonly SentinelDbContext _db;

    public DeviceRepository(SentinelDbContext db)
    {
        _db = db;
    }

    public Task<Device?> GetBySerialNumberAsync(
        string serialNumber,
        CancellationToken cancellationToken = default
    ) =>
        _db.Devices.FirstOrDefaultAsync(
            d => d.SerialNumber == serialNumber,
            cancellationToken
        );

    public Task<Device?> GetByDeviceIdAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    ) =>
        _db.Devices.FirstOrDefaultAsync(
            d => d.DeviceId == deviceId,
            cancellationToken
        );

    public async Task UpdateAsync(Device device, CancellationToken cancellationToken = default)
    {
        _db.Devices.Update(device);
        await _db.SaveChangesAsync(cancellationToken);
    }

    // Add to DeviceRepository
    public async Task<int> GetLastSequenceForPrefixAsync(
        string prefix,
        CancellationToken cancellationToken = default
    )
    {
        var last = await _db.Devices
            .Where(d => d.SerialNumber.StartsWith(prefix))
            .OrderByDescending(d => d.SerialNumber)
            .Select(d => d.SerialNumber)
            .FirstOrDefaultAsync(cancellationToken);

        if (last is null)
            return 0;

        // GP-YYYYMM-NNNNN → take the last segment
        var parts = last.Split('-');
        return int.TryParse(parts[^1], out var seq) ? seq : 0;
    }

    public async Task AddRangeAsync(
        IEnumerable<Device> devices,
        CancellationToken cancellationToken = default
    )
    {
        await _db.Devices.AddRangeAsync(devices, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }
}