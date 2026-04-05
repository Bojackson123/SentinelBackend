// PumpBackend.Application/Interfaces/IDeviceRepository.cs
namespace SentinelBackend.Application.Interfaces;

using SentinelBackend.Domain.Entities;

public interface IDeviceRepository
{
    Task<Device?> GetBySerialNumberAsync(
        string serialNumber,
        CancellationToken cancellationToken = default
    );
    Task<Device?> GetByDeviceIdAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    );
    Task UpdateAsync(Device device, CancellationToken cancellationToken = default);

    Task<int> GetLastSequenceForPrefixAsync(
        string prefix,
        CancellationToken cancellationToken = default
    );

    Task AddRangeAsync(
        IEnumerable<Device> devices,
        CancellationToken cancellationToken = default
    );
}