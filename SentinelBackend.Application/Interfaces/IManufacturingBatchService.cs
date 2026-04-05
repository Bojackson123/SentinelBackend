// PumpBackend.Application/Interfaces/IManufacturingBatchService.cs
namespace SentinelBackend.Application.Interfaces;

using SentinelBackend.Application.Models;

public interface IManufacturingBatchService
{
    Task<ManufacturingBatchResult> GenerateBatchAsync(
        int quantity,
        string? hardwareRevision,
        CancellationToken cancellationToken = default
    );

    byte[] ExportToCsv(IEnumerable<ManufacturingCsvRow> rows);
}