namespace SentinelBackend.Api.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Infrastructure.Persistence;

[ApiController]
[Route("api/sites")]
[Authorize(Policy = "CompanyOrInternal")]
public class SitesController : ControllerBase
{
    private readonly SentinelDbContext _db;
    private readonly ITenantContext _tenant;

    public SitesController(SentinelDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpPost]
    public async Task<IActionResult> CreateSite(
        [FromBody] CreateSiteRequest request,
        CancellationToken cancellationToken)
    {
        // Validate customer exists
        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.Id == request.CustomerId, cancellationToken);

        if (customer is null)
            return BadRequest("Customer not found.");

        // Tenant scoping: company users can only create sites for their company's customers
        if (_tenant.IsCompanyUser && customer.CompanyId != _tenant.CompanyId)
            return Forbid();

        var site = new Site
        {
            CustomerId = request.CustomerId,
            Name = request.Name,
            AddressLine1 = request.AddressLine1,
            AddressLine2 = request.AddressLine2,
            City = request.City,
            State = request.State,
            PostalCode = request.PostalCode,
            Country = request.Country,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Timezone = request.Timezone,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.Sites.Add(site);
        await _db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetSite), new { siteId = site.Id }, new { site.Id, site.Name });
    }

    [HttpGet("{siteId:int}")]
    public async Task<IActionResult> GetSite(int siteId, CancellationToken cancellationToken)
    {
        var site = await _tenant.ApplyScope(
                _db.Sites.AsNoTracking().Include(s => s.Customer))
            .FirstOrDefaultAsync(s => s.Id == siteId, cancellationToken);

        if (site is null)
            return NotFound();

        return Ok(new
        {
            site.Id,
            site.CustomerId,
            site.Name,
            site.AddressLine1,
            site.AddressLine2,
            site.City,
            site.State,
            site.PostalCode,
            site.Country,
            site.Latitude,
            site.Longitude,
            site.Timezone,
            site.CreatedAt,
        });
    }
}

public class CreateSiteRequest
{
    public int CustomerId { get; set; }
    public string Name { get; set; } = default!;
    public string AddressLine1 { get; set; } = default!;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = default!;
    public string State { get; set; } = default!;
    public string PostalCode { get; set; } = default!;
    public string Country { get; set; } = default!;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string Timezone { get; set; } = default!;
}
