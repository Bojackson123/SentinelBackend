namespace SentinelBackend.Tests.Shared;

using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;

/// <summary>
/// In-memory tenant context for unit tests. Simulates role/claim-based scoping
/// without requiring HttpContext or JWT.
/// </summary>
public class FakeTenantContext : ITenantContext
{
    public string UserId { get; set; } = "test-user-id";
    public int? CompanyId { get; set; }
    public int? CustomerId { get; set; }
    public bool IsInternal { get; set; }
    public bool IsCompanyUser { get; set; }
    public bool IsHomeowner { get; set; }

    public IQueryable<T> ApplyScope<T>(IQueryable<T> query) where T : class
    {
        if (IsInternal)
            return query;

        return query switch
        {
            IQueryable<Device> devices => (IQueryable<T>)ApplyDeviceScope(devices),
            IQueryable<Alarm> alarms => (IQueryable<T>)ApplyAlarmScope(alarms),
            IQueryable<Site> sites => (IQueryable<T>)ApplySiteScope(sites),
            IQueryable<TelemetryHistory> telemetry => (IQueryable<T>)ApplyTelemetryScope(telemetry),
            _ => query,
        };
    }

    private IQueryable<Device> ApplyDeviceScope(IQueryable<Device> query)
    {
        if (IsCompanyUser && CompanyId.HasValue)
        {
            var companyId = CompanyId.Value;
            return query.Where(d =>
                d.Assignments.Any(a => a.UnassignedAt == null
                    && a.Site.Customer.CompanyId == companyId));
        }
        if (IsHomeowner && CustomerId.HasValue)
        {
            var customerId = CustomerId.Value;
            return query.Where(d =>
                d.Assignments.Any(a => a.UnassignedAt == null
                    && a.Site.CustomerId == customerId));
        }
        return query.Where(_ => false);
    }

    private IQueryable<Alarm> ApplyAlarmScope(IQueryable<Alarm> query)
    {
        if (IsCompanyUser && CompanyId.HasValue)
        {
            var companyId = CompanyId.Value;
            return query.Where(a =>
                a.Device.Assignments.Any(da => da.UnassignedAt == null
                    && da.Site.Customer.CompanyId == companyId));
        }
        if (IsHomeowner && CustomerId.HasValue)
        {
            var customerId = CustomerId.Value;
            return query.Where(a =>
                a.Device.Assignments.Any(da => da.UnassignedAt == null
                    && da.Site.CustomerId == customerId));
        }
        return query.Where(_ => false);
    }

    private IQueryable<Site> ApplySiteScope(IQueryable<Site> query)
    {
        if (IsCompanyUser && CompanyId.HasValue)
        {
            var companyId = CompanyId.Value;
            return query.Where(s => s.Customer.CompanyId == companyId);
        }
        if (IsHomeowner && CustomerId.HasValue)
        {
            var customerId = CustomerId.Value;
            return query.Where(s => s.CustomerId == customerId);
        }
        return query.Where(_ => false);
    }

    private IQueryable<TelemetryHistory> ApplyTelemetryScope(IQueryable<TelemetryHistory> query)
    {
        if (IsCompanyUser && CompanyId.HasValue)
        {
            var companyId = CompanyId.Value;
            return query.Where(t => t.CompanyId == companyId);
        }
        if (IsHomeowner && CustomerId.HasValue)
        {
            var customerId = CustomerId.Value;
            return query.Where(t => t.CustomerId == customerId);
        }
        return query.Where(_ => false);
    }

    public static FakeTenantContext Internal(string userId = "internal-user") => new()
    {
        UserId = userId,
        IsInternal = true,
    };

    public static FakeTenantContext ForCompany(int companyId, string userId = "company-user") => new()
    {
        UserId = userId,
        CompanyId = companyId,
        IsCompanyUser = true,
    };

    public static FakeTenantContext ForHomeowner(int customerId, string userId = "homeowner-user") => new()
    {
        UserId = userId,
        CustomerId = customerId,
        IsHomeowner = true,
    };
}
