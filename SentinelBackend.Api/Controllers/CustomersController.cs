namespace SentinelBackend.Api.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Infrastructure.Persistence;

[ApiController]
[Route("api/customers")]
[Authorize(Policy = "CompanyOrInternal")]
public class CustomersController : ControllerBase
{
    private readonly SentinelDbContext _db;
    private readonly ITenantContext _tenant;

    public CustomersController(SentinelDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>GET /api/customers — tenant-scoped customer list</summary>
    [HttpGet]
    public async Task<IActionResult> GetCustomers(
        [FromQuery] int? companyId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var query = _db.Customers.AsNoTracking().AsQueryable();

        // Tenant scoping
        if (_tenant.IsCompanyUser)
            query = query.Where(c => c.CompanyId == _tenant.CompanyId);
        else if (_tenant.IsInternal && companyId.HasValue)
            query = query.Where(c => c.CompanyId == companyId.Value);

        var total = await query.CountAsync(cancellationToken);
        var customers = await query
            .OrderBy(c => c.LastName).ThenBy(c => c.FirstName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.CompanyId,
                c.FirstName,
                c.LastName,
                c.Email,
                c.Phone,
                SiteCount = c.Sites.Count(s => !s.IsDeleted),
                c.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page, pageSize, customers });
    }

    /// <summary>POST /api/customers — create a new customer</summary>
    [HttpPost]
    public async Task<IActionResult> CreateCustomer(
        [FromBody] CreateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        // Tenant scoping: company users can only create customers for their company
        int? resolvedCompanyId = request.CompanyId;
        if (_tenant.IsCompanyUser)
        {
            resolvedCompanyId = _tenant.CompanyId;
        }
        else if (!_tenant.IsInternal)
        {
            return Forbid();
        }

        var customer = new Customer
        {
            CompanyId = resolvedCompanyId,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            Phone = request.Phone,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(null, new { customerId = customer.Id }, new
        {
            customer.Id,
            customer.FirstName,
            customer.LastName,
            customer.Email,
        });
    }
}

public class CreateCustomerRequest
{
    public int? CompanyId { get; set; }
    public string FirstName { get; set; } = default!;
    public string LastName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? Phone { get; set; }
}
