// PumpBackend.Application/Models/ManufacturingBatchResult.cs
namespace SentinelBackend.Application.Models;

using SentinelBackend.Domain.Entities;

public class ManufacturingBatchResult
{
    public IReadOnlyList<Device> Devices { get; set; } = [];
    public IReadOnlyList<ManufacturingCsvRow> CsvRows { get; set; } = [];
}