// PumpBackend.Api/Controllers/DpsWebhookController.cs
namespace SentinelBackend.Api.Controllers;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SentinelBackend.Application.Dps;
using SentinelBackend.Application.Exceptions;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Contracts.Dps;

[ApiController]
[Route("api/dps")]
public class DpsWebhookController : ControllerBase
{
    private readonly IDpsAllocationService _allocationService;
    private readonly DpsOptions _options;
    private readonly ILogger<DpsWebhookController> _logger;

    public DpsWebhookController(
        IDpsAllocationService allocationService,
        IOptions<DpsOptions> options,
        ILogger<DpsWebhookController> logger
    )
    {
        _allocationService = allocationService;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Custom allocation webhook called by Azure DPS when a device provisions.
    /// Secure this URL — configure the webhook secret in DPS and validate it here.
    /// </summary>
    [HttpPost("allocate")]
    public async Task<IActionResult> Allocate(
        [FromBody] DpsAllocationRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!IsValidWebhookSecret())
        {
            _logger.LogWarning(
                "DPS webhook call rejected — invalid or missing secret"
            );
            return Unauthorized();
        }

        try
        {
            var response = await _allocationService.AllocateAsync(
                request.RegistrationId,
                request.LinkedHubs,
                cancellationToken
            );

            return Ok(response);
        }
        catch (DpsAllocationException ex)
        {
            _logger.LogWarning(
                "DPS allocation rejected for {RegistrationId}: {Reason}",
                request.RegistrationId,
                ex.Message
            );

            // DPS treats any non-2xx as a rejection
            return BadRequest(new { message = ex.Message });
        }
    }

    private bool IsValidWebhookSecret()
    {
        // DPS passes the webhook key as a query string param or custom header
        // depending on how you configured the webhook URL in the portal.
        // Using a query param here: ?code=<secret>
        if (!Request.Query.TryGetValue("code", out var code))
            return false;

        return string.Equals(
            code.ToString(),
            _options.WebhookSecret,
            StringComparison.Ordinal
        );
    }
}