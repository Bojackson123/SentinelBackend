// PumpBackend.Application/Models/ManufacturingCsvRow.cs
namespace SentinelBackend.Application.Models;

public class ManufacturingCsvRow
{
    public string SerialNumber { get; set; } = default!;
    public string DerivedKey { get; set; } = default!;
    public string HardwareRevision { get; set; } = default!;
    public DateTime ManufacturedAt { get; set; }
}