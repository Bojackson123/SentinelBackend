namespace SentinelBackend.Api.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
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

    /// <summary>POST /api/companies — create a new company (internal only)</summary>
    [HttpPost]
    public async Task<IActionResult> CreateCompany(
        [FromBody] CreateCompanyRequest request,
        CancellationToken cancellationToken)
    {
        var company = new Company
        {
            Name = request.Name,
            ContactEmail = request.ContactEmail,
            ContactPhone = request.ContactPhone,
            BillingEmail = request.BillingEmail,
            FocalPointName = request.FocalPointName,
            SubscriptionStatus = Enum.Parse<SubscriptionStatus>(request.SubscriptionStatus ?? "Trialing", ignoreCase: true),
            IsInternal = request.IsInternal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.Companies.Add(company);
        await _db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetCompany), new { companyId = company.Id }, new { company.Id, company.Name });
    }
}

public class CreateCompanyRequest
{
    public string Name { get; set; } = default!;
    public string ContactEmail { get; set; } = default!;
    public string? ContactPhone { get; set; }
    public string BillingEmail { get; set; } = default!;
    public string? FocalPointName { get; set; }
    public string? SubscriptionStatus { get; set; }
    public bool IsInternal { get; set; }
}
