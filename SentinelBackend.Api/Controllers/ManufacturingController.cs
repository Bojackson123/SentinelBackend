// PumpBackend.Api/Controllers/ManufacturingController.cs
namespace SentinelBackend.Api.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SentinelBackend.Application.Interfaces;

[ApiController]
[Route("api/manufacturing")]
[Authorize(Roles = "InternalAdmin")]
public class ManufacturingController : ControllerBase
{
    private readonly IManufacturingBatchService _batchService;

    public ManufacturingController(IManufacturingBatchService batchService)
    {
        _batchService = batchService;
    }

    [HttpPost("batches")]
    public async Task<IActionResult> GenerateBatch(
        [FromBody] GenerateBatchRequest request,
        CancellationToken cancellationToken
    )
    {
        var result = await _batchService.GenerateBatchAsync(
            request.Quantity,
            request.HardwareRevision,
            cancellationToken
        );

        var csv = _batchService.ExportToCsv(result.CsvRows);

        return File(
            csv,
            "text/csv",
            $"manufacturing-batch-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv"
        );
    }
}

public class GenerateBatchRequest
{
    public int Quantity { get; set; }
    public string? HardwareRevision { get; set; }
}