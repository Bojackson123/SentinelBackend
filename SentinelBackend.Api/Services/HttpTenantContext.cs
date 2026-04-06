namespace SentinelBackend.Api.Services;

using System.Security.Claims;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;

public class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _accessor;

    public HttpTenantContext(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    private ClaimsPrincipal User => _accessor.HttpContext?.User
        ?? throw new InvalidOperationException("No HttpContext available.");

    public string UserId =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("No user id claim.");

    public int? CompanyId =>
        int.TryParse(User.FindFirst("companyId")?.Value, out var id) ? id : null;

    public int? CustomerId =>
        int.TryParse(User.FindFirst("customerId")?.Value, out var id) ? id : null;

    public bool IsInternal =>
        User.IsInRole("InternalAdmin") || User.IsInRole("InternalTech");

    public bool IsCompanyUser =>
        User.IsInRole("CompanyAdmin") || User.IsInRole("CompanyTech");

    public bool IsHomeowner =>
        User.IsInRole("HomeownerViewer");

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

        // No scope match — return empty
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
}
