namespace SentinelBackend.Application.Interfaces;

public interface ITenantContext
{
    string UserId { get; }
    int? CompanyId { get; }
    int? CustomerId { get; }
    bool IsInternal { get; }
    bool IsCompanyUser { get; }
    bool IsHomeowner { get; }

    /// <summary>
    /// Returns an IQueryable filtered by the current user's tenant scope.
    /// Internal users see everything; company users see their company; homeowners see their customer.
    /// </summary>
    IQueryable<T> ApplyScope<T>(IQueryable<T> query) where T : class;
}
