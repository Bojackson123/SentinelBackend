// PumpBackend.Application/Services/DpsAllocationService.cs
namespace SentinelBackend.Application.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentinelBackend.Application.Dps;
using SentinelBackend.Application.Exceptions;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Contracts.Dps;
using SentinelBackend.Domain.Enums;

public class DpsAllocationService : IDpsAllocationService
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IOptions<DpsOptions> _dpsOptions;
    private readonly ILogger<DpsAllocationService> _logger;

    public DpsAllocationService(
        IDeviceRepository deviceRepository,
        IOptions<DpsOptions> dpsOptions,
        ILogger<DpsAllocationService> logger
    )
    {
        _deviceRepository = deviceRepository;
        _dpsOptions = dpsOptions;
        _logger = logger;
    }

    public async Task<DpsAllocationResponse> AllocateAsync(
        string registrationId,
        IEnumerable<string> linkedHubs,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation(
            "DPS allocation request received for registration ID {RegistrationId}",
            registrationId
        );

        var device = await _deviceRepository.GetBySerialNumberAsync(
            registrationId,
            cancellationToken
        );

        if (device is null)
        {
            _logger.LogWarning(
                "DPS allocation rejected — no device found with serial number {SerialNumber}",
                registrationId
            );
            throw new DpsAllocationException(
                $"Device with serial number '{registrationId}' is not recognized."
            );
        }

        if (device.Status == DeviceStatus.Decommissioned)
        {
            _logger.LogWarning(
                "DPS allocation rejected — device {SerialNumber} is decommissioned",
                registrationId
            );
            throw new DpsAllocationException(
                $"Device '{registrationId}' has been decommissioned and cannot provision."
            );
        }

        // First boot: transition from Manufactured → Unprovisioned
        // Re-provision: already Unprovisioned/Claimed/Unclaimed, just update timestamps
        var isFirstBoot = device.Status == DeviceStatus.Manufactured;

        device.DeviceId = registrationId; // serial number becomes the IoT Hub device ID
        device.ProvisionedAt ??= DateTime.UtcNow;
        device.UpdatedAt = DateTime.UtcNow;

        if (isFirstBoot)
        {
            device.Status = DeviceStatus.Unprovisioned;
            _logger.LogInformation(
                "Device {SerialNumber} provisioned for the first time",
                registrationId
            );
        }
        else
        {
            _logger.LogInformation(
                "Device {SerialNumber} re-provisioned, status remains {Status}",
                registrationId,
                device.Status
            );
        }

        await _deviceRepository.UpdateAsync(device, cancellationToken);

        return new DpsAllocationResponse
        {
            IotHubHostName = _dpsOptions.Value.IotHubHostName,
            InitialTwin = new DpsInitialTwin
            {
                Tags = new Dictionary<string, object>
                {
                    ["serialNumber"] = device.SerialNumber,
                    ["hardwareRevision"] = device.HardwareRevision ?? string.Empty,
                },
                Properties = new DpsInitialTwinProperties
                {
                    Desired = new Dictionary<string, object>
                    {
                        ["telemetryIntervalSeconds"] = 300,
                        ["diagnosticsEnabled"] = false,
                        ["highWaterThreshold"] = 1,
                    },
                },
            },
        };
    }
}