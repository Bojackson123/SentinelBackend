namespace SentinelBackend.Api.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Infrastructure.Persistence;

[ApiController]
[Route("api/companies")]
[Authorize(Policy = "InternalOnly")]
public class CompaniesController : ControllerBase
{
    private readonly SentinelDbContext _db;

    public CompaniesController(SentinelDbContext db)
    {
        _db = db;
    }

    /// <summary>GET /api/companies — list all companies (internal only)</summary>
    [HttpGet]
    public async Task<IActionResult> GetCompanies(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var query = _db.Companies.AsNoTracking()
            .Where(c => !c.IsDeleted);

        var total = await query.CountAsync(cancellationToken);
        var companies = await query
            .OrderBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.ContactEmail,
                c.ContactPhone,
                SubscriptionStatus = c.SubscriptionStatus.ToString(),
                CustomerCount = c.Customers.Count,
                SiteCount = c.Customers.SelectMany(cu => cu.Sites).Count(s => !s.IsDeleted),
                DeviceCount = c.Customers.SelectMany(cu => cu.Sites)
                    .SelectMany(s => s.DeviceAssignments)
                    .Count(da => da.UnassignedAt == null),
                c.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page, pageSize, companies });
    }

    /// <summary>GET /api/companies/{companyId} — company detail (internal only)</summary>
    [HttpGet("{companyId:int}")]
    public async Task<IActionResult> GetCompany(
        int companyId,
        CancellationToken cancellationToken)
    {
        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId && !c.IsDeleted)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.ContactEmail,
                c.ContactPhone,
                c.BillingEmail,
                c.FocalPointName,
                SubscriptionStatus = c.SubscriptionStatus.ToString(),
                CustomerCount = c.Customers.Count,
                SiteCount = c.Customers.SelectMany(cu => cu.Sites).Count(s => !s.IsDeleted),
                DeviceCount = c.Customers.SelectMany(cu => cu.Sites)
                    .SelectMany(s => s.DeviceAssignments)
                    .Count(da => da.UnassignedAt == null),
                c.CreatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (company is null)
            return NotFound();

        return Ok(company);
    }
}
