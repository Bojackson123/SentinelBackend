// PumpBackend.Application/Services/ManufacturingBatchService.cs
namespace SentinelBackend.Application.Services;

using System.Globalization;
using System.Text;
using CsvHelper;
using Microsoft.Extensions.Logging;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Application.Models;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;

public class ManufacturingBatchService : IManufacturingBatchService
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDpsEnrollmentService _dpsEnrollmentService;
    private readonly ILogger<ManufacturingBatchService> _logger;

    public ManufacturingBatchService(
        IDeviceRepository deviceRepository,
        IDpsEnrollmentService dpsEnrollmentService,
        ILogger<ManufacturingBatchService> logger
    )
    {
        _deviceRepository = deviceRepository;
        _dpsEnrollmentService = dpsEnrollmentService;
        _logger = logger;
    }

    public async Task<ManufacturingBatchResult> GenerateBatchAsync(
        int quantity,
        string? hardwareRevision,
        CancellationToken cancellationToken = default
    )
    {
        var now = DateTime.UtcNow;
        var prefix = $"GP-{now:yyyyMM}";

        var lastSequence = await _deviceRepository.GetLastSequenceForPrefixAsync(
            prefix,
            cancellationToken
        );

        var devices = new List<Device>();
        var rows = new List<ManufacturingCsvRow>();

        for (var i = 0; i < quantity; i++)
        {
            var sequence = lastSequence + i + 1;
            var serialNumber = $"{prefix}-{sequence:D5}";

            var device = new Device
            {
                SerialNumber = serialNumber,
                HardwareRevision = hardwareRevision,
                Status = DeviceStatus.Manufactured,
                ManufacturedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };

            devices.Add(device);

            var derivedKey = _dpsEnrollmentService.DeriveDeviceKey(serialNumber);

            rows.Add(new ManufacturingCsvRow
            {
                SerialNumber = serialNumber,
                DerivedKey = derivedKey,
                HardwareRevision = hardwareRevision ?? string.Empty,
                ManufacturedAt = now,
            });
        }

        await _deviceRepository.AddRangeAsync(devices, cancellationToken);

        _logger.LogInformation(
            "Manufacturing batch created: {Quantity} devices with prefix {Prefix}",
            quantity,
            prefix
        );

        return new ManufacturingBatchResult
        {
            Devices = devices,
            CsvRows = rows,
        };
    }

    public byte[] ExportToCsv(IEnumerable<ManufacturingCsvRow> rows)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.WriteRecords(rows);
        writer.Flush();
        return ms.ToArray();
    }
}